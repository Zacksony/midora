using System.Collections.Immutable;
using System.Numerics;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Midora.Application;

internal readonly record struct BoundedEditValueHash(ulong A, ulong B, ulong C, ulong D)
{
    public static BoundedEditValueHash Compute(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]), BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }
    public static BoundedEditValueHash operator +(BoundedEditValueHash a, BoundedEditValueHash b) =>
        new(unchecked(a.A + b.A), unchecked(a.B + b.B), unchecked(a.C + b.C), unchecked(a.D + b.D));
    public override string ToString() => $"{A:x16}{B:x16}{C:x16}{D:x16}";
}

/// <summary>
/// Immutable sorted value pages. A patch rewrites only intersected pages; every
/// other leaf retains its original backing store. No result-sized record array
/// or per-record managed object is retained by a published index.
/// </summary>
internal sealed class BoundedDirectMidiIndex<T> where T : unmanaged
{
    internal sealed record Leaf(BoundedImmutableValueSource<T> Source, int First, int Count,
        T Minimum, T Maximum, long MinimumTick, long MaximumTick, int MinimumKey, int MaximumKey,
        ulong Fingerprint, int DeletedCount, ulong[] DeletedBits, BoundedEditValueHash StrongFingerprint,
        BoundedImmutableValueSource<int>? DeletedOrdinals = null, int DeletedFirst = 0,
        int MinimumDeletedOrdinal = 0, int MaximumDeletedOrdinal = 0)
    {
        public T this[int offset] => Source[checked(First + offset)];
    }

    private readonly ImmutableList<Leaf> _leaves;
    private readonly int[] _starts;
    private readonly int[] _deletions;
    private readonly IComparer<T> _comparer;
    private readonly Func<T, (long Start, long End, int Key)> _bounds;
    private readonly Func<T, ulong> _fingerprint;
    private readonly Func<T, bool> _isDeleted;
    private readonly Func<T, BoundedEditValueHash> _strongFingerprint;
    private readonly Func<T, int>? _ordinal;
    private readonly int[] _deletedLeafIndices;
    private readonly int[] _deletedLastLivePositions;
    private readonly DirectoryAdmission? _directoryAdmission;

    private BoundedDirectMidiIndex(ImmutableList<Leaf> leaves, IComparer<T> comparer,
        Func<T, (long Start, long End, int Key)> bounds, Func<T, ulong> fingerprint, Func<T, bool> isDeleted,
        Func<T, BoundedEditValueHash> strongFingerprint, Func<T, int>? ordinal,
        BoundedEditResources? resources = null)
    {
        _leaves = leaves;
        _comparer = comparer;
        _bounds = bounds;
        _fingerprint = fingerprint;
        _isDeleted = isDeleted;
        _strongFingerprint = strongFingerprint;
        _ordinal = ordinal;
        int deletedLeaves = ordinal is null ? 0 : leaves.Count(static leaf => leaf.DeletedCount != 0);
        // Charge all four directories together, including simultaneous indexes
        // prepared by this transaction. Published immutable root metadata is
        // not a temporary record buffer, so admission ends at publication.
        long directoryBytes = checked(128L + ((long)leaves.Count + 1) * 2 * sizeof(int)
            + (long)deletedLeaves * 2 * sizeof(int));
        if (resources is not null) _directoryAdmission = new(resources, directoryBytes);
        _starts = new int[leaves.Count + 1];
        _deletions = new int[leaves.Count + 1];
        for (int index = 0; index < leaves.Count; index++)
        {
            _starts[index + 1] = checked(_starts[index] + leaves[index].Count);
            _deletions[index + 1] = checked(_deletions[index] + leaves[index].DeletedCount);
            StrongFingerprint += leaves[index].StrongFingerprint;
        }
        _deletedLeafIndices = new int[deletedLeaves];
        _deletedLastLivePositions = new int[deletedLeaves];
        for (int index = 0, destination = 0; index < leaves.Count && destination < deletedLeaves; index++)
        {
            if (leaves[index].DeletedCount == 0) continue;
            _deletedLeafIndices[destination] = index;
            _deletedLastLivePositions[destination++] = checked(leaves[index].MaximumDeletedOrdinal - _deletions[index + 1] + 1);
        }
    }

