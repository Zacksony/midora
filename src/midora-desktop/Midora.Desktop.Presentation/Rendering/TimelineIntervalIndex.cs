namespace Midora.Desktop.Presentation.Rendering;

public sealed class TimelineIntervalIndex
{
    private readonly IReadOnlyDictionary<int, LaneBucket> _lanes;

    public TimelineIntervalIndex(IEnumerable<TimelineRenderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _lanes = items
            .GroupBy(item => item.Lane)
            .ToDictionary(
                group => group.Key,
                group => new LaneBucket(group));
    }

    public int LaneCount => _lanes.Count;

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (startTick < 0 || endTick <= startTick)
        {
            throw new ArgumentOutOfRangeException(nameof(endTick));
        }
        if (firstLane < 0 || lastLaneExclusive <= firstLane)
        {
            throw new ArgumentOutOfRangeException(nameof(lastLaneExclusive));
        }

        destination.Clear();
        for (int lane = firstLane; lane < lastLaneExclusive; lane++)
        {
            if (_lanes.TryGetValue(lane, out LaneBucket? bucket))
            {
                bucket.QueryInto(startTick, endTick, destination);
            }
        }
    }

    internal void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (startTick < 0 || endTick <= startTick)
        {
            throw new ArgumentOutOfRangeException(nameof(endTick));
        }
        if (firstLane < 0 || lastLaneExclusive <= firstLane)
        {
            throw new ArgumentOutOfRangeException(nameof(lastLaneExclusive));
        }

        for (int lane = firstLane; lane < lastLaneExclusive; lane++)
        {
            if (_lanes.TryGetValue(lane, out LaneBucket? bucket))
            {
                bucket.VisitInto(startTick, endTick, visitor);
            }
        }
    }

    public void HitTestInto(
        long tick,
        long toleranceTicks,
        int lane,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (tick < 0 || toleranceTicks < 0 || lane < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }

        destination.Clear();
        if (!_lanes.TryGetValue(lane, out LaneBucket? bucket))
        {
            return;
        }

        long start = Math.Max(0, checked(tick - Math.Min(tick, toleranceTicks)));
        long end = checked(tick + toleranceTicks + 1);
        bucket.QueryInto(start, end, destination);
        destination.RemoveAll(static item =>
            item.State.HasFlag(TimelineItemState.HitTestDisabled));
        destination.Sort(HitTestComparer.Instance);
    }

    private sealed class LaneBucket
    {
        private readonly TimelineRenderItem[] _items;
        private readonly long[] _maximumEndTree;
        private readonly int _leafBase;

        public LaneBucket(IEnumerable<TimelineRenderItem> items)
        {
            _items = items
                .OrderBy(item => item.StartTick)
                .ThenBy(item => item.EndTick)
                .ThenBy(item => item.ZIndex)
                .ThenBy(item => item.Id)
                .ToArray();
            _leafBase = 1;
            while (_leafBase < _items.Length) _leafBase <<= 1;
            _maximumEndTree = new long[_leafBase << 1];
            Array.Fill(_maximumEndTree, long.MinValue);
            for (int index = 0; index < _items.Length; index++)
            {
                _maximumEndTree[_leafBase + index] = _items[index].EndTick;
            }
            for (int node = _leafBase - 1; node > 0; node--)
            {
                _maximumEndTree[node] = Math.Max(
                    _maximumEndTree[node << 1],
                    _maximumEndTree[(node << 1) | 1]);
            }
        }

        public void QueryInto(long startTick, long endTick, List<TimelineRenderItem> destination)
        {
            int candidateEnd = FirstStartAtOrAfter(endTick);
            QueryNode(
                node: 1,
                nodeStart: 0,
                nodeEnd: _leafBase,
                candidateEnd,
                startTick,
                destination);
        }

        public void VisitInto(
            long startTick,
            long endTick,
            Action<TimelineRenderItem> visitor)
        {
            int candidateEnd = FirstStartAtOrAfter(endTick);
            VisitNode(
                node: 1,
                nodeStart: 0,
                nodeEnd: _leafBase,
                candidateEnd,
                startTick,
                visitor);
        }

        private void QueryNode(
            int node,
            int nodeStart,
            int nodeEnd,
            int candidateEnd,
            long startTick,
            List<TimelineRenderItem> destination)
        {
            if (nodeStart >= candidateEnd || _maximumEndTree[node] <= startTick)
            {
                return;
            }
            if (nodeEnd - nodeStart == 1)
            {
                if (nodeStart < _items.Length) destination.Add(_items[nodeStart]);
                return;
            }
            int middle = nodeStart + ((nodeEnd - nodeStart) >> 1);
            QueryNode(node << 1, nodeStart, middle, candidateEnd, startTick, destination);
            QueryNode((node << 1) | 1, middle, nodeEnd, candidateEnd, startTick, destination);
        }

        private void VisitNode(
            int node,
            int nodeStart,
            int nodeEnd,
            int candidateEnd,
            long startTick,
            Action<TimelineRenderItem> visitor)
        {
            if (nodeStart >= candidateEnd || _maximumEndTree[node] <= startTick)
            {
                return;
            }
            if (nodeEnd - nodeStart == 1)
            {
                if (nodeStart < _items.Length) visitor(_items[nodeStart]);
                return;
            }
            int middle = nodeStart + ((nodeEnd - nodeStart) >> 1);
            VisitNode(node << 1, nodeStart, middle, candidateEnd, startTick, visitor);
            VisitNode((node << 1) | 1, middle, nodeEnd, candidateEnd, startTick, visitor);
        }

        private int FirstStartAtOrAfter(long tick)
        {
            int low = 0;
            int high = _items.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_items[middle].StartTick < tick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }

    private sealed class HitTestComparer : IComparer<TimelineRenderItem>
    {
        public static HitTestComparer Instance { get; } = new();

        public int Compare(TimelineRenderItem x, TimelineRenderItem y)
        {
            int byZ = y.ZIndex.CompareTo(x.ZIndex);
            if (byZ != 0)
            {
                return byZ;
            }
            int byLength = x.Length.CompareTo(y.Length);
            return byLength != 0 ? byLength : x.Id.CompareTo(y.Id);
        }
    }
}
