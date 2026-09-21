namespace Midora.Domain;

/// <summary>
/// Immutable revision view over Direct MIDI channel events.  Raster workers retain
/// this object instead of the live collection, so a concurrent edit can only appear
/// in a later presentation revision.
/// </summary>
public sealed class DirectMidiChannelEventQuerySnapshot
{
    private readonly IPureMidiSegmentContentSource? _source;
    private readonly IReadOnlySet<MidoraId>? _sourceExclusions;
    private readonly PureMidiPointOverlayIndex<DirectMidiChannelEventValue> _overlayIndex;
    private readonly PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> _sourceIdCache;
    private readonly DirectMidiChannelEventValue[] _excluded;

    internal DirectMidiChannelEventQuerySnapshot(
        IPureMidiSegmentContentSource? source,
        bool clearSource,
        IReadOnlySet<MidoraId>? sourceExclusions,
        IReadOnlyDictionary<MidoraId, DirectMidiChannelEventValue> sourceValues,
        PureMidiPointOverlayIndex<DirectMidiChannelEventValue> overlayIndex,
        PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> sourceIdCache,
        int count,
        long generation)
    {
        _source = clearSource ? null : source;
        _overlayIndex = overlayIndex;
        _sourceIdCache = sourceIdCache;
        _sourceExclusions = _source is not null && sourceExclusions?.Count != 0
            ? sourceExclusions
            : null;
        _excluded = _sourceExclusions is null
            ? []
            : _sourceExclusions
                .Where(sourceValues.ContainsKey)
                .Select(id => sourceValues[id])
                .OrderBy(static value => value.Tick)
                .ThenBy(static value => value.Order)
                .ThenBy(static value => value.Id)
                .ToArray();
        Count = count;
        Generation = generation;
    }

    public int Count { get; }
    public long Generation { get; }

