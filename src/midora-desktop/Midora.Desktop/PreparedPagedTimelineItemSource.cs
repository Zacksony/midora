using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>Cache-only UI access shared by the logical, template and parameter projections.</summary>
internal interface IPreparedPagedTimelineItemSource : IPreparedTimelineRenderItemSource
{
    bool HasExternalValueStorage { get; }
    bool IPreparedTimelineRenderItemSource.TryQueryIntoCached(long startTick, long endTick,
        int firstLane, int lastLaneExclusive, List<TimelineRenderItem> destination)
    {
        int originalCount = destination.Count;
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try
        {
            QueryInto(startTick, endTick, firstLane, lastLaneExclusive, destination);
            return true;
        }
        catch (TimelineValueReadPendingException)
        {
            destination.RemoveRange(originalCount, destination.Count - originalCount);
            return false;
        }
    }

    bool IPreparedTimelineRenderItemSource.TryQueryByIdsCached(IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        int originalCount = destination.Count;
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try
        {
            QueryByIds(ids, destination);
            return true;
        }
        catch (TimelineValueReadPendingException)
        {
            destination.RemoveRange(originalCount, destination.Count - originalCount);
            return false;
        }
    }

    void IPreparedTimelineRenderItemSource.PrefetchIds(IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        foreach (MidoraId id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryGetById(id, out _);
        }
    }
}
