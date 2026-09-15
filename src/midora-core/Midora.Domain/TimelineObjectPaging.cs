using System.Collections.Immutable;

namespace Midora.Domain;

/// <summary>
/// One immutable ordinal interval in a timeline source revision. Ordinals are
/// owner-local query addresses, never persistent identity.
/// </summary>
public readonly record struct TimelineOrdinalRange
{
    public TimelineOrdinalRange(int firstOrdinal, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        _ = checked(firstOrdinal + count);
        FirstOrdinal = firstOrdinal;
        Count = count;
    }

    public int FirstOrdinal { get; }
    public int Count { get; }
    public int LastOrdinalExclusive => checked(FirstOrdinal + Count);
}

/// <summary>
/// Immutable page returned by an <see cref="ITimelineObjectSource{TValue}"/>.
/// The page remains valid even if the live owner later advances to another
/// revision.
/// </summary>
public readonly record struct TimelineObjectPage<TValue>(
    long SourceRevision,
    int FirstOrdinal,
    IReadOnlyList<TValue> Values)
{
    public int Count => Values.Count;
    /// <summary>Backing payload array capacity held by this page, separate
    /// from its value-array storage. Shared arrays within the page count once.</summary>
    public long RetainedPayloadBytes { get; init; }
}

/// <summary>
/// Exact half-open timeline query. Lane bounds are inclusive because MIDI key
/// and formal event-lane domains are integral.
/// </summary>
public readonly record struct TimelineObjectRangeQuery
{
    public TimelineObjectRangeQuery(
        long startTick,
        long endTick,
        int minimumLane = int.MinValue,
        int maximumLane = int.MaxValue,
        ulong categoryMask = ulong.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
        if (maximumLane < minimumLane)
            throw new ArgumentOutOfRangeException(nameof(maximumLane));
        StartTick = startTick;
        EndTick = endTick;
        MinimumLane = minimumLane;
        MaximumLane = maximumLane;
        CategoryMask = categoryMask;
    }

    public long StartTick { get; }
    public long EndTick { get; }
    public int MinimumLane { get; }
    public int MaximumLane { get; }
    public ulong CategoryMask { get; }
}

/// <summary>
/// Common immutable timeline query contract used by virtual lists, compressed
/// selections, detached edits, and source tracing. Stable ID remains business
/// identity; ordinal is valid only for <see cref="SourceRevision"/>.
/// </summary>
public interface ITimelineObjectSource<TValue>
{
    int Count { get; }
    long SourceRevision { get; }
    int PageCapacity { get; }

    TValue GetByOrdinal(int ordinal)
    {
        if (!TryGetPageByOrdinal(ordinal, 1, out var page)) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return page.Values[0];
    }

    bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<TValue> page);

    int FindOrdinalAtOrAfterTick(long tick);
    bool TryFindOrdinalById(MidoraId id, out int ordinal);
    void PrepareOrdinalLookup(CancellationToken cancellationToken = default) => cancellationToken.ThrowIfCancellationRequested();
    void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken cancellationToken = default) =>
        PrepareOrdinalLookup(cancellationToken);
    IEnumerable<TValue> QueryTickRange(TimelineObjectRangeQuery query);
    void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable origin information for one value resolved from a frozen source.
/// Consumers can use this for diagnostics and post-edit result mapping without
/// displaying internal IDs to the user.
/// </summary>
public readonly record struct TimelineObjectSourceTrace(
    MidoraId OwnerId,
    long SourceRevision,
    int Ordinal,
    MidoraId ObjectId);

public readonly record struct TracedTimelineObject<TValue>(
    TimelineObjectSourceTrace Trace,
    TValue Value);

/// <summary>
/// Revision-bound compressed selection. Large contiguous selections retain
/// ordinal intervals rather than one identity entry per object; sparse include
/// and exclude sets represent Ctrl/toggle edits around those intervals.
/// </summary>
public sealed class TimelineObjectSelectionDescriptor
{
    private readonly ImmutableArray<TimelineOrdinalRange> _ranges;
    private readonly ImmutableHashSet<MidoraId> _includedIds;
    private readonly ImmutableHashSet<MidoraId> _excludedIds;

