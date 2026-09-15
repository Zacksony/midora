using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectTimelineReadPreparationTests
{
    [Fact]
    public void ReadOnlyIndexIsBoundedReusedAndReleasedWithProject()
    {
        using var fixture = new Fixture();
        var source = new IndexSource();
        long nextId = fixture.Project.NextStableId;
        ProjectTimelineReadPreparation.PrepareOrdinalLookup(fixture.Project, source);
        Assert.True(source.TryFindOrdinalById(new(9999), out int ordinal));
        Assert.Equal(1, ordinal);
        Assert.Equal(1, source.Builds);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.True(fixture.Resources.SpillBytes > 0);
        long retained = fixture.Resources.SpillBytes;
        ProjectTimelineReadPreparation.PrepareOrdinalLookup(fixture.Project, source);
        Assert.Equal(1, source.Builds);
        Assert.Equal(retained, fixture.Resources.SpillBytes);
        Assert.Equal(nextId, fixture.Project.NextStableId);
        fixture.Project.Dispose();
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledReadOnlyIndexCleansProvisionalPages(bool afterBuild)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var source = new IndexSource(cancellation, afterBuild);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProjectTimelineReadPreparation.PrepareOrdinalLookup(fixture.Project, source, cancellation.Token));
        Assert.False(source.Installed);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Path));
    }

    [Fact]
    public void ReadOnlyIndexHonorsParentCancellationWhenArgumentIsDefault()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token);
        cancellation.Cancel();
        var source = new IndexSource();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProjectTimelineReadPreparation.PrepareOrdinalLookup(fixture.Project, source));
        Assert.Equal(0, source.Builds);
    }

    private sealed class IndexSource(CancellationTokenSource? cancel = null, bool afterBuild = false)
        : ITimelineObjectSource<MidoraId>
    {
        private IImmutableTimelineIdIndex? _index;
        public bool Installed => _index is not null;
        public int Builds { get; private set; }
        public int Count => 10_000;
        public long SourceRevision => 0;
        public int PageCapacity => 4096;
        public MidoraId GetByOrdinal(int ordinal) => new(Count - ordinal);
        public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_index is not null) return;
            Builds++;
            var index = builder.CreateOrdinalIndex(Addresses(), token);
            if (afterBuild) cancel?.Cancel();
            token.ThrowIfCancellationRequested();
            index.RetainForSourceLifetime();
            _index = index;
        }
        private IEnumerable<TimelineIdOrdinal> Addresses()
        {
            for (int i = 0; i < Count; i++)
            {
                if (!afterBuild && i == 1000) cancel?.Cancel();
                yield return new(new(Count - i), i);
            }
        }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        { ordinal = -1; return _index?.TryFindOrdinalById(id, out ordinal) ?? false; }
        public bool TryGetPageByOrdinal(int firstOrdinal, int count, out TimelineObjectPage<MidoraId> page)
        { page = default; throw new NotSupportedException(); }
        public int FindOrdinalAtOrAfterTick(long tick) => throw new NotSupportedException();
        public IEnumerable<MidoraId> QueryTickRange(TimelineObjectRangeQuery query) => throw new NotSupportedException();
        public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) { }
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, ".tmp", "midora-read-index-" + Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480);
        public BoundedEditResources Resources { get; }
        private readonly BulkEditPreparationContext _scope;
        public Fixture()
        {
            Directory.CreateDirectory(Path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024,
                maximumWorkingBytes: 4 * 1024 * 1024), Path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
        }
        public void Dispose()
        { _scope.Dispose(); Project.Dispose(); Directory.Delete(Path, true); }
    }
}
