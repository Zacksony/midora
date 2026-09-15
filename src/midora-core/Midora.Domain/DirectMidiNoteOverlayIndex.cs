using System.Numerics;

namespace Midora.Domain;

/// <summary>
/// Immutable, structurally shared interval and ID indexes for the mutable
/// Direct-MIDI Note overlay. A query snapshot captures this object by reference;
/// later edits replace only paths affected by the changed Stable IDs.
/// </summary>
internal sealed class DirectMidiNoteOverlayIndex
{
    private readonly SpatialNode? _spatialRoot;
    private readonly SpatialNode? _tickRoot;
    private readonly IdNode? _idRoot;

    private DirectMidiNoteOverlayIndex(
        SpatialNode? spatialRoot,
        SpatialNode? tickRoot,
        IdNode? idRoot)
    {
        _spatialRoot = spatialRoot;
        _tickRoot = tickRoot;
        _idRoot = idRoot;
    }

    public static DirectMidiNoteOverlayIndex Empty { get; } = new(null, null, null);

    internal static DirectMidiNoteOverlayIndex Create(
        IEnumerable<DirectMidiNoteValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        DirectMidiNoteValue[] materialized = values as DirectMidiNoteValue[]
            ?? values.ToArray();
        return materialized.Length == 0 ? Empty : Build(materialized);
    }

    public int Count => _idRoot?.Count ?? 0;
    public long MaximumEndTick => _spatialRoot?.MaximumEndTick ?? 0;

    public bool TryGetById(MidoraId id, out DirectMidiNoteValue value)
    {
        IdNode? current = _idRoot;
        while (current is not null)
        {
            int comparison = id.CompareTo(current.Value.Id);
            if (comparison == 0)
            {
                value = current.Value;
                return true;
            }
            current = comparison < 0 ? current.Left : current.Right;
        }
        value = default;
        return false;
    }

    public DirectMidiNoteOverlayIndex ReplaceBatch(
        IReadOnlySet<MidoraId> dirtyIds,
        Func<MidoraId, DirectMidiNoteValue?> resolveCurrent)
    {
        ArgumentNullException.ThrowIfNull(dirtyIds);
        ArgumentNullException.ThrowIfNull(resolveCurrent);
        if (dirtyIds.Count == 0) return this;

        DirectMidiNoteValue[] replacements = dirtyIds
            .Select(resolveCurrent)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        if (Count == 0)
            return replacements.Length == 0 ? this : Build(replacements);

        if (dirtyIds.Count >= Count && AllIdsBelongTo(dirtyIds))
            return replacements.Length == 0 ? Empty : Build(replacements);

        SpatialNode? spatial = _spatialRoot;
        SpatialNode? tick = _tickRoot;
        IdNode? ids = _idRoot;
        foreach (MidoraId id in dirtyIds)
        {
            if (TryGetById(id, out DirectMidiNoteValue previous))
            {
                spatial = RemoveSpatial(spatial, previous, tickFirst: false);
                tick = RemoveSpatial(tick, previous, tickFirst: true);
                ids = RemoveId(ids, id);
            }
        }
        foreach (DirectMidiNoteValue value in replacements)
        {
            spatial = InsertSpatial(spatial, value, tickFirst: false);
            tick = InsertSpatial(tick, value, tickFirst: true);
            ids = InsertId(ids, value);
        }
        return new(spatial, tick, ids);
    }

