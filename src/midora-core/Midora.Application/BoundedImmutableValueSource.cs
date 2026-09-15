using System.Collections;
using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Immutable record-page adapter for published roots. The process-wide read
/// cache is bounded independently from the command's preparation budget.
/// </summary>
internal class BoundedImmutableValueSource<T> : IImmutableTimelineValueSource<T>, IBoundedPublicationValueSource, IDisposable
    where T : unmanaged
{
    private readonly BoundedEditRecordStore<T> _store;
    private readonly long _cacheIdentity = BoundedEditPageCache.AllocateIdentity();
    private bool _disposed;

    public BoundedImmutableValueSource(BoundedEditRecordStore<T> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!store.IsSealed) throw new ArgumentException("The immutable source must be sealed.", nameof(store));
        BulkEditPreparationContext.Current?.Resources.TrackProvider(this);
        BulkEditPreparationContext.Current?.Project?.RegisterRuntimeResource(new WeakLifetime(this));
    }

    public int Count => _store.Count;
    public int PageCapacity => _store.PageCapacity;
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return ReadPage(index / PageCapacity).Span[index % PageCapacity];
        }
    }

    public ReadOnlyMemory<T> ReadPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)pageIndex >= (uint)_store.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (BoundedEditPageCache.TryGet<T>(_cacheIdentity, pageIndex, out var cached)) return cached;
        TimelineValueReadScope.ThrowIfReadWouldBlock();
        return ReadUncachedPage(pageIndex);
    }

    // Keep the loader closure out of the cached scalar lookup path. Binary ID
    // lookups can otherwise allocate a closure for every already-resident page.
    private ReadOnlyMemory<T> ReadUncachedPage(int pageIndex)
    {
        return BoundedEditPageCache.Get<T>(_cacheIdentity, pageIndex, () =>
        {
            T[] values = new T[_store.GetPageRecordCount(pageIndex)];
            _store.CopyPage(pageIndex, values);
            return values;
        });
    }

    public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BoundedEditPageCache.TryGet(_cacheIdentity, pageIndex, out values);
    }

    public void Prefetch(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ReadPage(pageIndex);
    }

    void IBoundedPublicationValueSource.PrepareForPublication(CancellationToken cancellationToken)
    {
        // Temporary planners are often disposed before the publication gate.
        // Live roots/history retain only spill pages plus the shared read cache,
        // never a separate resident budget for every completed command.
        if (!_disposed && !_store.IsDisposed) _store.SpillResidentPages(cancellationToken);
    }

    public IEnumerator<T> GetEnumerator() => TimelineValueReadScope.IsCacheOnly
        ? EnumerateCached().GetEnumerator() : _store.GetEnumerator();

    private IEnumerable<T> EnumerateCached()
    {
        for (int page = 0; page < _store.PageCount; page++)
        {
            ReadOnlyMemory<T> values = ReadPage(page);
            for (int i = 0; i < values.Length; i++) yield return values.Span[i];
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BoundedEditPageCache.Remove(_cacheIdentity);
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    ~BoundedImmutableValueSource()
    {
        // A root may outlive its history entry (render/compiler snapshots).
        // Last-reference cleanup never escapes the finalizer thread. Owned
        // directory pruning also recovers interrupted cleanup on next startup.
        try { Dispose(); } catch { }
    }

    private sealed class WeakLifetime(BoundedImmutableValueSource<T> source) : IDisposable
    {
        private readonly WeakReference<BoundedImmutableValueSource<T>> _source = new(source);
        public void Dispose()
        {
            if (_source.TryGetTarget(out var value)) value.Dispose();
        }
    }
}

internal static class BoundedEditPageCache
{
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly Dictionary<(long Source, int Page), LinkedListNode<Entry>> Entries = [];
    private static readonly LinkedList<Entry> Recent = [];
    private static long _identity;
    private static long _bytes;
    public static long AllocateIdentity() => Interlocked.Increment(ref _identity);
    internal static long ResidentBytes { get { lock (Sync) return _bytes; } }

    public static bool TryGet<T>(long source, int page, out ReadOnlyMemory<T> values) where T : unmanaged
    {
        lock (Sync)
        {
            if (Entries.TryGetValue((source, page), out var found))
            {
                Recent.Remove(found);
                Recent.AddFirst(found);
                values = (T[])found.Value.Values;
                return true;
            }
        }
        values = default;
        return false;
    }

    public static T[] Get<T>(long source, int page, Func<T[]> read) where T : unmanaged
    {
        lock (Sync)
        {
            if (Entries.TryGetValue((source, page), out var found))
            {
                Recent.Remove(found);
                Recent.AddFirst(found);
                return (T[])found.Value.Values;
            }
        }
        // Never do file I/O while holding the cache lock.
        T[] values = read();
        long bytes = checked((long)values.Length * Unsafe.SizeOf<T>());
        lock (Sync)
        {
            if (Entries.TryGetValue((source, page), out var raced)) return (T[])raced.Value.Values;
            if (bytes > MaximumBytes) return values;
            while (_bytes + bytes > MaximumBytes && Recent.Last is { } last) Remove(last);
            var added = Recent.AddFirst(new Entry(source, page, values, bytes));
            Entries.Add((source, page), added);
            _bytes += bytes;
            return values;
        }
    }

    public static void Remove(long source)
    {
        lock (Sync)
        {
            for (var node = Recent.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Source == source) Remove(node);
                node = next;
            }
        }
    }
    private static void Remove(LinkedListNode<Entry> node)
    {
        Entries.Remove((node.Value.Source, node.Value.Page));
        Recent.Remove(node);
        _bytes -= node.Value.Bytes;
    }
    private sealed record Entry(long Source, int Page, object Values, long Bytes);
}
