using System.Collections;

namespace Midora.Domain;

/// <summary>
/// Stable-ID identified immutable Conductor records in tick/ID order. Readers
/// capture a shared persistent root; changing one point copies only its leaves.
/// </summary>
public sealed class ConductorCollection<T> : IList<T>, IReadOnlyList<T> where T : class
{
    private PagedTimelineObjectList<T, T> _store = CreateStore();
    private ConductorQuerySnapshot<T>? _snapshot;
    private bool _sharedStorage;
    public int Count => _store.Count;
    public bool IsReadOnly => false;
    public long Generation => _store.Generation;
    public int PageCount => _store.PageCount;

    public T this[int index]
    {
        get => ReadAt(index);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            T old = ReadAt(index);
            if (Equals(old, value)) return;
            // A structural replacement keeps the spatial index exact too;
            // Conductor step fingerprints never inspect arbitrary overlays.
            var replacement = new ConductorCollection<T>();
            replacement.AdoptSnapshot(CreateQuerySnapshot());
            using (replacement.BeginBatchChange())
            {
                replacement.RemoveAt(index);
                replacement.Add(value);
            }
            _store = replacement._store;
            _snapshot = null;
            _sharedStorage = true;
        }
    }

    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Count == 0 || ConductorRecord<T>.Compare(ReadAt(Count - 1), item) <= 0) _store.Add(item);
        else _store.Insert(LowerBound(item), item);
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using IDisposable batch = _store.BeginBatchChange();
        foreach (T item in items) Add(item);
    }

    // Insert preserves the formal sorted order. Callers may retain the index
    // for Undo bookkeeping, but list positions are never Conductor identity.
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        Add(item);
    }
    public void InsertRange(int index, IEnumerable<T> values)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        AddRange(values);
    }
    public void RemoveAt(int index) => _store.RemoveAt(index);
    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }
    public int RemoveAll(Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int removed = 0;
        using IDisposable batch = _store.BeginBatchChange();
        for (int index = Count - 1; index >= 0; index--)
            if (predicate(ReadAt(index))) { _store.RemoveAt(index); removed++; }
        return removed;
    }
    public void Clear() => _store.Clear();
    public bool Contains(T item) => IndexOf(item) >= 0;
    public int IndexOf(T item)
    {
        if (item is null) return -1;
        int index = LowerBound(item);
        for (; index < Count && ConductorRecord<T>.Compare(ReadAt(index), item) == 0; index++)
            if (Equals(ReadAt(index), item)) return index;
        return -1;
    }
    public int FindIndex(Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int index = 0;
        foreach (T item in this) { if (predicate(item)) return index; index++; }
        return -1;
    }
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length - Count) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (T item in this) array[arrayIndex++] = item;
    }
    public IEnumerator<T> GetEnumerator() => CreateQuerySnapshot().EnumerateAll().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IDisposable BeginBatchChange() => _store.BeginBatchChange();

    public ConductorQuerySnapshot<T> CaptureQuerySnapshot() => CreateQuerySnapshot();
    public ConductorQuerySnapshot<T> CreateQuerySnapshot()
    {
        var values = _store.CreateSnapshot();
        if (!_sharedStorage)
        {
            // Retire the import/build-only mutable page and membership tables.
            // Published roots share their immutable directories instead of
            // retaining a second mutable directory per Conductor event.
            var shared = CreateStore();
            shared.AdoptSnapshot(values, static value => value);
            _store = shared;
            _sharedStorage = true;
        }
        if (_snapshot is null || !ReferenceEquals(_snapshot.Values, values)) _snapshot = new(values);
        return _snapshot;
    }

    public void AdoptSnapshot(ConductorQuerySnapshot<T> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var replacement = CreateStore();
        replacement.AdoptSnapshot(snapshot.Values, static value => value);
        _store = replacement;
        _snapshot = snapshot;
        _sharedStorage = true;
    }

    public void AdoptSource(IImmutableTimelineValueSource<T> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        T? previous = null;
        int index = 0;
        foreach (T item in source)
        {
            if ((index++ & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (previous is not null && ConductorRecord<T>.Compare(previous, item) > 0)
                throw new ArgumentException("Conductor source records must be sorted by tick and Stable ID.", nameof(source));
            previous = item;
        }
        var replacement = CreateStore();
        replacement.AdoptSource(source, static value => value, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _store = replacement;
        _snapshot = null;
        _sharedStorage = true;
    }

    public void AdoptEditedSnapshot(ConductorQuerySnapshot<T> snapshot,
        IImmutableTimelineValueSource<TimelineValueEdit<T>> changes,
        IImmutableTimelineValueSource<T>? appended = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        if ((long)changes.Count + (appended?.Count ?? 0) > 256)
            throw new ArgumentException("Large Conductor edits must merge a bounded ordered source before adoption.", nameof(changes));
        // Prepare privately. Failure/cancellation leaves this collection intact.
        var replacement = new ConductorCollection<T>();
        replacement.AdoptSnapshot(snapshot);
        var edits = changes.ToArray();
        Array.Sort(edits, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        for (int index = 0; index < edits.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)edits[index].Ordinal >= (uint)snapshot.Count
                || (index > 0 && edits[index - 1].Ordinal == edits[index].Ordinal))
                throw new ArgumentException("Conductor edits must address unique existing ordinals.", nameof(changes));
        }
        using (replacement.BeginBatchChange())
        {
            for (int index = edits.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                replacement.RemoveAt(edits[index].Ordinal);
            }
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!edit.IsDeleted) replacement.Add(edit.Replacement);
            }
            if (appended is not null)
                foreach (T item in appended) { cancellationToken.ThrowIfCancellationRequested(); replacement.Add(item); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _store = replacement._store;
        _snapshot = null;
        _sharedStorage = true;
    }

    private int LowerBound(T item)
    {
        int first = 0, last = Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (ConductorRecord<T>.Compare(ReadAt(middle), item) < 0) first = middle + 1;
            else last = middle;
        }
        return first;
    }

    // Records are themselves immutable values, not mutable editing facades.
    // Reads must not register them in the generic facade weak-reference table:
    // a base root legitimately retains its values for its entire lifetime.
    private T ReadAt(int index) => _sharedStorage ? _store.CreateSnapshot().GetByOrdinal(index) : _store[index];

    private static PagedTimelineObjectList<T, T> CreateStore() => new(
        static value => value, ConductorRecord<T>.Id, ConductorRecord<T>.Tick,
        static value => ConductorRecord<T>.Tick(value) is long tick && tick < long.MaxValue ? tick + 1 : long.MaxValue,
        static _ => 0, ConductorRecord<T>.Fingerprint, static (_, _) => { });
}

