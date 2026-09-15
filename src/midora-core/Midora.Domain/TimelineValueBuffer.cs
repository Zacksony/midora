using System.Collections;

namespace Midora.Domain;

/// <summary>A small immutable leaf view which does not pin decoded spill pages.</summary>
internal sealed class TimelineValueBuffer<T> : IReadOnlyList<T>
{
    private readonly T[]? _array;
    private readonly IImmutableTimelineValueSource<T>? _source;
    private readonly int _first;

    public TimelineValueBuffer(T[] array) { _array = array; Count = array.Length; }
    public TimelineValueBuffer(IImmutableTimelineValueSource<T> source, int first, int count)
    { _source = source; _first = first; Count = count; }
    public int Count { get; }
    public int Length => Count;
    public bool UsesExternalStorage => _source is not null;
    internal bool TryGetSource(out IImmutableTimelineValueSource<T>? source, out int first)
    { source = _source; first = _first; return source is not null; }
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_array is not null) return _array[index];
            int absolute = checked(_first + index);
            if (!TimelineValueReadScope.IsCacheOnly) return _source![absolute];
            if (_source!.TryReadCachedValue(absolute, out T value)) return value;
            throw new TimelineValueReadPendingException();
        }
    }
    public T[] ToArray()
    {
        if (_array is not null) return _array;
        T[] values = new T[Count];
        int offset = 0;
        foreach (T value in this) values[offset++] = value;
        return values;
    }
    public IEnumerator<T> GetEnumerator()
    {
        if (_array is not null)
        {
            foreach (T value in _array) yield return value;
            yield break;
        }
        int read = 0;
        while (read < Count)
        {
            int absolute = checked(_first + read);
            int page = absolute / _source!.PageCapacity;
            int local = absolute % _source.PageCapacity;
            ReadOnlyMemory<T> memory = ReadSourcePage(page);
            int take = Math.Min(Count - read, memory.Length - local);
            if (take <= 0) throw new InvalidOperationException("An immutable timeline source returned an incomplete page.");
            for (int i = 0; i < take; i++) yield return memory.Span[local + i];
            read += take;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ReadOnlyMemory<T> ReadSourcePage(int page)
    {
        if (!TimelineValueReadScope.IsCacheOnly) return _source!.ReadPage(page);
        if (_source!.TryReadCachedPage(page, out var values)) return values;
        throw new TimelineValueReadPendingException();
    }
}
