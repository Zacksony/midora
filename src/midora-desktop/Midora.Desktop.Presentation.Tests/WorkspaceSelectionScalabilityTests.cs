using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Midora.Desktop.Presentation.Tests;

public sealed class WorkspaceSelectionScalabilityTests
{
    [Fact]
    public void ExactRangeSelectionStreamsBeyondCacheOnlyCapacity()
    {
        const int count = 100_000;
        StreamingPreparedSource source = new(
            count,
            static index => new(
                new MidoraId(index + 1L),
                TimelineItemKind.DirectMidiNote,
                index,
                index + 1L,
                index & 127,
                (index % 127) + 1,
                ZIndex: 0,
                TimelineItemState.None));
        TimelineRenderSnapshot snapshot = CreateSnapshot(source);
        TimelineSelectionSnapshot empty = new(41, [], null);

        TimelineMaterializedSelection result = snapshot.MaterializeRangeSelection(
            0,
            count,
            0,
            128,
            0,
            1,
            filterByValue: false,
            empty,
            WorkspaceSelectionRangeMode.Replace);

        Assert.Equal(count, result.Ids.Count);
        Assert.True(result.MetricsAreComplete);
        Assert.True(result.IsCurrent(41));
        Assert.False(result.IsCurrent(42));
        Assert.Equal(count, result.Metrics[TimelineItemKind.DirectMidiNote].Count);
        Assert.Equal(count, result.Metrics[TimelineItemKind.Velocity].Count);
        Assert.Equal(1, source.VisitCalls);
        Assert.Equal(0, source.CacheOnlyQueryCalls);
        Assert.Equal(0, source.BlockingQueryCalls);
        Assert.Equal(0, source.EnumerateAllCalls);
    }

    [Fact]
    public void ExactRangeSelectionPreservesAllFourRangeModes()
    {
        StreamingPreparedSource source = new(
            3,
            static index => new(
                new MidoraId(index + 1L),
                TimelineItemKind.DirectMidiNote,
                index,
                index + 1L,
                60 + index,
                100,
                ZIndex: 0,
                TimelineItemState.None));
        TimelineRenderSnapshot snapshot = CreateSnapshot(source);
        TimelineRenderItem existing = new(
            new MidoraId(10),
            TimelineItemKind.DirectMidiNote,
            10,
            11,
            70,
            90,
            0,
            TimelineItemState.None);
        TimelineSelectionSnapshot baseSelection = new(
            7,
            [new MidoraId(1), existing.Id],
            existing.Id,
            [source.ItemAt(0), existing],
            anchor: existing.Id);

        TimelineMaterializedSelection replace = Select(
            snapshot,
            baseSelection,
            WorkspaceSelectionRangeMode.Replace);
        Assert.True(replace.Ids.SetEquals(
            [new MidoraId(1), new MidoraId(2), new MidoraId(3)]));
        Assert.Equal(new MidoraId(1), replace.Primary);
        Assert.Equal(new MidoraId(1), replace.Anchor);
        Assert.True(replace.MetricsAreComplete);

        TimelineMaterializedSelection add = Select(
            snapshot,
            baseSelection,
            WorkspaceSelectionRangeMode.Add);
        Assert.True(add.Ids.SetEquals(
            [new MidoraId(1), new MidoraId(2), new MidoraId(3), existing.Id]));
        Assert.Equal(existing.Id, add.Primary);
        Assert.Equal(existing.Id, add.Anchor);
        Assert.True(add.MetricsAreComplete);
        Assert.NotNull(add.RenderIndex);
        Assert.Equal(4, add.RenderIndex!.Count);

        TimelineMaterializedSelection remove = Select(
            snapshot,
            baseSelection,
            WorkspaceSelectionRangeMode.Remove);
        Assert.Equal([existing.Id], remove.Ids);
        Assert.Equal(existing.Id, remove.Primary);
        Assert.Equal(existing.Id, remove.Anchor);
        Assert.False(remove.MetricsAreComplete);
        Assert.NotNull(remove.RenderIndex);
        Assert.Equal(1, remove.RenderIndex!.Count);

        TimelineMaterializedSelection toggle = Select(
            snapshot,
            baseSelection,
            WorkspaceSelectionRangeMode.Toggle);
        Assert.True(toggle.Ids.SetEquals(
            [new MidoraId(2), new MidoraId(3), existing.Id]));
        Assert.Equal(existing.Id, toggle.Primary);
        Assert.Equal(existing.Id, toggle.Anchor);
        Assert.False(toggle.MetricsAreComplete);
        Assert.NotNull(toggle.RenderIndex);
        Assert.Equal(3, toggle.RenderIndex!.Count);

        // Remove/Toggle must publish without a second all-items scan. Their
        // compact metrics are deliberately completed later by the Workspace.
        Assert.Equal(0, source.EnumerateAllCalls);
    }