    private sealed class DirectoryAdmission : IDisposable, IBoundedPublicationValueSource
    {
        private IDisposable? _reservation;
        public DirectoryAdmission(BoundedEditResources resources, long bytes)
        {
            _reservation = resources.ReserveWorking(bytes);
            resources.TrackProvider(this);
        }
        public void PrepareForPublication(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dispose();
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _reservation, null)?.Dispose();
            GC.SuppressFinalize(this);
        }
        ~DirectoryAdmission() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
    }

    public int Count => _starts[^1];
    public int DeletedCount => _deletions[^1];
    public BoundedEditValueHash StrongFingerprint { get; }
    public IReadOnlyList<Leaf> Leaves => _leaves;
    public static BoundedDirectMidiIndex<T> Empty(IComparer<T> comparer,
        Func<T, (long Start, long End, int Key)> bounds, Func<T, ulong> fingerprint,
        Func<T, bool>? isDeleted = null, Func<T, BoundedEditValueHash>? strongFingerprint = null,
        Func<T, int>? ordinal = null) =>
        new(ImmutableList<Leaf>.Empty, comparer, bounds, fingerprint, isDeleted ?? (static _ => false),
            strongFingerprint ?? (static _ => default), ordinal);

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int leaf = Array.BinarySearch(_starts, index);
            if (leaf < 0) leaf = ~leaf - 1;
            return _leaves[leaf][index - _starts[leaf]];
        }
    }

    public bool TryGet(T key, out T value)
    {
        int index = LowerBound(key);
        if (index < Count && _comparer.Compare(this[index], key) == 0)
        {
            value = this[index];
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetCached(T key, out bool found, out T value)
    {
        int low = 0, high = _leaves.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_comparer.Compare(_leaves[middle].Maximum, key) < 0) low = middle + 1;
            else high = middle;
        }
        found = false; value = default;
        if (low == _leaves.Count || _comparer.Compare(_leaves[low].Minimum, key) > 0) return true;
        Leaf leaf = _leaves[low];
        low = 0; high = leaf.Count;
        int loadedPage = -1;
        ReadOnlyMemory<T> page = default;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int absolute = leaf.First + middle;
            int pageIndex = absolute / leaf.Source.PageCapacity;
            if (loadedPage != pageIndex)
            {
                if (!leaf.Source.TryReadCachedPage(pageIndex, out page)) return false;
                loadedPage = pageIndex;
            }
            T candidate = page.Span[absolute % leaf.Source.PageCapacity];
            int order = _comparer.Compare(candidate, key);
            if (order == 0) { found = true; value = candidate; return true; }
            if (order < 0) low = middle + 1; else high = middle;
        }
        return true;
    }

    public bool TryQueryCached(long start, long end, int minKey, int maxKey, List<T> destination)
    {
        int initial = destination.Count;
        foreach (Leaf leaf in _leaves)
        {
            if (leaf.MaximumTick <= start || leaf.MinimumTick >= end
                || leaf.MaximumKey < minKey || leaf.MinimumKey > maxKey) continue;
            for (int index = 0; index < leaf.Count;)
            {
                int absolute = leaf.First + index;
                if (!leaf.Source.TryReadCachedPage(absolute / leaf.Source.PageCapacity, out var page))
                {
                    destination.RemoveRange(initial, destination.Count - initial);
                    return false;
                }
                int offset = absolute % leaf.Source.PageCapacity;
                int take = Math.Min(leaf.Count - index, page.Length - offset);
                foreach (T value in page.Span.Slice(offset, take))
                {
                    var bounds = _bounds(value);
                    if (bounds.Start < end && bounds.End > start && bounds.Key >= minKey && bounds.Key <= maxKey)
                        destination.Add(value);
                }
                index += take;
            }
        }
        return true;
    }

    public int LowerBound(T key)
    {
        int low = 0, high = _leaves.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_comparer.Compare(_leaves[middle].Maximum, key) < 0) low = middle + 1;
            else high = middle;
        }
        if (low == _leaves.Count) return Count;
        int leafIndex = low;
        Leaf leaf = _leaves[leafIndex];
        low = 0; high = leaf.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_comparer.Compare(leaf[middle], key) < 0) low = middle + 1;
            else high = middle;
        }
        return _starts[leafIndex] + low;
    }

    public int CountDeletedBefore(T key)
    {
        if (DeletedCount == 0) return 0;
        if (_ordinal is not null) return CountDeletedBeforeOrdinal(_ordinal(key), cachedOnly: false, out _);
        int ordinal = LowerBound(key);
        if (ordinal == Count) return DeletedCount;
        int leaf = Array.BinarySearch(_starts, ordinal);
        if (leaf < 0) leaf = ~leaf - 1;
        int result = _deletions[leaf];
        int local = ordinal - _starts[leaf];
        ulong[] bits = _leaves[leaf].DeletedBits;
        for (int word = 0; word < local / 64 && word < bits.Length; word++)
            result += BitOperations.PopCount(bits[word]);
        if (local % 64 != 0 && local / 64 < bits.Length)
            result += BitOperations.PopCount(bits[local / 64] & ((1UL << (local % 64)) - 1));
        return result;
    }

    public bool TryCountDeletedBeforeCached(T key, out int result)
    {
        result = 0;
        if (DeletedCount == 0) return true;
        if (_ordinal is not null)
        {
            result = CountDeletedBeforeOrdinal(_ordinal(key), cachedOnly: true, out bool ready);
            return ready;
        }
        int low = 0, high = _leaves.Count;
        while (low < high)
        { int middle = low + ((high - low) >> 1); if (_comparer.Compare(_leaves[middle].Maximum, key) < 0) low = middle + 1; else high = middle; }
        if (low == _leaves.Count) { result = DeletedCount; return true; }
        int leafIndex = low;
        var leaf = _leaves[leafIndex];
        low = 0; high = leaf.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1), absolute = leaf.First + middle;
            if (!leaf.Source.TryReadCachedPage(absolute / leaf.Source.PageCapacity, out var page)) return false;
            if (_comparer.Compare(page.Span[absolute % leaf.Source.PageCapacity], key) < 0) low = middle + 1; else high = middle;
        }
        result = _deletions[leafIndex];
        for (int word = 0; word < low / 64 && word < leaf.DeletedBits.Length; word++) result += BitOperations.PopCount(leaf.DeletedBits[word]);
        if (low % 64 != 0 && low / 64 < leaf.DeletedBits.Length)
            result += BitOperations.PopCount(leaf.DeletedBits[low / 64] & ((1UL << (low % 64)) - 1));
        return true;
    }

    private int CountDeletedBeforeOrdinal(int ordinal, bool cachedOnly, out bool ready)
    {
        ready = true;
        int low = 0, high = _deletedLeafIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_leaves[_deletedLeafIndices[middle]].MaximumDeletedOrdinal < ordinal) low = middle + 1;
            else high = middle;
        }
        if (low == _deletedLeafIndices.Length) return DeletedCount;
        int leafIndex = _deletedLeafIndices[low];
        Leaf leaf = _leaves[leafIndex];
        int prefix = _deletions[leafIndex];
        if (ordinal <= leaf.MinimumDeletedOrdinal) return prefix;
        low = 0; high = leaf.DeletedCount;
        int loadedPage = -1;
        ReadOnlyMemory<int> page = default;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (!TryReadDeletedOrdinal(leaf, middle, cachedOnly, ref loadedPage, ref page, out int value))
            { ready = false; return 0; }
            if (value < ordinal) low = middle + 1; else high = middle;
        }
        return checked(prefix + low);
    }

    /// <summary>
    /// Selects an undeleted physical ordinal directly. For deletion d[j],
    /// d[j]-j is its position among survivors. These values are nondecreasing,
    /// so one directory search and one small int-page search replace nested
    /// binary searches of full Note records. The int pages share the global
    /// bounded read cache and the immutable history/source lifetime.
    /// </summary>
    public int SelectUndeletedOrdinal(int liveOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveOrdinal);
        if (DeletedCount == 0) return liveOrdinal;
        if (_ordinal is null) throw new InvalidOperationException("An ordinal selector is required.");
        int low = 0, high = _deletedLastLivePositions.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_deletedLastLivePositions[middle] <= liveOrdinal) low = middle + 1; else high = middle;
        }
        if (low == _deletedLeafIndices.Length) return checked(liveOrdinal + DeletedCount);
        int leafIndex = _deletedLeafIndices[low];
        Leaf leaf = _leaves[leafIndex];
        int prefix = _deletions[leafIndex];
        low = 0; high = leaf.DeletedCount;
        int loadedPage = -1;
        ReadOnlyMemory<int> page = default;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            TryReadDeletedOrdinal(leaf, middle, cachedOnly: false, ref loadedPage, ref page, out int value);
            if (value - prefix - middle <= liveOrdinal) low = middle + 1; else high = middle;
        }
        return checked(liveOrdinal + prefix + low);
    }

    private static bool TryReadDeletedOrdinal(Leaf leaf, int index, bool cachedOnly,
        ref int loadedPage, ref ReadOnlyMemory<int> page, out int value)
    {
        var source = leaf.DeletedOrdinals
            ?? throw new InvalidOperationException("A deleted ordinal leaf has no rank/select pages.");
        int absolute = checked(leaf.DeletedFirst + index);
        int pageIndex = absolute / source.PageCapacity;
        if (loadedPage != pageIndex)
        {
            if (cachedOnly)
            {
                if (!source.TryReadCachedPage(pageIndex, out page)) { value = 0; return false; }
            }
            else page = source.ReadPage(pageIndex);
            loadedPage = pageIndex;
        }
        value = page.Span[absolute % source.PageCapacity];
        return true;
    }

    public bool TryContainsAllOrdinalsCached(int firstOrdinal, int count, out bool all)
    {
        all = false;
        if (!TryOrdinalLowerBound(firstOrdinal, cachedOnly: true, out int first)
            || !TryOrdinalLowerBound(checked(firstOrdinal + count), cachedOnly: true, out int end)) return false;
        all = count != 0 && end - first == count;
        return true;
    }

    public bool TryGetOrdinalsInRange(int firstOrdinal, int count, bool cachedOnly, out ArraySegment<int> result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        result = default;
        if (!TryOrdinalLowerBound(firstOrdinal, cachedOnly, out int first)
            || !TryOrdinalLowerBound(checked(firstOrdinal + count), cachedOnly, out int end)) return false;
        if (first == end) return true;
        int[] ordinals = new int[end - first];
        int target = 0;
        int leafIndex = Array.BinarySearch(_starts, first);
        if (leafIndex < 0) leafIndex = ~leafIndex - 1;
        while (first < end)
        {
            Leaf leaf = _leaves[leafIndex];
            int absolute = checked(leaf.First + first - _starts[leafIndex]);
            ReadOnlyMemory<T> page;
            if (cachedOnly)
            {
                if (!leaf.Source.TryReadCachedPage(absolute / leaf.Source.PageCapacity, out page)) return false;
            }
            else page = leaf.Source.ReadPage(absolute / leaf.Source.PageCapacity);
            int offset = absolute % leaf.Source.PageCapacity;
            int take = Math.Min(end - first, Math.Min(_starts[leafIndex + 1] - first, page.Length - offset));
            foreach (T value in page.Span.Slice(offset, take)) ordinals[target++] = _ordinal!(value);
            first += take;
            if (first == _starts[leafIndex + 1]) leafIndex++;
        }
        result = new(ordinals);
        return true;
    }

    private bool TryOrdinalLowerBound(int ordinal, bool cachedOnly, out int result)
    {
        if (_ordinal is null) throw new InvalidOperationException("An ordinal selector is required.");
        int low = 0, high = _leaves.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_ordinal(_leaves[middle].Maximum) < ordinal) low = middle + 1; else high = middle;
        }
        if (low == _leaves.Count) { result = Count; return true; }
        int leafIndex = low;
        Leaf leaf = _leaves[leafIndex];
        int minimum = _ordinal(leaf.Minimum);
        if (ordinal <= minimum) { result = _starts[leafIndex]; return true; }
        if ((long)_ordinal(leaf.Maximum) - minimum + 1 == leaf.Count)
        { result = checked(_starts[leafIndex] + ordinal - minimum); return true; }
        low = 0; high = leaf.Count;
        int loadedPage = -1;
        ReadOnlyMemory<T> page = default;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1), absolute = leaf.First + middle;
            int pageIndex = absolute / leaf.Source.PageCapacity;
            if (loadedPage != pageIndex)
            {
                if (cachedOnly)
                {
                    if (!leaf.Source.TryReadCachedPage(pageIndex, out page)) { result = 0; return false; }
                }
                else page = leaf.Source.ReadPage(pageIndex);
                loadedPage = pageIndex;
            }
            if (_ordinal(page.Span[absolute % leaf.Source.PageCapacity]) < ordinal) low = middle + 1; else high = middle;
        }
        result = checked(_starts[leafIndex] + low);
        return true;
    }

    public IEnumerable<T> Enumerate(CancellationToken token = default)
    {
        foreach (Leaf leaf in _leaves)
            for (int index = 0; index < leaf.Count;)
            {
                int absolute = leaf.First + index;
                ReadOnlyMemory<T> page = leaf.Source.ReadPage(absolute / leaf.Source.PageCapacity);
                int offset = absolute % leaf.Source.PageCapacity;
                int take = Math.Min(leaf.Count - index, page.Length - offset);
                for (int local = 0; local < take; local++)
                {
                    if ((local & 255) == 0) token.ThrowIfCancellationRequested();
                    yield return page.Span[offset + local];
                }
                index += take;
            }
    }

    /// <summary>Exact half-open range in this index's sort order. Unlike the
    /// spatial bounds query, this seeks inside a leaf instead of rescanning its
    /// whole page for each requested point.</summary>
    public IEnumerable<T> QueryKeys(T inclusive, T exclusive, CancellationToken token = default)
    {
        int first = LowerBound(inclusive), end = LowerBound(exclusive);
        if (first >= end) yield break;
        int leafIndex = Array.BinarySearch(_starts, first);
        if (leafIndex < 0) leafIndex = ~leafIndex - 1;
        while (first < end)
        {
            token.ThrowIfCancellationRequested();
            Leaf leaf = _leaves[leafIndex];
            int absolute = leaf.First + first - _starts[leafIndex];
            var page = leaf.Source.ReadPage(absolute / leaf.Source.PageCapacity);
            int offset = absolute % leaf.Source.PageCapacity;
            int take = Math.Min(end - first, Math.Min(_starts[leafIndex + 1] - first, page.Length - offset));
            for (int i = 0; i < take; i++) { if ((i & 255) == 0) token.ThrowIfCancellationRequested(); yield return page.Span[offset + i]; }
            first += take;
            if (first == _starts[leafIndex + 1]) leafIndex++;
        }
    }

    public IEnumerable<T> Query(long start, long end, int minKey = 0, int maxKey = 127,
        CancellationToken cancellationToken = default)
    {
        foreach (Leaf leaf in _leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leaf.MaximumTick <= start || leaf.MinimumTick >= end
                || leaf.MaximumKey < minKey || leaf.MinimumKey > maxKey) continue;
            for (int index = 0; index < leaf.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int absolute = leaf.First + index;
                ReadOnlyMemory<T> page = leaf.Source.ReadPage(absolute / leaf.Source.PageCapacity);
                int offset = absolute % leaf.Source.PageCapacity;
                int take = Math.Min(leaf.Count - index, page.Length - offset);
                for (int local = 0; local < take; local++)
                {
                    if ((local & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    T value = page.Span[offset + local];
                    var bounds = _bounds(value);
                    if (bounds.Start < end && bounds.End > start && bounds.Key >= minKey && bounds.Key <= maxKey)
                        yield return value;
                }
                index += take;
            }
        }
    }

    public ulong GetRangeFingerprint(long start, long end, int minKey = 0, int maxKey = 127)
    {
        ulong result = 0;
        foreach (Leaf leaf in _leaves)
            if (leaf.MaximumTick > start && leaf.MinimumTick < end
                && leaf.MaximumKey >= minKey && leaf.MinimumKey <= maxKey)
                result = unchecked(result + leaf.Fingerprint);
        return result;
    }

    /// <param name="patch">Strictly increasing keys, one final value per key.</param>
    public BoundedDirectMidiIndex<T> ApplyPatch(IEnumerable<T> patch, BoundedEditResources resources,
        CancellationToken token, Func<T, bool>? omit = null)
    {
        using IEnumerator<T> changes = patch.GetEnumerator();
        bool has = changes.MoveNext();
        if (!has) return this;
        var leaves = ImmutableList.CreateBuilder<Leaf>();
        using var output = new PendingPages(this, resources, leaves, token);
        T previous = default;
        bool sawPrevious = false;
        void Advance()
        {
            T consumed = changes.Current;
            if (sawPrevious && _comparer.Compare(previous, consumed) >= 0)
                throw new ArgumentException("Direct MIDI index patches must have distinct ordered keys.", nameof(patch));
            previous = consumed; sawPrevious = true;
            has = changes.MoveNext();
        }
        foreach (Leaf leaf in _leaves)
        {
            token.ThrowIfCancellationRequested();
            while (has && _comparer.Compare(changes.Current, leaf.Minimum) < 0)
            {
                if (omit?.Invoke(changes.Current) != true) output.Add(changes.Current);
                Advance();
            }
            if (!has || _comparer.Compare(changes.Current, leaf.Maximum) > 0)
            {
                output.FlushLeaf();
                leaves.Add(leaf);
                continue;
            }
            for (int index = 0; index < leaf.Count; index++)
            {
                T original = leaf[index];
                while (has && _comparer.Compare(changes.Current, original) < 0)
                {
                    if (omit?.Invoke(changes.Current) != true) output.Add(changes.Current);
                    Advance();
                }
                if (has && _comparer.Compare(changes.Current, original) == 0)
                {
                    if (omit?.Invoke(changes.Current) != true) output.Add(changes.Current);
                    Advance();
                }
                else output.Add(original);
            }
        }
        while (has)
        {
            if (omit?.Invoke(changes.Current) != true) output.Add(changes.Current);
            Advance();
        }
        output.Complete();
        return new(leaves.ToImmutable(), _comparer, _bounds, _fingerprint, _isDeleted, _strongFingerprint, _ordinal, resources);
    }

    private sealed class PendingPages : IDisposable
    {
        private readonly BoundedDirectMidiIndex<T> _owner;
        private readonly BoundedEditRecordStore<T> _store;
        private readonly BoundedEditRecordStore<int>? _deletedStore;
        private readonly ImmutableList<Leaf>.Builder _destination;
        private readonly List<(int Position, int First, int Count, T Minimum, T Maximum,
            long Start, long End, int MinKey, int MaxKey, ulong Fingerprint, int Deleted, ulong[] Bits,
            BoundedEditValueHash Strong, int DeletedFirst, int MinimumDeleted, int MaximumDeleted)> _pending = [];
        private readonly CancellationToken _token;
        private int _first, _count, _deleted;
        private int _deletedFirst, _minimumDeleted, _maximumDeleted;
        private long _start = long.MaxValue, _end;
        private int _minKey = 127, _maxKey;
        private ulong _hash;
        private BoundedEditValueHash _strong;
        private ulong[]? _bits;
        private T _minimum, _maximum;
        private bool _complete;
        public PendingPages(BoundedDirectMidiIndex<T> owner, BoundedEditResources resources,
            ImmutableList<Leaf>.Builder destination, CancellationToken token)
        {
            _owner = owner; _store = new(resources); _destination = destination; _token = token;
            if (owner._ordinal is not null) _deletedStore = new(resources);
        }
        public void Add(T value)
        {
            if (_count == 0) { _first = _store.Count; _minimum = value; }
            _maximum = value;
            _store.Add(value, _token);
            var bounds = _owner._bounds(value);
            _start = Math.Min(_start, bounds.Start); _end = Math.Max(_end, bounds.End);
            _minKey = Math.Min(_minKey, bounds.Key); _maxKey = Math.Max(_maxKey, bounds.Key);
            _hash = unchecked(_hash + _owner._fingerprint(value));
            _strong += _owner._strongFingerprint(value);
            if (_owner._isDeleted(value))
            {
                if (_deletedStore is not null)
                {
                    int ordinal = _owner._ordinal!(value);
                    if (_deleted == 0) { _deletedFirst = _deletedStore.Count; _minimumDeleted = ordinal; }
                    _maximumDeleted = ordinal;
                    _deletedStore.Add(ordinal, _token);
                }
                _deleted++;
                (_bits ??= new ulong[(_store.PageCapacity + 63) / 64])[_count / 64] |= 1UL << (_count % 64);
            }
            if (++_count == _store.PageCapacity) FlushLeaf();
        }
        public void FlushLeaf()
        {
            if (_count == 0) return;
            _pending.Add((_destination.Count, _first, _count, _minimum, _maximum,
                _start, _end, _minKey, _maxKey, _hash, _deleted, _bits ?? [], _strong,
                _deletedFirst, _minimumDeleted, _maximumDeleted));
            _destination.Add(null!);
            _count = _deleted = 0; _start = long.MaxValue; _end = 0;
            _minKey = 127; _maxKey = 0; _hash = 0;
            _bits = null;
            _strong = default;
        }
        public void Complete()
        {
            FlushLeaf();
            _store.Seal();
            _store.SpillResidentPages(_token);
            var source = new BoundedImmutableValueSource<T>(_store);
            BoundedImmutableValueSource<int>? deletedSource = null;
            if (_deletedStore is not null)
            {
                _deletedStore.Seal();
                if (_deletedStore.Count != 0)
                {
                    _deletedStore.SpillResidentPages(_token);
                    deletedSource = new(_deletedStore);
                }
                else _deletedStore.Dispose();
            }
            foreach (var leaf in _pending)
                _destination[leaf.Position] = new(source, leaf.First, leaf.Count, leaf.Minimum,
                    leaf.Maximum, leaf.Start, leaf.End, leaf.MinKey, leaf.MaxKey, leaf.Fingerprint,
                    leaf.Deleted, leaf.Bits, leaf.Strong, leaf.Deleted == 0 ? null : deletedSource,
                    leaf.DeletedFirst, leaf.MinimumDeleted, leaf.MaximumDeleted);
            _complete = true;
        }
        public void Dispose() { if (!_complete) { _store.Dispose(); _deletedStore?.Dispose(); } }
    }
}
