using System.Collections.Immutable;

namespace Midora.Domain;

internal sealed class PureMidiFormalTimelineObjectSource<TValue> : ITimelineObjectSource<TValue>
    where TValue : struct
{
    private const int DefaultPageCapacity = 4096;
    private readonly int _sourceCount;
    private readonly Func<int, TValue> _getSourceValue;
    private readonly Func<MidoraId, int> _findSourceIndex;
    private readonly ImmutableHashSet<MidoraId> _removedSourceIds;
    private readonly ImmutableDictionary<MidoraId, TValue> _replacements;
    private readonly PersistentFormalValueSequence<TValue> _added;
    private readonly ImmutableDictionary<MidoraId, int>? _addedIndices;
    private readonly Func<TValue, MidoraId> _getId;
    private readonly Func<TValue, long> _getStartTick;
    private readonly Func<TimelineObjectRangeQuery, IEnumerable<TValue>> _query;
    private readonly Action<TimelineObjectRangeQuery, CancellationToken> _prefetch;
    private readonly int[] _removedSourceOrdinals;
    private readonly int _liveSourceCount;

    public PureMidiFormalTimelineObjectSource(
        int sourceCount,
        Func<int, TValue> getSourceValue,
        Func<MidoraId, int> findSourceIndex,
        bool clearsSource,
        ImmutableHashSet<MidoraId> removedSourceIds,
        ImmutableDictionary<MidoraId, TValue> replacements,
        PersistentFormalValueSequence<TValue> added,
        int count,
        long sourceRevision,
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getStartTick,
        Func<TimelineObjectRangeQuery, IEnumerable<TValue>> query,
        Action<TimelineObjectRangeQuery, CancellationToken> prefetch,
        ImmutableDictionary<MidoraId, int>? addedIndices = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRevision);
        _getSourceValue = getSourceValue ?? throw new ArgumentNullException(nameof(getSourceValue));
        _findSourceIndex = findSourceIndex ?? throw new ArgumentNullException(nameof(findSourceIndex));
        _removedSourceIds = removedSourceIds ?? throw new ArgumentNullException(nameof(removedSourceIds));
        _replacements = replacements ?? throw new ArgumentNullException(nameof(replacements));
        _added = added ?? throw new ArgumentNullException(nameof(added));
        _addedIndices = addedIndices?.Count == added.Count ? addedIndices : null;
        _getId = getId ?? throw new ArgumentNullException(nameof(getId));
        _getStartTick = getStartTick ?? throw new ArgumentNullException(nameof(getStartTick));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _prefetch = prefetch ?? throw new ArgumentNullException(nameof(prefetch));
        _sourceCount = clearsSource ? 0 : sourceCount;
        _removedSourceOrdinals = clearsSource
            ? []
            : removedSourceIds
                .Select(findSourceIndex)
                .Where(static ordinal => ordinal >= 0)
                .Distinct()
                .Order()
                .ToArray();
        _liveSourceCount = checked(_sourceCount - _removedSourceOrdinals.Length);
        if (count != checked(_liveSourceCount + added.Count))
        {
            throw new InvalidOperationException(
                "A Pure MIDI formal source count does not match its source and overlay roots.");
        }
        Count = count;
        SourceRevision = sourceRevision;
    }

    public int Count { get; }
    public long SourceRevision { get; }
    public int PageCapacity => DefaultPageCapacity;

    internal bool TryGetAdded(MidoraId id, out int ordinal, out TValue value)
    {
        if (_addedIndices is not null && _addedIndices.TryGetValue(id, out int index))
        { ordinal = checked(_liveSourceCount + index); value = _added[index]; return true; }
        ordinal = -1; value = default; return false;
    }

    internal bool MapSourceValue(int sourceIndex, TValue sourceValue, out int ordinal, out TValue value)
    {
        MidoraId id = _getId(sourceValue);
        if ((uint)sourceIndex >= (uint)_sourceCount || _removedSourceIds.Contains(id))
        { ordinal = -1; value = default; return false; }
        ordinal = checked(sourceIndex - LowerBound(_removedSourceOrdinals, sourceIndex));
        value = _replacements.TryGetValue(id, out var replacement) ? replacement : sourceValue;
        return true;
    }

    internal bool MapSourceAddress(int sourceIndex, MidoraId id, out int ordinal)
    {
        if ((uint)sourceIndex >= (uint)_sourceCount || _removedSourceIds.Contains(id))
        { ordinal = -1; return false; }
        ordinal = checked(sourceIndex - LowerBound(_removedSourceOrdinals, sourceIndex));
        return true;
    }

    public bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<TValue> page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (firstOrdinal >= Count)
        {
            page = default;
            return false;
        }
        int actualCount = Math.Min(count, Count - firstOrdinal);
        TValue[] values = new TValue[actualCount];
        for (int offset = 0; offset < values.Length; offset++)
            values[offset] = GetByOrdinal(checked(firstOrdinal + offset));
        page = new(SourceRevision, firstOrdinal, values);
        return true;
    }

    public int FindOrdinalAtOrAfterTick(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        int ordinal = 0;
        while (ordinal < Count)
        {
            int count = Math.Min(PageCapacity, Count - ordinal);
            for (int offset = 0; offset < count; offset++)
            {
                if (_getStartTick(GetByOrdinal(ordinal + offset)) >= tick)
                    return ordinal + offset;
            }
            ordinal += count;
        }
        return -1;
    }

    public bool TryFindOrdinalById(MidoraId id, out int ordinal)
    {
        if (id == default || _removedSourceIds.Contains(id))
        {
            ordinal = -1;
            return false;
        }
        int sourceIndex = _findSourceIndex(id);
        if (sourceIndex >= 0 && sourceIndex < _sourceCount)
        {
            ordinal = checked(sourceIndex - LowerBound(_removedSourceOrdinals, sourceIndex));
            return true;
        }
        if (_addedIndices is not null)
        {
            if (_addedIndices.TryGetValue(id, out int addedIndex))
            {
                ordinal = checked(_liveSourceCount + addedIndex);
                return true;
            }
            ordinal = -1;
            return false;
        }
        for (int index = 0; index < _added.Count; index++)
        {
            if (_getId(_added[index]) != id) continue;
            ordinal = checked(_liveSourceCount + index);
            return true;
        }
        ordinal = -1;
        return false;
    }

    public IEnumerable<TValue> QueryTickRange(TimelineObjectRangeQuery query) =>
        _query(query);

    public void Prefetch(
        TimelineObjectRangeQuery query,
        CancellationToken cancellationToken = default) =>
        _prefetch(query, cancellationToken);

    internal TValue GetByOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (ordinal >= _liveSourceCount)
            return _added[ordinal - _liveSourceCount];
        int sourceIndex = SourceIndexForVisibleOrdinal(ordinal);
        TValue sourceValue = _getSourceValue(sourceIndex);
        return _replacements.TryGetValue(_getId(sourceValue), out TValue replacement)
            ? replacement
            : sourceValue;
    }

    private int SourceIndexForVisibleOrdinal(int visibleOrdinal)
    {
        if (_removedSourceOrdinals.Length == 0) return visibleOrdinal;
        int low = 0;
        int high = _sourceCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int removedThroughMiddle = UpperBound(_removedSourceOrdinals, middle);
            int liveThroughMiddle = checked(middle + 1 - removedThroughMiddle);
            if (liveThroughMiddle > visibleOrdinal) high = middle;
            else low = middle + 1;
        }
        if (low >= _sourceCount
            || Array.BinarySearch(_removedSourceOrdinals, low) >= 0)
        {
            throw new InvalidOperationException("A Pure MIDI visible ordinal could not be mapped to its source page.");
        }
        return low;
    }

    private static int LowerBound(int[] values, int value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(int[] values, int value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

public sealed class DirectMidiNoteObjectSource : ITimelineObjectSource<DirectMidiNoteValue>
{
    private readonly PureMidiFormalTimelineObjectSource<DirectMidiNoteValue> _source;
    private readonly DirectMidiNoteQuerySnapshot _query;
    private readonly IPureMidiSegmentContentSource? _paged;

    internal DirectMidiNoteObjectSource(
        DirectMidiNoteFormalSequenceSnapshot formal,
        DirectMidiNoteQuerySnapshot query)
    {
        IPureMidiSegmentContentSource? source = formal.ClearsSource ? null : formal.Source;
        _query = query; _paged = source;
        _source = new(
            source?.NoteCount ?? 0,
            index => source!.GetNote(index),
            id => source?.FindNoteIndex(id) ?? -1,
            formal.ClearsSource,
            formal.RemovedSourceIds,
            formal.Replacements,
            formal.Added,
            formal.Count,
            formal.Generation,
            static value => value.Id,
            static value => value.StartTick,
            range => query.QueryValues(
                range.StartTick,
                range.EndTick,
                range.MinimumLane,
                range.MaximumLane),
            (range, cancellationToken) => query.PrefetchRange(
                range.StartTick,
                range.EndTick,
                range.MinimumLane,
                range.MaximumLane,
                cancellationToken),
            formal.AddedIndices);
    }

    public int Count => _source.Count;
    public long SourceRevision => _source.SourceRevision;
    public int PageCapacity => _source.PageCapacity;
    public DirectMidiNoteValue GetByOrdinal(int ordinal) => _source.GetByOrdinal(ordinal);
    internal IEnumerable<DirectMidiNoteSourceMatch> QueryByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var match in _query.ResolveSourceMatches(ids))
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) yield return new(ordinal, value);
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) yield return new(ordinal, value);
    }
    internal bool TryQueryByIdsCached(IReadOnlySet<MidoraId> ids, List<DirectMidiNoteSourceMatch> destination)
    {
        List<DirectMidiNoteSourceMatch> matches = [];
        if (_paged is IPureMidiCachedContentSource cached)
        { if (!cached.TryQueryCachedNotesByIds(ids, matches)) return false; }
        else if (_paged is not null) matches.AddRange(_paged.QueryNotesByIds(ids));
        foreach (var match in matches)
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        return true;
    }
    public bool TryGetPageByOrdinal(int firstOrdinal, int count, out TimelineObjectPage<DirectMidiNoteValue> page) =>
        _source.TryGetPageByOrdinal(firstOrdinal, count, out page);
    public int FindOrdinalAtOrAfterTick(long tick) => _source.FindOrdinalAtOrAfterTick(tick);
    public bool TryFindOrdinalById(MidoraId id, out int ordinal) => _source.TryFindOrdinalById(id, out ordinal);
    public IEnumerable<DirectMidiNoteValue> QueryTickRange(TimelineObjectRangeQuery query) => _source.QueryTickRange(query);
    public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) =>
        _source.Prefetch(query, cancellationToken);
}

