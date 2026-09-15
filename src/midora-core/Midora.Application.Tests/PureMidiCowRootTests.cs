using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PureMidiCowRootTests
{
    [Fact]
    public void CompilationMirrorCanForkAnEditableRootWithoutLosingAddedIdLookups()
    {
        using MidoraProject project = new(480);
        MidiSegment source = new(project);
        var note = new DirectMidiNote(project) { StartTick = 80, LengthTicks = 12, Key = 64 };
        var channel = new DirectMidiChannelEvent(project)
            { Tick = 80, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 42 };
        var opaque = new OpaqueMidiEvent(project) { Tick = 80, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [65] };
        source.Notes.Add(note);
        source.ChannelEvents.Add(channel);
        source.OpaqueEvents.Add(opaque);
        MidiSegment mirror = new(project);
        mirror.Notes.RestoreFormalSequenceForCompilation(source.Notes.CreateFormalSequenceSnapshot());
        mirror.ChannelEvents.RestoreFormalSequenceForCompilation(source.ChannelEvents.CreateFormalSequenceSnapshot());
        mirror.OpaqueEvents.RestoreFormalSequenceForCompilation(source.OpaqueEvents.CreateFormalSequenceSnapshot());
        MidiSegment fork = new(project);
        mirror.CloneContentTo(fork, default);
        Assert.Single(fork.Notes.CreateQuerySnapshot().QueryValues(0, 100, 0, 127));
        Assert.Single(fork.ChannelEvents.CreateQuerySnapshot().QueryValues(0, 100));
        Assert.Single(fork.OpaqueEvents.CreateQuerySnapshot().QueryValues(0, 100));
        Assert.True(fork.Notes.Remove(Assert.Single(fork.Notes.ResolveValuesByIds(new HashSet<MidoraId> { note.Id }))));
        Assert.True(fork.ChannelEvents.Remove(Assert.Single(fork.ChannelEvents.ResolveValuesByIds(new HashSet<MidoraId> { channel.Id }))));
        Assert.True(fork.OpaqueEvents.Remove(Assert.Single(fork.OpaqueEvents.ResolveValuesByIds(new HashSet<MidoraId> { opaque.Id }))));
        Assert.Empty(fork.Notes);
        Assert.Empty(fork.ChannelEvents);
        Assert.Empty(fork.OpaqueEvents);
        Assert.Single(source.Notes);
        Assert.Single(source.ChannelEvents);
        Assert.Single(source.OpaqueEvents);
    }

    [Fact]
    public void LargeEditedRootsForkWithoutDuplicatingMutableObjectsOrIndexes()
    {
        using MidoraProject project = new(480);
        MidiSegment source = new(project);
        const int count = 60_000;
        source.Notes.AddRange(Enumerable.Range(0, count).Select(index => new DirectMidiNote(project)
        {
            StartTick = index * 4L, LengthTicks = 2, Key = index % 128,
            NoteOnVelocity = 70, NoteOffVelocity = 33
        }));
        source.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(index => new DirectMidiChannelEvent(project)
        {
            Tick = index * 4L, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 70
        }));
        source.OpaqueEvents.AddRange(Enumerable.Range(0, count).Select(index => new OpaqueMidiEvent(project)
        {
            Tick = index * 4L, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [65, 66]
        }));
        _ = source.Notes.CreateQuerySnapshot();
        _ = source.ChannelEvents.CreateQuerySnapshot();
        _ = source.OpaqueEvents.CreateQuerySnapshot();
        MidiSegment warmup = new(project);
        source.CloneContentTo(warmup, default);

        long before = GC.GetAllocatedBytesForCurrentThread();
        MidiSegment clone = new(project);
        source.CloneContentTo(clone, default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Same(source.Notes.CreateFormalSequenceSnapshot().Added, clone.Notes.CreateFormalSequenceSnapshot().Added);
        Assert.Same(source.ChannelEvents.CreateFormalSequenceSnapshot().Added, clone.ChannelEvents.CreateFormalSequenceSnapshot().Added);
        Assert.Same(source.OpaqueEvents.CreateFormalSequenceSnapshot().Added, clone.OpaqueEvents.CreateFormalSequenceSnapshot().Added);
        Assert.Same(source.Notes.CreateQuerySnapshot(), clone.Notes.CreateQuerySnapshot());
        Assert.Equal(count, clone.Notes.Count);
        Assert.Equal(count, clone.ChannelEvents.Count);
        Assert.Equal(count, clone.OpaqueEvents.Count);

        clone.Notes[count / 2].Key = 27;
        clone.ChannelEvents[count / 2].Data2 = 42;
        clone.OpaqueEvents[count / 2].Tick = 123;
        Assert.Equal(count / 2 % 128, source.Notes[count / 2].Key);
        Assert.Equal(70, source.ChannelEvents[count / 2].Data2);
        Assert.Equal(count / 2 * 4L, source.OpaqueEvents[count / 2].Tick);
        source.Notes[count / 2 + 1].Key = 22;
        Assert.Equal((count / 2 + 1) % 128, clone.Notes[count / 2 + 1].Key);
    }

    [Fact]
    public void SourceReplacementsAndRemovalsRemainIsolatedAcrossForks()
    {
        using MidoraProject project = new(480);
        FormulaSource pages = new(4096);
        MidiSegment source = new(project);
        source.AttachPagedContent(pages);
        source.Notes[100].Key = 12;
        source.ChannelEvents[100].Data2 = 45;
        source.OpaqueEvents[100].Tick = 999;
        source.Notes.RemoveAt(200);
        source.ChannelEvents.RemoveAt(200);
        source.OpaqueEvents.RemoveAt(200);
        MidiSegment clone = new(project);
        source.CloneContentTo(clone, default);
        Assert.Same(source.Notes.CreateFormalSequenceSnapshot().Replacements, clone.Notes.CreateFormalSequenceSnapshot().Replacements);
        Assert.Same(source.Notes.CreateFormalSequenceSnapshot().RemovedSourceIds, clone.Notes.CreateFormalSequenceSnapshot().RemovedSourceIds);

        clone.Notes[100].Key = 70;
        clone.ChannelEvents[100].Data2 = 32;
        clone.OpaqueEvents[100].Tick = 500;
        source.Notes[101].Key = 13;
        source.ChannelEvents[101].Data2 = 46;
        source.OpaqueEvents[101].Tick = 1000;

        Assert.Equal(12, source.Notes[100].Key);
        Assert.Equal(45, source.ChannelEvents[100].Data2);
        Assert.Equal(999, source.OpaqueEvents[100].Tick);
        Assert.Equal(60, clone.Notes[101].Key);
        Assert.Equal(100, clone.ChannelEvents[101].Data2);
        Assert.Equal(101 * 4L, clone.OpaqueEvents[101].Tick);
        Assert.Equal(4095, clone.Notes.Count);
        Assert.Equal(pages.GetNote(201).Id, clone.Notes[200].Id);
        Assert.Equal(pages.GetChannelEvent(201).Id, clone.ChannelEvents[200].Id);
        Assert.Equal(pages.GetOpaqueEvent(201).Id, clone.OpaqueEvents[200].Id);
    }

    [Fact]
    public void ForkedOverlaySplicesPreserveFormalOrderAcrossPartialChunks()
    {
        using MidoraProject project = new(480);
        MidiSegment source = new(project);
        source.Notes.AddRange(Enumerable.Range(0, 1100).Select(index => new DirectMidiNote(project)
        {
            StartTick = index * 4L, LengthTicks = 2, Key = 60
        }));
        source.Notes.RemoveRange([source.Notes[2], source.Notes[257], source.Notes[512], source.Notes[1000]]);
        MidoraId[] expected = source.Notes.Select(note => note.Id).ToArray();
        MidiSegment fork = new(project);
        source.CloneContentTo(fork, default);
        Assert.Equal(expected, fork.Notes.Select(note => note.Id));
        DirectMidiNote inserted = new(project) { StartTick = 100, LengthTicks = 4, Key = 61 };
        fork.Notes.Insert(510, inserted);
        MidoraId[] nextExpected = [.. expected.Take(510), inserted.Id, .. expected.Skip(510)];
        MidiSegment secondFork = new(project);
        fork.CloneContentTo(secondFork, default);
        Assert.Equal(nextExpected, secondFork.Notes.Select(note => note.Id));
        Assert.Equal(expected, source.Notes.Select(note => note.Id));
        Assert.Equal(510, secondFork.Notes.IndexOf(secondFork.Notes[510]));
    }

    [Fact]
    public void ConsecutiveForksKeepTheOriginalPagedSourceAndRangeFingerprint()
    {
        using MidoraProject project = new(480);
        FormulaSource pages = new(4096);
        MidiSegment source = new(project);
        source.AttachPagedContent(pages);
        source.Notes[100].Key = 12;
        ulong unaffected = source.Notes.CreateQuerySnapshot().GetRangeFingerprint(8000, 9000);
        for (int index = 0; index < 32; index++)
        {
            MidiSegment next = new(project);
            source.CloneContentTo(next, default);
            Assert.Same(pages, next.Notes.PagedSource);
            next.Notes[100].Key = 12 + index % 4;
            Assert.Equal(unaffected, next.Notes.CreateQuerySnapshot().GetRangeFingerprint(8000, 9000));
            source = next;
        }
    }

    [Fact]
    public void AdoptResultPagesReadsNoRecordsAndCreatesOnlyRequestedMutableFacades()
    {
        using MidoraProject project = new(480);
        FormulaSource pages = new(1_000_000);
        MidiSegment owner = new(project);
        owner.Notes.AdoptContentSource(pages, 10);
        owner.ChannelEvents.AdoptContentSource(pages, 11);
        owner.OpaqueEvents.AdoptContentSource(pages, 12);
        Assert.Equal(0, pages.ReadCount);
        Assert.Equal(1_000_000, owner.Notes.Count);
        Assert.Equal(10, owner.Notes.Generation);
        Assert.Equal(11, owner.ChannelEvents.Generation);
        Assert.Equal(12, owner.OpaqueEvents.Generation);
        owner.Notes[500_000].Key = 24;
        Assert.Equal(1, pages.ReadCount);
        Assert.Equal(24, owner.Notes[500_000].Key);
        Assert.Equal(60, pages.GetNote(500_000).Key);
        Assert.Throws<InvalidOperationException>(() => owner.Notes.AdoptContentSource(pages, 20));
    }

    private sealed class FormulaSource(int count) : IPureMidiSegmentContentSource, IPureMidiContentRangeFingerprintSource, IPureMidiContentBoundsSource
    {
        public int ReadCount { get; private set; }
        public int NoteCount => count;
        public int ChannelEventCount => count;
        public int OpaqueEventCount => count;
        public string ContentFingerprint => "formula-root";
        public long MaximumNoteEndTick => count * 4L;
        public DirectMidiNoteValue GetNote(int index)
        {
            ReadCount++;
            return new(new MidoraId(10_000_000L + index), index * 4L, 2, 60, 100, 7, index * 2L, index * 2L + 1);
        }
        public DirectMidiChannelEventValue GetChannelEvent(int index)
        {
            ReadCount++;
            return new(new MidoraId(20_000_000L + index), index * 4L, DirectMidiChannelEventKind.ControlChange, 11, 100, index);
        }
        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            ReadCount++;
            return new(new MidoraId(30_000_000L + index), index * 4L, OpaqueMidiEventKind.Meta, 1, new byte[] { 65 }, index);
        }
        public int FindNoteIndex(MidoraId id) => Find(id, 10_000_000);
        public int FindChannelEventIndex(MidoraId id) => Find(id, 20_000_000);
        public int FindOpaqueEventIndex(MidoraId id) => Find(id, 30_000_000);
        private int Find(MidoraId id, long first) => id.Value >= first && id.Value < first + count ? (int)(id.Value - first) : -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127)
        {
            if (minimumKey > 60 || maximumKey < 60) yield break;
            for (int index = (int)Math.Max(0, (startTick - 1) / 4); index < count && index * 4L < endTick; index++)
                if (index * 4L + 2 > startTick) yield return GetNote(index);
        }
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick)
        {
            for (int index = (int)Math.Max(0, (startTick + 3) / 4); index < count && index * 4L < endTick; index++) yield return GetChannelEvent(index);
        }
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick)
        {
            for (int index = (int)Math.Max(0, (startTick + 3) / 4); index < count && index * 4L < endTick; index++) yield return GetOpaqueEvent(index);
        }
        public ulong GetNoteRangeFingerprint(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => 123;
        public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) => 234;
        public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) => 345;
    }
}
