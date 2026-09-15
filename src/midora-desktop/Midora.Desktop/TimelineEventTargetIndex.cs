using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>
/// Immutable target-specific projection for point-event presentation. A source
/// revision is scanned once; subsequent exact range queries are O(log N + K),
/// fingerprints are O(log N), and low-LOD raster work is bounded by output
/// columns instead of the number of events in unrelated targets.
/// </summary>
internal sealed class TimelineEventTargetIndex<TTarget>
    where TTarget : notnull
{
    private readonly Dictionary<TTarget, TimelineEventTargetLaneIndex> _lanes;

    private TimelineEventTargetIndex(
        Dictionary<TTarget, TimelineEventTargetLaneIndex> lanes,
        TTarget[] targets)
    {
        _lanes = lanes;
        Targets = targets;
    }

    public IReadOnlyList<TTarget> Targets { get; }

    public bool TryGetLane(TTarget target, out TimelineEventTargetLaneIndex? lane) =>
        _lanes.TryGetValue(target, out lane);

    public static TimelineEventTargetIndex<TTarget> Build(
        IEnumerable<(TTarget Target, TimelineEventTargetPoint Point)> values,
        IComparer<TTarget>? targetComparer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<TTarget, List<TimelineEventTargetPoint>> grouped = [];
        int visited = 0;
        foreach ((TTarget target, TimelineEventTargetPoint point) in values)
        {
            if ((visited++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!grouped.TryGetValue(target, out List<TimelineEventTargetPoint>? points))
            {
                points = [];
                grouped.Add(target, points);
            }
            points.Add(point);
        }

        Dictionary<TTarget, TimelineEventTargetLaneIndex> lanes = new(grouped.Count);
        foreach ((TTarget target, List<TimelineEventTargetPoint> points) in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lanes.Add(target, new(points, cancellationToken));
        }
        TTarget[] targets = grouped.Keys.ToArray();
        Array.Sort(targets, targetComparer ?? Comparer<TTarget>.Default);
        return new(lanes, targets);
    }
}

internal readonly record struct TimelineEventTargetPoint(
    MidoraId Id,
    long Tick,
    double Value);

internal sealed class TimelineEventTargetLaneIndex
{
    private const int LeafSize = 64;
    private readonly TimelineEventTargetPoint[] _points;
    private readonly ulong[] _prefixXor;
    private readonly ulong[] _prefixSum;
    private readonly ulong[] _prefixRotatedSum;
    private readonly AggregateNode? _root;

    public static TimelineEventTargetLaneIndex Empty { get; } = new(
        [],
        CancellationToken.None);

    public TimelineEventTargetLaneIndex(
        List<TimelineEventTargetPoint> points,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        points.Sort(static (left, right) =>
        {
            int tick = left.Tick.CompareTo(right.Tick);
            return tick != 0 ? tick : left.Id.CompareTo(right.Id);
        });
        _points = points.ToArray();
        _prefixXor = new ulong[_points.Length + 1];
        _prefixSum = new ulong[_points.Length + 1];
        _prefixRotatedSum = new ulong[_points.Length + 1];
        for (int index = 0; index < _points.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            ulong mixed = Mix(PointFingerprint(_points[index]));
            _prefixXor[index + 1] = _prefixXor[index] ^ mixed;
            _prefixSum[index + 1] = unchecked(_prefixSum[index] + mixed);
            _prefixRotatedSum[index + 1] = unchecked(
                _prefixRotatedSum[index] + System.Numerics.BitOperations.RotateLeft(mixed, 23));
        }
        _root = BuildTree(0, _points.Length, cancellationToken);
        ContentFingerprint = GetRangeFingerprint(0, long.MaxValue);
        MaximumEndTick = _points.Length == 0
            ? 0
            : _points[^1].Tick == long.MaxValue ? long.MaxValue : _points[^1].Tick + 1;
    }

    public int Count => _points.Length;
    public long MaximumEndTick { get; }
    public ulong ContentFingerprint { get; }

    public void Visit(
        long startTick,
        long endTick,
        Action<TimelineEventTargetPoint> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        int first = LowerBound(startTick);
        int last = LowerBound(endTick);
        for (int index = first; index < last; index++) visitor(_points[index]);
    }

    public IEnumerable<TimelineEventTargetPoint> Query(long startTick, long endTick)
    {
        int first = LowerBound(startTick);
        int last = LowerBound(endTick);
        for (int index = first; index < last; index++) yield return _points[index];
    }

    public ulong GetRangeFingerprint(long startTick, long endTick)
    {
        int first = LowerBound(startTick);
        int last = LowerBound(endTick);
        ulong xor = _prefixXor[last] ^ _prefixXor[first];
        ulong sum = unchecked(_prefixSum[last] - _prefixSum[first]);
        ulong rotated = unchecked(_prefixRotatedSum[last] - _prefixRotatedSum[first]);
        ulong result = 14695981039346656037UL;
        Add(ref result, xor);
        Add(ref result, sum);
        Add(ref result, rotated);
        Add(ref result, unchecked((ulong)(last - first)));
        return result;
    }

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination.Clear();
        if (_root is null || destination.IsEmpty) return 0;
        int work = 0;
        Accumulate(
            _root,
            projection,
            destination,
            ref work,
            cancellationToken);
        return work;
    }

    private void Accumulate(
        AggregateNode node,
        TimelineRasterColumnProjection projection,
        Span<TimelineRasterColumnSummary> destination,
        ref int work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.MaximumEndTick <= projection.StartTick
            || node.MinimumTick >= projection.EndTick) return;
        work++;
        int columnSpan = projection.ColumnSpan(node.MinimumTick, node.MaximumEndTick);
        if (columnSpan <= 1)
        {
            Include(
                destination,
                projection,
                node.MinimumTick,
                node.MaximumEndTick,
                node.MinimumValue,
                node.MaximumValue,
                node.Count);
            return;
        }
        if (node.Left is not null)
        {
            Accumulate(node.Left, projection, destination, ref work, cancellationToken);
            Accumulate(node.Right!, projection, destination, ref work, cancellationToken);
            return;
        }
        int last = checked(node.First + node.Count);
        for (int index = node.First; index < last; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            TimelineEventTargetPoint point = _points[index];
            if (point.Tick < projection.StartTick || point.Tick >= projection.EndTick) continue;
            long pointEnd = point.Tick == long.MaxValue ? long.MaxValue : point.Tick + 1;
            Include(
                destination,
                projection,
                point.Tick,
                pointEnd,
                point.Value,
                point.Value,
                1);
            work++;
        }
    }

    private AggregateNode? BuildTree(
        int first,
        int count,
        CancellationToken cancellationToken)
    {
        if (count == 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= LeafSize)
        {
            long minimumTick = long.MaxValue;
            long maximumEndTick = 0;
            double minimumValue = double.PositiveInfinity;
            double maximumValue = double.NegativeInfinity;
            int last = checked(first + count);
            for (int index = first; index < last; index++)
            {
                TimelineEventTargetPoint point = _points[index];
                minimumTick = Math.Min(minimumTick, point.Tick);
                long pointEnd = point.Tick == long.MaxValue ? long.MaxValue : point.Tick + 1;
                maximumEndTick = Math.Max(maximumEndTick, pointEnd);
                minimumValue = Math.Min(minimumValue, point.Value);
                maximumValue = Math.Max(maximumValue, point.Value);
            }
            return new(
                first,
                count,
                minimumTick,
                maximumEndTick,
                minimumValue,
                maximumValue,
                Left: null,
                Right: null);
        }
        int leftCount = count / 2;
        AggregateNode left = BuildTree(first, leftCount, cancellationToken)!;
        AggregateNode right = BuildTree(
            checked(first + leftCount),
            count - leftCount,
            cancellationToken)!;
        return new(
            first,
            count,
            Math.Min(left.MinimumTick, right.MinimumTick),
            Math.Max(left.MaximumEndTick, right.MaximumEndTick),
            Math.Min(left.MinimumValue, right.MinimumValue),
            Math.Max(left.MaximumValue, right.MaximumValue),
            left,
            right);
    }

    private int LowerBound(long tick)
    {
        int low = 0;
        int high = _points.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_points[middle].Tick < tick) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static void Include(
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick,
        double minimumValue,
        double maximumValue,
        int count)
    {
        if (!projection.TryGetColumns(
                contentStartTick,
                contentEndTick,
                out int first,
                out int lastExclusive)) return;
        for (int column = first; column < lastExclusive; column++)
        {
            destination[column].Include(
                1,
                0,
                minimumValue,
                maximumValue,
                count);
        }
    }

    private static ulong PointFingerprint(TimelineEventTargetPoint point)
    {
        ulong value = 14695981039346656037UL;
        Add(ref value, unchecked((ulong)point.Id.Value));
        Add(ref value, unchecked((ulong)point.Tick));
        Add(ref value, unchecked((ulong)BitConverter.DoubleToInt64Bits(point.Value)));
        return value;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private sealed record AggregateNode(
        int First,
        int Count,
        long MinimumTick,
        long MaximumEndTick,
        double MinimumValue,
        double MaximumValue,
        AggregateNode? Left,
        AggregateNode? Right);
}