public sealed class DirectMidiChannelEventObjectSource : ITimelineObjectSource<DirectMidiChannelEventValue>
{
    private readonly PureMidiFormalTimelineObjectSource<DirectMidiChannelEventValue> _source;
    private readonly DirectMidiChannelEventQuerySnapshot _query;
    private readonly IPureMidiSegmentContentSource? _paged;

    internal DirectMidiChannelEventObjectSource(
        DirectMidiChannelEventFormalSequenceSnapshot formal,
        DirectMidiChannelEventQuerySnapshot query)
    {
        IPureMidiSegmentContentSource? source = formal.ClearsSource ? null : formal.Source;
        _query = query; _paged = source;
        _source = new(
            source?.ChannelEventCount ?? 0,
            index => source!.GetChannelEvent(index),
            id => source?.FindChannelEventIndex(id) ?? -1,
            formal.ClearsSource,
            formal.RemovedSourceIds,
            formal.Replacements,
            formal.Added,
            formal.Count,
            formal.Generation,
            static value => value.Id,
            static value => value.Tick,
            range => query.QueryValues(range.StartTick, range.EndTick),
            (range, cancellationToken) => query.PrefetchRange(
                range.StartTick,
                range.EndTick,
                cancellationToken),
            formal.AddedIndices);
    }

