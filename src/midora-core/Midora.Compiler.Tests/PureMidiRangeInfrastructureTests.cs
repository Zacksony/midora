using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

[Collection("Process-wide memory measurements")]
public sealed class PureMidiRangeInfrastructureTests
{
    [Theory]
    [InlineData(120, false)] // future raw events do not force prefix replay
    [InlineData(0, false)]   // unmatched Off before any ordinary On is harmless
    [InlineData(10, true)]   // Off consumes an ordinary Note: exact merged prefix needed
    public void RawPrefixBuildIsLimitedToActualInteractions(long rawTick, bool needsIndex)
    {
        using MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Root" };
        PureMidiTrack track = new(p) { Name = "T", MidiChannelRootId = root.Id };
        MidiSegment s = new(p) { LengthTicks = 200 };
        s.Notes.Add(new(p) { StartTick = 5, LengthTicks = 80, Key = 60, NoteOnVelocity = 100 });
        s.ChannelEvents.Add(new(p) { Tick = rawTick, Kind = DirectMidiChannelEventKind.NoteOff, Data1 = 60, Data2 = 0 });
        track.Segments.Add(s); p.MidiChannelRoots.Add(root); p.PureMidiTracks.Add(track);
        p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using MidoraCompiler compiler = new();
        var result = compiler.CompileFull(p, new CompilationRequest { StartTick = 0, EndTick = 50 });
        Assert.True(result.IsConsumable);
        Assert.Equal(needsIndex ? 0 : 1, result.Events.ToArray().Count(e => e.Tick == 50 && e.Message.MessageType == MidiMessageType.NoteOff));
        var cache = (PureMidiEndpointPrefixCache)typeof(MidoraCompiler).GetField("_pureMidiPrefixes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(compiler)!;
        Assert.Equal(needsIndex, cache.CheckpointCount > 0);
        compiler.ClearCache(); Assert.Equal(0, cache.CheckpointCount);
    }

    [Fact]
    public void EmptyMidiTrackStillCompilesWithoutAZeroWidthPageQuery()
    {
        using MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Root" };
        PureMidiTrack track = new(p) { Name = "Empty", MidiChannelRootId = root.Id };
        p.MidiChannelRoots.Add(root); p.PureMidiTracks.Add(track);
        p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(p);
        Assert.True(result.IsConsumable);
        Assert.Equal(0, result.TotalEventCount);
        Assert.False(result.HasPagedEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalSpillRetainsCompletePureSourceAndOrder(bool descending)
    {
        using BoundedCanonicalMidiRenderEventSorter sorter = new(3, 2, descending);
        List<CanonicalMidiEvent> expected = [];
        for (int i = 0; i < 40; i++)
        {
            MidoraId track = MidoraId.FromSequence(3), segment = MidoraId.FromSequence(4), root = MidoraId.FromSequence(2);
            MidoraId id = i % 3 == 0 ? default : MidoraId.FromSequence(100 + i);
            SourceReference source = new(TrackId: track, SegmentId: segment, SourceEventId: id, Tick: i / 2,
                Origin: i % 3 == 0 ? SourceOrigin.MidiChannelRootLifecycle : SourceOrigin.RangeRestore,
                MidiChannelRootId: root, PureMidiTrackId: track, MidiSegmentId: segment, DirectMidiObjectId: id, ExportTrackId: track);
            CanonicalMidiEvent value = new(i, 15, 15, MidiMessage.ControlChange(15, (byte)i, 127),
                CanonicalEventRole.RangeRestore, long.MinValue + i, i, long.MaxValue - i, source, track, int.MinValue + 1, long.MaxValue - i);
            expected.Add(value); sorter.Add(value);
        }
        if (descending) expected.Reverse();
        Assert.Equal(expected, sorter.ReadCanonicalPages(default).SelectMany(page => page.Items));
    }

    [Fact]
    public void RawPrefixReusesExactTickAndExtensionsAndDoesNotPublishCancelledBuilds()
    {
        PureMidiEndpointPrefixCache cache = new();
        long readStart = -1;
        long end = 40000;
        IEnumerable<CanonicalMidiRenderEventPage> Read(long from)
        {
            readStart = from;
            for (long tick = from; tick <= end; tick++)
            {
                // Deliberate unmatched Offs, same-tick multiple messages and a
                // NoteOn excluded at the consumer end.
                List<CanonicalMidiRenderEvent> page = [Event(tick, false), Event(tick, false)];
                if (tick < end) { page.Add(Event(tick, true)); page.Add(Event(tick, true)); page.Add(Event(tick, true)); }
                yield return new(page.ToArray());
            }
        }
        long[] first = cache.GetCounts("revision-a", 0, end, Read, default);
        Assert.Equal(40000, first[60]);
        Assert.Equal(0, readStart);
        Assert.Equal(first, cache.GetCounts("revision-a", 0, end, Read, default));
        Assert.Equal(end, readStart);
        end = 45000;
        Assert.Equal(45000, cache.GetCounts("revision-a", 0, end, Read, default)[60]);
        Assert.Equal(40000, readStart);
        int before = cache.CheckpointCount;
        using CancellationTokenSource cancel = new();
        IEnumerable<CanonicalMidiRenderEventPage> Cancel(long from)
        { yield return new([Event(from, true)]); cancel.Cancel(); yield return new([Event(from + 1, true)]); }
        Assert.Throws<OperationCanceledException>(() => cache.GetCounts("revision-b", 0, end, Cancel, cancel.Token));
        Assert.Equal(before, cache.CheckpointCount);
        end = 100;
        Assert.Equal(100, cache.GetCounts("revision-b", 0, end, Read, default)[60]);
        Assert.Equal(0, readStart);
        cache.Clear(); Assert.Equal(0, cache.CheckpointCount);
    }

    [Fact]
    public void RawPrefixCacheEvictionHasAnExplicitBudget()
    {
        PureMidiEndpointPrefixCache cache = new();
        for (int i = 0; i <= PureMidiEndpointPrefixCache.MaximumCheckpoints; i++)
        {
            Assert.Equal(0, cache.GetCounts(i.ToString(), 0, 1, _ => [], default)[60]);
            Assert.True(cache.CheckpointCount <= PureMidiEndpointPrefixCache.MaximumCheckpoints);
        }
        cache.Clear(); Assert.Equal(0, cache.CheckpointCount);
    }

    [Fact]
    public void PagedStartCountsIncludeCowMovesRemovalsAndNewNotes()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "counts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using MidoraProject p = new(480);
            MidiSegment segment = new(p) { LengthTicks = 1000 };
            MidoraId[] ids = Enumerable.Range(0, 20).Select(_ => p.AllocateStableId()).ToArray();
            using PureMidiContentPackWriter writer = new(Path.Combine(folder, "source.mpk"));
            for (int i = 0; i < ids.Length; i++) writer.AddNote(segment.Id, new(ids[i], i * 10, 10, 60, 100, 127, i, i));
            using PureMidiContentPack pack = writer.Complete(); segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            segment.Notes.Single(n => n.Id == ids[3]).StartTick = 330;
            segment.Notes.Remove(segment.Notes.Single(n => n.Id == ids[5]));
            segment.Notes.Add(new(p) { StartTick = 35, LengthTicks = 100, Key = 70, NoteOnVelocity = 100 });
            DirectMidiNoteValue[] values = segment.Notes.EnumerateValues().ToArray();
            for (long left = 0; left < 400; left += 11)
                for (long right = left + 1; right < 410; right += 23)
                    Assert.Equal(values.LongCount(v => v.StartTick >= left && v.StartTick < right),
                        segment.Notes.CreateQuerySnapshot().CountStarts(left, right));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void InterleavedEndpointCursorsDoNotPinEveryDecodedPage()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "cursors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using PureMidiContentPackDecodedCache cache = new(4 * 1024 * 1024);
            using PureMidiContentPack pack = CreatePack(Path.Combine(folder, "source.mpk"), cache);
            var endpoints = (IPureMidiPlaybackEndpointSource)pack.GetSegmentSource(MidoraId.FromSequence(1));
            Assert.Equal(300000, endpoints.CountNoteStarts(0, 1));
            long before = GC.GetTotalMemory(forceFullCollection: true);
            using IEnumerator<DirectMidiNoteValue> cursor = endpoints.QueryNoteStarts(0, 1).GetEnumerator();
            Assert.True(cursor.MoveNext()); // all interleaved pages enter the merge queue
            long delta = GC.GetTotalMemory(forceFullCollection: true) - before;
            Assert.True(delta < 12 * 1024 * 1024, $"Endpoint cursors retained {delta:N0} extra bytes after collection.");
            long count = 1;
            MidoraId previous = cursor.Current.Id;
            while (cursor.MoveNext())
            {
                Assert.Equal(0, cursor.Current.StartTick);
                Assert.True(cursor.Current.Id.CompareTo(previous) > 0);
                previous = cursor.Current.Id; count++;
            }
            Assert.Equal(300000, count);
            Assert.True(cache.ByteCount <= 4 * 1024 * 1024);
        }
        finally { Directory.Delete(folder, true); }

        static PureMidiContentPack CreatePack(string path, PureMidiContentPackDecodedCache cache)
        {
            using PureMidiContentPackWriter writer = new(path);
            for (int i = 0; i < 600000; i++) writer.AddNote(MidoraId.FromSequence(1),
                new(MidoraId.FromSequence(2 + i), i % 2 == 0 ? 0 : 1000000, 1, 60, 100, 0, 0, 1));
            return writer.Complete(cache);
        }
    }

    private static CanonicalMidiRenderEvent Event(long tick, bool on) => new(tick, 0,
        on ? MidiMessage.NoteOn(0, 60, 100) : MidiMessage.NoteOff(0, 60, 0),
        MidoraId.FromSequence(1), MidoraId.FromSequence(1));
}

// GC.GetTotalMemory covers the process, not this fixture. Concurrent large
// compilation tests would otherwise be attributed to the endpoint cursor.
[CollectionDefinition("Process-wide memory measurements", DisableParallelization = true)]
public sealed class ProcessWideMemoryMeasurementsCollection;
