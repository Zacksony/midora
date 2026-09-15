using System.Reflection;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PureMidiReadOnlyValuesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void FullReadUsesConstantAuxiliaryMemoryAndCreatesNoEditableIdentityEntries(int count)
    {
        using MidoraProject project = new(480);
        FormulaSource source = new(count);
        MidiSegment segment = new(project) { ContentOffsetTick = 300, LengthTicks = 1 };
        segment.AttachPagedContent(source);
        // Warm the iterator/JIT without heating any per-record facade or spatial index.
        Read(segment);
        source.Reads = 0;
        var generations = (segment.Notes.Generation, segment.ChannelEvents.Generation, segment.OpaqueEvents.Generation);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(3L * count, Read(segment));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.InRange(allocated, 0, 32 * 1024);
        Assert.Equal(3L * count, source.Reads);
        Assert.Equal(generations, (segment.Notes.Generation, segment.ChannelEvents.Generation, segment.OpaqueEvents.Generation));
        AssertNoFacades(segment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlayReplacementTombstoneAndReorderingKeepFormalOrder(bool useBase)
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project);
        if (useBase) segment.AttachPagedContent(new FormulaSource(6));
        AddTail(project, segment, 12);
        segment.Notes[1].StartTick = 9999;
        segment.ChannelEvents[1].Tick = 9999;
        segment.OpaqueEvents[1].Tick = 9999;
        segment.Notes.RemoveAt(2);
        segment.ChannelEvents.RemoveAt(2);
        segment.OpaqueEvents.RemoveAt(2);
        // Insert is a formal-list splice, not a time sort. Keep the deliberately
        // negative value: a persistence validator must see invalid/hidden data.
        segment.Notes.Insert(1, new(project) { StartTick = -1, LengthTicks = 7, Key = 72 });
        segment.ChannelEvents.Insert(1, new(project) { Tick = -1, Data1 = 11, Data2 = 7 });
        segment.OpaqueEvents.Insert(1, new(project) { Tick = -1, Payload = [9, 8] });

        AssertMatchesEditableList(segment);
        MidiSegment clone = new(project);
        segment.CloneContentTo(clone, default);
        AssertMatchesEditableList(clone);
        Assert.Equal(segment.Notes.Count, clone.Notes.Count);
    }

    [Fact]
    public void OverlayForkAndCompilationMirrorCanReadWithoutRecreatingFacades()
    {
        using MidoraProject project = new(480);
        MidiSegment source = new(project);
        source.AttachPagedContent(new FormulaSource(500));
        source.Notes[10].Key = 71;
        source.ChannelEvents[10].Data2 = 12;
        source.OpaqueEvents[10].Tick = 2000;
        AddTail(project, source, 1000);
        MidiSegment mirror = new(project);
        mirror.Notes.RestoreFormalSequenceForCompilation(source.Notes.CreateFormalSequenceSnapshot());
        mirror.ChannelEvents.RestoreFormalSequenceForCompilation(source.ChannelEvents.CreateFormalSequenceSnapshot());
        mirror.OpaqueEvents.RestoreFormalSequenceForCompilation(source.OpaqueEvents.CreateFormalSequenceSnapshot());
        MidiSegment fork = new(project);
        mirror.CloneContentTo(fork, default);
        Assert.Equal(source.Notes.EnumerateValues(), fork.Notes.EnumerateValues());
        Assert.Equal(source.ChannelEvents.EnumerateValues(), fork.ChannelEvents.EnumerateValues());
        Assert.Equal(source.OpaqueEvents.EnumerateValues(), fork.OpaqueEvents.EnumerateValues());
        AssertNoFacades(mirror);
        AssertNoFacades(fork);
    }

    [Fact]
    public void CaptureFreezesAllThreeRootsBeforeEnumerationAndKeepsOldValuesAfterClear()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project);
        segment.AttachPagedContent(new FormulaSource(20));
        AddTail(project, segment, 12);
        var notes = segment.Notes.EnumerateValues();
        var events = segment.ChannelEvents.EnumerateValues();
        var opaque = segment.OpaqueEvents.EnumerateValues();
        var expectedNotes = notes.ToArray();
        var expectedEvents = events.ToArray();
        var expectedOpaque = opaque.ToArray();
        segment.Notes[0].Key = 1;
        segment.ChannelEvents[0].Data2 = 1;
        segment.OpaqueEvents[0].Payload = [2, 3, 4];
        segment.Notes.Clear(); segment.ChannelEvents.Clear(); segment.OpaqueEvents.Clear();
        AddTail(project, segment, 5);
        Assert.Equal(expectedNotes, notes);
        Assert.Equal(expectedEvents, events);
        Assert.Equal(expectedOpaque, opaque);
        Assert.Equal(15, Read(segment));
        AssertMatchesEditableList(segment);
    }

    [Fact]
    public void ReadThenRetainedFacadeEditAndRevertStillTracksSourceIdentity()
    {
        using MidoraProject project = new(480);
        FormulaSource source = new(16);
        MidiSegment segment = new(project);
        segment.AttachPagedContent(source);
        var originalNotes = segment.Notes.EnumerateValues().ToArray();
        var originalEvents = segment.ChannelEvents.EnumerateValues().ToArray();
        var originalOpaque = segment.OpaqueEvents.EnumerateValues().ToArray();
        Assert.True(segment.Notes.TryGetById(originalNotes[4].Id, out var note));
        Assert.True(segment.ChannelEvents.TryGetById(originalEvents[4].Id, out var midiEvent));
        Assert.True(segment.OpaqueEvents.TryGetById(originalOpaque[4].Id, out var opaque));
        using (segment.Notes.BeginBatchChange([note!])) note!.Key = 7;
        using (segment.ChannelEvents.BeginBatchChange([midiEvent!])) midiEvent!.Data2 = 7;
        using (segment.OpaqueEvents.BeginBatchChange([opaque!])) opaque!.Tick = 7;
        Assert.Equal(7, segment.Notes.EnumerateValues().ElementAt(4).Key);
        Assert.Equal(7, segment.ChannelEvents.EnumerateValues().ElementAt(4).Data2);
        Assert.Equal(7, segment.OpaqueEvents.EnumerateValues().ElementAt(4).Tick);
        using (segment.Notes.BeginBatchChange([note!])) note!.Key = originalNotes[4].Key;
        using (segment.ChannelEvents.BeginBatchChange([midiEvent!])) midiEvent!.Data2 = originalEvents[4].Data2;
        using (segment.OpaqueEvents.BeginBatchChange([opaque!])) opaque!.Tick = originalOpaque[4].Tick;
        Assert.Equal(originalNotes, segment.Notes.EnumerateValues());
        Assert.Equal(originalEvents, segment.ChannelEvents.EnumerateValues());
        Assert.Equal(originalOpaque, segment.OpaqueEvents.EnumerateValues());
        Assert.Empty(segment.Notes.CreateFormalSequenceSnapshot().Replacements);
        Assert.Empty(segment.ChannelEvents.CreateFormalSequenceSnapshot().Replacements);
        Assert.Empty(segment.OpaqueEvents.CreateFormalSequenceSnapshot().Replacements);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationIsObservedDuringBaseAndAddedTraversal(int kind)
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project);
        segment.AttachPagedContent(new FormulaSource(2000));
        VerifyCancellation();
        segment.Notes.Clear(); segment.ChannelEvents.Clear(); segment.OpaqueEvents.Clear();
        AddTail(project, segment, 2000);
        VerifyCancellation();

        void VerifyCancellation()
        {
            using CancellationTokenSource cancellation = new();
            IEnumerable<MidoraId> values = kind switch
            {
                0 => segment.Notes.EnumerateValues(cancellation.Token).Select(v => v.Id),
                1 => segment.ChannelEvents.EnumerateValues(cancellation.Token).Select(v => v.Id),
                _ => segment.OpaqueEvents.EnumerateValues(cancellation.Token).Select(v => v.Id)
            };
            int read = 0;
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                foreach (var _ in values)
                {
                    if (++read == 1) cancellation.Cancel();
                }
            });
            Assert.InRange(read, 1, 256);
            Assert.ThrowsAny<OperationCanceledException>(() => segment.Notes.EnumerateValues(cancellation.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => segment.ChannelEvents.EnumerateValues(cancellation.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => segment.OpaqueEvents.EnumerateValues(cancellation.Token));
        }
    }

    [Fact]
    public void ReadDoesNotReplaceIndexedIdentityLookupWithAFullSourceScan()
    {
        using MidoraProject project = new(480);
        FormulaSource source = new(1_000_000);
        MidiSegment segment = new(project);
        segment.AttachPagedContent(source);
        Read(segment);
        source.Reads = 0;
        source.FindCalls = 0;
        MidoraId id = new(10_999_999);
        Assert.True(segment.Notes.TryGetById(id, out var first));
        Assert.True(segment.Notes.TryGetById(id, out var second));
        Assert.Same(first, second);
        Assert.Equal(1, source.FindCalls);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void UncommittedBatchCannotEscapeAsAStaleReadSnapshot()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project);
        using (segment.Notes.BeginBatchChange([]))
            Assert.Throws<InvalidOperationException>(() => segment.Notes.EnumerateValues());
        using (segment.ChannelEvents.BeginBatchChange([]))
            Assert.Throws<InvalidOperationException>(() => segment.ChannelEvents.EnumerateValues());
        using (segment.OpaqueEvents.BeginBatchChange([]))
            Assert.Throws<InvalidOperationException>(() => segment.OpaqueEvents.EnumerateValues());
        Assert.Equal(0, Read(segment));
    }

    private static long Read(MidiSegment segment)
    {
        long count = 0;
        foreach (var _ in segment.Notes.EnumerateValues()) count++;
        foreach (var _ in segment.ChannelEvents.EnumerateValues()) count++;
        foreach (var _ in segment.OpaqueEvents.EnumerateValues()) count++;
        return count;
    }

    private static void AddTail(MidoraProject project, MidiSegment segment, int count)
    {
        // Repeated starts are intentional imported duplicates, not edit collisions.
        segment.Notes.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiNote(project)
            { StartTick = i / 2, LengthTicks = 99, Key = 60, NoteOffVelocity = 127, NoteOnOrder = count - i }));
        segment.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(project)
            { Tick = i / 2, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = i % 128, Order = count - i }));
        segment.OpaqueEvents.AddRange(Enumerable.Range(0, count).Select(i => new OpaqueMidiEvent(project)
            { Tick = i / 2, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [(byte)(i % 128)], Order = count - i }));
    }

    private static void AssertMatchesEditableList(MidiSegment segment)
    {
        var notes = segment.Notes.EnumerateValues().ToArray();
        var events = segment.ChannelEvents.EnumerateValues().ToArray();
        var opaque = segment.OpaqueEvents.EnumerateValues().ToArray();
        Assert.Equal(segment.Notes.Count, notes.Length);
        Assert.Equal(segment.ChannelEvents.Count, events.Length);
        Assert.Equal(segment.OpaqueEvents.Count, opaque.Length);
        Assert.Equal(segment.Notes.Select(v => new DirectMidiNoteValue(v.Id, v.StartTick, v.LengthTicks,
            v.Key, v.NoteOnVelocity, v.NoteOffVelocity, v.NoteOnOrder, v.NoteOffOrder)), notes);
        Assert.Equal(segment.ChannelEvents.Select(v => new DirectMidiChannelEventValue(v.Id, v.Tick,
            v.Kind, v.Data1, v.Data2, v.Order)), events);
        Assert.Equal(segment.OpaqueEvents.Select(v => (v.Id, v.Tick, v.Kind, v.MetaType, Convert.ToHexString(v.Payload), v.Order)),
            opaque.Select(v => (v.Id, v.Tick, v.Kind, v.MetaType, Convert.ToHexString(v.Payload.Span), v.Order)));
    }

    private static void AssertNoFacades(MidiSegment segment)
    {
        foreach (object collection in new object[] { segment.Notes, segment.ChannelEvents, segment.OpaqueEvents })
        {
            foreach (string field in new[] { "_materializedSourceIds", "_materializedSourceValues", "_materializedSourceItems", "_sourceIndices" })
            {
                object? value = collection.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(collection);
                if (value is not null) Assert.Equal(0, (int)value.GetType().GetProperty("Count")!.GetValue(value)!);
            }
            foreach (string field in new[] { "_added", "_replacements" })
            {
                object value = collection.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collection)!;
                var items = (System.Collections.IDictionary)value.GetType().GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
                Assert.Empty(items);
            }
        }
    }

    // Spatial queries deliberately fail: validation must enumerate every ordinal,
    // including hidden/invalid values, without constructing any spatial snapshot.
    private sealed class FormulaSource(int count) : IPureMidiSegmentContentSource
    {
        private static readonly byte[] Payload = [0x41, 0xf7];
        public long Reads { get; set; }
        public int FindCalls { get; set; }
        public int NoteCount => count;
        public int ChannelEventCount => count;
        public int OpaqueEventCount => count;
        public string ContentFingerprint => "read-only-formula";
        public DirectMidiNoteValue GetNote(int i)
        { Reads++; return new(new(10_000_000L + i), i * 4L, 2, 60, 100, 7, i * 2L, i * 2L + 1); }
        public DirectMidiChannelEventValue GetChannelEvent(int i)
        { Reads++; return new(new(20_000_000L + i), i * 4L, DirectMidiChannelEventKind.ControlChange, 11, 100, i); }
        public OpaqueMidiEventValue GetOpaqueEvent(int i)
        { Reads++; return new(new(30_000_000L + i), i * 4L, OpaqueMidiEventKind.SystemExclusive, 0, Payload, i); }
        public int FindNoteIndex(MidoraId id) => Find(id, 10_000_000);
        public int FindChannelEventIndex(MidoraId id) => Find(id, 20_000_000);
        public int FindOpaqueEventIndex(MidoraId id) => Find(id, 30_000_000);
        private int Find(MidoraId id, long start)
        { FindCalls++; return id.Value >= start && id.Value < start + count ? (int)(id.Value - start) : -1; }
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => throw new NotSupportedException();
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => throw new NotSupportedException();
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => throw new NotSupportedException();
    }
}
