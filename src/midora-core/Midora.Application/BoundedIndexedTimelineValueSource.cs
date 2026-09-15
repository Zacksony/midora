using System.Collections;
using Midora.Domain;

namespace Midora.Application;

internal sealed class BoundedIndexedTimelineValueSource<T> : IIndexedImmutableTimelineValueSource<T>, IImmutableTimelineOrdinalIndexBuilder, IDisposable where T : unmanaged
{
    private readonly BoundedImmutableValueSource<T> _values;
    private readonly BoundedImmutableValueSource<IdOrdinal> _index;
    private readonly record struct IdOrdinal(MidoraId Id, int Ordinal);

    public BoundedIndexedTimelineValueSource(BoundedImmutableValueSource<T> values, Func<T, MidoraId> id,
        BoundedEditResources resources, CancellationToken token)
    {
        _values = values;
        var index = BoundedEditSort.Sort(values.Select((v, ordinal) => new IdOrdinal(id(v), ordinal)),
            Comparer<IdOrdinal>.Create((a, b) => a.Id.CompareTo(b.Id)), resources, token);
        try { index.SpillResidentPages(token); _index = new(index); }
        catch { index.Dispose(); throw; }
    }
    public int Count => _values.Count;
    public int PageCapacity => _values.PageCapacity;
    public T this[int index] => _values[index];
    public ReadOnlyMemory<T> ReadPage(int pageIndex) => _values.ReadPage(pageIndex);
    public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values) => _values.TryReadCachedPage(pageIndex, out values);
    public IImmutableTimelineIdIndex DetachedIdIndex => new DetachedIndex(_index);
    public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token) =>
        BoundedTimelineOrdinalIndex.Create(values, token);
    public bool TryFindOrdinalById(MidoraId id, out int ordinal) => Find(_index, id, out ordinal);
    private static bool Find(BoundedImmutableValueSource<IdOrdinal> index, MidoraId id, out int ordinal)
    {
        int low = 0, high = index.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var value = index[middle];
            int comparison = value.Id.CompareTo(id);
            if (comparison < 0) low = middle + 1;
            else if (comparison > 0) high = middle - 1;
            else { ordinal = value.Ordinal; return true; }
        }
        ordinal = -1;
        return false;
    }
    private sealed class DetachedIndex(BoundedImmutableValueSource<IdOrdinal> index) : IImmutableTimelineIdIndex
    {
        public bool TryFindOrdinalById(MidoraId id, out int ordinal) => Find(index, id, out ordinal);
    }
    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { _values.Dispose(); _index.Dispose(); }
}
