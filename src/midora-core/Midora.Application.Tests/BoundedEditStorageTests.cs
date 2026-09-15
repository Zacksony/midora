namespace Midora.Application.Tests;

public sealed class BoundedEditStorageTests
{
    [Fact]
    public void PublicationSpillsEveryRetainedHistoryRootInsteadOfAccumulatingCommandBudgets()
    {
        using Midora.Domain.MidoraProject project = new(480);
        List<Midora.Domain.Segment> history = [];
        List<BoundedEditResources> budgets = [];
        for (int revision = 0; revision < 24; revision++)
        {
            var resources = new BoundedEditResources();
            budgets.Add(resources);
            using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
            using var lease = resources.BeginResourceLease();
            var store = new BoundedEditRecordStore<Midora.Domain.LogicalNoteSnapshotValue>(resources);
            for (int i = 0; i < 2_000; i++)
                store.Add(new(new Midora.Domain.MidoraId(1_000 + i), i * 8L, 3, i % 128, revision + 1));
            store.Seal();
            var source = new BoundedImmutableValueSource<Midora.Domain.LogicalNoteSnapshotValue>(store);
            var segment = new Midora.Domain.Segment(project);
            segment.Notes.AdoptSource(project, source);
            Assert.True(resources.ResidentBytes > 0);
            using var publication = lease.Complete();
            Assert.Equal(0, resources.ResidentBytes);
            publication.MarkPublished();
            history.Add(segment);
        }
        Assert.All(budgets, budget => Assert.Equal(0, budget.ResidentBytes));
        for (int i = history.Count - 1; i >= 0; i--)
            Assert.Equal(i + 1, history[i].Notes.CreateQuerySnapshot().GetByOrdinal(1_900).Velocity);
        Assert.All(budgets, budget => Assert.Equal(0, budget.ResidentBytes));
    }

    [Fact]
    public void CancelledPublicationGateReleasesAllPreparedSpillProviders()
    {
        using Midora.Domain.MidoraProject project = new(480);
        var resources = new BoundedEditResources();
        using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
        using (var lease = resources.BeginResourceLease())
        {
            var store = new BoundedEditRecordStore<long>(resources);
            store.AddRange(Enumerable.Range(0, 8_000).Select(i => (long)i));
            store.Seal();
            _ = new BoundedImmutableValueSource<long>(store);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => lease.Complete(cancellation.Token));
        }
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    [Fact]
    public void CancellationBetweenSpilledProvidersRollsBackAllOwnedStorage()
    {
        using Midora.Domain.MidoraProject project = new(480);
        using var cancellation = new CancellationTokenSource();
        var resources = new BoundedEditResources();
        using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
        using (var lease = resources.BeginResourceLease())
        {
            AddSource();
            resources.TrackProvider(new CancelPublication(cancellation));
            AddSource();
            Assert.Throws<OperationCanceledException>(() => lease.Complete(cancellation.Token));
            Assert.True(resources.SpillBytes > 0);
            Assert.True(resources.ResidentBytes > 0);
        }
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.SpillBytes);

