using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;

namespace Midora.Application;

internal readonly record struct BoundedDirectEventDelta(int Ordinal, bool Deleted,
    DirectMidiChannelEventValue Original, DirectMidiChannelEventValue Value);
internal readonly record struct BoundedDirectEventSpatial(DirectMidiChannelEventValue Value, bool Deleted);

/// <summary>Scalar, spill-backed channel-event edits over an unchanged source.
/// Independent formal/ID/time indexes share every untouched value page.</summary>
internal sealed class BoundedDirectMidiEventSource : IPureMidiSegmentContentSource,
    IPureMidiContentRangeFingerprintSource, IPureMidiCachedContentSource, IPureMidiPlaybackEndpointSource,
    IPureMidiContentOverviewSource, IDirectMidiTargetSummarySource
{
    internal static readonly IComparer<BoundedDirectEventDelta> OrdinalComparer =
        Comparer<BoundedDirectEventDelta>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
    private static readonly IComparer<BoundedDirectNoteId> IdComparer =
        Comparer<BoundedDirectNoteId>.Create(static (a, b) => a.Id.CompareTo(b.Id));
    private static readonly IComparer<BoundedDirectEventSpatial> SpatialComparer =
        Comparer<BoundedDirectEventSpatial>.Create(static (a, b) => Compare(a.Value, b.Value));
    private readonly Baseline _baseline;
    private readonly BoundedDirectMidiIndex<BoundedDirectEventDelta> _ordinals;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteId> _ids;
    private readonly BoundedDirectMidiIndex<BoundedDirectEventSpatial> _spatial;
    private readonly BoundedDirectMidiIndex<BoundedDirectEventSpatial> _removedSpatial;
    internal int FormalExtent { get; }
    private System.Collections.Immutable.ImmutableDictionary<DirectMidiLaneKey, int> _targetCountDelta =
        System.Collections.Immutable.ImmutableDictionary<DirectMidiLaneKey, int>.Empty;

    public IReadOnlyDictionary<DirectMidiLaneKey, int> GetTargetCounts(CancellationToken cancellationToken)
    {
        var counts = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<DirectMidiLaneKey, int>();
        counts.AddRange(_baseline.Query.GetTargetCounts(cancellationToken));
        foreach (var (key, delta) in _targetCountDelta)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked(counts.GetValueOrDefault(key) + delta);
            if (count == 0) counts.Remove(key); else counts[key] = count;
        }
        return counts.ToImmutable();
    }

    private BoundedDirectMidiEventSource(Baseline baseline,
        BoundedDirectMidiIndex<BoundedDirectEventDelta> ordinals, BoundedDirectMidiIndex<BoundedDirectNoteId> ids,
        BoundedDirectMidiIndex<BoundedDirectEventSpatial> spatial, BoundedDirectMidiIndex<BoundedDirectEventSpatial> removed,
        int extent)
    {
        _baseline = baseline; _ordinals = ordinals; _ids = ids; _spatial = spatial; _removedSpatial = removed;
        FormalExtent = extent;
        ContentFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"direct-event-root-v1|{baseline.Identity}|{extent}|{ordinals.StrongFingerprint}")));
    }

    internal static BoundedDirectMidiEventSource Capture(DirectMidiChannelEventCollection events, bool requireOrderedChannelEvents = true)
    {
        if (events.IsPristinePagedSource && events.PagedSource is BoundedDirectMidiEventSource pristine) return pristine;
        var formal = events.CreateFormalSequenceSnapshot();
        if (!formal.ClearsSource && formal.Source is BoundedDirectMidiEventSource edited)
        {
            using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default);
            using var sorted = BoundedEditSort.Sort(Overlay(), OrdinalComparer, scope.Resources, scope.Token);
            using var changes = new BoundedImmutableValueSource<BoundedDirectEventDelta>(sorted);
            return edited.Apply(changes, scope.Resources, scope.Token, checked(edited.FormalExtent + formal.Added.Count));
            IEnumerable<BoundedDirectEventDelta> Overlay()
            {
                foreach (var id in formal.RemovedSourceIds)
                {
                    if (!edited.TryGetById(id, out int ordinal, out var value)) throw new InvalidOperationException("Invalid MIDI Event tombstone.");
                    yield return new(ordinal, true, value, value);
                }
                foreach (var value in formal.Replacements.Values)
                {
                    if (!edited.TryGetById(value.Id, out int ordinal, out var old)) throw new InvalidOperationException("Invalid MIDI Event replacement.");
                    yield return new(ordinal, false, old, value);
                }
                int next = edited.FormalExtent;
                foreach (var value in formal.Added.Enumerate(scope.Token)) yield return new(next++, false, default, value);
            }
        }
        BoundedEditValueHash hash = default;
        foreach (var id in formal.RemovedSourceIds) hash += StrongHash(new(-1, true, default, new(id, 0, 0, 0, 0, 0)));
        foreach (var value in formal.Replacements.Values) hash += StrongHash(new(-1, false, default, value));
        int index = formal.Source?.ChannelEventCount ?? 0;
        foreach (var value in formal.Added.Enumerate(BulkEditPreparationContext.Current?.Token ?? default))
            hash += StrongHash(new(index++, false, default, value));
        string identity = $"{formal.Source?.ContentFingerprint}|{formal.ClearsSource}|{formal.Count}|{hash}";
        return new(new(events.CreateObjectSource(), events.CreateQuerySnapshot(), identity, requireOrderedChannelEvents),
            BoundedDirectMidiIndex<BoundedDirectEventDelta>.Empty(OrdinalComparer, static x => Bounds(x.Value),
                static x => Hash(x.Value), static x => x.Deleted, StrongHash, static x => x.Ordinal),
            BoundedDirectMidiIndex<BoundedDirectNoteId>.Empty(IdComparer, static _ => (0, 1, 0), static x => (ulong)x.Id.Value),
            EmptySpatial(), EmptySpatial(), events.Count);
    }

    private static BoundedDirectMidiIndex<BoundedDirectEventSpatial> EmptySpatial() =>
        BoundedDirectMidiIndex<BoundedDirectEventSpatial>.Empty(SpatialComparer, static x => Bounds(x.Value), static x => Hash(x.Value));
    private static (long, long, int) Bounds(DirectMidiChannelEventValue value) =>
        (value.Tick, value.Tick == long.MaxValue ? value.Tick : value.Tick + 1, 0);
    private static int Compare(DirectMidiChannelEventValue a, DirectMidiChannelEventValue b)
    {
        int tick = a.Tick.CompareTo(b.Tick);
        if (tick != 0) return tick;
        int order = a.Order.CompareTo(b.Order);
        return order != 0 ? order : a.Id.CompareTo(b.Id);
    }
    private static ulong Hash(DirectMidiChannelEventValue value)
    {
        ulong hash = unchecked((ulong)value.Id.Value * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.Tick) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.Kind) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.Data1) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.Data2) * 1099511628211UL);
        return unchecked((hash ^ (ulong)value.Order) * 1099511628211UL);
    }
    private static BoundedEditValueHash StrongHash(BoundedDirectEventDelta value)
    {
        Span<byte> bytes = stackalloc byte[64];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Ordinal);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.Deleted ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], value.Value.Id.Value);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], value.Value.Tick);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], (int)value.Value.Kind);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[40..], value.Value.Data1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], value.Value.Data2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[56..], value.Value.Order);
        return BoundedEditValueHash.Compute(bytes);
    }

    public int NoteCount => 0;
    public int ChannelEventCount => checked(FormalExtent - _ordinals.DeletedCount);
    public int OpaqueEventCount => 0;
    public string ContentFingerprint { get; }
    internal bool TryGetById(MidoraId id, out int ordinal, out DirectMidiChannelEventValue value)
    {
        if (_ids.TryGet(new(id, 0, false), out var changed))
        {
            ordinal = changed.Ordinal;
            if (changed.Deleted) { value = default; return false; }
            value = GetPhysical(ordinal); return true;
        }
        if (_baseline.Objects.TryFindOrdinalById(id, out ordinal)) { value = _baseline.Objects.GetByOrdinal(ordinal); return true; }
        value = default; return false;
    }
    internal DirectMidiChannelEventValue GetPhysical(int ordinal) =>
        _ordinals.TryGet(new(ordinal, false, default, default), out var changed)
            ? changed.Deleted ? throw new ArgumentOutOfRangeException(nameof(ordinal)) : changed.Value
            : _baseline.Objects.GetByOrdinal(ordinal);
    internal IEnumerable<BoundedDirectEventDelta> ResolveIds(IEnumerable<MidoraId> ids, CancellationToken token, bool requireAll = true)
    {
        if (ids is IReadOnlySet<MidoraId> selection && selection.Count >= 4096
            && selection.Count >= _ordinals.Count / 8)
        {
            int found = 0;
            foreach (var item in _ordinals.Enumerate(token))
            {
                if (!selection.Contains(item.Value.Id)) continue;
                found++;
                if (item.Deleted)
                { if (requireAll) throw new ArgumentOutOfRangeException(nameof(ids)); continue; }
                yield return item with { Original = item.Value };
            }
            if (found < selection.Count)
                foreach (var match in ReadBaselineSelection())
                {
                    token.ThrowIfCancellationRequested();
                    if (_ordinals.TryGet(new(match.Index, false, default, default), out _)) continue;
                    found++;
                    yield return new(match.Index, false, match.Value, match.Value);
                }
            if (requireAll && found != selection.Count) throw new ArgumentOutOfRangeException(nameof(ids));
            yield break;

            IEnumerable<DirectMidiChannelEventSourceMatch> ReadBaselineSelection()
            {
                if (selection.Count >= _baseline.Objects.Count / 8)
                {
                    for (int ordinal = 0; ordinal < _baseline.Objects.Count; ordinal++)
                    {
                        token.ThrowIfCancellationRequested();
                        var value = _baseline.Objects.GetByOrdinal(ordinal);
                        if (selection.Contains(value.Id)) yield return new(ordinal, value);
                    }
                }
                else
                {
                    HashSet<MidoraId> batch = [];
                    foreach (var id in selection)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_ids.TryGet(new(id, 0, false), out _)) continue;
                        batch.Add(id);
                        if (batch.Count < 4096) continue;
                        foreach (var match in _baseline.Objects.QueryByIds(batch)) yield return match;
                        batch.Clear();
                    }
                    if (batch.Count != 0)
                        foreach (var match in _baseline.Objects.QueryByIds(batch)) yield return match;
                }
            }
        }
        HashSet<MidoraId> pending = [];
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (id == default) { if (requireAll) throw new ArgumentOutOfRangeException(nameof(ids)); continue; }
            if (_ids.TryGet(new(id, 0, false), out var changed))
            {
                if (changed.Deleted) { if (requireAll) throw new ArgumentOutOfRangeException(nameof(ids)); continue; }
                var value = GetPhysical(changed.Ordinal);
                yield return new(changed.Ordinal, false, value, value);
            }
            else if (!pending.Add(id) && requireAll) throw new ArgumentException("Direct MIDI Event IDs must be distinct.", nameof(ids));
            if (pending.Count < 4096) continue;
            foreach (var value in Flush()) yield return value;
        }
        if (pending.Count != 0) foreach (var value in Flush()) yield return value;
        IEnumerable<BoundedDirectEventDelta> Flush()
        {
            int found = 0;
            foreach (var match in _baseline.Objects.QueryByIds(pending))
            { token.ThrowIfCancellationRequested(); found++; yield return new(match.Index, false, match.Value, match.Value); }
            if (requireAll && found != pending.Count) throw new ArgumentOutOfRangeException(nameof(ids));
            pending.Clear();
        }
    }
    internal IEnumerable<BoundedDirectEventDelta> ResolveValues(IEnumerable<DirectMidiChannelEventValue> values, CancellationToken token)
    {
        using var scope = BulkEditPreparationContext.Enter(token);
        using var ordered = BoundedEditSort.Sort(values,
            Comparer<DirectMidiChannelEventValue>.Create(static (a, b) => a.Id.CompareTo(b.Id)), scope.Resources, token);
        Dictionary<MidoraId, DirectMidiChannelEventValue> pending = [];
        MidoraId previous = default;
        foreach (var value in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (value.Id == default || value.Id == previous) throw new InvalidOperationException("A frozen event query returned duplicate identities.");
            previous = value.Id;
            if (_ids.TryGet(new(value.Id, 0, false), out var edited))
            {
                if (edited.Deleted) throw new InvalidOperationException("A frozen event query returned a removed identity.");
                yield return new(edited.Ordinal, false, value, value);
            }
            else pending.Add(value.Id, value);
            if (pending.Count < 4096) continue;
            foreach (var match in Flush()) yield return match;
        }
        if (pending.Count != 0) foreach (var match in Flush()) yield return match;
        IEnumerable<BoundedDirectEventDelta> Flush()
        {
            HashSet<MidoraId> requested = new(pending.Keys);
            int found = 0;
            foreach (var match in _baseline.Objects.QueryByIds(requested))
            {
                token.ThrowIfCancellationRequested(); found++;
                var value = pending[match.Value.Id];
                yield return new(match.Index, false, value, value);
            }
            if (found != pending.Count) throw new InvalidOperationException("A frozen event query returned an unknown identity.");
            pending.Clear();
        }
    }

    public DirectMidiChannelEventValue GetChannelEvent(int index)
    {
        if ((uint)index >= (uint)ChannelEventCount) throw new ArgumentOutOfRangeException(nameof(index));
        return GetPhysical(_ordinals.SelectUndeletedOrdinal(index));
    }
    public int FindChannelEventIndex(MidoraId id) => TryGetById(id, out int ordinal, out _)
        ? ordinal - _ordinals.CountDeletedBefore(new(ordinal, false, default, default)) : -1;
    public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var item in ResolveIds(ids, BulkEditPreparationContext.Current?.Token ?? default, requireAll: false))
            yield return new(item.Ordinal - _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default)), item.Value);
    }
    public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsAtStarts(IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        foreach (var item in ResolveIds(QueryStartKeys(keys).Select(static value => value.Id), BulkEditPreparationContext.Current?.Token ?? default))
            yield return new(item.Ordinal - _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default)), item.Value);
    }
    private bool Changed(MidoraId id) => _ids.TryGet(new(id, 0, false), out _);
    public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => QueryOrderedChannelEvents(startTick, endTick);
    public IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(long startTick, long endTick)
    {
        if (!_baseline.RequireOrderedChannelEvents)
        {
            // Internal opaque metadata: source order is intentionally opaque;
            // canonical opaque consumers already own their bounded sort.
            foreach (var value in _baseline.Query.QueryValues(startTick, endTick))
                if (!Changed(value.Id)) yield return value;
            foreach (var value in QuerySpatial(startTick, endTick)) yield return value.Value;
            yield break;
        }
        using var left = _baseline.Query.QueryOrderedValues(startTick, endTick).Where(value => !Changed(value.Id)).GetEnumerator();
        using var right = QuerySpatial(startTick, endTick).GetEnumerator();
        bool a = left.MoveNext(), b = right.MoveNext();
        while (a || b)
        {
            if (a && (!b || Compare(left.Current, right.Current.Value) <= 0)) { yield return left.Current; a = left.MoveNext(); }
            else { yield return right.Current.Value; b = right.MoveNext(); }
        }
    }
    internal IEnumerable<DirectMidiChannelEventValue> QueryStartKeys(IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        foreach (var value in _baseline.Query.QueryStartKeys(keys))
            if (!Changed(value.Id)) yield return value;
        foreach (long tick in keys.Select(static key => key.Tick).Distinct())
            foreach (var item in QuerySpatial(tick, tick == long.MaxValue ? tick : tick + 1))
                if (keys.Contains(Key(item.Value))) yield return item.Value;
    }
    private IEnumerable<BoundedDirectEventSpatial> QuerySpatial(long start, long end) =>
        _spatial.QueryKeys(new(new(default, start, default, 0, 0, long.MinValue), false),
            new(new(default, end, default, 0, 0, long.MinValue), false), BulkEditPreparationContext.Current?.Token ?? default);
    internal static DirectMidiEventStartKey Key(DirectMidiChannelEventValue value) => new(value.Tick, value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff ? value.Data1 : 0);

    internal BoundedDirectMidiEventSource Apply(BoundedImmutableValueSource<BoundedDirectEventDelta> patch,
        BoundedEditResources resources, CancellationToken token, int? extent = null)
    {
        IEnumerable<BoundedDirectEventDelta> Normalize()
        {
            foreach (var value in patch)
            {
                token.ThrowIfCancellationRequested();
                yield return _ordinals.TryGet(value, out var old) ? value with { Original = old.Original } : value;
            }
        }
        using var byId = BoundedEditSort.Sort(patch.Select(static value => new BoundedDirectNoteId(value.Value.Id,
            value.Ordinal, value.Deleted)), IdComparer, resources, token);
        IEnumerable<BoundedDirectEventSpatial> Edits()
        {
            foreach (var value in patch)
            {
                if (_ordinals.TryGet(value, out var old) && !old.Deleted) yield return new(old.Value, true);
                if (!value.Deleted) yield return new(value.Value, false);
            }
        }
        using var spatial = BoundedEditSort.Sort(Edits(), SpatialComparer, resources, token);
        IEnumerable<BoundedDirectEventSpatial> Coalesced()
        {
            using var values = spatial.GetEnumerator();
            if (!values.MoveNext()) yield break;
            var previous = values.Current;
            while (values.MoveNext())
            {
                if (SpatialComparer.Compare(previous, values.Current) == 0) previous = previous.Deleted ? values.Current : previous;
                else { yield return previous; previous = values.Current; }
            }
            yield return previous;
        }
        using var removed = BoundedEditSort.Sort(Normalize().Where(value => value.Ordinal < _baseline.Objects.Count)
            .Select(static value => new BoundedDirectEventSpatial(value.Original, false)), SpatialComparer, resources, token);
        var counts = _targetCountDelta.ToBuilder();
        foreach (var change in patch)
        {
            token.ThrowIfCancellationRequested();
            if (change.Original.Id != default) AddCount(change.Original, -1);
            if (!change.Deleted) AddCount(change.Value, 1);
        }
        void AddCount(DirectMidiChannelEventValue value, int delta)
        {
            var key = DirectMidiLaneKey.From(value);
            int count = checked(counts.GetValueOrDefault(key) + delta);
            if (count == 0) counts.Remove(key); else counts[key] = count;
        }
        return new(_baseline, _ordinals.ApplyPatch(Normalize(), resources, token), _ids.ApplyPatch(byId, resources, token),
            _spatial.ApplyPatch(Coalesced(), resources, token, static value => value.Deleted),
            _removedSpatial.ApplyPatch(removed, resources, token), extent ?? FormalExtent)
        { _targetCountDelta = counts.ToImmutable() };
    }

    public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) => unchecked(
        _baseline.Query.GetRangeFingerprint(startTick, endTick) + _spatial.GetRangeFingerprint(startTick, endTick)
            + _removedSpatial.GetRangeFingerprint(startTick, endTick));
    public ulong GetNoteRangeFingerprint(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => 0;
    public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) => 0;
    public bool TryQueryCachedChannelEvents(long startTick, long endTick, List<DirectMidiChannelEventValue> destination)
    {
        List<DirectMidiChannelEventValue> baseline = [];
        if (!_baseline.Query.TryQueryValuesCached(startTick, endTick, baseline)) return false;
        List<BoundedDirectEventSpatial> edited = [];
        if (!_spatial.TryQueryCached(startTick, endTick, 0, 127, edited)) return false;
        int initial = destination.Count;
        foreach (var value in baseline)
        {
            if (!_ids.TryGetCached(new(value.Id, 0, false), out bool found, out _))
            { destination.RemoveRange(initial, destination.Count - initial); return false; }
            if (!found) destination.Add(value);
        }
        foreach (var value in edited) destination.Add(value.Value);
        return true;
    }
    public void PrefetchChannelEvents(long startTick, long endTick, CancellationToken token)
    { foreach (var _ in QueryChannelEvents(startTick, endTick)) token.ThrowIfCancellationRequested(); }
    public void PrefetchChannelEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token)
    {
        foreach (var item in ResolveIds(ids, token, requireAll: false))
        {
            _ = _ids.TryGet(new(item.Value.Id, 0, false), out _);
            _ = _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default));
        }
    }
    public bool TryQueryCachedChannelEventsByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiChannelEventSourceMatch> destination)
    {
        int initial = destination.Count;
        HashSet<MidoraId> pending = [];
        List<DirectMidiChannelEventSourceMatch> baseline = [];
        foreach (var id in ids)
        {
            if (!_ids.TryGetCached(new(id, 0, false), out bool found, out var changed)) return Fail();
            if (found)
            {
                if (changed.Deleted) continue;
                if (!_ordinals.TryGetCached(new(changed.Ordinal, false, default, default), out bool exists, out var value)
                    || !exists || !Append(changed.Ordinal, value.Value)) return Fail();
            }
            else pending.Add(id);
            if (pending.Count >= 4096 && !Flush()) return Fail();
        }
        return Flush() || Fail();
        bool Flush()
        {
            if (pending.Count == 0) return true;
            baseline.Clear();
            if (!_baseline.Objects.TryQueryByIdsCached(pending, baseline)) return false;
            foreach (var value in baseline) if (!Append(value.Index, value.Value)) return false;
            pending.Clear(); return true;
        }
        bool Append(int physical, DirectMidiChannelEventValue value)
        {
            if (!_ordinals.TryCountDeletedBeforeCached(new(physical, false, default, default), out int removed)) return false;
            destination.Add(new(physical - removed, value)); return true;
        }
        bool Fail() { destination.RemoveRange(initial, destination.Count - initial); return false; }
    }
    public IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries()
    {
        foreach (var value in _baseline.Query.GetRangeSummaries()) yield return value;
        foreach (var leaf in _spatial.Leaves) yield return new(leaf.MinimumTick, leaf.MaximumTick, leaf.Count);
    }
    public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() => [];
    public DirectMidiNoteValue GetNote(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public OpaqueMidiEventValue GetOpaqueEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public int FindNoteIndex(MidoraId id) => -1;
    public int FindOpaqueEventIndex(MidoraId id) => -1;
    public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
    public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];
    public IEnumerable<DirectMidiNoteValue> QueryNoteStarts(long startTick, long endTick) => [];
    public IEnumerable<DirectMidiNoteValue> QueryNoteEnds(long startTick, long endTick) => [];
    public IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick) => [];
    public bool TryQueryCachedNotes(long startTick, long endTick, int minimumKey, int maximumKey, List<DirectMidiNoteValue> destination) => true;
    public bool TryQueryCachedOpaqueEvents(long startTick, long endTick, List<OpaqueMidiEventValue> destination) => true;
    public bool TryQueryCachedNotesByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiNoteSourceMatch> destination) => true;
    public bool TryQueryCachedOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, List<OpaqueMidiEventSourceMatch> destination) => true;
    public void PrefetchNotes(long startTick, long endTick, int minimumKey, int maximumKey, CancellationToken token) { }
    public void PrefetchOpaqueEvents(long startTick, long endTick, CancellationToken token) { }
    public void PrefetchNotesByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }
    public void PrefetchOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }
    private sealed record Baseline(DirectMidiChannelEventObjectSource Objects, DirectMidiChannelEventQuerySnapshot Query,
        string Identity, bool RequireOrderedChannelEvents);
}
