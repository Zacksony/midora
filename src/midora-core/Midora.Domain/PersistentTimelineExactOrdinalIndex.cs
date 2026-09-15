namespace Midora.Domain;

/// <summary>
/// Exact metadata-only ID lookup for formal pages with overlapping ID ranges.
/// Leaves contain at most 128 (Int64 ID, Int32 ordinal) pairs. Appends copy only
/// touched leaves and tree paths, not old musical values or a full ID table.
/// </summary>
internal sealed class PersistentTimelineExactOrdinalIndex
{
    private const int Capacity = 128;
    private readonly Node? _root;
    private PersistentTimelineExactOrdinalIndex(Node? root) => _root = root;
    internal int MaximumLookupComparisons => (_root?.Height ?? 0) + 10;

    public static PersistentTimelineExactOrdinalIndex Create(IEnumerable<TimelineIdOrdinal> addresses, int count,
        CancellationToken token = default)
    {
        // This is address metadata, never a second musical source. Preparation
        // uses 16 bytes/address temporarily; completed leaves use 12 bytes plus
        // bounded tree/array headers. Adopted external ID indexes bypass it.
        TimelineIdOrdinal[] ordered = new TimelineIdOrdinal[count];
        int index = 0;
        foreach (var address in addresses)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            ordered[index++] = address;
        }
        if (index != count) throw new InvalidOperationException("An ID address source changed while preparing its index.");
        token.ThrowIfCancellationRequested();
        if (!token.CanBeCanceled) Array.Sort(ordered, Compare);
        else
        {
            int comparisons = 0;
            try
            {
                Array.Sort(ordered, (left, right) =>
                {
                    if ((++comparisons & 255) == 0) token.ThrowIfCancellationRequested();
                    return Compare(left, right);
                });
            }
            catch (InvalidOperationException) when (token.IsCancellationRequested)
            { token.ThrowIfCancellationRequested(); throw; }
        }
        token.ThrowIfCancellationRequested();
        Node? root = Build(ordered, 0, ordered.Length, token);
        token.ThrowIfCancellationRequested();
        return new(root);
    }

    public int Find(MidoraId id) => Find(id, out _);

    internal int Find(MidoraId id, out int comparisons)
    {
        comparisons = 2;
        Node? node = _root;
        if (node is null || id.CompareTo(node.Minimum) < 0 || id.CompareTo(node.Maximum) > 0) return -1;
        while (node.Ids is null)
        {
            comparisons++;
            node = id.CompareTo(node.Left!.Maximum) <= 0 ? node.Left : node.Right!;
        }
        int low = 0, high = node.Ids.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            comparisons++;
            if (node.Ids[middle].CompareTo(id) < 0) low = middle + 1; else high = middle;
        }
        return low < node.Ids.Length && node.Ids[low] == id ? node.Ordinals![low] : -1;
    }

    public PersistentTimelineExactOrdinalIndex Append(IEnumerable<TimelineIdOrdinal> addresses)
    {
        TimelineIdOrdinal[] values = addresses.ToArray();
        if (values.Length == 0) return this;
        Array.Sort(values, Compare);
        if (_root is null || values[0].Id.CompareTo(_root.Maximum) > 0)
        {
            // The ordinary allocation path appends a monotonic ID suffix even
            // when the pre-existing formal sequence has been shuffled.
            Node? prefix = _root;
            int first = 0;
            if (prefix is not null)
            {
                Node tail = prefix;
                while (tail.Right is not null) tail = tail.Right;
                int take = Math.Min(Capacity - tail.Ids!.Length, values.Length);
                if (take != 0)
                {
                    TimelineIdOrdinal[] merged = new TimelineIdOrdinal[tail.Ids.Length + take];
                    for (int i = 0; i < tail.Ids.Length; i++) merged[i] = new(tail.Ids[i], tail.Ordinals![i]);
                    Array.Copy(values, 0, merged, tail.Ids.Length, take);
                    prefix = Join(RemoveRightmost(prefix), Leaf(merged, 0, merged.Length));
                    first = take;
                }
            }
            return new(Join(prefix, Build(values, first, values.Length - first)));
        }
        Node root = _root;
        foreach (var value in values) root = Insert(root, value);
        return new(root);
    }

    public IEnumerable<TimelineIdOrdinal> Enumerate()
    {
        if (_root is null) yield break;
        Stack<Node> stack = new();
        stack.Push(_root);
        while (stack.TryPop(out Node? node))
        {
            if (node.Ids is { } ids)
            {
                for (int i = 0; i < ids.Length; i++) yield return new(ids[i], node.Ordinals![i]);
            }
            else { stack.Push(node.Right!); stack.Push(node.Left!); }
        }
    }

    private static Node Insert(Node node, TimelineIdOrdinal value)
    {
        if (node.Ids is { } ids)
        {
            int index = Array.BinarySearch(ids, value.Id);
            if (index >= 0) return node; // Invalid duplicate source IDs remain validator-owned.
            index = ~index;
            TimelineIdOrdinal[] merged = new TimelineIdOrdinal[ids.Length + 1];
            for (int i = 0; i < ids.Length; i++) merged[i < index ? i : i + 1] = new(ids[i], node.Ordinals![i]);
            merged[index] = value;
            if (merged.Length <= Capacity) return Leaf(merged, 0, merged.Length);
            int half = merged.Length / 2;
            return Branch(Leaf(merged, 0, half), Leaf(merged, half, merged.Length - half));
        }
        return value.Id.CompareTo(node.Left!.Maximum) <= 0
            ? Balance(Branch(Insert(node.Left, value), node.Right!))
            : Balance(Branch(node.Left, Insert(node.Right!, value)));
    }

    private static Node? Build(TimelineIdOrdinal[] values, int first, int count,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (count == 0) return null;
        if (count <= Capacity) return Leaf(values, first, count);
        int leftCount = ((count + Capacity - 1) / Capacity / 2) * Capacity;
        return Branch(Build(values, first, leftCount, token)!, Build(values, first + leftCount, count - leftCount, token)!);
    }

    private static Node Leaf(TimelineIdOrdinal[] values, int first, int count)
    {
        MidoraId[] ids = new MidoraId[count];
        int[] ordinals = new int[count];
        for (int i = 0; i < count; i++) { ids[i] = values[first + i].Id; ordinals[i] = values[first + i].Ordinal; }
        return new(ids, ordinals, null, null);
    }

    private static Node Branch(Node left, Node right) => new(null, null, left, right);
    private static Node? RemoveRightmost(Node node)
    {
        if (node.Ids is not null) return null;
        Node? right = RemoveRightmost(node.Right!);
        return right is null ? node.Left : Balance(Branch(node.Left!, right));
    }
    private static Node? Join(Node? left, Node? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        if (left.Height > right.Height + 1) return Balance(Branch(left.Left!, Join(left.Right, right)!));
        if (right.Height > left.Height + 1) return Balance(Branch(Join(left, right.Left)!, right.Right!));
        return Branch(left, right);
    }
    private static Node Balance(Node node)
    {
        Node left = node.Left!, right = node.Right!;
        if (left.Height > right.Height + 1)
            return left.Left!.Height >= left.Right!.Height
                ? Branch(left.Left, Branch(left.Right, right))
                : Branch(Branch(left.Left, left.Right.Left!), Branch(left.Right.Right!, right));
        if (right.Height > left.Height + 1)
            return right.Right!.Height >= right.Left!.Height
                ? Branch(Branch(left, right.Left), right.Right)
                : Branch(Branch(left, right.Left.Left!), Branch(right.Left.Right!, right.Right));
        return node;
    }
    private static int Compare(TimelineIdOrdinal left, TimelineIdOrdinal right)
    {
        int comparison = left.Id.CompareTo(right.Id);
        return comparison != 0 ? comparison : left.Ordinal.CompareTo(right.Ordinal);
    }
    private sealed class Node(MidoraId[]? ids, int[]? ordinals, Node? left, Node? right)
    {
        public MidoraId[]? Ids { get; } = ids;
        public int[]? Ordinals { get; } = ordinals;
        public Node? Left { get; } = left;
        public Node? Right { get; } = right;
        public int Height { get; } = 1 + Math.Max(left?.Height ?? 0, right?.Height ?? 0);
        public MidoraId Minimum { get; } = ids is null ? left!.Minimum : ids[0];
        public MidoraId Maximum { get; } = ids is null ? right!.Maximum : ids[^1];
    }
}