        void AddSource()
        {
            var store = new BoundedEditRecordStore<long>(resources);
            store.AddRange(Enumerable.Range(0, 8_000).Select(i => (long)i));
            store.Seal();
            _ = new BoundedImmutableValueSource<long>(store);
        }
    }

    private sealed class CancelPublication(CancellationTokenSource cancellation)
        : IBoundedPublicationValueSource, IDisposable
    {
        public void PrepareForPublication(CancellationToken token) => cancellation.Cancel();
        public void Dispose() { }
    }

    [Fact]
    public void PublishedSpillPagesCannotReadSynchronouslyInsideCacheOnlyScope()
    {
        using Midora.Domain.MidoraProject project = new(480);
        using var preparation = BulkEditPreparationContext.Enter(project: project);
        var store = new BoundedEditRecordStore<long>(preparation.Resources);
        store.AddRange(Enumerable.Range(0, store.PageCapacity + 5).Select(i => (long)i));
        store.Seal();
        store.SpillResidentPages();
        using var source = new BoundedImmutableValueSource<long>(store);
        using (Midora.Domain.TimelineValueReadScope.EnterCacheOnly())
        {
            Assert.False(source.TryReadCachedPage(0, out _));
            Assert.Throws<Midora.Domain.TimelineValueReadPendingException>(() => source[0]);
        }
        source.Prefetch(0);
        using (Midora.Domain.TimelineValueReadScope.EnterCacheOnly())
        {
            Assert.Equal(0, source[0]);
            Assert.True(source.TryReadCachedPage(0, out _));
            Assert.Throws<Midora.Domain.TimelineValueReadPendingException>(() => source.ToArray());
        }
        source.Prefetch(1);
        using (Midora.Domain.TimelineValueReadScope.EnterCacheOnly())
            Assert.Equal(store.PageCapacity + 4, source.Last());
    }

    [Fact]
    public void DefaultBudgetIsInitializedRatherThanAZeroedStruct()
    {
        PagedEditResourceBudget budget = new();
        Assert.Equal(64L * 1024 * 1024, budget.MaximumWorkingBytes);
        Assert.Equal(64L * 1024 * 1024, budget.MaximumResidentBytes);
        Assert.Equal(100_000_000, budget.MaximumRecordCount);
    }

    [Fact]
    public void SharedBudgetSpillsAndDisposalReleasesAllResources()
    {
        string path = Path.Combine(Path.GetTempPath(), "midora-bulk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumResidentBytes: 512, maximumWorkingBytes: 4096,
                pageRecordCount: 64), path);
            using (var first = new BoundedEditRecordStore<long>(resources))
            using (var second = new BoundedEditRecordStore<long>(resources))
            {
                first.AddRange(Enumerable.Range(0, 1000).Select(i => (long)i));
                first.Seal();
                second.AddRange(Enumerable.Range(0, 1000).Select(i => -(long)i));
                second.Seal();
                Assert.Equal(512, resources.ResidentBytes);
                Assert.Equal(16000 - 512, resources.SpillBytes);
                Assert.Equal(999, first[999]);
                Assert.Equal(-999, second[999]);
                Assert.Equal(Enumerable.Range(0, 1000).Select(i => (long)i), first.ReadValues());
                Assert.True(resources.PeakWorkingBytes <= 4096);
            }
            Assert.Equal(0, resources.ResidentBytes);
            Assert.Equal(0, resources.SpillBytes);
            Assert.Equal(0, resources.WorkingBytes);
            Assert.Empty(Directory.EnumerateDirectories(path));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void ExternalSortIsOrderedAndBoundedAcrossMultipleMergePasses()
    {
        string path = Path.Combine(Path.GetTempPath(), "midora-bulk-sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumResidentBytes: 1024, maximumWorkingBytes: 256 * 1024,
                pageRecordCount: 64), path);
            using (var result = BoundedEditSort.Sort(
                Enumerable.Range(0, 200_000).Select(i => (long)(199_999 - i)),
                Comparer<long>.Default, resources))
            {
                Assert.Equal(Enumerable.Range(0, 200_000).Select(i => (long)i), result);
                Assert.True(resources.PeakResidentBytes <= 1024);
                Assert.True(resources.PeakWorkingBytes <= 256 * 1024);
            }
            Assert.Equal(0, resources.WorkingBytes);
            Assert.Equal(0, resources.SpillBytes);
            Assert.Empty(Directory.EnumerateDirectories(path));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void CancelledSortCleansItsOwnedRuns()
    {
        string path = Path.Combine(Path.GetTempPath(), "midora-bulk-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumResidentBytes: 0, maximumWorkingBytes: 256 * 1024,
                pageRecordCount: 64), path);
            Assert.Throws<OperationCanceledException>(() => BoundedEditSort.Sort(
                Input(), Comparer<long>.Default, resources, cancellation.Token));
            Assert.Equal(0, resources.WorkingBytes);
            Assert.Equal(0, resources.ResidentBytes);
            Assert.Equal(0, resources.SpillBytes);
            Assert.Empty(Directory.EnumerateDirectories(path));

            IEnumerable<long> Input()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    if (i == 20_000) cancellation.Cancel();
                    yield return i;
                }
            }
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
