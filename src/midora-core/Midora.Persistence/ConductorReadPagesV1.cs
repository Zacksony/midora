using System.Collections;
using Midora.Domain;

namespace Midora.Persistence;

/// <summary>Final source records, not a second DTO graph or editable facade directory.</summary>
internal sealed class ConductorReadPagesV1<T> : IImmutableTimelineValueSource<T> where T : class
{
    private readonly List<T[]> _pages = [];
    private T[] _tail = new T[4096];
    private int _tailCount;
    private bool _sorted = true, _finished;
    private T? _previous;
    public int Count { get; private set; }
    public int PageCapacity => 4096;
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return ReadPage(index / PageCapacity).Span[index % PageCapacity];
        }
    }

    public void Add(T value)
    {
        if (_finished) throw new InvalidOperationException("The Conductor source is already immutable.");
        if (_previous is not null && ConductorRecord<T>.Compare(_previous, value) > 0) _sorted = false;
        _previous = value;
        _tail[_tailCount++] = value;
        Count = checked(Count + 1);
        if (_tailCount != PageCapacity) return;
        _pages.Add(_tail);
        _tail = new T[PageCapacity];
        _tailCount = 0;
    }

    public ConductorReadPagesV1<T> Finish(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished) return this;
        if (_tailCount != 0) _pages.Add(_tail.AsSpan(0, _tailCount).ToArray());
        _tail = [];
        _previous = null;
        _finished = true;
        if (_sorted) return this;
        // Historical JSON accepted unsorted arrays. Sort each bounded page and
        // merge references; the source objects themselves are never duplicated.
        var comparer = Comparer<T>.Create(ConductorRecord<T>.Compare);
        var queue = new PriorityQueue<(int Page, int Offset), T>(comparer);
        for (int page = 0; page < _pages.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Sort(_pages[page], comparer);
            queue.Enqueue((page, 0), _pages[page][0]);
        }
        var ordered = new ConductorReadPagesV1<T>();
        while (queue.TryDequeue(out var cursor, out T? value))
        {
            if ((ordered.Count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(value);
            if (++cursor.Offset < _pages[cursor.Page].Length)
                queue.Enqueue(cursor, _pages[cursor.Page][cursor.Offset]);
        }
        return ordered.Finish(cancellationToken);
    }

    public ReadOnlyMemory<T> ReadPage(int pageIndex)
    {
        if (!_finished) throw new InvalidOperationException("The Conductor source has not been frozen.");
        if ((uint)pageIndex >= (uint)_pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return _pages[pageIndex];
    }
    public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
    {
        if (_finished && (uint)pageIndex < (uint)_pages.Count) { values = _pages[pageIndex]; return true; }
        values = default;
        return false;
    }
    public IEnumerator<T> GetEnumerator()
    {
        if (!_finished) throw new InvalidOperationException("The Conductor source has not been frozen.");
        foreach (T[] page in _pages)
            foreach (T value in page) yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
