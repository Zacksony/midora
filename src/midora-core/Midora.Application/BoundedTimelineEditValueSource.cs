using Midora.Domain;

namespace Midora.Application;

internal sealed class BoundedTimelineOrdinalIndexBuilder : IImmutableTimelineOrdinalIndexBuilder
{
    public static BoundedTimelineOrdinalIndexBuilder Instance { get; } = new();
    private BoundedTimelineOrdinalIndexBuilder() { }
    public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token) =>
        BoundedTimelineOrdinalIndex.Create(values, token);
}

internal sealed class BoundedTimelineEditValueSource<T>(BoundedEditRecordStore<TimelineValueEdit<T>> store)
    : BoundedImmutableValueSource<TimelineValueEdit<T>>(store), IImmutableTimelineEditAddressSource where T : unmanaged
{
    public IImmutableTimelineValueSource<int> CreateDeletedOrdinals(CancellationToken token)
    {
        var resources = BulkEditPreparationContext.Current?.Resources
            ?? throw new InvalidOperationException("Address indexes require an active bounded edit context.");
        var result = new BoundedEditRecordStore<int>(resources);
        try
        {
            foreach (var edit in this)
            {
                token.ThrowIfCancellationRequested();
                if (edit.IsDeleted) result.Add(edit.Ordinal, token);
            }
            result.Seal(); result.SpillResidentPages(token);
            return new BoundedImmutableValueSource<int>(result);
        }
        catch { result.Dispose(); throw; }
    }

    public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token) =>
        BoundedTimelineOrdinalIndex.Create(values, token);
}

internal sealed class BoundedTimelineOrdinalIndex(BoundedImmutableValueSource<TimelineIdOrdinal> values)
    : IImmutableTimelineIdIndex, IDisposable
{
    public static BoundedTimelineOrdinalIndex Create(IEnumerable<TimelineIdOrdinal> values, CancellationToken token)
    {
        var resources = BulkEditPreparationContext.Current?.Resources
            ?? throw new InvalidOperationException("Address indexes require an active bounded edit context.");
        var result = BoundedEditSort.Sort(values, Comparer<TimelineIdOrdinal>.Create((a, b) => a.Id.CompareTo(b.Id)), resources, token);
        try { result.SpillResidentPages(token); return new(new(result)); }
        catch { result.Dispose(); throw; }
    }
    public bool TryFindOrdinalById(MidoraId id, out int ordinal)
    {
        int low = 0, high = values.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var candidate = values[middle];
            int order = candidate.Id.CompareTo(id);
            if (order < 0) low = middle + 1;
            else if (order > 0) high = middle - 1;
            else { ordinal = candidate.Ordinal; return true; }
        }
        ordinal = -1; return false;
    }
    public void RetainForSourceLifetime() => BoundedEditResourceLease.RetainForSourceLifetime(values);
    public void Dispose() => values.Dispose();
}