    [Fact]
    public void EventRangeValueFilterUsesBothParenthesizedBounds()
    {
        TimelineRenderItem[] items =
        [
            new(new MidoraId(1), TimelineItemKind.DirectMidiEvent, 0, 1, 0, 0.1, 0, TimelineItemState.None),
            new(new MidoraId(2), TimelineItemKind.DirectMidiEvent, 1, 2, 0, 0.5, 0, TimelineItemState.None),
            new(new MidoraId(3), TimelineItemKind.DirectMidiEvent, 2, 3, 0, 0.9, 0, TimelineItemState.None),
            new(new MidoraId(4), TimelineItemKind.DirectMidiNote, 3, 4, 0, 127, 0, TimelineItemState.None)
        ];
        StreamingPreparedSource source = new(items);
        TimelineMaterializedSelection result = CreateSnapshot(source).MaterializeRangeSelection(
            0,
            4,
            0,
            1,
            0.4,
            0.6,
            filterByValue: true,
            new TimelineSelectionSnapshot(0, [], null),
            WorkspaceSelectionRangeMode.Replace);

        Assert.Equal([new MidoraId(2)], result.Ids);
    }

    [Fact]
    public void VelocityRangePublishesIncompleteCrossProjectionMetricsAndClearsAliasesOnRemove()
    {
        TimelineRenderItem velocityOne = new(
            new MidoraId(1),
            TimelineItemKind.Velocity,
            0,
            1,
            0,
            100d / 127,
            67,
            TimelineItemState.None);
        TimelineRenderItem velocityTwo = velocityOne with
        {
            Id = new MidoraId(2),
            StartTick = 1,
            EndTick = 2
        };
        StreamingPreparedSource source = new([velocityOne, velocityTwo]);
        TimelineRenderSnapshot snapshot = CreateSnapshot(source);
        TimelineRenderItem noteOne = velocityOne with
        {
            Kind = TimelineItemKind.DirectMidiNote,
            Lane = 60,
            Value = 100
        };
        TimelineRenderItem noteTwo = noteOne with
        {
            Id = new MidoraId(2),
            StartTick = 1,
            EndTick = 2
        };
        TimelineSelectionSnapshot completeBase = new(
            9,
            [new MidoraId(1), new MidoraId(2)],
            new MidoraId(1),
            [noteOne, noteTwo, velocityOne, velocityTwo]);

        TimelineMaterializedSelection replace = snapshot.MaterializeRangeSelection(
            0, 2, 0, 1, 0, 1, true,
            new TimelineSelectionSnapshot(8, [], null),
            WorkspaceSelectionRangeMode.Replace);
        Assert.False(replace.MetricsAreComplete);
        Assert.True(replace.Metrics.ContainsKey(TimelineItemKind.Velocity));
        Assert.False(replace.Metrics.ContainsKey(TimelineItemKind.DirectMidiNote));

        TimelineMaterializedSelection remove = snapshot.MaterializeRangeSelection(
            0, 2, 0, 1, 0, 1, true,
            completeBase,
            WorkspaceSelectionRangeMode.Remove);
        Assert.Empty(remove.Ids);
        Assert.True(remove.MetricsAreComplete);
        Assert.Empty(remove.Metrics);
    }

