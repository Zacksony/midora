namespace Midora.Domain;

/// <summary>
/// Structurally shared point index used by Direct channel-event and opaque-event
/// overlays. It keeps both tick and Stable-ID trees so frozen query snapshots do
/// not copy or sort the complete mutable overlay on every edit revision.
/// </summary>
internal sealed class PureMidiPointOverlayIndex<TValue>
    where TValue : struct
{
    private readonly Func<TValue, MidoraId> _getId;
    private readonly Func<TValue, long> _getTick;
    private readonly Func<TValue, long> _getOrder;
    private readonly TickNode? _tickRoot;
    private readonly IdNode? _idRoot;

    public PureMidiPointOverlayIndex(
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getTick,
        Func<TValue, long> getOrder)
        : this(getId, getTick, getOrder, tickRoot: null, idRoot: null)
    {
    }

    private PureMidiPointOverlayIndex(
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getTick,
        Func<TValue, long> getOrder,
        TickNode? tickRoot,
        IdNode? idRoot)
    {
        _getId = getId;
        _getTick = getTick;
        _getOrder = getOrder;
        _tickRoot = tickRoot;
        _idRoot = idRoot;
    }

    public int Count => _idRoot?.Count ?? 0;
    public long MaximumTick => _tickRoot?.MaximumTick ?? 0;

    internal PureMidiPointOverlayIndex<TValue> Create(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        TValue[] materialized = values as TValue[] ?? values.ToArray();
        return materialized.Length == 0
            ? new(_getId, _getTick, _getOrder)
            : Build(materialized);
    }

    public bool TryGetById(MidoraId id, out TValue value)
    {
        IdNode? current = _idRoot;
        while (current is not null)
        {
            int comparison = id.CompareTo(_getId(current.Value));
            if (comparison == 0)
            {
                value = current.Value;
                return true;
            }
            current = comparison < 0 ? current.Left : current.Right;
        }
        value = default!;
        return false;
    }

    public PureMidiPointOverlayIndex<TValue> ReplaceBatch(
        IReadOnlySet<MidoraId> dirtyIds,
        Func<MidoraId, TValue?> resolveCurrent)
    {
        if (dirtyIds.Count == 0) return this;
        List<TValue> replacements = new(dirtyIds.Count);
        foreach (MidoraId id in dirtyIds)
        {
            TValue? current = resolveCurrent(id);
            if (current.HasValue) replacements.Add(current.Value);
        }
        if (Count == 0)
            return replacements.Count == 0 ? this : Build([.. replacements]);
        if (dirtyIds.Count >= Count && AllIdsBelongTo(dirtyIds))
            return replacements.Count == 0
                ? new(_getId, _getTick, _getOrder)
                : Build([.. replacements]);

        TickNode? tick = _tickRoot;
        IdNode? ids = _idRoot;
        foreach (MidoraId id in dirtyIds)
        {
            if (!TryGetById(id, out TValue previous)) continue;
            tick = RemoveTick(tick, previous);
            ids = RemoveId(ids, id);
        }
        foreach (TValue value in replacements)
        {
            tick = InsertTick(tick, value);
            ids = InsertId(ids, value);
        }
        return new(_getId, _getTick, _getOrder, tick, ids);
    }

    public IEnumerable<TValue> Query(long startTick, long endTick)
    {
        if (endTick <= startTick || _tickRoot is null) return [];
        List<TValue> result = [];
        Query(_tickRoot, startTick, endTick, result);
        return result;
    }

    public IEnumerable<TValue> QueryAtTick(long tick) =>
        tick == long.MaxValue ? [] : Query(tick, tick + 1);

    /// <summary>Same immutable tick/order traversal, with O(tree height) scratch rather than an all-match list.</summary>
    public IEnumerable<TValue> EnumerateRange(long startTick, long endTick)
    {
        if (endTick <= startTick || _tickRoot is null) yield break;
        Stack<TickNode> stack = new(_tickRoot.Height);
        TickNode? current = _tickRoot;
        while (current is not null || stack.Count != 0)
        {
            while (current is not null)
            {
                if (current.MaximumTick < startTick || current.MinimumTick >= endTick) { current = null; break; }
                stack.Push(current); current = current.Left;
            }
            if (stack.Count == 0) break;
            current = stack.Pop();
            long tick = _getTick(current.Value);
            if (tick >= startTick && tick < endTick) yield return current.Value;
            current = current.Right;
        }
    }

    public IEnumerable<TValue> ResolveByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (MidoraId id in ids)
            if (TryGetById(id, out TValue value)) yield return value;
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
            if (!ids.Contains(_getId(current.Value))) return false;
            current = current.Right;
        }
        return true;
    }

    private PureMidiPointOverlayIndex<TValue> Build(TValue[] values)
    {
        TValue[] ticks = [.. values];
        Array.Sort(ticks, CompareTick);
        TValue[] ids = [.. values];
        Array.Sort(ids, (left, right) => _getId(left).CompareTo(_getId(right)));
        return new(
            _getId,
            _getTick,
            _getOrder,
            BuildTick(ticks, 0, ticks.Length),
            BuildId(ids, 0, ids.Length));
    }

    private TickNode? BuildTick(TValue[] values, int first, int count)
    {
        if (count == 0) return null;
        int leftCount = count / 2;
        int middle = first + leftCount;
        return new(
            values[middle],
            BuildTick(values, first, leftCount),
            BuildTick(values, middle + 1, count - leftCount - 1),
            _getTick);
    }

    private IdNode? BuildId(TValue[] values, int first, int count)
    {
        if (count == 0) return null;
        int leftCount = count / 2;
        int middle = first + leftCount;
        return new(
            values[middle],
            BuildId(values, first, leftCount),
            BuildId(values, middle + 1, count - leftCount - 1));
    }

    private void Query(TickNode? node, long startTick, long endTick, List<TValue> destination)
    {
        if (node is null || node.MaximumTick < startTick || node.MinimumTick >= endTick) return;
        Query(node.Left, startTick, endTick, destination);
        long tick = _getTick(node.Value);
        if (tick >= startTick && tick < endTick) destination.Add(node.Value);
        Query(node.Right, startTick, endTick, destination);
    }

    private TickNode InsertTick(TickNode? node, TValue value)
    {
        if (node is null) return new(value, null, null, _getTick);
        int comparison = CompareTick(value, node.Value);
        if (comparison == 0) throw new InvalidOperationException("A point overlay tick key is duplicated.");
        return Balance(comparison < 0
            ? new TickNode(node.Value, InsertTick(node.Left, value), node.Right, _getTick)
            : new TickNode(node.Value, node.Left, InsertTick(node.Right, value), _getTick));
    }

    private TickNode? RemoveTick(TickNode? node, TValue value)
    {
        if (node is null) return null;
        int comparison = CompareTick(value, node.Value);
        if (comparison < 0)
            return Balance(new TickNode(node.Value, RemoveTick(node.Left, value), node.Right, _getTick));
        if (comparison > 0)
            return Balance(new TickNode(node.Value, node.Left, RemoveTick(node.Right, value), _getTick));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        TickNode successor = Minimum(node.Right);
        return Balance(new TickNode(successor.Value, node.Left, RemoveMinimum(node.Right), _getTick));
    }

    private IdNode InsertId(IdNode? node, TValue value)
    {
        if (node is null) return new(value, null, null);
        int comparison = _getId(value).CompareTo(_getId(node.Value));
        if (comparison == 0) throw new InvalidOperationException("A point overlay Stable ID is duplicated.");
        return Balance(comparison < 0
            ? new IdNode(node.Value, InsertId(node.Left, value), node.Right)
            : new IdNode(node.Value, node.Left, InsertId(node.Right, value)));
    }

    private IdNode? RemoveId(IdNode? node, MidoraId id)
    {
        if (node is null) return null;
        int comparison = id.CompareTo(_getId(node.Value));
        if (comparison < 0) return Balance(new IdNode(node.Value, RemoveId(node.Left, id), node.Right));
        if (comparison > 0) return Balance(new IdNode(node.Value, node.Left, RemoveId(node.Right, id)));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        IdNode successor = Minimum(node.Right);
        return Balance(new IdNode(successor.Value, node.Left, RemoveMinimum(node.Right)));
    }

    private static TickNode Minimum(TickNode node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static IdNode Minimum(IdNode node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private TickNode? RemoveMinimum(TickNode node) => node.Left is null
        ? node.Right
        : Balance(new TickNode(node.Value, RemoveMinimum(node.Left), node.Right, _getTick));

    private static IdNode? RemoveMinimum(IdNode node) => node.Left is null
        ? node.Right
        : Balance(new IdNode(node.Value, RemoveMinimum(node.Left), node.Right));

    private TickNode Balance(TickNode node)
    {
        int balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node = new(node.Value, RotateLeft(node.Left), node.Right, _getTick);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node = new(node.Value, node.Left, RotateRight(node.Right), _getTick);
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

    private TickNode RotateLeft(TickNode node)
    {
        TickNode pivot = node.Right!;
        return new(pivot.Value, new(node.Value, node.Left, pivot.Left, _getTick), pivot.Right, _getTick);
    }

    private TickNode RotateRight(TickNode node)
    {
        TickNode pivot = node.Left!;
        return new(pivot.Value, pivot.Left, new(node.Value, pivot.Right, node.Right, _getTick), _getTick);
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

    private static int Height(TickNode? node) => node?.Height ?? 0;
    private static int Height(IdNode? node) => node?.Height ?? 0;

    private int CompareTick(TValue left, TValue right)
    {
        int tick = _getTick(left).CompareTo(_getTick(right));
        if (tick != 0) return tick;
        int order = _getOrder(left).CompareTo(_getOrder(right));
        return order != 0 ? order : _getId(left).CompareTo(_getId(right));
    }

    private sealed class TickNode
    {
        public TickNode(
            TValue value,
            TickNode? left,
            TickNode? right,
            Func<TValue, long> getTick)
        {
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + Math.Max(PureMidiPointOverlayIndex<TValue>.Height(left), PureMidiPointOverlayIndex<TValue>.Height(right));
            long tick = getTick(value);
            MinimumTick = Math.Min(tick, Math.Min(left?.MinimumTick ?? long.MaxValue, right?.MinimumTick ?? long.MaxValue));
            MaximumTick = Math.Max(tick, Math.Max(left?.MaximumTick ?? long.MinValue, right?.MaximumTick ?? long.MinValue));
        }

        public TValue Value { get; }
        public TickNode? Left { get; }
        public TickNode? Right { get; }
        public int Height { get; }
        public long MinimumTick { get; }
        public long MaximumTick { get; }
    }

    private sealed class IdNode
    {
        public IdNode(TValue value, IdNode? left, IdNode? right)
        {
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + Math.Max(PureMidiPointOverlayIndex<TValue>.Height(left), PureMidiPointOverlayIndex<TValue>.Height(right));
            Count = checked(1 + (left?.Count ?? 0) + (right?.Count ?? 0));
        }

        public TValue Value { get; }
        public IdNode? Left { get; }
        public IdNode? Right { get; }
        public int Height { get; }
        public int Count { get; }
    }
}
