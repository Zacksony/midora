using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

[CollectionDefinition("MemoryStage4LogicalValueRoots", DisableParallelization = true)]
public sealed class MemoryStage4LogicalValueRootCollection;

[Collection("MemoryStage4LogicalValueRoots")]
public sealed class MemoryStage4LogicalValueRootTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void OrdinaryLogicalConstructionUsesValueRootAndPreservesLiveIdentity(int count)
    {
        using MidoraProject project = new(480);
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var (segment, held, discarded) = BuildLogical(project, count);
        output.WriteLine($"Logical ordinary count={count}, elapsedMs={watch.Elapsed.TotalMilliseconds:F2}, allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocation}");
        Assert.InRange(segment.Notes.StorageCounts.RetainedObjects, 0, 4095);
        Collect();
        Assert.False(discarded.IsAlive);
        LogicalNoteQuerySnapshot old = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(0, segment.Notes.StorageCounts.RetainedObjects);
        Assert.InRange(segment.Notes.StorageCounts.FacadeSlots, 1, 4096);
        Assert.Same(held, segment.Notes[21]);
        Assert.Same(old.Values, segment.Notes.CreateQuerySnapshot().Values);
        Assert.Equal(count, old.Count);
        for (int i = 0; i < count; i += 997)
        {
            var value = old.GetByOrdinal(i);
            Assert.Equal(i * 4L, value.StartTick);
            Assert.True(old.TryFindOrdinalById(value.Id, out int resolved));
            Assert.Equal(i, resolved);
        }
        ulong farFingerprint = old.GetRangeFingerprint((count - 100) * 4L, count * 4L);
        allocation = GC.GetAllocatedBytesForCurrentThread();
        var clone = new Segment(project);
        clone.Notes.AdoptSnapshot(project, old);
        Assert.Same(old.Values, clone.Notes.CreateQuerySnapshot().Values);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocation, 0, 64 * 1024);
        held.SetValues(85, 6, 61, 57);
        var changed = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(57, changed.GetByOrdinal(21).Velocity);
        Assert.Equal(100, old.GetByOrdinal(21).Velocity);
        Assert.Equal(100, clone.Notes[21].Velocity);
        Assert.Equal(farFingerprint, changed.GetRangeFingerprint((count - 100) * 4L, count * 4L));
        Assert.Contains(changed.QueryValues(85, 86), value => value.Id == held.Id);
        Assert.Equal(count, old.EnumerateAll().Count());
        GC.KeepAlive(held);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void OrdinarySubVoiceConstructionUsesValueRootWithoutRecreatingMapping(int count)
    {
        using MidoraProject project = new(480);
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var (voice, held, discarded) = BuildTemplate(project, count);
        output.WriteLine($"SubVoice ordinary count={count}, elapsedMs={watch.Elapsed.TotalMilliseconds:F2}, allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocation}");
        Assert.InRange(voice.Events.StorageCounts.RetainedObjects, 0, 4095);
        Collect();
        Assert.False(discarded.IsAlive);
        var old = voice.Events.CreateQuerySnapshot();
        Assert.Equal(0, voice.Events.StorageCounts.RetainedObjects);
        Assert.Same(old.Values, voice.Events.CreateQuerySnapshot().Values);
        Assert.Same(held, voice.Events[21]);
        Assert.Equal(2, voice.EventMappings.Count);
        for (int i = 0; i < count; i += 997)
        {
            var value = old.GetByOrdinal(i);
            Assert.Equal(i * 4L, value.Tick);
            Assert.True(old.TryFindOrdinalById(value.Id, out int resolved));
            Assert.Equal(i, resolved);
        }
        var clone = new SubVoice(project);
        clone.Events.AdoptSnapshot(project, old);
        Assert.Empty(clone.EventMappings);
        held.Value = 57;
        var changed = voice.Events.CreateQuerySnapshot();
        Assert.Equal(57, changed.GetByOrdinal(21).Value);
        Assert.Equal(100, old.GetByOrdinal(21).Value);
        Assert.Equal(100, clone.Events[21].Value);
        Assert.Empty(clone.EventMappings);
        Assert.Equal(old.GetNoteRangeFingerprint((count - 100) * 4L, count * 4L),
            changed.GetNoteRangeFingerprint((count - 100) * 4L, count * 4L));
        GC.KeepAlive(held);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void AdoptedLogicalAndTemplateRootsShareSnapshotsAndKeepOldRevision(int count)
    {
        using MidoraProject project = new(480);
        Segment segment = new(project);
        SubVoice voice = new(project);
        segment.Notes.AdoptSource(project, new GeneratedSource<LogicalNoteSnapshotValue>(count,
            i => new(new MidoraId(i + 100), i * 4L, 3, i % 128, 100)));
        voice.Events.AdoptSource(project, new GeneratedSource<TemplateEventSnapshotValue>(count,
            i => new(new MidoraId(i + count + 100), TemplateEventKind.Note, i * 4L, 3, i % 128, 100, 0, true, true, true)));
        var logical = segment.Notes.CreateQuerySnapshot();
        var template = voice.Events.CreateQuerySnapshot();
        Assert.Equal((0, 0), segment.Notes.StorageCounts);
        Assert.Equal((0, 0), voice.Events.StorageCounts);
        Assert.Same(logical.Values, segment.Notes.CreateQuerySnapshot().Values);
        Assert.Same(template.Values, voice.Events.CreateQuerySnapshot().Values);
        segment.Notes[count / 2].Velocity = 7;
        voice.Events[count / 2].Value = 8;
        Assert.Equal(100, logical.GetByOrdinal(count / 2).Velocity);
        Assert.Equal(100, template.GetByOrdinal(count / 2).Value);
        Assert.Equal(7, segment.Notes.CreateQuerySnapshot().GetByOrdinal(count / 2).Velocity);
        Assert.Equal(8, voice.Events.CreateQuerySnapshot().GetByOrdinal(count / 2).Value);
        Assert.Empty(voice.EventMappings);
    }

    [Fact]
    public void PendingTailEditStructuralInsertDeleteAndClearKeepExactOrder()
    {
        using MidoraProject project = new(480);
        var (segment, held, _) = BuildLogical(project, 9_001);
        LogicalNote tail = segment.Notes[^1];
        tail.Velocity = 5;
        var before = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(5, before.GetByOrdinal(9_000).Velocity);
        LogicalNote inserted = new(project) { StartTick = 17, LengthTicks = 2, Note = 60, Velocity = 90 };
        segment.Notes.Insert(3, inserted);
        Assert.Same(held, segment.Notes[22]);
        Assert.True(segment.Notes.Remove(inserted));
        Assert.Equal(before.ContentFingerprint, segment.Notes.CreateQuerySnapshot().ContentFingerprint);
        Assert.Same(held, segment.Notes[21]);
        segment.Notes.Add(new LogicalNote(project) { StartTick = 100_000, LengthTicks = 1 });
        segment.Notes.Clear();
        Assert.Equal(0, segment.Notes.CreateQuerySnapshot().Count);
        Assert.Equal(9_001, before.Count);
    }

    [Fact]
    public void AddRangePromotionPreservesOrderAndExternalObjectIdentity()
    {
        using MidoraProject project = new(480);
        Segment segment = new(project);
        LogicalNote[] values = Enumerable.Range(0, 10_000).Select(i => new LogicalNote(project)
            { StartTick = i % 71, LengthTicks = 3, Note = i % 128, Velocity = 100 }).ToArray();
        segment.Notes.AddRange(values);
        Assert.Equal(0, segment.Notes.StorageCounts.RetainedObjects);
        Assert.Equal(values.Select(v => v.Id), segment.Notes.CreateQuerySnapshot().EnumerateAll().Select(v => v.Id));
        Assert.Same(values[8_999], segment.Notes[8_999]);
        values[8_999].Velocity = 19;
        Assert.Equal(19, segment.Notes.CreateQuerySnapshot().GetByOrdinal(8_999).Velocity);
    }

    [Fact]
    public void RepeatedSmallAppendSnapshotAndEditedAppendKeepIdsAndOldRoots()
    {
        using MidoraProject project = new(480);
        var (segment, held, _) = BuildLogical(project, 4_097);
        var old = segment.Notes.CreateQuerySnapshot();
        for (int i = 0; i < 1_000; i++)
        {
            LogicalNote added = new(project) { StartTick = 50_000 + i, LengthTicks = 1, Note = 60, Velocity = 1 };
            segment.Notes.Add(added);
            var current = segment.Notes.CreateQuerySnapshot();
            Assert.True(current.TryFindOrdinalById(added.Id, out int ordinal));
            Assert.Equal(4_097 + i, ordinal);
            Assert.Same(added, segment.Notes[ordinal]);
        }
        held.Velocity = 42;
        for (int i = 0; i < 17; i++) segment.Notes.Add(new LogicalNote(project)
            { StartTick = 60_000 + i, LengthTicks = 2, Note = 60, Velocity = 3 });
        var final = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(5_114, final.Count);
        Assert.Equal((final.Count + 127) / 128, final.Values.StorageLeafCount);
        Assert.Equal(42, final.GetByOrdinal(21).Velocity);
        Assert.Equal(100, old.GetByOrdinal(21).Velocity);
        Assert.Equal(4_097, old.Count);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void OrdinaryParameterAndCurvePointsPreserveExactDoubleBitsAndSharedSnapshot(int count)
    {
        using MidoraProject project = new(480);
        LogicalParameterLane lane = new(project);
        for (int i = 0; i < count; i++) lane.Points.Add(new CurvePoint(project, i,
            BitConverter.Int64BitsToDouble(0x3fe0000000000001L + i), CurveInterpolation.Step));
        var old = lane.Points.CreateQuerySnapshot();
        Assert.Same(old.Values, lane.Points.CreateQuerySnapshot().Values);
        int index = count / 2;
        CurvePoint prior = lane.Points[index];
        lane.Points[index] = prior with { Value = -0.0 };
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(lane.Points.CreateQuerySnapshot().GetByOrdinal(index).Value));
        Assert.Equal(0x3fe0000000000001L + index, BitConverter.DoubleToInt64Bits(old.GetByOrdinal(index).Value));
        Assert.True(old.TryFindOrdinalById(prior.Id, out int ordinal));
        Assert.Equal(index, ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Segment Segment, LogicalNote Held, WeakReference Discarded) BuildLogical(MidoraProject project, int count)
    {
        Segment segment = new(project);
        LogicalNote? held = null;
        WeakReference? discarded = null;
        for (int i = 0; i < count; i++)
        {
            LogicalNote value = new(project) { StartTick = i * 4L, LengthTicks = 3, Note = i % 128, Velocity = 100 };
            segment.Notes.Add(value);
            if (i == 21) held = value;
            if (i == 0) discarded = new(value);
        }
        return (segment, held!, discarded!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubVoice Voice, TemplateEvent Held, WeakReference Discarded) BuildTemplate(MidoraProject project, int count)
    {
        SubVoice voice = new(project);
        TemplateEvent? held = null;
        WeakReference? discarded = null;
        for (int i = 0; i < count; i++)
        {
            TemplateEvent value = TemplateEvent.Note(project, i * 4L, 3, i % 128, 100);
            voice.Events.Add(value);
            if (i == 21) held = value;
            if (i == 0) discarded = new(value);
        }
        return (voice, held!, discarded!);
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactIdDirectoryHasLogarithmicLookupAndPersistentAppendForAdversarialFormalOrder(int mode)
    {
        const int count = 100_000;
        int[] order = Enumerable.Range(0, count).ToArray();
        if (mode == 0) new Random(913_401).Shuffle(order);
        else if (mode == 1) Array.Reverse(order);
        else for (int i = 0; i < count; i++) order[i] = i % 2 == 0 ? i / 2 : count - 1 - i / 2;
        var index = PersistentTimelineExactOrdinalIndex.Create(order.Select((id, ordinal) =>
            new TimelineIdOrdinal(new MidoraId(id + 1), ordinal)), count);
        for (int ordinal = 0; ordinal < count; ordinal += 19)
        {
            Assert.Equal(ordinal, index.Find(new MidoraId(order[ordinal] + 1), out int comparisons));
            Assert.InRange(comparisons, 1, 32);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var next = index.Append(Enumerable.Range(0, 1_024).Select(i =>
            new TimelineIdOrdinal(new MidoraId(count + i + 1), count + i)));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 512 * 1024);
        Assert.Equal(-1, index.Find(new MidoraId(count + 1)));
        Assert.Equal(count, next.Find(new MidoraId(count + 1)));
        Assert.Equal(0, next.Find(new MidoraId(order[0] + 1)));
        // A key inserted between existing IDs path-copies only its bounded leaf.
        var sparse = PersistentTimelineExactOrdinalIndex.Create(Enumerable.Range(0, count).Select(i =>
            new TimelineIdOrdinal(new MidoraId(i * 2L + 1), i)), count);
        var withMiddle = sparse.Append([new(new MidoraId(60_000), count)]);
        Assert.Equal(count, withMiddle.Find(new MidoraId(60_000), out int visited));
        Assert.InRange(visited, 1, 32);
        Assert.Equal(-1, sparse.Find(new MidoraId(60_000)));

        using MidoraProject project = new(480);
        LogicalNote[] authored = Enumerable.Range(0, count).Select(i => new LogicalNote(project)
            { StartTick = i, LengthTicks = 1, Note = 60, Velocity = 100 }).ToArray();
        Segment segment = new(project);
        segment.Notes.AddRange(order.Select(i => authored[i]));
        var snapshot = segment.Notes.CreateQuerySnapshot();
        for (int i = 0; i < count; i += 997)
        {
            Assert.True(snapshot.TryFindOrdinalById(authored[order[i]].Id, out int ordinal));
            Assert.Equal(i, ordinal);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread();
        LogicalNote appended = new(project) { StartTick = count, LengthTicks = 1, Note = 60, Velocity = 100 };
        segment.Notes.Add(appended);
        var appendedSnapshot = segment.Notes.CreateQuerySnapshot();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 512 * 1024);
        Assert.True(appendedSnapshot.TryFindOrdinalById(appended.Id, out int appendedOrdinal));
        Assert.Equal(count, appendedOrdinal);
        Assert.False(snapshot.TryFindOrdinalById(appended.Id, out _));
        authored[order[9]].Velocity = 81;
        Assert.Equal(100, snapshot.GetByOrdinal(9).Velocity);
        Assert.Equal(81, segment.Notes.CreateQuerySnapshot().GetByOrdinal(9).Velocity);
        GC.KeepAlive(authored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonTailOrdinaryInsertionAlsoPromotesAndPreservesSharedOldRevision(bool useRange)
    {
        using MidoraProject project = new(480);
        Segment segment = new(project);
        List<MidoraId> expected = [];
        for (int i = 0; i < 4_128; i++)
        {
            int at = i % 2 == 0 ? 0 : segment.Notes.Count / 2;
            LogicalNote value = new(project) { StartTick = i, LengthTicks = 1, Note = 60, Velocity = 100 };
            if (useRange) segment.Notes.InsertRange(at, [value]); else segment.Notes.Insert(at, value);
            expected.Insert(at, value.Id);
        }
        Assert.Equal(0, segment.Notes.StorageCounts.RetainedObjects);
        var old = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(expected, old.EnumerateAll().Select(v => v.Id));
        for (int i = 0; i < expected.Count; i += 13)
        {
            Assert.True(old.TryFindOrdinalById(expected[i], out int ordinal));
            Assert.Equal(i, ordinal);
        }
        segment.Notes[19].Velocity = 44;
        Assert.Equal(100, old.GetByOrdinal(19).Velocity);
        Assert.Equal(44, segment.Notes.CreateQuerySnapshot().GetByOrdinal(19).Velocity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(16_384)]
    public void ExactIdFinalTreeBuildObservesCancellationAtEmptyLeafAndBranchBoundaries(int count)
    {
        // Exercise the post-sort phase directly: canceling enumeration would
        // never reach the formerly unchecked final O(N) tree allocation.
        MethodInfo? build = typeof(PersistentTimelineExactOrdinalIndex).GetMethod("Build",
            BindingFlags.NonPublic | BindingFlags.Static, [typeof(TimelineIdOrdinal[]), typeof(int), typeof(int), typeof(CancellationToken)]);
        Assert.NotNull(build);
        TimelineIdOrdinal[] ordered = Enumerable.Range(0, count)
            .Select(i => new TimelineIdOrdinal(new MidoraId(i + 1), i)).ToArray();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var failure = Assert.Throws<TargetInvocationException>(() => build.Invoke(null, [ordered, 0, count, cancellation.Token]));
        Assert.IsType<OperationCanceledException>(failure.InnerException);
        var completed = PersistentTimelineExactOrdinalIndex.Create(ordered, count);
        if (count != 0) Assert.Equal(count - 1, completed.Find(new MidoraId(count)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task OldSnapshotExternalIndexPreparationCanOverlapOrdinaryAppend(bool exact, int publishPhase)
    {
        Action? onIdRead = null, onSpatialRead = null;
        var store = new PagedTimelineObjectList<TestValue, LogicalNoteSnapshotValue>(
            value => value.Value, value => { onIdRead?.Invoke(); return value.Id; },
            value => { onSpatialRead?.Invoke(); return value.StartTick; },
            value => value.StartTick + value.LengthTicks, value => value.Note,
            value => unchecked((ulong)value.Id.Value), static (_, _) => { }, materialize: value => new(value));
        const int count = 4_097;
        for (int i = 0; i < count; i++) store.Add(new(new(new MidoraId(exact ? count - i : i + 1), i * 4L, 3, 60, 100)));
        var old = store.CreateSnapshot();
        var oldValues = old.EnumerateAll().ToArray();
        var added = new TestValue(new(new MidoraId(count + 1), 50_000, 3, 60, 75));
        store.Add(added);
        using GatedOrdinalBuilder builder = new();
        using ManualResetEventSlim published = new();
        Task preparation = Task.Run(() =>
        {
            try { old.PrepareOrdinalLookup(builder); }
            finally { published.Set(); }
        });
        try
        {
            Assert.True(builder.Ready.Wait(TimeSpan.FromSeconds(10)));
            void PublishExternal()
            {
                onIdRead = onSpatialRead = null;
                builder.Continue.Set();
                Assert.True(published.Wait(TimeSpan.FromSeconds(10)));
            }
            if (publishPhase == 0)
            {
                // Already external at the atomic TryAppend decision: use the
                // existing COW overlay without changing old musical values.
                PublishExternal();
                await preparation;
            }
            else if (publishPhase == 1)
                // Publication while the captured in-memory prefix is extended.
                onIdRead = PublishExternal;
            else
                // Former CanAppend -> leaf metadata mutation -> Append window.
                onSpatialRead = PublishExternal;
            var current = store.CreateSnapshot();
            await preparation;
            Assert.Equal(1, builder.Index!.Retained);
            Assert.Equal(count + 1, current.Count);
            Assert.Equal(oldValues, old.EnumerateAll());
            Assert.Equal(oldValues.Append(added.Value), current.EnumerateAll());
            for (int i = 0; i < count; i += 17)
            {
                Assert.True(old.TryFindOrdinalById(oldValues[i].Id, out int oldOrdinal));
                Assert.Equal(i, oldOrdinal);
                Assert.True(current.TryFindOrdinalById(oldValues[i].Id, out int currentOrdinal));
                Assert.Equal(i, currentOrdinal);
            }
            Assert.False(old.TryFindOrdinalById(added.Value.Id, out _));
            Assert.True(current.TryFindOrdinalById(added.Value.Id, out int addedOrdinal));
            Assert.Equal(count, addedOrdinal);
            Assert.Equal(added.Value, Assert.Single(current.Query(50_000, 50_001, 0, 127)));
            Assert.Empty(old.Query(50_000, 50_001, 0, 127));
            store.Add(new(new(new MidoraId(count + 2), 50_010, 2, 61, 80)));
            Assert.Equal(count + 2, store.CreateSnapshot().Count);
            Assert.Equal(count, old.Count);
        }
        finally
        {
            onIdRead = onSpatialRead = null;
            builder.Continue.Set();
            await preparation;
        }
    }

    [Fact]
    public void CanceledExternalPreparationDoesNotReplaceTheExactSnapshotIndexAndCanRetry()
    {
        using MidoraProject project = new(480);
        Segment segment = new(project);
        LogicalNote[] authored = Enumerable.Range(0, 4_111).Select(i => new LogicalNote(project)
            { StartTick = i, LengthTicks = 1, Note = 60, Velocity = 100 }).ToArray();
        segment.Notes.AddRange(authored.Reverse());
        var old = segment.Notes.CreateQuerySnapshot();
        using CancellationTokenSource cancellation = new();
        using GatedOrdinalBuilder builder = new() { BeforeReturn = cancellation.Cancel };
        builder.Continue.Set();
        Assert.Throws<OperationCanceledException>(() => old.PrepareOrdinalLookup(builder, cancellation.Token));
        Assert.Equal(0, builder.Index!.Retained);
        Assert.True(old.TryFindOrdinalById(authored[0].Id, out int ordinal));
        Assert.Equal(authored.Length - 1, ordinal);
        builder.BeforeReturn = null;
        old.PrepareOrdinalLookup(builder);
        Assert.Equal(1, builder.Index!.Retained);
        segment.Notes.Add(new LogicalNote(project) { StartTick = 50_000, LengthTicks = 1 });
        Assert.Equal(authored.Length + 1, segment.Notes.CreateQuerySnapshot().Count);
        Assert.Equal(authored.Length, old.Count);
    }

    private sealed record TestValue(LogicalNoteSnapshotValue Value);

    private sealed class GatedOrdinalBuilder : IImmutableTimelineOrdinalIndexBuilder, IDisposable
    {
        public ManualResetEventSlim Ready { get; } = new();
        public ManualResetEventSlim Continue { get; } = new();
        public TestOrdinalIndex? Index { get; private set; }
        public Action? BeforeReturn { get; set; }
        public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken cancellationToken)
        {
            Index = new(values.ToDictionary(value => value.Id, value => value.Ordinal));
            Ready.Set();
            Assert.True(Continue.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            BeforeReturn?.Invoke();
            return Index;
        }
        public void Dispose() { Ready.Dispose(); Continue.Dispose(); }
    }

    private sealed class TestOrdinalIndex(Dictionary<MidoraId, int> ordinals) : IImmutableTimelineIdIndex
    {
        public int Retained { get; private set; }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal) => ordinals.TryGetValue(id, out ordinal);
        public void RetainForSourceLifetime() => Retained++;
    }

    private sealed class GeneratedSource<T>(int count, Func<int, T> generate) : IImmutableTimelineValueSource<T>
    {
        private int _page = -1;
        private T[] _values = [];
        public int Count => count;
        public int PageCapacity => 4096;
        public T this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            if (_page != pageIndex)
            {
                int first = pageIndex * PageCapacity;
                _values = new T[Math.Min(PageCapacity, count - first)];
                for (int i = 0; i < _values.Length; i++) _values[i] = generate(first + i);
                _page = pageIndex;
            }
            return _values;
        }
        public IEnumerator<T> GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