    public int Count => _source.Count;
    public long SourceRevision => _source.SourceRevision;
    public int PageCapacity => _source.PageCapacity;
    public DirectMidiChannelEventValue GetByOrdinal(int ordinal) => _source.GetByOrdinal(ordinal);
    internal IEnumerable<DirectMidiChannelEventSourceMatch> QueryByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var match in _query.ResolveSourceMatches(ids))
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) yield return new(ordinal, value);
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) yield return new(ordinal, value);
    }
    internal bool TryQueryByIdsCached(IReadOnlySet<MidoraId> ids, List<DirectMidiChannelEventSourceMatch> destination)
    {
        List<DirectMidiChannelEventSourceMatch> matches = [];
        if (_paged is IPureMidiCachedContentSource cached)
        { if (!cached.TryQueryCachedChannelEventsByIds(ids, matches)) return false; }
        else if (_paged is not null) matches.AddRange(_paged.QueryChannelEventsByIds(ids));
        foreach (var match in matches)
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        return true;
    }
    public bool TryGetPageByOrdinal(int firstOrdinal, int count, out TimelineObjectPage<DirectMidiChannelEventValue> page) =>
        _source.TryGetPageByOrdinal(firstOrdinal, count, out page);
    public int FindOrdinalAtOrAfterTick(long tick) => _source.FindOrdinalAtOrAfterTick(tick);
    public bool TryFindOrdinalById(MidoraId id, out int ordinal) => _source.TryFindOrdinalById(id, out ordinal);
    public IEnumerable<DirectMidiChannelEventValue> QueryTickRange(TimelineObjectRangeQuery query) => _source.QueryTickRange(query);
    public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) =>
        _source.Prefetch(query, cancellationToken);
}