/// <summary>Immutable, revision-bound, tick/ID ordered query facade.</summary>
public sealed class ConductorQuerySnapshot<T> : IReadOnlyList<T> where T : class
{
    private static long _nextRevision;
    private volatile bool _preparedOrdinalLookup;
    internal ConductorQuerySnapshot(PagedTimelineValueSnapshot<T> values)
    {
        Values = values;
        SourceRevision = Interlocked.Increment(ref _nextRevision);
    }
    internal PagedTimelineValueSnapshot<T> Values { get; }
    public int Count => Values.Count;
    public int PageCapacity => PersistentTimelineSequence<T>.LeafCapacity;
    public long Generation => Values.Generation;
    public long SourceRevision { get; }
    public ulong ContentFingerprint => Values.ContentFingerprint;
    public long MaximumTick => Values.MaximumStartTickMetadata;
    public bool UsesExternalStorage => Values.UsesExternalStorage;
    public T this[int index] => GetByOrdinal(index);
    public T GetByOrdinal(int index) => Values.GetByOrdinal(index);
    public T GetSortedByOrdinal(int index) => GetByOrdinal(index);
    public bool TryGetPageByOrdinal(int first, int count, out TimelineObjectPage<T> page) => Values.TryGetPageByOrdinal(first, count, out page);
    public bool TryGetSortedCached(int index, out T value)
    {
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try { value = GetByOrdinal(index); return true; }
        catch (TimelineValueReadPendingException) { value = default!; return false; }
    }
    public bool TryFindCachedOrdinalById(MidoraId id, out int ordinal)
    {
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try { return TryFindOrdinalById(id, out ordinal); }
        catch (TimelineValueReadPendingException) { ordinal = -1; return false; }
    }
    // Small edits use the searchable (tick, ID) key without building a full
    // directory. Dense selections may explicitly prepare the existing shared
    // root's bounded ID/ordinal index once, avoiding one binary search per row.
    public void PrepareOrdinalLookup(CancellationToken token = default) => token.ThrowIfCancellationRequested();
    public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        token.ThrowIfCancellationRequested();
        if (_preparedOrdinalLookup) return;
        Values.PrepareOrdinalLookup(builder, token);
        token.ThrowIfCancellationRequested();
        _preparedOrdinalLookup = true;
    }
    public bool TryFindOrdinalById(MidoraId id, out int ordinal)
    {
        ordinal = -1;
        if (_preparedOrdinalLookup) return Values.TryFindOrdinalById(id, out ordinal);
        if (!TryGetById(id, out T value)) return false;
        int first = 0, last = Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            int comparison = ConductorRecord<T>.Compare(GetByOrdinal(middle), value);
            if (comparison < 0) first = middle + 1;
            else if (comparison > 0) last = middle;
            else { ordinal = middle; return true; }
        }
        return false;
    }
    public bool TryGetById(MidoraId id, out T value) => Values.TryGetOrderedValueById(id, out value);
    public bool TryGetCachedById(MidoraId id, out T value)
    {
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try { return TryGetById(id, out value); }
        catch (TimelineValueReadPendingException) { value = default!; return false; }
    }
    public int LowerBoundTick(long tick)
    {
        int first = 0, last = Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (ConductorRecord<T>.Tick(GetByOrdinal(middle)) < tick) first = middle + 1;
            else last = middle;
        }
        return first;
    }
    public int FindOrdinalAtOrAfterTick(long tick) => LowerBoundTick(tick);
    public bool TryGetBeforeTick(long tick, out T value)
    {
        int index = LowerBoundTick(tick) - 1;
        value = index >= 0 ? GetByOrdinal(index) : default!;
        return index >= 0;
    }
    public bool TryGetAtOrBeforeTick(long tick, out T value)
    {
        int index = tick == long.MaxValue ? Count - 1 : LowerBoundTick(tick + 1) - 1;
        value = index >= 0 ? GetByOrdinal(index) : default!;
        return index >= 0;
    }
    public IEnumerable<T> QueryTickRange(long startTick, long endTick, CancellationToken cancellationToken = default)
    {
        if (endTick <= startTick) yield break;
        cancellationToken.ThrowIfCancellationRequested();
        for (int first = LowerBoundTick(startTick); first < Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetPageByOrdinal(first, PageCapacity, out var page)) yield break;
            foreach (T value in page.Values)
            {
                if (ConductorRecord<T>.Tick(value) >= endTick) yield break;
                yield return value;
            }
            first += page.Count;
        }
    }
    public bool TryQueryTickRangeCached(long startTick, long endTick, Action<T> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try { foreach (T value in QueryTickRange(startTick, endTick)) visitor(value); return true; }
        catch (TimelineValueReadPendingException) { return false; }
    }
    public ulong GetRangeFingerprint(long startTick, long endTick) => Values.GetOrderedMetadataFingerprint(startTick, endTick, false);
    public ulong GetStepRangeFingerprint(long startTick, long endTick) => Values.GetOrderedMetadataFingerprint(startTick, endTick, true);
    public void Prefetch(long startTick, long endTick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = TryGetBeforeTick(startTick, out _);
        foreach (T value in QueryTickRange(startTick, endTick, cancellationToken)) _ = value;
    }
    public IEnumerable<T> EnumerateAll() => Values.EnumerateAll();
    public IEnumerator<T> GetEnumerator() => EnumerateAll().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class ConductorRecord<T> where T : class
{
    public static MidoraId Id(T value) => value switch
    {
        TempoChange item => item.Id, TimeSignatureChange item => item.Id,
        KeySignatureChange item => item.Id, ProjectMarker item => item.Id,
        _ => throw new NotSupportedException("Unsupported Conductor record type.")
    };
    public static long Tick(T value) => value switch
    {
        TempoChange item => item.Tick, TimeSignatureChange item => item.Tick,
        KeySignatureChange item => item.Tick, ProjectMarker item => item.Tick,
        _ => throw new NotSupportedException("Unsupported Conductor record type.")
    };
    public static int Compare(T left, T right)
    {
        int comparison = Tick(left).CompareTo(Tick(right));
        return comparison == 0 ? Id(left).CompareTo(Id(right)) : comparison;
    }
    public static ulong Fingerprint(T value)
    {
        ulong result = PagedTimelineFingerprint.Offset;
        PagedTimelineFingerprint.Add(ref result, unchecked((ulong)Id(value).Value));
        PagedTimelineFingerprint.Add(ref result, unchecked((ulong)Tick(value)));
        switch (value)
        {
            case TempoChange tempo:
                Span<int> parts = stackalloc int[4];
                decimal.GetBits(tempo.BeatsPerMinute, parts);
                foreach (int part in parts) PagedTimelineFingerprint.Add(ref result, unchecked((ulong)part));
                break;
            case TimeSignatureChange signature:
                PagedTimelineFingerprint.Add(ref result, unchecked((ulong)signature.Numerator));
                PagedTimelineFingerprint.Add(ref result, unchecked((ulong)signature.Denominator));
                break;
            case KeySignatureChange key:
                PagedTimelineFingerprint.Add(ref result, unchecked((ulong)key.SharpsFlats));
                PagedTimelineFingerprint.Add(ref result, key.IsMinor ? 1UL : 0UL);
                break;
            case ProjectMarker marker:
                foreach (char character in marker.Name) PagedTimelineFingerprint.Add(ref result, character);
                break;
        }
        return result;
    }
}
