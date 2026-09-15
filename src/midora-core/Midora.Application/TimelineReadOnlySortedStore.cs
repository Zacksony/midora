using System.Buffers;

namespace Midora.Application;

/// <summary>
/// Cancellable, process-local scalar directory for an explicitly opened owner-data
/// view. Reuses edit storage's bounded working tier and owned temporary files;
/// neither the directory nor its sort order is Project data.
/// </summary>
public sealed class TimelineReadOnlySortedStore<T> : IDisposable where T : unmanaged
{
    private readonly BoundedEditRecordStore<T> _store;
    private TimelineReadOnlySortedStore(BoundedEditRecordStore<T> store) => _store = store;
    public int Count => _store.Count;
    public long ResidentBytes => _store.ResidentBytes;
    public long SpillBytes => _store.SpillBytes;
    public static TimelineReadOnlySortedStore<T> Create(IEnumerable<T> values, IComparer<T> comparer,
        CancellationToken cancellationToken = default)
    {
        BoundedEditResources resources = new();
        var store = BoundedEditSort.Sort(values, comparer, resources, cancellationToken);
        try
        {
            store.SpillResidentPages(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(store);
        }
        catch { store.Dispose(); throw; }
    }

    public T[] ReadRange(int first, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (first > Count || count > Count - first) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > 4096) throw new ArgumentOutOfRangeException(nameof(count), "Read the directory in bounded pages.");
        T[] result = new T[count];
        int copied = 0;
        foreach (T value in EnumerateRange(first, count, cancellationToken)) result[copied++] = value;
        return result;
    }

    public IEnumerable<T> EnumerateRange(int first, int count, CancellationToken cancellationToken = default)
    {
        if (first < 0 || count < 0 || first > Count || count > Count - first) throw new ArgumentOutOfRangeException(nameof(count));
        T[] buffer = ArrayPool<T>.Shared.Rent(_store.PageCapacity);
        try
        {
            int copied = 0;
            while (copied < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ordinal = first + copied;
                int page = ordinal / _store.PageCapacity;
                int offset = ordinal % _store.PageCapacity;
                _store.CopyPage(page, buffer);
                int length = Math.Min(count - copied, _store.GetPageRecordCount(page) - offset);
                for (int i = 0; i < length; i++)
                {
                    if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    yield return buffer[offset + i];
                }
                copied += length;
            }
        }
        finally { ArrayPool<T>.Shared.Return(buffer); }
    }

    public void Dispose() => _store.Dispose();
}
