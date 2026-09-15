using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class PureMidiBoundaryProjectionTests
{
    [Fact]
    public void CowEditsInvalidateRawFifoPrefixesAndIncrementalMatchesFreshFullCompile()
    {
        using MidoraProject project = Create(4, raw: true);
        string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "cow-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using PureMidiContentPack pack = Page(project, Path.Combine(folder, "source.mpk"));
            using MidoraCompiler cached = new();
            CompilationRequest request = new() { Purpose = CompilationPurpose.Playback, StartTick = 23, EndTick = 57 };
            cached.CompileFull(project, request);
            for (int i = 0; i < 12; i++)
            {
                PureMidiTrack track = project.PureMidiTracks[i % 3];
                MidiSegment segment = track.Segments[0];
                DirectMidiChannelEvent raw = segment.ChannelEvents.First(e => e.Kind == DirectMidiChannelEventKind.NoteOff);
                raw.Tick = 10 + i * 3; raw.Data1 = 58 + i % 5;
                DirectMidiNote note = segment.Notes[0];
                note.StartTick = 10 + i; note.LengthTicks = 5 + i * 3; note.Key = 60 + i % 2;
                if (i == 4) segment.ChannelEvents.Add(new(project) { Tick = 30, Kind = DirectMidiChannelEventKind.NoteOn, Data1 = 60, Data2 = 80, Order = 1000 });
                if (i == 8) segment.Notes.RemoveAt(1);
                if (i == 9) track.Segments[1].ContentOffsetTick = 12;
                ProjectChangeSet changes = new(); changes.PureMidiTrackIds.Add(track.Id);
                CanonicalCompiledResult incremental = cached.CompileIncremental(project, changes, request);
                using MidoraCompiler fresh = new();
                CanonicalCompiledResult full = fresh.CompileFull(project, request);
                Assert.True(full.IsConsumable); Assert.True(incremental.IsConsumable);
                Assert.Equal(full.Fingerprint, incremental.Fingerprint);
                CanonicalMidiEvent[] expected = full.QueryEventPages(23, 57, true).SelectMany(p => p.Items).ToArray();
                CanonicalMidiEvent[] actual = incremental.QueryEventPages(23, 57, true).SelectMany(p => p.Items).ToArray();
                Assert.Equal(expected, actual);
                Assert.Equal(actual.LongLength, incremental.TotalEventCount);
                Assert.Equal(actual.LongCount(e => e.Message.MessageType == MidiMessageType.NoteOn && e.Message.Byte2 != 0), incremental.TotalNoteOnEventCount);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void EarlierSiblingPresetAndStateRemainPartOfRangeAudioIdentity()
    {
        using MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Shared" };
        p.MidiChannelRoots.Add(root);
        for (int i = 0; i < 2; i++)
        {
            PureMidiTrack t = new(p) { Name = "T" + i, MidiChannelRootId = root.Id };
            MidiSegment s = new(p) { LengthTicks = i == 0 ? 100 : 20 };
            if (i == 0) s.Notes.Add(new(p) { StartTick = 45, LengthTicks = 40, Key = 60, NoteOnVelocity = 100 });
            else
            {
                s.ChannelEvents.Add(new(p) { Tick = 1, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 0, Data2 = 3 });
                s.ChannelEvents.Add(new(p) { Tick = 2, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 41 });
            }
            t.Segments.Add(s); p.PureMidiTracks.Add(t); p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, t.Id));
        }
        string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "preset-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using PureMidiContentPack pack = Page(p, Path.Combine(folder, "source.mpk"));
            using MidoraCompiler compiler = new();
            CompilationRequest request = new() { StartTick = 40, EndTick = 60, Purpose = CompilationPurpose.Playback };
            CanonicalCompiledResult before = compiler.CompileFull(p, request);
            Assert.True(before.IsConsumable);
            Assert.Contains(before.PureMidiPresetReferences, r => r.Bank == 3 && r.Program == 41);
            var fragment = Assert.Single(before.PureMidiAudioFragments);
            Assert.Contains(p.PureMidiTracks[1].Id, fragment.ContributingTrackIds);
            p.PureMidiTracks[1].Segments[0].ChannelEvents.Single(e => e.Kind == DirectMidiChannelEventKind.ProgramChange).Data1 = 42;
            CanonicalCompiledResult after = compiler.CompileFull(p, request);
            Assert.Contains(after.PureMidiPresetReferences, r => r.Bank == 3 && r.Program == 42);
            Assert.NotEqual(fragment.SemanticFingerprint, Assert.Single(after.PureMidiAudioFragments).SemanticFingerprint);
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomFifoCropsRangesAndWindowReadsAreIndependentOfSourceLayout(bool raw)
    {
        for (int seed = 0; seed < 12; seed++)
        {
            using MidoraProject resident = Create(seed, raw);
            using MidoraProject paged = Create(seed, raw);
            string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "range-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                using PureMidiContentPack pack = Page(paged, Path.Combine(folder, "source.mpk"));
                using MidoraCompiler a = new(), b = new();
                foreach ((long start, long end) in new[] { (0L, 100L), (23L, 57L), (40L, 80L), (50L, 100L), (49L, 50L) })
                {
                    CompilationRequest request = new() { StartTick = start, EndTick = end, Purpose = CompilationPurpose.Playback };
                    CanonicalCompiledResult x = a.CompileFull(resident, request), y = b.CompileFull(paged, request);
                    Assert.True(x.IsConsumable, string.Join("\n", x.Diagnostics));
                    Assert.True(y.IsConsumable, string.Join("\n", y.Diagnostics));
                    CanonicalMidiEvent[] xe = x.QueryEventPages(start, end, true).SelectMany(p => p.Items).ToArray();
                    CanonicalMidiEvent[] ye = y.QueryEventPages(start, end, true).SelectMany(p => p.Items).ToArray();
                    Assert.Equal(xe, ye);
                    Assert.True(ye.LongLength == y.TotalEventCount, $"seed={seed},raw={raw},range={start}..{end}: actual={ye.LongLength},declared={y.TotalEventCount}");
                    Assert.Equal(ye.LongCount(v => v.Message.MessageType == MidiMessageType.NoteOn && v.Message.Byte2 != 0), y.TotalNoteOnEventCount);
                    Assert.Equal(x.TotalEventCount, y.TotalEventCount);
                    var expected = ExpectedNotes(resident, start, end);
                    var actual = ye.Where(v => v.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff)
                        .Select(v => (v.Tick, v.Message.MessageType, v.Message.Byte1, v.Message.Byte2, v.Source.DirectMidiObjectId)).ToArray();
                    Assert.True(expected.SequenceEqual(actual), $"seed={seed},raw={raw},range={start}..{end}\nExpected: {string.Join(';', expected)}\nActual: {string.Join(';', actual)}");
                    List<CanonicalMidiEvent> windows = [];
                    for (long tick = start; tick < end; tick += 7)
                        windows.AddRange(y.QueryEventPages(tick, Math.Min(end, tick + 7), false).SelectMany(p => p.Items));
                    Assert.Equal(ye, windows);
                    foreach (PureMidiTrack track in resident.PureMidiTracks)
                        Assert.Equal(x.QuerySmfTrackChannelEventPages(track.Id).SelectMany(p => p.Items),
                            y.QuerySmfTrackChannelEventPages(track.Id).SelectMany(p => p.Items));
                    foreach (CanonicalMidiEvent restore in ye.Where(v => v.Role == CanonicalEventRole.RangeRestore && v.Message.Byte1 == 11))
                    {
                        int reset = Array.FindLastIndex(ye, v => v.Tick == start && v.Role == CanonicalEventRole.RangeRestore && v.Message.Byte1 == 121);
                        Assert.True(reset < Array.IndexOf(ye, restore));
                    }
                }
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    private static MidoraProject Create(int seed, bool raw)
    {
        MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Root", RoutingMode = seed % 2 == 0 ? MidiChannelRootRoutingMode.Auto : MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2, FixedZeroBasedChannel = 9 };
        p.MidiChannelRoots.Add(root);
        Random random = new(seed);
        for (int t = 0; t < 3; t++)
        {
            PureMidiTrack track = new(p) { Name = "T" + t, MidiChannelRootId = root.Id };
            p.PureMidiTracks.Add(track); p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            for (int s = 0; s < 2; s++)
            {
                MidiSegment segment = new(p) { ProjectStartTick = 50 * s, ContentOffsetTick = 10, LengthTicks = 50 };
                track.Segments.Add(segment);
                for (int n = 0; n < 25; n++) segment.Notes.Add(new(p) { StartTick = random.Next(0, 70), LengthTicks = random.Next(1, 60),
                    Key = random.Next(58, 63), NoteOnVelocity = random.Next(1, 128), NoteOffVelocity = random.Next(128),
                    NoteOnOrder = n * 3, NoteOffOrder = n * 3 + 2 });
                segment.ChannelEvents.Add(new(p) { Tick = 20, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 50 + t, Order = 80 });
                if (raw) for (int n = 0; n < 20; n++) segment.ChannelEvents.Add(new(p) { Tick = random.Next(10, 60),
                    Kind = n % 3 == 0 ? DirectMidiChannelEventKind.NoteOn : DirectMidiChannelEventKind.NoteOff,
                    Data1 = random.Next(58, 63), Data2 = n % 3 == 0 ? 90 : 32, Order = n + 90 });
            }
        }
        return p;
    }

    private static PureMidiContentPack Page(MidoraProject p, string path)
    {
        using PureMidiContentPackWriter writer = new(path);
        foreach (MidiSegment s in p.PureMidiTracks.SelectMany(t => t.Segments))
        {
            foreach (DirectMidiNote n in s.Notes) writer.AddNote(s.Id, new(n.Id, n.StartTick, n.LengthTicks, n.Key, n.NoteOnVelocity, n.NoteOffVelocity, n.NoteOnOrder, n.NoteOffOrder));
            foreach (DirectMidiChannelEvent e in s.ChannelEvents) writer.AddChannelEvent(s.Id, new(e.Id, e.Tick, e.Kind, e.Data1, e.Data2, e.Order));
        }
        PureMidiContentPack pack = writer.Complete();
        foreach (MidiSegment s in p.PureMidiTracks.SelectMany(t => t.Segments))
        { s.Notes.Clear(); s.ChannelEvents.Clear(); s.AttachPagedContent(pack.GetSegmentSource(s.Id)); }
        return pack;
    }

    // Independent small oracle: original endpoints, per-Segment raw FIFO closure,
    // then Channel FIFO. No production range/count/placement helpers are reused.
    private static (long, MidiMessageType, byte, byte, MidoraId)[] ExpectedNotes(MidoraProject p, long start, long end)
    {
        List<(long Tick, int Track, long Order, long Tie, bool On, byte Key, byte Velocity, MidoraId Id)> all = [];
        for (int t = 0; t < p.PureMidiTracks.Count; t++) foreach (MidiSegment s in p.PureMidiTracks[t].Segments)
        {
            if (s.ProjectStartTick >= end) continue;
            foreach (DirectMidiNote n in s.Notes.Where(n => n.StartTick >= s.ContentOffsetTick && n.StartTick < s.ContentOffsetTick + s.LengthTicks))
            {
                long tick = s.ProjectStartTick + n.StartTick - s.ContentOffsetTick;
                all.Add((tick, t, n.NoteOnOrder, n.Id.Value, true, (byte)n.Key, (byte)n.NoteOnVelocity, n.Id));
                all.Add((Math.Min(s.ProjectStartTick + s.LengthTicks, tick + n.LengthTicks), t, n.NoteOffOrder, long.MinValue + n.Id.Value, false, (byte)n.Key, (byte)n.NoteOffVelocity, n.Id));
            }
            Queue<MidoraId>[] rawActive = Enumerable.Range(0, 128).Select(_ => new Queue<MidoraId>()).ToArray();
            foreach (DirectMidiChannelEvent e in s.ChannelEvents.OrderBy(e => e.Tick).ThenBy(e => e.Order).ThenBy(e => e.Id))
            {
                if (e.Kind is not (DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)) continue;
                bool on = e.Kind == DirectMidiChannelEventKind.NoteOn && e.Data2 != 0;
                all.Add((s.ProjectStartTick + e.Tick - s.ContentOffsetTick, t, e.Order, on ? e.Id.Value : long.MinValue + e.Id.Value,
                    on, (byte)e.Data1, (byte)e.Data2, e.Id));
                if (on) rawActive[e.Data1].Enqueue(e.Id); else if (rawActive[e.Data1].Count != 0) rawActive[e.Data1].Dequeue();
            }
            long order = long.MaxValue / 8;
            for (int key = 0; key < 128; key++) foreach (MidoraId id in rawActive[key])
                all.Add((s.ProjectStartTick + s.LengthTicks, t, order++, 0, false, (byte)key, 0, id));
        }
        Queue<MidoraId>[] active = Enumerable.Range(0, 128).Select(_ => new Queue<MidoraId>()).ToArray();
        List<(long, MidiMessageType, byte, byte, MidoraId)> actual = [], terminal = [];
        foreach (var e in all.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Order).ThenBy(e => e.Tie))
        {
            if (e.Tick > end || e.Tick == end && e.On) continue;
            if (e.On) active[e.Key].Enqueue(e.Id); else if (active[e.Key].Count != 0) active[e.Key].Dequeue();
            if (e.Tick >= start) actual.Add((e.Tick, e.On ? MidiMessageType.NoteOn : MidiMessageType.NoteOff, e.Key, e.Velocity, e.Id));
        }
        for (int key = 0; key < 128; key++) foreach (MidoraId id in active[key]) terminal.Add((end, MidiMessageType.NoteOff, (byte)key, 0, id));
        int firstEnd = actual.FindIndex(e => e.Item1 == end);
        actual.InsertRange(firstEnd < 0 ? actual.Count : firstEnd, terminal);
        return actual.ToArray();
    }
}