    public TimelineObjectSelectionDescriptor(
        MidoraId ownerId,
        long sourceRevision,
        IEnumerable<TimelineOrdinalRange>? ranges = null,
        IEnumerable<MidoraId>? includedIds = null,
        IEnumerable<MidoraId>? excludedIds = null)
    {
        if (ownerId == default) throw new ArgumentOutOfRangeException(nameof(ownerId));
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRevision);
        OwnerId = ownerId;
        SourceRevision = sourceRevision;
        _ranges = NormalizeRanges(ranges ?? []);
        _includedIds = ValidateIds(includedIds ?? [], nameof(includedIds));
        _excludedIds = ValidateIds(excludedIds ?? [], nameof(excludedIds));
        if (_includedIds.Overlaps(_excludedIds))
        {
            throw new ArgumentException(
                "A compressed timeline selection cannot include and exclude the same Stable ID.");
        }
    }

    public MidoraId OwnerId { get; }
    public long SourceRevision { get; }
    public IReadOnlyList<TimelineOrdinalRange> OrdinalRanges => _ranges;
    public IReadOnlySet<MidoraId> IncludedIds => _includedIds;
    public IReadOnlySet<MidoraId> ExcludedIds => _excludedIds;

    public long MaximumResolvedCount
    {
        get
        {
            long count = _includedIds.Count;
            foreach (TimelineOrdinalRange range in _ranges)
                count = checked(count + range.Count);
            return count;
        }
    }

    public IEnumerable<TValue> Resolve<TValue>(
        ITimelineObjectSource<TValue> source,
        Func<TValue, MidoraId> getId,
        CancellationToken cancellationToken = default) =>
        ResolveTraced(source, getId, cancellationToken).Select(static value => value.Value);

    public IEnumerable<TracedTimelineObject<TValue>> ResolveTraced<TValue>(
        ITimelineObjectSource<TValue> source,
        Func<TValue, MidoraId> getId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(getId);
        EnsureRevision(source);
        int visited = 0;
        foreach (TimelineOrdinalRange range in _ranges)
        {
            if (range.LastOrdinalExclusive > source.Count)
                throw new InvalidOperationException("A compressed timeline selection range is outside its source revision.");
            for (int first = range.FirstOrdinal;
                first < range.LastOrdinalExclusive;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(
                    source.PageCapacity,
                    range.LastOrdinalExclusive - first);
                if (!source.TryGetPageByOrdinal(first, count, out TimelineObjectPage<TValue> page)
                    || page.FirstOrdinal != first
                    || page.SourceRevision != SourceRevision
                    || page.Count == 0
                    || page.Count > count)
                {
                    throw new InvalidOperationException("A timeline source returned an invalid ordinal page.");
                }
                for (int offset = 0; offset < page.Count; offset++)
                {
                    if ((visited++ & 0xff) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    TValue value = page.Values[offset];
                    MidoraId id = getId(value);
                    if (_excludedIds.Contains(id)) continue;
                    yield return new(
                        new(OwnerId, SourceRevision, checked(first + offset), id),
                        value);
                }
                first = checked(first + page.Count);
            }
        }

        foreach (MidoraId id in _includedIds.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.TryFindOrdinalById(id, out int ordinal)) continue;
            // Included IDs are sparse exceptions around potentially enormous
            // ordinal ranges.  Resolve the sparse ID to its ordinal and test
            // range membership directly instead of retaining every ID yielded
            // by the ranges in a range-sized HashSet.
            if (ContainsOrdinal(ordinal)) continue;
            if (!source.TryGetPageByOrdinal(ordinal, 1, out TimelineObjectPage<TValue> page)
                || page.Count != 1
                || getId(page.Values[0]) != id)
            {
                throw new InvalidOperationException("A timeline source ID-to-ordinal lookup is inconsistent.");
            }
            yield return new(
                new(OwnerId, SourceRevision, ordinal, id),
                page.Values[0]);
        }
    }

    private bool ContainsOrdinal(int ordinal)
    {
        int low = 0;
        int high = _ranges.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            TimelineOrdinalRange range = _ranges[middle];
            if (ordinal < range.FirstOrdinal)
            {
                high = middle - 1;
            }
            else if (ordinal >= range.LastOrdinalExclusive)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public void EnsureRevision<TValue>(ITimelineObjectSource<TValue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SourceRevision != SourceRevision)
        {
            throw new InvalidOperationException(
                "The timeline selection belongs to a stale source revision.");
        }
    }

    private static ImmutableHashSet<MidoraId> ValidateIds(
        IEnumerable<MidoraId> ids,
        string parameterName)
    {
        ImmutableHashSet<MidoraId>.Builder result = ImmutableHashSet.CreateBuilder<MidoraId>();
        foreach (MidoraId id in ids)
        {
            if (id == default) throw new ArgumentOutOfRangeException(parameterName);
            result.Add(id);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<TimelineOrdinalRange> NormalizeRanges(
        IEnumerable<TimelineOrdinalRange> ranges)
    {
        TimelineOrdinalRange[] ordered = ranges.OrderBy(static value => value.FirstOrdinal).ToArray();
        if (ordered.Length == 0) return [];
        ImmutableArray<TimelineOrdinalRange>.Builder result = ImmutableArray.CreateBuilder<TimelineOrdinalRange>();
        int first = ordered[0].FirstOrdinal;
        int end = ordered[0].LastOrdinalExclusive;
        for (int index = 1; index < ordered.Length; index++)
        {
            TimelineOrdinalRange current = ordered[index];
            if (current.FirstOrdinal <= end)
            {
                end = Math.Max(end, current.LastOrdinalExclusive);
                continue;
            }
            result.Add(new(first, checked(end - first)));
            first = current.FirstOrdinal;
            end = current.LastOrdinalExclusive;
        }
        result.Add(new(first, checked(end - first)));
        return result.ToImmutable();
    }
}