    public IEnumerable<DirectMidiNoteValue> Query(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endTick <= startTick || maximumKey < minimumKey || _spatialRoot is null)
            return [];
        List<DirectMidiNoteValue> result = [];
        QuerySpatial(_spatialRoot, startTick, endTick, minimumKey, maximumKey, result, cancellationToken);
        return result;
    }

    public IEnumerable<DirectMidiNoteValue> ResolveByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out DirectMidiNoteValue value)) yield return value;
        }
    }

    public IEnumerable<DirectMidiNoteValue> QueryStartKey(long startTick, int key)
    {
        List<DirectMidiNoteValue> result = [];
        QueryStartKey(_spatialRoot, startTick, key, result);
        return result;
    }

    public IEnumerable<DirectMidiNoteValue> QueryStartKeys(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || _spatialRoot is null) return [];
        if (keys.Count <= 64)
        {
            List<DirectMidiNoteValue> small = [];
            foreach (DirectMidiNoteStartKey key in keys)
                QueryStartKey(_spatialRoot, key.Tick, key.Key, small);
            return small;
        }

        long minimumTick = keys.Min(static key => key.Tick);
        long maximumTick = keys.Max(static key => key.Tick);
        List<DirectMidiNoteValue> result = [];
        QueryStartKeys(_spatialRoot, keys, minimumTick, maximumTick, result);
        return result;
    }

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey)
    {
        FingerprintAggregate aggregate = default;
        AccumulateFingerprint(
            _spatialRoot,
            startTick,
            endTick,
            minimumKey,
            maximumKey,
            ref aggregate);
        return aggregate.ToFingerprint();
    }

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumKey,
        int maximumKey,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumKey < minimumKey || destination.IsEmpty)
            return 0;
        int work = 0;
        AccumulateRasterColumns(
            _tickRoot,
            projection,
            minimumKey,
            maximumKey,
            destination,
            ref work,
            cancellationToken);
        return work;
    }

    private bool AllIdsBelongTo(IReadOnlySet<MidoraId> ids)
    {
        Stack<IdNode> stack = new();
        IdNode? current = _idRoot;
        while (current is not null || stack.Count != 0)
        {
            while (current is not null)
            {
                stack.Push(current);
                current = current.Left;
            }
            current = stack.Pop();
            if (!ids.Contains(current.Value.Id)) return false;
            current = current.Right;
        }
        return true;
    }

    private static DirectMidiNoteOverlayIndex Build(DirectMidiNoteValue[] values)
    {
        DirectMidiNoteValue[] spatial = [.. values];
        Array.Sort(spatial, CompareSpatial);
        DirectMidiNoteValue[] tick = [.. values];
        Array.Sort(tick, CompareTickSpatial);
        DirectMidiNoteValue[] ids = [.. values];
        Array.Sort(ids, static (left, right) => left.Id.CompareTo(right.Id));
        return new(
            BuildSpatial(spatial, 0, spatial.Length),
            BuildSpatial(tick, 0, tick.Length),
            BuildIds(ids, 0, ids.Length));
    }

    private static SpatialNode? BuildSpatial(DirectMidiNoteValue[] values, int first, int count)
    {
        if (count == 0) return null;
        int leftCount = count / 2;
        int middle = first + leftCount;
        return new(
            values[middle],
            BuildSpatial(values, first, leftCount),
            BuildSpatial(values, middle + 1, count - leftCount - 1));
    }

    private static IdNode? BuildIds(DirectMidiNoteValue[] values, int first, int count)
    {
        if (count == 0) return null;
        int leftCount = count / 2;
        int middle = first + leftCount;
        return new(
            values[middle],
            BuildIds(values, first, leftCount),
            BuildIds(values, middle + 1, count - leftCount - 1));
    }

    private static void QuerySpatial(
        SpatialNode? node,
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        List<DirectMidiNoteValue> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null
            || node.MaximumEndTick <= startTick
            || node.MinimumStartTick >= endTick
            || node.MaximumKey < minimumKey
            || node.MinimumKey > maximumKey)
        {
            return;
        }
        QuerySpatial(node.Left, startTick, endTick, minimumKey, maximumKey, destination, cancellationToken);
        DirectMidiNoteValue value = node.Value;
        if (value.StartTick < endTick
            && EndTick(value) > startTick
            && value.Key >= minimumKey
            && value.Key <= maximumKey)
        {
            destination.Add(value);
        }
        QuerySpatial(node.Right, startTick, endTick, minimumKey, maximumKey, destination, cancellationToken);
    }

    private static void QueryStartKey(
        SpatialNode? node,
        long startTick,
        int key,
        List<DirectMidiNoteValue> destination)
    {
        if (node is null
            || node.MinimumStartTick > startTick
            || node.MaximumStartTick < startTick
            || node.MinimumKey > key
            || node.MaximumKey < key)
        {
            return;
        }
        DirectMidiNoteValue value = node.Value;
        QueryStartKey(node.Left, startTick, key, destination);
        if (value.StartTick == startTick && value.Key == key) destination.Add(value);
        QueryStartKey(node.Right, startTick, key, destination);
    }

    private static void QueryStartKeys(
        SpatialNode? node,
        IReadOnlySet<DirectMidiNoteStartKey> keys,
        long minimumTick,
        long maximumTick,
        List<DirectMidiNoteValue> destination)
    {
        if (node is null
            || node.MaximumStartTick < minimumTick
            || node.MinimumStartTick > maximumTick)
        {
            return;
        }
        DirectMidiNoteValue value = node.Value;
        QueryStartKeys(node.Left, keys, minimumTick, maximumTick, destination);
        if (value.StartTick >= minimumTick
            && value.StartTick <= maximumTick
            && keys.Contains(new(value.StartTick, value.Key)))
        {
            destination.Add(value);
        }
        QueryStartKeys(node.Right, keys, minimumTick, maximumTick, destination);
    }

    private static void AccumulateFingerprint(
        SpatialNode? node,
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        ref FingerprintAggregate destination)
    {
        if (node is null
            || node.MaximumEndTick <= startTick
            || node.MinimumStartTick >= endTick
            || node.MaximumKey < minimumKey
            || node.MinimumKey > maximumKey)
        {
            return;
        }
        if (startTick <= node.MinimumStartTick
            && endTick >= node.MaximumEndTick
            && minimumKey <= node.MinimumKey
            && maximumKey >= node.MaximumKey)
        {
            destination.Combine(node.Aggregate);
            return;
        }
        AccumulateFingerprint(node.Left, startTick, endTick, minimumKey, maximumKey, ref destination);
        DirectMidiNoteValue value = node.Value;
        if (value.StartTick < endTick
            && EndTick(value) > startTick
            && value.Key >= minimumKey
            && value.Key <= maximumKey)
        {
            destination.Add(Fingerprint(value));
        }
        AccumulateFingerprint(node.Right, startTick, endTick, minimumKey, maximumKey, ref destination);
    }

    private static void AccumulateRasterColumns(
        SpatialNode? node,
        TimelineRasterColumnProjection projection,
        int minimumKey,
        int maximumKey,
        Span<TimelineRasterColumnSummary> destination,
        ref int work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null
            || node.MaximumEndTick <= projection.StartTick
            || node.MinimumStartTick >= projection.EndTick
            || node.MaximumKey < minimumKey
            || node.MinimumKey > maximumKey)
        {
            return;
        }

        bool fullyContained = projection.StartTick <= node.MinimumStartTick
            && projection.EndTick >= node.MaximumEndTick
            && minimumKey <= node.MinimumKey
            && maximumKey >= node.MaximumKey;
        if (fullyContained
            && projection.TryGetColumns(
                node.MinimumStartTick,
                node.MaximumEndTick,
                out int firstColumn,
                out int lastExclusive)
            && lastExclusive - firstColumn == 1)
        {
            IncludeNode(
                node,
                projection,
                firstColumn,
                lastExclusive,
                destination);
            work++;
            return;
        }

        AccumulateRasterColumns(
            node.Left,
            projection,
            minimumKey,
            maximumKey,
            destination,
            ref work,
            cancellationToken);
        DirectMidiNoteValue value = node.Value;
        if (value.StartTick < projection.EndTick
            && EndTick(value) > projection.StartTick
            && value.Key >= minimumKey
            && value.Key <= maximumKey)
        {
            ulong low = value.Key < 64 ? 1UL << value.Key : 0;
            ulong high = value.Key >= 64 ? 1UL << (value.Key - 64) : 0;
            PagedTimelineRasterProjection.IncludeExact(
                destination,
                projection,
                value.StartTick,
                EndTick(value),
                low,
                high,
                value.NoteOnVelocity / 127d,
                value.NoteOnVelocity / 127d,
                1);
            work++;
        }
        AccumulateRasterColumns(
            node.Right,
            projection,
            minimumKey,
            maximumKey,
            destination,
            ref work,
            cancellationToken);
    }

    private static void IncludeNode(
        SpatialNode node,
        TimelineRasterColumnProjection projection,
        int firstColumn,
        int lastExclusive,
        Span<TimelineRasterColumnSummary> destination)
    {
        for (int column = firstColumn; column < lastExclusive; column++)
        {
            destination[column].Include(
                node.LaneMaskLow,
                node.LaneMaskHigh,
                node.MinimumVelocity / 127d,
                node.MaximumVelocity / 127d,
                node.Count);
        }
        if (node.MinimumStartTick >= projection.StartTick
            && node.MinimumStartTick < projection.EndTick)
        {
            destination[firstColumn].IncludeStartBoundary(
                node.LaneMaskLow,
                node.LaneMaskHigh);
        }
        if (node.MaximumEndTick > projection.StartTick
            && node.MaximumEndTick <= projection.EndTick)
        {
            destination[lastExclusive - 1].IncludeEndBoundary(
                node.LaneMaskLow,
                node.LaneMaskHigh);
        }
    }

    private static int Column(long tick, long startTick, long endTick, int width)
    {
        double normalized = (Math.Clamp(tick, startTick, endTick - 1) - startTick)
            / (double)(endTick - startTick);
        return Math.Clamp((int)(normalized * width), 0, width - 1);
    }

    private static SpatialNode InsertSpatial(
        SpatialNode? node,
        DirectMidiNoteValue value,
        bool tickFirst)
    {
        if (node is null) return new(value, null, null);
        int comparison = tickFirst
            ? CompareTickSpatial(value, node.Value)
            : CompareSpatial(value, node.Value);
        if (comparison == 0)
            throw new InvalidOperationException("A Direct MIDI Note overlay spatial key is duplicated.");
        return Balance(comparison < 0
            ? new SpatialNode(node.Value, InsertSpatial(node.Left, value, tickFirst), node.Right)
            : new SpatialNode(node.Value, node.Left, InsertSpatial(node.Right, value, tickFirst)));
    }

    private static SpatialNode? RemoveSpatial(
        SpatialNode? node,
        DirectMidiNoteValue value,
        bool tickFirst)
    {
        if (node is null) return null;
        int comparison = tickFirst
            ? CompareTickSpatial(value, node.Value)
            : CompareSpatial(value, node.Value);
        if (comparison < 0) return Balance(new SpatialNode(node.Value, RemoveSpatial(node.Left, value, tickFirst), node.Right));
        if (comparison > 0) return Balance(new SpatialNode(node.Value, node.Left, RemoveSpatial(node.Right, value, tickFirst)));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        SpatialNode successor = Minimum(node.Right);
        return Balance(new SpatialNode(successor.Value, node.Left, RemoveMinimum(node.Right)));
    }

    private static IdNode InsertId(IdNode? node, DirectMidiNoteValue value)
    {
        if (node is null) return new(value, null, null);
        int comparison = value.Id.CompareTo(node.Value.Id);
        if (comparison == 0)
            throw new InvalidOperationException("A Direct MIDI Note overlay Stable ID is duplicated.");
        return Balance(comparison < 0
            ? new IdNode(node.Value, InsertId(node.Left, value), node.Right)
            : new IdNode(node.Value, node.Left, InsertId(node.Right, value)));
    }

    private static IdNode? RemoveId(IdNode? node, MidoraId id)
    {
        if (node is null) return null;
        int comparison = id.CompareTo(node.Value.Id);
        if (comparison < 0) return Balance(new IdNode(node.Value, RemoveId(node.Left, id), node.Right));
        if (comparison > 0) return Balance(new IdNode(node.Value, node.Left, RemoveId(node.Right, id)));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        IdNode successor = Minimum(node.Right);
        return Balance(new IdNode(successor.Value, node.Left, RemoveMinimum(node.Right)));
    }

    private static SpatialNode Minimum(SpatialNode node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static IdNode Minimum(IdNode node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static SpatialNode? RemoveMinimum(SpatialNode node) => node.Left is null
        ? node.Right
        : Balance(new SpatialNode(node.Value, RemoveMinimum(node.Left), node.Right));

    private static IdNode? RemoveMinimum(IdNode node) => node.Left is null
        ? node.Right
        : Balance(new IdNode(node.Value, RemoveMinimum(node.Left), node.Right));

    private static SpatialNode Balance(SpatialNode node)
    {
        int balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node = new(node.Value, RotateLeft(node.Left), node.Right);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node = new(node.Value, node.Left, RotateRight(node.Right));
            return RotateLeft(node);
        }
        return node;
    }

    private static IdNode Balance(IdNode node)
    {
        int balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node = new(node.Value, RotateLeft(node.Left), node.Right);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node = new(node.Value, node.Left, RotateRight(node.Right));
            return RotateLeft(node);
        }
        return node;
    }

    private static SpatialNode RotateLeft(SpatialNode node)
    {
        SpatialNode pivot = node.Right!;
        return new(pivot.Value, new(node.Value, node.Left, pivot.Left), pivot.Right);
    }

    private static SpatialNode RotateRight(SpatialNode node)
    {
        SpatialNode pivot = node.Left!;
        return new(pivot.Value, pivot.Left, new(node.Value, pivot.Right, node.Right));
    }

    private static IdNode RotateLeft(IdNode node)
    {
        IdNode pivot = node.Right!;
        return new(pivot.Value, new(node.Value, node.Left, pivot.Left), pivot.Right);
    }

    private static IdNode RotateRight(IdNode node)
    {
        IdNode pivot = node.Left!;
        return new(pivot.Value, pivot.Left, new(node.Value, pivot.Right, node.Right));
    }

    private static int Height(SpatialNode? node) => node?.Height ?? 0;
    private static int Height(IdNode? node) => node?.Height ?? 0;

    private static int CompareSpatial(DirectMidiNoteValue left, DirectMidiNoteValue right)
    {
        int key = left.Key.CompareTo(right.Key);
        if (key != 0) return key;
        int start = left.StartTick.CompareTo(right.StartTick);
        return start != 0 ? start : left.Id.CompareTo(right.Id);
    }

    private static int CompareTickSpatial(DirectMidiNoteValue left, DirectMidiNoteValue right)
    {
        int start = left.StartTick.CompareTo(right.StartTick);
        if (start != 0) return start;
        int key = left.Key.CompareTo(right.Key);
        return key != 0 ? key : left.Id.CompareTo(right.Id);
    }

    private static long EndTick(DirectMidiNoteValue value) =>
        value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
            ? long.MaxValue
            : value.StartTick + Math.Max(1, value.LengthTicks);

    private static ulong Fingerprint(DirectMidiNoteValue value)
    {
        ulong result = FingerprintAggregate.Offset;
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.Id.Value));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.StartTick));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.LengthTicks));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.Key));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.NoteOnVelocity));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.NoteOffVelocity));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.NoteOnOrder));
        FingerprintAggregate.Hash(ref result, unchecked((ulong)value.NoteOffOrder));
        return result;
    }

    private sealed class SpatialNode
    {
        public SpatialNode(DirectMidiNoteValue value, SpatialNode? left, SpatialNode? right)
        {
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + Math.Max(DirectMidiNoteOverlayIndex.Height(left), DirectMidiNoteOverlayIndex.Height(right));
            Count = checked(1 + (left?.Count ?? 0) + (right?.Count ?? 0));
            MinimumStartTick = Math.Min(value.StartTick, Math.Min(left?.MinimumStartTick ?? long.MaxValue, right?.MinimumStartTick ?? long.MaxValue));
            MaximumStartTick = Math.Max(value.StartTick, Math.Max(left?.MaximumStartTick ?? long.MinValue, right?.MaximumStartTick ?? long.MinValue));
            MaximumEndTick = Math.Max(EndTick(value), Math.Max(left?.MaximumEndTick ?? 0, right?.MaximumEndTick ?? 0));
            MinimumKey = Math.Min(value.Key, Math.Min(left?.MinimumKey ?? int.MaxValue, right?.MinimumKey ?? int.MaxValue));
            MaximumKey = Math.Max(value.Key, Math.Max(left?.MaximumKey ?? int.MinValue, right?.MaximumKey ?? int.MinValue));
            LaneMaskLow = (left?.LaneMaskLow ?? 0)
                | (value.Key < 64 ? 1UL << value.Key : 0)
                | (right?.LaneMaskLow ?? 0);
            LaneMaskHigh = (left?.LaneMaskHigh ?? 0)
                | (value.Key >= 64 ? 1UL << (value.Key - 64) : 0)
                | (right?.LaneMaskHigh ?? 0);
            MinimumVelocity = Math.Min(
                value.NoteOnVelocity,
                Math.Min(left?.MinimumVelocity ?? int.MaxValue, right?.MinimumVelocity ?? int.MaxValue));
            MaximumVelocity = Math.Max(
                value.NoteOnVelocity,
                Math.Max(left?.MaximumVelocity ?? int.MinValue, right?.MaximumVelocity ?? int.MinValue));
            FingerprintAggregate aggregate = default;
            if (left is not null) aggregate.Combine(left.Aggregate);
            aggregate.Add(Fingerprint(value));
            if (right is not null) aggregate.Combine(right.Aggregate);
            Aggregate = aggregate;
        }

        public DirectMidiNoteValue Value { get; }
        public SpatialNode? Left { get; }
        public SpatialNode? Right { get; }
        public int Height { get; }
        public int Count { get; }
        public long MinimumStartTick { get; }
        public long MaximumStartTick { get; }
        public long MaximumEndTick { get; }
        public int MinimumKey { get; }
        public int MaximumKey { get; }
        public ulong LaneMaskLow { get; }
        public ulong LaneMaskHigh { get; }
        public int MinimumVelocity { get; }
        public int MaximumVelocity { get; }
        public FingerprintAggregate Aggregate { get; }
    }

    private sealed class IdNode
    {
        public IdNode(DirectMidiNoteValue value, IdNode? left, IdNode? right)
        {
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + Math.Max(DirectMidiNoteOverlayIndex.Height(left), DirectMidiNoteOverlayIndex.Height(right));
            Count = checked(1 + (left?.Count ?? 0) + (right?.Count ?? 0));
        }

        public DirectMidiNoteValue Value { get; }
        public IdNode? Left { get; }
        public IdNode? Right { get; }
        public int Height { get; }
        public int Count { get; }
    }

    private struct FingerprintAggregate
    {
        internal const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _xor;
        private ulong _sum;
        private ulong _rotatedSum;
        private ulong _count;

        public void Add(ulong value)
        {
            ulong mixed = Mix(value);
            _xor ^= mixed;
            _sum = unchecked(_sum + mixed);
            _rotatedSum = unchecked(_rotatedSum + BitOperations.RotateLeft(mixed, 23));
            _count++;
        }

        public void Combine(FingerprintAggregate other)
        {
            _xor ^= other._xor;
            _sum = unchecked(_sum + other._sum);
            _rotatedSum = unchecked(_rotatedSum + other._rotatedSum);
            _count = unchecked(_count + other._count);
        }

        public readonly ulong ToFingerprint()
        {
            ulong result = Offset;
            Hash(ref result, _xor);
            Hash(ref result, _sum);
            Hash(ref result, _rotatedSum);
            Hash(ref result, _count);
            return result;
        }

        internal static void Hash(ref ulong result, ulong value)
        {
            result ^= value;
            result *= Prime;
        }

        private static ulong Mix(ulong value)
        {
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }
}