    [Fact]
    public void ExactRangeSelectionHonorsCancellationBeforePublication()
    {
        StreamingPreparedSource source = new(
            10_000,
            static index => new(
                new MidoraId(index + 1L),
                TimelineItemKind.DirectMidiNote,
                index,
                index + 1L,
                index & 127,
                100,
                0,
                TimelineItemState.None));
        CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CreateSnapshot(source).MaterializeRangeSelection(
                0,
                10_000,
                0,
                128,
                0,
                1,
                filterByValue: false,
                new TimelineSelectionSnapshot(5, [], null),
                WorkspaceSelectionRangeMode.Replace,
                cancellation.Token));
    }

    [Fact]
    public void TimelineSnapshotSharesCompressedMillionItemSelection()
    {
        CompressedMidoraIdSet ids = CompressedMidoraIdSet.Create(
            Enumerable.Range(1, 1_000_000)
                .Select(static value => new MidoraId(value)));

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TimelineSelectionSnapshot snapshot = new(1, ids, new MidoraId(1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(ids, snapshot.SharedIds);
        Assert.Equal(ids.Count, snapshot.Count);
        Assert.True(
            allocated < 64 * 1024,
            $"Refreshing a million-item immutable selection allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void TinyRangeUpdateDoesNotCopyMillionItemSelection()
    {
        const int count = 1_000_000;
        MidoraId[] ids = Enumerable.Range(1, count)
            .Select(static value => new MidoraId(value))
            .ToArray();
        WorkspaceSelection selection = new();
        Assert.True(selection.ReplaceAll(ids, ids[0]));
        long revision = selection.Revision;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        selection.ApplyRange([ids[^1]], WorkspaceSelectionRangeMode.Add);
        selection.ApplyRange([new MidoraId(count + 1L)], WorkspaceSelectionRangeMode.Remove);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(revision, selection.Revision);
        Assert.Equal(count, selection.Ids.Count);
        Assert.True(
            allocated < 256 * 1024,
            $"A two-item range update allocated {allocated:N0} bytes for an existing million-item selection.");
    }

    [Fact]
    public void TrustedWorkspaceSnapshotPublicationDoesNotRescanMillionItemRoot()
    {
        WorkspaceSelection selection = new();
        MidoraId[] ids = Enumerable.Range(1, 1_000_000)
            .Select(static value => new MidoraId(value))
            .ToArray();
        Assert.True(selection.ReplaceAll(ids, ids[0]));

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        TimelineSelectionSnapshot snapshot =
            TimelineSelectionSnapshot.FromWorkspaceSelection(selection);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ids.Length, snapshot.Count);
        Assert.Same(selection.SharedIds, snapshot.SharedIds);
        Assert.True(
            allocated < 64 * 1024,
            $"Trusted selection publication allocated {allocated:N0} bytes.");
        Assert.True(
            watch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Trusted selection publication took {watch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    public void PreparedSelectionAdoptionIsConstantCostAndAllocationFree()
    {
        const int count = 200_000;
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource source = new(
            WorkspaceTimelineSelectionKind.DirectMidiNote,
            new MidoraId(9));
        selection.Replace(new MidoraId(1), source);
        MidoraId[] resultIds = Enumerable.Range(10, count)
            .Select(static value => new MidoraId(value))
            .ToArray();
        PreparedWorkspaceSelectionProjection projection =
            selection.PrepareProjection(resultIds);

        // Invoke the publication path once before measuring so the assertion
        // covers steady-state Dispatcher work rather than first-call JIT cost.
        selection.AdoptPrepared(projection);
        selection.Replace(new MidoraId(count + 100L), source);
        long revision = selection.Revision;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        selection.AdoptPrepared(projection);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(revision + 1, selection.Revision);
        Assert.Equal(count, selection.Ids.Count);
        Assert.Same(projection.Ids, selection.SharedIds);
        Assert.Equal(source, selection.HomogeneousTimelineSource);
        Assert.Equal(source, selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void RangeOperationsPreserveRevisionAndEndpointSemanticsWithoutBeforeSet()
    {
        WorkspaceSelection selection = new();
        MidoraId one = new(1);
        MidoraId two = new(2);
        MidoraId three = new(3);
        selection.ApplyRange([one, two], WorkspaceSelectionRangeMode.Replace);
        Assert.Equal(one, selection.Primary);
        Assert.Equal(one, selection.Anchor);

        long revision = selection.Revision;
        selection.ApplyRange([one], WorkspaceSelectionRangeMode.Add);
        selection.ApplyRange([three], WorkspaceSelectionRangeMode.Remove);
        Assert.Equal(revision, selection.Revision);

        selection.ApplyRange([one], WorkspaceSelectionRangeMode.Remove);
        Assert.Equal(two, selection.Primary);
        Assert.Equal(two, selection.Anchor);
        Assert.Equal(revision + 1, selection.Revision);

        selection.ApplyRange([two, three], WorkspaceSelectionRangeMode.Toggle);
        Assert.Equal([three], selection.Ids);
        Assert.Equal(three, selection.Primary);
        Assert.Equal(three, selection.Anchor);
    }

    [Fact]
    public void LargePrunePublishesOneSelectionRevision()
    {
        const int count = 200_000;
        MidoraId[] ids = Enumerable.Range(1, count)
            .Select(static value => new MidoraId(value))
            .ToArray();
        WorkspaceSelection selection = new();
        Assert.True(selection.ReplaceAll(ids, ids[^1]));
        long revision = selection.Revision;
        ImmutableHashSet<MidoraId> retained = ids
            .Where(static value => (value.Value & 1) == 0)
            .ToImmutableHashSet();

        Stopwatch watch = Stopwatch.StartNew();
        Assert.True(selection.RetainOnly(retained));
        watch.Stop();

        Assert.Equal(revision + 1, selection.Revision);
        Assert.Equal(retained.Count, selection.Ids.Count);
        Assert.Equal(ids[^1], selection.Primary);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(1),
            $"Atomic selection pruning took {watch.Elapsed.TotalMilliseconds:N1} ms.");
    }

    private static TimelineMaterializedSelection Select(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection,
        WorkspaceSelectionRangeMode mode) =>
        snapshot.MaterializeRangeSelection(
            0,
            3,
            0,
            128,
            0,
            1,
            filterByValue: false,
            selection,
            mode);

    private static TimelineRenderSnapshot CreateSnapshot(
        ITimelineRenderItemSource source) =>
        new(
            semanticRevision: 1,
            projectionKey: "selection-streaming-test",
            items: [],
            itemSource: source);

    private sealed class StreamingPreparedSource : IPreparedTimelineRenderItemSource
    {
        private readonly TimelineRenderItem[]? _items;
        private readonly int _count;
        private readonly Func<int, TimelineRenderItem>? _itemFactory;

        public StreamingPreparedSource(TimelineRenderItem[] items)
        {
            _items = items;
            _count = items.Length;
        }

        public StreamingPreparedSource(
            int count,
            Func<int, TimelineRenderItem> itemFactory)
        {
            _count = count;
            _itemFactory = itemFactory;
        }

        public int VisitCalls { get; private set; }
        public int CacheOnlyQueryCalls { get; private set; }
        public int BlockingQueryCalls { get; private set; }
        public int EnumerateAllCalls { get; private set; }
        public long Count => _count;
        public long MaximumEndTick => _count == 0 ? 0 : ItemAt(_count - 1).EndTick;
        public ulong ContentFingerprint => 0x583a_491dUL;

        public TimelineRenderItem ItemAt(int index) =>
            _items is null ? _itemFactory!(index) : _items[index];

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            BlockingQueryCalls++;
            throw new InvalidOperationException(
                "The streaming selection path must not materialize a blocking query list.");
        }

        public void VisitInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            Action<TimelineRenderItem> visitor)
        {
            VisitCalls++;
            for (int index = 0; index < _count; index++)
            {
                TimelineRenderItem item = ItemAt(index);
                if (item.StartTick < endTick
                    && item.EndTick > startTick
                    && item.Lane >= firstLane
                    && item.Lane < lastLaneExclusive)
                {
                    visitor(item);
                }
            }
        }

        public bool TryQueryIntoCached(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            CacheOnlyQueryCalls++;
            return false;
        }

        public void PrefetchRange(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            CancellationToken cancellationToken)
        {
        }

        public bool TryQueryByIdsCached(
            IReadOnlySet<MidoraId> ids,
            List<TimelineRenderItem> destination) => false;

        public void PrefetchIds(
            IReadOnlySet<MidoraId> ids,
            CancellationToken cancellationToken)
        {
        }

        public bool TryGetById(MidoraId id, out TimelineRenderItem item)
        {
            item = default;
            return false;
        }

        public IEnumerable<TimelineRenderItem> EnumerateAll()
        {
            EnumerateAllCalls++;
            for (int index = 0; index < _count; index++) yield return ItemAt(index);
        }
    }
}
