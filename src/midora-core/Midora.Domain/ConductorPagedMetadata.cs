namespace Midora.Domain;

internal sealed partial class PagedTimelineValueSnapshot<TValue>
{
    internal bool TryGetOrderedValueById(MidoraId id, out TValue value)
    {
        if (_idDelta.TryGetValue(id, out PagedTimelineIdDelta<TValue> delta))
        {
            value = delta.Value;
            return delta.Exists;
        }
        return _baseById.TryGetValue(id, out value!);
    }

    // Conductor edits replace ordered leaves, not arbitrary note-value overlays.
    // Hash complete boundary blocks conservatively so UI invalidation never
    // reads a cold value page. The previous block is sufficient for step holds.
    internal ulong GetOrderedMetadataFingerprint(long startTick, long endTick, bool includePredecessor)
    {
        if (!_spatialValueOverlay.IsEmpty)
            throw new InvalidOperationException("An ordered Conductor root cannot contain unordered spatial overlays.");
        return _spatialIndex.GetOrderedMetadataFingerprint(startTick, endTick, includePredecessor);
    }

    internal long MaximumStartTickMetadata => _spatialIndex.MaximumStartTickMetadata;
}

internal sealed partial class PagedTimelineSpatialBlockIndex<TValue>
{
    internal long MaximumStartTickMetadata => _root?.MaximumStartTick ?? 0;

    internal ulong GetOrderedMetadataFingerprint(long startTick, long endTick, bool includePredecessor)
    {
        PagedTimelineRangeFingerprintAggregate result = default;
        if (endTick <= startTick) return result.ToFingerprint();
        AccumulateOrderedMetadata(_root, startTick, endTick, ref result);
        if (includePredecessor)
        {
            Node? node = _root;
            Node? previous = null;
            while (node is not null)
            {
                if (node.Entry.Block.MinimumStartTick < startTick)
                {
                    previous = node;
                    node = node.Right;
                }
                else node = node.Left;
            }
            if (previous is not null) result.Combine(previous.Entry.Block.Aggregate);
        }
        return result.ToFingerprint();
    }

    private static void AccumulateOrderedMetadata(Node? node, long start, long end,
        ref PagedTimelineRangeFingerprintAggregate result)
    {
        if (node is null || node.MaximumStartTick < start || node.MinimumStartTick >= end) return;
        if (node.MinimumStartTick >= start && node.MaximumStartTick < end)
        {
            result.Combine(node.MetadataAggregate);
            return;
        }
        AccumulateOrderedMetadata(node.Left, start, end, ref result);
        if (node.Entry.Block.MaximumStartTick >= start && node.Entry.Block.MinimumStartTick < end)
            result.Combine(node.Entry.Block.Aggregate);
        AccumulateOrderedMetadata(node.Right, start, end, ref result);
    }
}
