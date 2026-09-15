using System.Collections;

namespace Midora.Domain;

/// <summary>A non-copying union used only for immutable source exclusions.</summary>
internal sealed class PureMidiUnionIdSet(IReadOnlySet<MidoraId> left, IReadOnlySet<MidoraId> right)
    : IReadOnlySet<MidoraId>, IPureMidiIdRangeSet, IPureMidiCachedOrdinalRangeSet
{
    public int Count => checked(left.Count + right.Count -
        (left.Count <= right.Count ? left.Count(right.Contains) : right.Count(left.Contains)));
    public bool Contains(MidoraId id) => left.Contains(id) || right.Contains(id);
    public bool MayContain(MidoraId minimumId, MidoraId maximumId) =>
        left is not IPureMidiIdRangeSet l || l.MayContain(minimumId, maximumId)
        || right is not IPureMidiIdRangeSet r || r.MayContain(minimumId, maximumId);
    public bool HasUnknownOrdinals =>
        left is not IPureMidiOrdinalRangeSet l || l.HasUnknownOrdinals
        || right is not IPureMidiOrdinalRangeSet r || r.HasUnknownOrdinals;
    public bool ContainsUnknownOrdinalId(MidoraId id) =>
        (left is IPureMidiOrdinalRangeSet l ? l.ContainsUnknownOrdinalId(id) : left.Contains(id))
        || (right is IPureMidiOrdinalRangeSet r ? r.ContainsUnknownOrdinalId(id) : right.Contains(id));
    public bool MayContainOrdinalRange(int firstOrdinal, int count) =>
        left is not IPureMidiOrdinalRangeSet l || l.MayContainOrdinalRange(firstOrdinal, count)
        || right is not IPureMidiOrdinalRangeSet r || r.MayContainOrdinalRange(firstOrdinal, count);
    public bool ContainsAllOrdinals(int firstOrdinal, int count) =>
        left is IPureMidiOrdinalRangeSet l && l.ContainsAllOrdinals(firstOrdinal, count)
        || right is IPureMidiOrdinalRangeSet r && r.ContainsAllOrdinals(firstOrdinal, count);
    public ArraySegment<int> GetOrdinalsInRange(int firstOrdinal, int count) => Merge(
        left is IPureMidiOrdinalRangeSet l ? l.GetOrdinalsInRange(firstOrdinal, count) : default,
        right is IPureMidiOrdinalRangeSet r ? r.GetOrdinalsInRange(firstOrdinal, count) : default);
    public bool TryGetOrdinalsInRange(int firstOrdinal, int count, out ArraySegment<int> ordinals)
    {
        ordinals = default;
        if (!TryRead(left, firstOrdinal, count, out var l) || !TryRead(right, firstOrdinal, count, out var r))
            return false;
        ordinals = Merge(l, r);
        return true;
    }
    private static bool TryRead(IReadOnlySet<MidoraId> ids, int first, int count, out ArraySegment<int> ordinals)
    {
        if (ids is IPureMidiCachedOrdinalRangeSet cached)
            return cached.TryGetOrdinalsInRange(first, count, out ordinals);
        ordinals = ids is IPureMidiOrdinalRangeSet known ? known.GetOrdinalsInRange(first, count) : default;
        return true;
    }
    private static ArraySegment<int> Merge(ArraySegment<int> left, ArraySegment<int> right)
    {
        if (left.Count == 0) return right;
        if (right.Count == 0) return left;
        int[] values = new int[checked(left.Count + right.Count)];
        int l = 0, r = 0, count = 0;
        while (l < left.Count || r < right.Count)
        {
            int value;
            if (r == right.Count || l < left.Count && left[l] < right[r]) value = left[l++];
            else if (l == left.Count || right[r] < left[l]) value = right[r++];
            else { value = left[l++]; r++; }
            values[count++] = value;
        }
        return new(values, 0, count);
    }
    public IEnumerator<MidoraId> GetEnumerator()
    {
        foreach (MidoraId id in left) yield return id;
        foreach (MidoraId id in right) if (!left.Contains(id)) yield return id;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsProperSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsProperSupersetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsSupersetOf(IEnumerable<MidoraId> other) => other.All(Contains);
    public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);
    public bool SetEquals(IEnumerable<MidoraId> other) => throw new NotSupportedException();
}