public sealed class OpaqueMidiEventObjectSource : ITimelineObjectSource<OpaqueMidiEventValue>
{
    public const long MaximumPagePayloadBytes = 4L * 1024 * 1024;
    private readonly PureMidiFormalTimelineObjectSource<OpaqueMidiEventValue> _source;
    private readonly OpaqueMidiEventQuerySnapshot _query;
    private readonly IPureMidiSegmentContentSource? _paged;

    internal OpaqueMidiEventObjectSource(
        OpaqueMidiEventFormalSequenceSnapshot formal,
        OpaqueMidiEventQuerySnapshot query)
    {
        IPureMidiSegmentContentSource? source = formal.ClearsSource ? null : formal.Source;
        _query = query; _paged = source;
        _source = new(
            source?.OpaqueEventCount ?? 0,
            index => source!.GetOpaqueEvent(index),
            id => source?.FindOpaqueEventIndex(id) ?? -1,
            formal.ClearsSource,
            formal.RemovedSourceIds,
            formal.Replacements,
            formal.Added,
            formal.Count,
            formal.Generation,
            static value => value.Id,
            static value => value.Tick,
            range => query.QueryValues(range.StartTick, range.EndTick),
            (range, cancellationToken) => query.PrefetchRange(
                range.StartTick,
                range.EndTick,
                cancellationToken),
            formal.AddedIndices);
    }

    public int Count => _source.Count;
    public long SourceRevision => _source.SourceRevision;
    public int PageCapacity => _source.PageCapacity;
    public OpaqueMidiEventValue GetByOrdinal(int ordinal) => _source.GetByOrdinal(ordinal);
    internal IEnumerable<OpaqueMidiEventSourceMatch> QueryByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var match in _query.ResolveSourceMatches(ids))
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) yield return new(ordinal, value);
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) yield return new(ordinal, value);
    }
    internal IEnumerable<OpaqueSourceAddress> QueryAddressesByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var address in _query.ResolveSourceAddresses(ids))
            if (_source.MapSourceAddress(address.Index, address.Id, out int ordinal))
                yield return new(address.Id, ordinal);
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out _)) yield return new(id, ordinal);
    }
    internal bool TryQueryByIdsCached(IReadOnlySet<MidoraId> ids, List<OpaqueMidiEventSourceMatch> destination)
    {
        List<OpaqueMidiEventSourceMatch> matches = [];
        if (_paged is IPureMidiCachedContentSource cached)
        { if (!cached.TryQueryCachedOpaqueEventsByIds(ids, matches)) return false; }
        else if (_paged is not null) matches.AddRange(_paged.QueryOpaqueEventsByIds(ids));
        foreach (var match in matches)
            if (_source.MapSourceValue(match.Index, match.Value, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        foreach (var id in ids)
            if (_source.TryGetAdded(id, out int ordinal, out var value)) destination.Add(new(ordinal, value));
        return true;
    }
    public bool TryGetPageByOrdinal(int firstOrdinal, int count, out TimelineObjectPage<OpaqueMidiEventValue> page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (firstOrdinal >= Count) { page = default; return false; }
        int maximumCount = Math.Min(Math.Min(count, PageCapacity), Count - firstOrdinal);
        List<OpaqueMidiEventValue> values = new(Math.Min(maximumCount, 128));
        long retained = 0;
        for (int offset = 0; offset < maximumCount; offset++)
        {
            OpaqueMidiEventValue value = GetByOrdinal(firstOrdinal + offset);
            long bytes = OpaqueMidiPayloadMemory.GetRetainedBytes(value.Payload);
            // A single legal large event is never rejected or sliced. The
            // temporary look-ahead value is not retained by the returned page.
            if (values.Count != 0 && bytes > MaximumPagePayloadBytes - retained) break;
            values.Add(value);
            retained = checked(retained + bytes);
            if (retained >= MaximumPagePayloadBytes) break;
        }
        page = new(SourceRevision, firstOrdinal, values.ToArray())
        {
            RetainedPayloadBytes = OpaqueMidiPayloadMemory.GetRetainedBytes(values)
        };
        return true;
    }
    public int FindOrdinalAtOrAfterTick(long tick) => _source.FindOrdinalAtOrAfterTick(tick);
    public bool TryFindOrdinalById(MidoraId id, out int ordinal) => _source.TryFindOrdinalById(id, out ordinal);
    public IEnumerable<OpaqueMidiEventValue> QueryTickRange(TimelineObjectRangeQuery query) => _source.QueryTickRange(query);
    public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) =>
        _source.Prefetch(query, cancellationToken);
}