    private IReadOnlyDictionary<DirectMidiLaneKey, int>? _targetCounts;
    public IReadOnlyDictionary<DirectMidiLaneKey, int> GetTargetCounts(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _targetCounts) is { } ready) return ready;
        var counts = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<DirectMidiLaneKey, int>();
        if (_source is not null)
            counts.AddRange(DirectMidiTargetSummary.Read(_source, cancellationToken));
        foreach (var value in _excluded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectMidiTargetSummary.Add(counts, DirectMidiLaneKey.From(value), -1);
        }
        foreach (var value in _overlayIndex.Query(0, long.MaxValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectMidiTargetSummary.Add(counts, DirectMidiLaneKey.From(value), 1);
        }
        var result = counts.ToImmutable();
        if (result.Values.Any(static count => count < 0) || result.Values.Sum() != Count)
            throw new InvalidOperationException("Direct MIDI target summary does not match its immutable source.");
        Interlocked.CompareExchange(ref _targetCounts, result, null);
        return _targetCounts;
    }

    internal IEnumerable<DirectMidiChannelEventSourceMatch> ResolveSourceMatches(IReadOnlySet<MidoraId> ids) =>
        _source is null ? [] : _sourceIdCache.Resolve(ids, _source.QueryChannelEventsByIds);

    internal IEnumerable<PureMidiContentRangeSummary> GetRangeSummaries()
    {
        if (_source is IPureMidiContentOverviewSource overview)
            foreach (var value in overview.GetChannelEventRangeSummaries()) yield return value;
        else if (_source is not null && _source.ChannelEventCount != 0)
            foreach (var value in _source.QueryChannelEvents(0, long.MaxValue)) yield return new(value.Tick, value.Tick, 1);
        foreach (var value in _overlayIndex.Query(0, long.MaxValue)) yield return new(value.Tick, value.Tick, 1);
    }

    public ulong GetRangeFingerprint(long startTick, long endTick)
    {
        ulong result = 14695981039346656037UL;
        if (_source is IPureMidiContentRangeFingerprintSource ranged)
            Add(ref result, ranged.GetChannelEventRangeFingerprint(startTick, endTick));
        else if (_source is not null)
            Add(ref result, HashText(_source.ContentFingerprint));
        AddRange(ref result, _excluded, startTick, endTick, exclusion: true);
        AddRange(ref result, _overlayIndex.EnumerateRange(startTick, endTick), exclusion: false);
        return result;
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (_source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_sourceExclusions?.Contains(value.Id) != true) yield return value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.EnumerateRange(startTick, endTick))
            yield return value;
    }

    internal IEnumerable<DirectMidiChannelEventValue> QueryStartKeys(IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        if (_source is not null)
            foreach (var match in _source.QueryChannelEventsAtStarts(keys))
                if (_sourceExclusions?.Contains(match.Value.Id) != true) yield return match.Value;
        foreach (long tick in keys.Select(static key => key.Tick).Distinct())
            foreach (var value in _overlayIndex.QueryAtTick(tick))
            {
                int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                    or DirectMidiChannelEventKind.PolyphonicKeyPressure
                    or DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff ? value.Data1 : 0;
                if (keys.Contains(new(value.Tick, value.Kind, selector))) yield return value;
            }
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryOrderedValues(
        long startTick,
        long endTick)
    {
        if (endTick <= startTick) yield break;
        IEnumerable<DirectMidiChannelEventValue> source = _source switch
        {
            IPureMidiPlaybackEndpointSource endpoints =>
                endpoints.QueryOrderedChannelEvents(startTick, endTick),
            not null => _source.QueryChannelEvents(startTick, endTick)
                .OrderBy(static value => value.Tick)
                .ThenBy(static value => value.Order)
                .ThenBy(static value => value.Id),
            _ => []
        };
        IEnumerable<DirectMidiChannelEventValue> filteredSource = source.Where(value =>
            _sourceExclusions?.Contains(value.Id) != true);
        IEnumerable<DirectMidiChannelEventValue> overlay = _overlayIndex.Query(
            startTick,
            endTick);
        using IEnumerator<DirectMidiChannelEventValue> left = filteredSource.GetEnumerator();
        using IEnumerator<DirectMidiChannelEventValue> right = overlay.GetEnumerator();
        bool hasLeft = left.MoveNext();
        bool hasRight = right.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && Compare(left.Current, right.Current) <= 0;
            if (takeLeft)
            {
                yield return left.Current;
                hasLeft = left.MoveNext();
            }
            else
            {
                yield return right.Current;
                hasRight = right.MoveNext();
            }
        }
    }

    private static int Compare(
        DirectMidiChannelEventValue left,
        DirectMidiChannelEventValue right)
    {
        int result = left.Tick.CompareTo(right.Tick);
        if (result != 0) return result;
        result = left.Order.CompareTo(right.Order);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        List<DirectMidiChannelEventValue>? sourceValues = null;
        if (_source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEvents(startTick, endTick, sourceValues)) return false;
            }
            else
            {
                sourceValues.AddRange(_source.QueryChannelEvents(startTick, endTick));
            }
        }
        if (sourceValues is not null)
        {
            foreach (DirectMidiChannelEventValue value in sourceValues)
                if (_sourceExclusions?.Contains(value.Id) != true) destination.Add(value);
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(long startTick, long endTick, CancellationToken cancellationToken)
    {
        if (endTick > startTick && _source is IPureMidiCachedContentSource cached)
            cached.PrefetchChannelEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        List<DirectMidiChannelEventSourceMatch>? sourceMatches = null;
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEventsByIds(sourceIds, sourceMatches)) return false;
            }
            else
            {
                sourceMatches.AddRange(_source.QueryChannelEventsByIds(sourceIds));
            }
        }
        HashSet<MidoraId> emitted = [];
        if (sourceMatches is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in sourceMatches)
                if (emitted.Add(match.Value.Id)) destination.Add(match.Value);
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) destination.Add(value);
        return true;
    }

    public IEnumerable<DirectMidiChannelEventValue> ResolveByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;
        HashSet<MidoraId> emitted = [];
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            foreach (DirectMidiChannelEventSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source.QueryChannelEventsByIds))
            {
                if (emitted.Add(match.Value.Id)) yield return match.Value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) yield return value;
    }

    public void PrefetchIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        if (_source is not IPureMidiCachedContentSource cached || ids.Count == 0) return;
        HashSet<MidoraId> sourceIds = [.. ids];
        if (_sourceExclusions is not null)
            sourceIds.RemoveWhere(_sourceExclusions.Contains);
        cached.PrefetchChannelEventsByIds(sourceIds, cancellationToken);
    }

    private static int FirstAtOrAfter(DirectMidiChannelEventValue[] values, long tick)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle].Tick < tick) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static DirectMidiChannelEventValue ToValue(DirectMidiChannelEvent value) => new(
        value.Id, value.Tick, value.Kind, value.Data1, value.Data2, value.Order);

    private static void AddRange(
        ref ulong fingerprint,
        IEnumerable<DirectMidiChannelEventValue> values,
        bool exclusion)
    {
        foreach (DirectMidiChannelEventValue value in values)
        {
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, unchecked((ulong)value.Data1));
            Add(ref fingerprint, unchecked((ulong)value.Data2));
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static void AddRange(
        ref ulong fingerprint,
        DirectMidiChannelEventValue[] values,
        long startTick,
        long endTick,
        bool exclusion)
    {
        int first = FirstAtOrAfter(values, startTick);
        int last = FirstAtOrAfter(values, endTick);
        for (int index = first; index < last; index++)
        {
            DirectMidiChannelEventValue value = values[index];
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, unchecked((ulong)value.Data1));
            Add(ref fingerprint, unchecked((ulong)value.Data2));
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static ulong HashText(string value)
    {
        ulong result = 14695981039346656037UL;
        foreach (char character in value) Add(ref result, character);
        return result;
    }

    private static void Add(ref ulong value, ulong part)
    {
        value ^= part;
        value *= 1099511628211UL;
    }
}

/// <summary>Immutable revision view over imported Meta/SysEx events.</summary>
public sealed class OpaqueMidiEventQuerySnapshot
{
    private readonly IPureMidiSegmentContentSource? _source;
    private readonly IReadOnlySet<MidoraId>? _sourceExclusions;
    private readonly PureMidiPointOverlayIndex<OpaqueMidiEventValue> _overlayIndex;
    private readonly PureMidiOpaqueIdResolutionCache _sourceIdCache;
    private readonly OpaqueMidiEventValue[] _excluded;

    internal OpaqueMidiEventQuerySnapshot(
        IPureMidiSegmentContentSource? source,
        bool clearSource,
        IReadOnlySet<MidoraId>? sourceExclusions,
        IReadOnlyDictionary<MidoraId, OpaqueMidiEventValue> sourceValues,
        PureMidiPointOverlayIndex<OpaqueMidiEventValue> overlayIndex,
        PureMidiOpaqueIdResolutionCache sourceIdCache,
        int count,
        long generation)
    {
        _source = clearSource ? null : source;
        _overlayIndex = overlayIndex;
        _sourceIdCache = sourceIdCache;
        _sourceExclusions = _source is not null && sourceExclusions?.Count != 0
            ? sourceExclusions
            : null;
        _excluded = _sourceExclusions is null
            ? []
            : _sourceExclusions.Where(sourceValues.ContainsKey).Select(id => sourceValues[id])
                .OrderBy(static value => value.Tick).ThenBy(static value => value.Order)
                .ThenBy(static value => value.Id).ToArray();
        Count = count;
        Generation = generation;
    }

    public int Count { get; }
    public long Generation { get; }

    internal IEnumerable<OpaqueMidiEventSourceMatch> ResolveSourceMatches(IReadOnlySet<MidoraId> ids) =>
        _source is null ? [] : _sourceIdCache.Resolve(ids, _source);

    internal IReadOnlyList<OpaqueSourceAddress> ResolveSourceAddresses(IReadOnlySet<MidoraId> ids) =>
        _source is null ? [] : _sourceIdCache.ResolveAddresses(ids, _source);

    internal IEnumerable<PureMidiContentRangeSummary> GetRangeSummaries()
    {
        if (_source is IPureMidiContentOverviewSource overview)
            foreach (var value in overview.GetOpaqueEventRangeSummaries()) yield return value;
        else if (_source is not null)
            foreach (var value in _source.QueryOpaqueEvents(0, long.MaxValue)) yield return new(value.Tick, value.Tick, 1);
        foreach (var value in _overlayIndex.Query(0, long.MaxValue)) yield return new(value.Tick, value.Tick, 1);
    }

    public ulong GetRangeFingerprint(long startTick, long endTick)
    {
        ulong result = 14695981039346656037UL;
        if (_source is IPureMidiContentRangeFingerprintSource ranged)
            Add(ref result, ranged.GetOpaqueEventRangeFingerprint(startTick, endTick));
        else if (_source is not null)
            Add(ref result, HashText(_source.ContentFingerprint));
        AddRange(ref result, _excluded, startTick, endTick, exclusion: true);
        AddRange(ref result, _overlayIndex.EnumerateRange(startTick, endTick), exclusion: false);
        return result;
    }

    public IEnumerable<OpaqueMidiEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (_source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(startTick, endTick))
                if (_sourceExclusions?.Contains(value.Id) != true) yield return value;
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.EnumerateRange(startTick, endTick))
            yield return value;
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        List<OpaqueMidiEventValue>? sourceValues = null;
        if (_source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEvents(startTick, endTick, sourceValues)) return false;
            }
            else sourceValues.AddRange(_source.QueryOpaqueEvents(startTick, endTick));
        }
        if (sourceValues is not null)
        {
            foreach (OpaqueMidiEventValue value in sourceValues)
                if (_sourceExclusions?.Contains(value.Id) != true) destination.Add(value);
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(long startTick, long endTick, CancellationToken cancellationToken)
    {
        if (endTick > startTick && _source is IPureMidiCachedContentSource cached)
            cached.PrefetchOpaqueEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        List<OpaqueMidiEventSourceMatch>? sourceMatches = null;
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEventsByIds(sourceIds, sourceMatches)) return false;
            }
            else sourceMatches.AddRange(_source.QueryOpaqueEventsByIds(sourceIds));
        }
        HashSet<MidoraId> emitted = [];
        if (sourceMatches is not null)
        {
            foreach (OpaqueMidiEventSourceMatch match in sourceMatches)
                if (emitted.Add(match.Value.Id)) destination.Add(match.Value);
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) destination.Add(value);
        return true;
    }

    public IEnumerable<OpaqueMidiEventValue> ResolveByIds(IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;
        HashSet<MidoraId> emitted = [];
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            foreach (OpaqueMidiEventSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source))
            {
                if (emitted.Add(match.Value.Id)) yield return match.Value;
            }
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) yield return value;
    }

    public void PrefetchIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        if (_source is not IPureMidiCachedContentSource cached || ids.Count == 0) return;
        HashSet<MidoraId> sourceIds = [.. ids];
        if (_sourceExclusions is not null)
            sourceIds.RemoveWhere(_sourceExclusions.Contains);
        cached.PrefetchOpaqueEventsByIds(sourceIds, cancellationToken);
    }

    private static int FirstAtOrAfter(OpaqueMidiEventValue[] values, long tick)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle].Tick < tick) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static OpaqueMidiEventValue ToValue(OpaqueMidiEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload.ToArray(),
        value.Order);

    private static void AddRange(
        ref ulong fingerprint,
        IEnumerable<OpaqueMidiEventValue> values,
        bool exclusion)
    {
        foreach (OpaqueMidiEventValue value in values)
        {
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, value.MetaType);
            foreach (byte part in value.Payload.Span) Add(ref fingerprint, part);
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static void AddRange(
        ref ulong fingerprint,
        OpaqueMidiEventValue[] values,
        long startTick,
        long endTick,
        bool exclusion)
    {
        int first = FirstAtOrAfter(values, startTick);
        int last = FirstAtOrAfter(values, endTick);
        for (int index = first; index < last; index++)
        {
            OpaqueMidiEventValue value = values[index];
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, value.MetaType);
            foreach (byte part in value.Payload.Span) Add(ref fingerprint, part);
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static ulong HashText(string value)
    {
        ulong result = 14695981039346656037UL;
        foreach (char character in value) Add(ref result, character);
        return result;
    }

    private static void Add(ref ulong value, ulong part)
    {
        value ^= part;
        value *= 1099511628211UL;
    }
}
