using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PureMidiSourceIdResolutionCacheTests
{
    [Fact]
    public void SmallInteractiveResolutionIsRetained()
    {
        PureMidiSourceIdResolutionCache<SourceMatch> cache = CreateCache();
        HashSet<MidoraId> ids = Enumerable.Range(1, 128)
            .Select(static value => new MidoraId(value))
            .ToHashSet();
        int queryCount = 0;

        IReadOnlyList<SourceMatch> first = cache.Resolve(ids, Query);
        IReadOnlyList<SourceMatch> second = cache.Resolve(ids, Query);

        Assert.Equal(1, queryCount);
        Assert.Equal(ids.Count, first.Count);
        Assert.Equal(first, second);

        IEnumerable<SourceMatch> Query(IReadOnlySet<MidoraId> requested)
        {
            queryCount++;
            return requested.Select(static id => new SourceMatch(id, checked((int)id.Value)));
        }
    }

    [Fact]
    public void BulkResolutionDoesNotBecomeAProjectLifetimeDuplicate()
    {
        PureMidiSourceIdResolutionCache<SourceMatch> cache = CreateCache();
        HashSet<MidoraId> ids = Enumerable.Range(1, 65_537)
            .Select(static value => new MidoraId(value))
            .ToHashSet();
        int queryCount = 0;

        IReadOnlyList<SourceMatch> first = cache.Resolve(ids, Query);
        IReadOnlyList<SourceMatch> second = cache.Resolve(ids, Query);

        Assert.Equal(2, queryCount);
        Assert.Equal(ids.Count, first.Count);
        Assert.Equal(first, second);

        IEnumerable<SourceMatch> Query(IReadOnlySet<MidoraId> requested)
        {
            queryCount++;
            return requested.Select(static id => new SourceMatch(id, checked((int)id.Value)));
        }
    }

    private static PureMidiSourceIdResolutionCache<SourceMatch> CreateCache() =>
        new(static value => value.Id, static value => value.Index);

    private readonly record struct SourceMatch(MidoraId Id, int Index);
}
