using System.Collections;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;

namespace Midora.Application;

internal readonly record struct BoundedDirectNoteDelta(
    int Ordinal, bool Deleted, DirectMidiNoteValue Original, DirectMidiNoteValue Value);
internal readonly record struct BoundedDirectNoteId(MidoraId Id, int Ordinal, bool Deleted);
internal readonly record struct BoundedDirectNoteSpatial(DirectMidiNoteValue Value, bool Deleted);

/// <summary>
/// An immutable sparse edit root. The imported source stays untouched. Formal
/// ordinals, IDs and spatial values are independent ordered page indexes, so a
/// later edit shares all untouched pages instead of retaining a chain of roots.
/// </summary>
internal sealed class BoundedDirectMidiNoteSource : IPureMidiSegmentContentSource,
    IPureMidiContentBoundsSource, IPureMidiContentRangeFingerprintSource,
    IPureMidiNoteExclusionAwareSource, IPureMidiCachedContentSource, IPureMidiIdRangeSet,
    IPureMidiCachedOrdinalRangeSet, IPureMidiPlaybackEndpointSource, IPureMidiContentOverviewSource, IReadOnlySet<MidoraId>
{
    internal static readonly IComparer<BoundedDirectNoteDelta> OrdinalComparer =
        Comparer<BoundedDirectNoteDelta>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
    internal static readonly IComparer<BoundedDirectNoteId> IdComparer =
        Comparer<BoundedDirectNoteId>.Create(static (a, b) => a.Id.CompareTo(b.Id));
    internal static readonly IComparer<BoundedDirectNoteSpatial> SpatialComparer =
        Comparer<BoundedDirectNoteSpatial>.Create(static (a, b) =>
        {
            int key = a.Value.Key.CompareTo(b.Value.Key);
            if (key != 0) return key;
            int tick = a.Value.StartTick.CompareTo(b.Value.StartTick);
            return tick != 0 ? tick : a.Value.Id.CompareTo(b.Value.Id);
        });
    private readonly FrozenBaseline _baseline;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteDelta> _ordinals;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteId> _ids;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteSpatial> _spatial;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteSpatial> _removedSpatial;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteSpatial> _starts;
    private readonly BoundedDirectMidiIndex<BoundedDirectNoteSpatial> _ends;
    private readonly int _formalExtent;
    private readonly long _overlayMaximumEndTick;

    private BoundedDirectMidiNoteSource(FrozenBaseline baseline,
        BoundedDirectMidiIndex<BoundedDirectNoteDelta> ordinals,
        BoundedDirectMidiIndex<BoundedDirectNoteId> ids,
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial> spatial,
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial> removedSpatial,
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial> starts,
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial> ends, int formalExtent)
    {
        _baseline = baseline; _ordinals = ordinals; _ids = ids;
        _spatial = spatial; _removedSpatial = removedSpatial; _starts = starts; _ends = ends; _formalExtent = formalExtent;
        _overlayMaximumEndTick = spatial.Leaves.Count == 0 ? 0 : spatial.Leaves.Max(static leaf => leaf.MaximumTick);
        ContentFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"direct-note-edit-root-v1|{baseline.Identity}|{formalExtent}|{ordinals.StrongFingerprint}")));
    }

    public static BoundedDirectMidiNoteSource Capture(DirectMidiNoteCollection notes)
    {
        if (notes.IsPristinePagedSource && notes.PagedSource is BoundedDirectMidiNoteSource existing)
            return existing;
        var formal = notes.CreateFormalSequenceSnapshot();
        if (!formal.ClearsSource && formal.Source is BoundedDirectMidiNoteSource edited)
        {
            using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default);
            using var sorted = BoundedEditSort.Sort(OverlayChanges(), OrdinalComparer, scope.Resources, scope.Token);
            using var values = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(sorted);
            return edited.Apply(values, scope.Resources, scope.Token, checked(edited.FormalExtent + formal.Added.Count));

            IEnumerable<BoundedDirectNoteDelta> OverlayChanges()
            {
                foreach (var id in formal.RemovedSourceIds)
                {
                    if (!edited.TryGetById(id, out int ordinal, out var value))
                        throw new InvalidOperationException("A Direct MIDI tombstone does not belong to its source root.");
                    yield return new(ordinal, true, value, value);
                }
                foreach (var value in formal.Replacements.Values)
                {
                    if (!edited.TryGetById(value.Id, out int ordinal, out var original))
                        throw new InvalidOperationException("A Direct MIDI replacement does not belong to its source root.");
                    yield return new(ordinal, false, original, value);
                }
                int addedOrdinal = edited.FormalExtent;
                foreach (var value in formal.Added.Enumerate(scope.Token))
                    yield return new(addedOrdinal++, false, default, value);
            }
        }
        var baseline = new FrozenBaseline(notes.CreateObjectSource(), notes.CreateQuerySnapshot(),
            notes.IsPristinePagedSource, BaselineIdentityOf(formal));
        return new(baseline,
            BoundedDirectMidiIndex<BoundedDirectNoteDelta>.Empty(OrdinalComparer,
                static x => Bounds(x.Value), static x => Hash(x.Value), static x => x.Deleted, StrongHash,
                static x => x.Ordinal),
            BoundedDirectMidiIndex<BoundedDirectNoteId>.Empty(IdComparer,
                static _ => (0, 1, 0), static x => unchecked((ulong)x.Id.Value)),
            EmptySpatial(), EmptySpatial(), EmptyEndpoints(true), EmptyEndpoints(false), notes.Count);
    }

    private static BoundedDirectMidiIndex<BoundedDirectNoteSpatial> EmptySpatial() =>
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial>.Empty(SpatialComparer,
            static x => Bounds(x.Value), static x => Hash(x.Value));
    private static IComparer<BoundedDirectNoteSpatial> EndpointComparer(bool starts) =>
        Comparer<BoundedDirectNoteSpatial>.Create((a, b) => CompareEndpoint(a.Value, b.Value, starts));
    private static BoundedDirectMidiIndex<BoundedDirectNoteSpatial> EmptyEndpoints(bool starts) =>
        BoundedDirectMidiIndex<BoundedDirectNoteSpatial>.Empty(EndpointComparer(starts), value =>
        {
            long tick = starts ? value.Value.StartTick : checked(value.Value.StartTick + value.Value.LengthTicks);
            return (tick, tick == long.MaxValue ? tick : tick + 1, value.Value.Key);
        }, static x => Hash(x.Value));
    private static int CompareEndpoint(DirectMidiNoteValue a, DirectMidiNoteValue b, bool starts)
    {
        long left = starts ? a.StartTick : checked(a.StartTick + a.LengthTicks);
        long right = starts ? b.StartTick : checked(b.StartTick + b.LengthTicks);
        int tick = left.CompareTo(right);
        if (tick != 0) return tick;
        int order = (starts ? a.NoteOnOrder : a.NoteOffOrder).CompareTo(starts ? b.NoteOnOrder : b.NoteOffOrder);
        return order != 0 ? order : a.Id.CompareTo(b.Id);
    }
    private static (long, long, int) Bounds(DirectMidiNoteValue value) =>
        (value.StartTick, checked(value.StartTick + value.LengthTicks), value.Key);
    internal static ulong Hash(DirectMidiNoteValue value)
    {
        ulong hash = 14695981039346656037UL;
        hash = unchecked((hash ^ (ulong)value.Id.Value) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.StartTick) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.LengthTicks) * 1099511628211UL);
        hash = unchecked((hash ^ (uint)value.Key) * 1099511628211UL);
        hash = unchecked((hash ^ (uint)value.NoteOnVelocity) * 1099511628211UL);
        hash = unchecked((hash ^ (uint)value.NoteOffVelocity) * 1099511628211UL);
        hash = unchecked((hash ^ (ulong)value.NoteOnOrder) * 1099511628211UL);
        return unchecked((hash ^ (ulong)value.NoteOffOrder) * 1099511628211UL);
    }
    private static BoundedEditValueHash StrongHash(BoundedDirectNoteDelta value)
    {
        Span<byte> bytes = stackalloc byte[80];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Ordinal);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.Deleted ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], value.Value.Id.Value);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], value.Value.StartTick);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], value.Value.LengthTicks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[40..], value.Value.Key);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], value.Value.NoteOnVelocity);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[56..], value.Value.NoteOffVelocity);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[64..], value.Value.NoteOnOrder);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[72..], value.Value.NoteOffOrder);
        return BoundedEditValueHash.Compute(bytes);
    }
    private static string BaselineIdentityOf(DirectMidiNoteFormalSequenceSnapshot formal)
    {
        BoundedEditValueHash overlay = default;
        foreach (var id in formal.RemovedSourceIds)
            overlay += StrongHash(new(-1, true, default, new(id, 0, 0, 0, 0, 0, 0, 0)));
        foreach (var value in formal.Replacements.Values)
            overlay += StrongHash(new(-1, false, default, value));
        int ordinal = formal.Source?.NoteCount ?? 0;
        foreach (var value in formal.Added.Enumerate(BulkEditPreparationContext.Current?.Token ?? default))
            overlay += StrongHash(new(ordinal++, false, default, value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"direct-note-baseline-v1|{formal.Source?.ContentFingerprint}|{formal.ClearsSource}|{formal.Count}|{overlay}")));
    }

    public int NoteCount => checked(_formalExtent - _ordinals.DeletedCount);
    public int ChannelEventCount => 0;
    public int OpaqueEventCount => 0;
    public string ContentFingerprint { get; }
    public long MaximumNoteEndTick => Math.Max(_baseline.Query.MaximumEndTick, _overlayMaximumEndTick);
    internal int FormalExtent => _formalExtent;
    internal int SharedBaselineCount => _baseline.Objects.Count;
    internal object BaselineIdentity => _baseline;
    internal IReadOnlyList<BoundedDirectMidiIndex<BoundedDirectNoteDelta>.Leaf> OrdinalLeaves => _ordinals.Leaves;
    public int Count => _ids.Count;
    public bool Contains(MidoraId id) => _ids.TryGet(new(id, 0, false), out _);
    public bool MayContain(MidoraId minimumId, MidoraId maximumId)
    {
        foreach (var leaf in _ids.Leaves)
        {
            if (leaf.Minimum.Id.Value > maximumId.Value) break;
            if (leaf.Maximum.Id.Value >= minimumId.Value) return true;
        }
        return false;
    }
    public bool HasUnknownOrdinals => !_baseline.SourceOrdinalsMatch;
    public bool MayContainOrdinalRange(int firstOrdinal, int count)
    {
        if (HasUnknownOrdinals) return true;
        int index = _ordinals.LowerBound(new(firstOrdinal, false, default, default));
        return index < _ordinals.Count && _ordinals[index].Ordinal < (long)firstOrdinal + count;
    }
    public bool ContainsAllOrdinals(int firstOrdinal, int count)
    {
        if (HasUnknownOrdinals) return false;
        int first = _ordinals.LowerBound(new(firstOrdinal, false, default, default));
        int end = _ordinals.LowerBound(new(checked(firstOrdinal + count), false, default, default));
        return end - first == count;
    }
    public ArraySegment<int> GetOrdinalsInRange(int firstOrdinal, int count)
    {
        if (HasUnknownOrdinals) return default;
        // ContentPack calls this for one of its bounded source pages, never a
        // complete owner. It is a compact page mask, not a result-sized ID set.
        _ordinals.TryGetOrdinalsInRange(firstOrdinal, count, cachedOnly: false, out var values);
        return values;
    }
    public bool TryGetOrdinalsInRange(int firstOrdinal, int count, out ArraySegment<int> result)
    {
        result = default;
        return HasUnknownOrdinals || _ordinals.TryGetOrdinalsInRange(firstOrdinal, count, cachedOnly: true, out result);
    }

    internal bool TryGetById(MidoraId id, out int ordinal, out DirectMidiNoteValue value)
    {
        if (_ids.TryGet(new(id, 0, false), out BoundedDirectNoteId changed))
        {
            ordinal = changed.Ordinal;
            if (changed.Deleted) { value = default; return false; }
            value = GetByPhysicalOrdinal(ordinal);
            return true;
        }
        if (_baseline.Objects.TryFindOrdinalById(id, out ordinal))
        {
            value = _baseline.Get(ordinal);
            return true;
        }
        value = default;
        return false;
    }

    internal IEnumerable<BoundedDirectNoteDelta> ResolveIds(IEnumerable<MidoraId> ids, CancellationToken token, bool requireAll = true)
    {
        // Hash-set enumeration is intentionally unordered. Randomly probing two
        // spill-backed indexes for every selected ID can thrash the bounded page
        // cache after a million-note edit. Dense selection reads each ordinal
        // page once; membership is already owned by the immutable selection.
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

            IEnumerable<DirectMidiNoteSourceMatch> ReadBaselineSelection()
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
                var value = GetByPhysicalOrdinal(changed.Ordinal);
                yield return new(changed.Ordinal, false, value, value);
            }
            else if (!pending.Add(id) && requireAll) throw new ArgumentException("Direct MIDI Note IDs must be distinct.", nameof(ids));
            if (pending.Count < 4096) continue;
            foreach (var value in Flush()) yield return value;
        }
        if (pending.Count != 0) foreach (var value in Flush()) yield return value;
        IEnumerable<BoundedDirectNoteDelta> Flush()
        {
            int found = 0;
            foreach (var match in _baseline.Objects.QueryByIds(pending))
            {
                token.ThrowIfCancellationRequested(); found++;
                yield return new(match.Index, false, match.Value, match.Value);
            }
            if (requireAll && found != pending.Count) throw new ArgumentOutOfRangeException(nameof(ids));
            pending.Clear();
        }
    }

    internal DirectMidiNoteValue GetByPhysicalOrdinal(int ordinal) =>
        _ordinals.TryGet(new(ordinal, false, default, default), out var changed)
            ? changed.Deleted ? throw new ArgumentOutOfRangeException(nameof(ordinal)) : changed.Value
            : _baseline.Get(ordinal);

    internal IEnumerable<BoundedDirectNoteDelta> ResolveValues(IEnumerable<DirectMidiNoteValue> values, CancellationToken token)
    {
        using var scope = BulkEditPreparationContext.Enter(token);
        using var ordered = BoundedEditSort.Sort(values,
            Comparer<DirectMidiNoteValue>.Create(static (a, b) => a.Id.CompareTo(b.Id)), scope.Resources, token);
        Dictionary<MidoraId, DirectMidiNoteValue> pending = [];
        MidoraId previous = default;
        foreach (var value in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (value.Id == default || value.Id == previous) throw new InvalidOperationException("A frozen note query returned duplicate identities.");
            previous = value.Id;
            if (_ids.TryGet(new(value.Id, 0, false), out var edited))
            {
                if (edited.Deleted) throw new InvalidOperationException("A frozen note query returned a removed identity.");
                // The spatial query already read this scalar. Re-reading its
                // formal page here would turn key/time order into random I/O.
                yield return new(edited.Ordinal, false, value, value);
            }
            else pending.Add(value.Id, value);
            if (pending.Count < 4096) continue;
            foreach (var match in Flush()) yield return match;
        }
        if (pending.Count != 0) foreach (var match in Flush()) yield return match;
        IEnumerable<BoundedDirectNoteDelta> Flush()
        {
            HashSet<MidoraId> requested = new(pending.Keys);
            int found = 0;
            foreach (var match in _baseline.Objects.QueryByIds(requested))
            {
                token.ThrowIfCancellationRequested(); found++;
                var value = pending[match.Value.Id];
                yield return new(match.Index, false, value, value);
            }
            if (found != pending.Count) throw new InvalidOperationException("A frozen note query returned an unknown identity.");
            pending.Clear();
        }
    }

    public DirectMidiNoteValue GetNote(int index)
    {
        if ((uint)index >= (uint)NoteCount) throw new ArgumentOutOfRangeException(nameof(index));
        return GetByPhysicalOrdinal(_ordinals.SelectUndeletedOrdinal(index));
    }

    public int FindNoteIndex(MidoraId id) => TryGetById(id, out int ordinal, out _)
        ? checked(ordinal - _ordinals.CountDeletedBefore(new(ordinal, false, default, default))) : -1;
    public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var item in ResolveIds(ids, BulkEditPreparationContext.Current?.Token ?? default, requireAll: false))
            yield return new(item.Ordinal - _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default)), item.Value);
    }
    public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesAtStarts(IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        foreach (var item in ResolveIds(QueryStartKeys(keys).Select(static value => value.Id), BulkEditPreparationContext.Current?.Token ?? default))
            yield return new(item.Ordinal - _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default)), item.Value);
    }
    public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public OpaqueMidiEventValue GetOpaqueEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public int FindChannelEventIndex(MidoraId id) => -1;
    public int FindOpaqueEventIndex(MidoraId id) => -1;
    public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
    public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];

    public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127)
    {
        foreach (var value in _baseline.Query.QueryValuesExcluding(startTick, endTick, minimumKey, maximumKey, this))
            yield return value;
        foreach (var value in _spatial.Query(startTick, endTick, minimumKey, maximumKey)) yield return value.Value;
    }
    internal IEnumerable<DirectMidiNoteValue> QueryStartKeys(IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        foreach (var value in _baseline.Query.QueryStartKeys(keys))
            if (!Contains(value.Id)) yield return value;
        foreach (var key in keys)
        {
            var probe = new BoundedDirectNoteSpatial(new(default, key.Tick, 1, key.Key, 1, 0, 0, 0), false);
            int index = _spatial.LowerBound(probe);
            while (index < _spatial.Count)
            {
                var value = _spatial[index++].Value;
                if (value.Key != key.Key || value.StartTick != key.Tick) break;
                yield return value;
            }
        }
    }
    public IEnumerable<DirectMidiNoteValue> QueryNotesExcluding(long startTick, long endTick,
        int minimumKey, int maximumKey, IReadOnlySet<MidoraId> excludedIds)
    {
        foreach (var value in _baseline.Query.QueryValuesExcluding(startTick, endTick, minimumKey, maximumKey,
            new PureMidiUnionIdSet(this, excludedIds))) yield return value;
        foreach (var value in _spatial.Query(startTick, endTick, minimumKey, maximumKey))
            if (!excludedIds.Contains(value.Value.Id)) yield return value.Value;
    }
    public ulong GetNoteRangeFingerprint(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) =>
        unchecked(_baseline.Query.GetRangeFingerprint(startTick, endTick, minimumKey, maximumKey)
            + _spatial.GetRangeFingerprint(startTick, endTick, minimumKey, maximumKey)
            + _removedSpatial.GetRangeFingerprint(startTick, endTick, minimumKey, maximumKey));
    public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) => 0;
    public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) => 0;

    public IEnumerable<DirectMidiNoteValue> QueryNoteStarts(long startTick, long endTick) => QueryEndpoints(startTick, endTick, true);
    public IEnumerable<DirectMidiNoteValue> QueryNoteEnds(long startTick, long endTick) => QueryEndpoints(startTick, endTick, false);
    public IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick) =>
        QueryNotes(tick, tick == long.MaxValue ? tick : tick + 1)
            .Where(value => value.StartTick < tick && checked(value.StartTick + value.LengthTicks) > tick);
    public IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(long startTick, long endTick) => [];
    private IEnumerable<DirectMidiNoteValue> QueryEndpoints(long startTick, long endTick, bool starts)
    {
        var original = starts ? _baseline.Query.QueryStartValues(startTick, endTick)
            : _baseline.Query.QueryEndValues(startTick, endTick);
        using var left = original.Where(value => !Contains(value.Id)).GetEnumerator();
        using var right = (starts ? _starts : _ends).Query(startTick, endTick).GetEnumerator();
        bool hasLeft = left.MoveNext(), hasRight = right.MoveNext();
        while (hasLeft || hasRight)
        {
            if (hasLeft && (!hasRight || CompareEndpoint(left.Current, right.Current.Value, starts) <= 0))
            {
                yield return left.Current; hasLeft = left.MoveNext();
            }
            else { yield return right.Current.Value; hasRight = right.MoveNext(); }
        }
    }

    public bool TryQueryCachedNotesExcluding(long startTick, long endTick, int minimumKey,
        int maximumKey, IReadOnlySet<MidoraId> excludedIds, List<DirectMidiNoteValue> destination) =>
        TryQueryCachedNotesCore(startTick, endTick, minimumKey, maximumKey, excludedIds, destination);
    public bool TryQueryCachedNotes(long startTick, long endTick, int minimumKey,
        int maximumKey, List<DirectMidiNoteValue> destination) =>
        TryQueryCachedNotesCore(startTick, endTick, minimumKey, maximumKey, null, destination);
    private bool TryQueryCachedNotesCore(long startTick, long endTick, int minimumKey,
        int maximumKey, IReadOnlySet<MidoraId>? excludedIds, List<DirectMidiNoteValue> destination)
    {
        var cachedExclusions = new CacheOnlyExclusions(this);
        IReadOnlySet<MidoraId> exclusions = excludedIds is null ? cachedExclusions
            : new PureMidiUnionIdSet(cachedExclusions, excludedIds);
        List<DirectMidiNoteValue> baseline = [];
        if (!_baseline.Query.TryQueryValuesCachedExcluding(startTick, endTick, minimumKey, maximumKey, exclusions, baseline)
            || cachedExclusions.HadCacheMiss) return false;
        List<BoundedDirectNoteSpatial> edited = [];
        if (!_spatial.TryQueryCached(startTick, endTick, minimumKey, maximumKey, edited)) return false;
        destination.AddRange(baseline);
        foreach (var value in edited)
            if (excludedIds?.Contains(value.Value.Id) != true) destination.Add(value.Value);
        return true;
    }
    public bool TryQueryCachedChannelEvents(long startTick, long endTick, List<DirectMidiChannelEventValue> destination) => true;
    public bool TryQueryCachedOpaqueEvents(long startTick, long endTick, List<OpaqueMidiEventValue> destination) => true;
    public bool TryQueryCachedNotesByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiNoteSourceMatch> destination)
    {
        int initial = destination.Count;
        HashSet<MidoraId> pending = [];
        List<DirectMidiNoteSourceMatch> baseline = [];
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
        bool Append(int physical, DirectMidiNoteValue value)
        {
            if (!_ordinals.TryCountDeletedBeforeCached(new(physical, false, default, default), out int removed)) return false;
            destination.Add(new(physical - removed, value)); return true;
        }
        bool Fail() { destination.RemoveRange(initial, destination.Count - initial); return false; }
    }
    public bool TryQueryCachedChannelEventsByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiChannelEventSourceMatch> destination) => true;
    public bool TryQueryCachedOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, List<OpaqueMidiEventSourceMatch> destination) => true;
    public void PrefetchNotes(long startTick, long endTick, int minimumKey, int maximumKey, CancellationToken cancellationToken)
    {
        int visited = 0;
        foreach (var _ in QueryNotes(startTick, endTick, minimumKey, maximumKey))
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
    }
    public void PrefetchNotesByIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        foreach (var item in ResolveIds(ids, cancellationToken, requireAll: false))
        {
            _ = _ids.TryGet(new(item.Value.Id, 0, false), out _);
            _ = _ordinals.CountDeletedBefore(new(item.Ordinal, false, default, default));
        }
    }
    public void PrefetchChannelEvents(long startTick, long endTick, CancellationToken cancellationToken) { }
    public void PrefetchOpaqueEvents(long startTick, long endTick, CancellationToken cancellationToken) { }
    public void PrefetchChannelEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken) { }
    public void PrefetchOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken) { }
    public void PrefetchNotesExcluding(long startTick, long endTick, int minimumKey, int maximumKey,
        IReadOnlySet<MidoraId> excludedIds, CancellationToken cancellationToken)
    {
        int visited = 0;
        foreach (var _ in QueryNotesExcluding(startTick, endTick, minimumKey, maximumKey, excludedIds))
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
    }

    public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries()
    {
        foreach (var summary in _baseline.Query.GetOverviewRangeSummaries()) yield return summary;
        foreach (var leaf in _spatial.Leaves)
            yield return new(leaf.MinimumTick, leaf.MaximumTick, leaf.Count, leaf.MinimumKey, leaf.MaximumKey);
    }

    public bool TryAccumulateNoteStartColumns(long extent, Span<byte> destination, IReadOnlySet<MidoraId>? excludedIds)
    {
        IReadOnlySet<MidoraId> exclusions = excludedIds is null ? this : new PureMidiUnionIdSet(this, excludedIds);
        if (!_baseline.Query.TryAccumulateNoteStartColumnsExcluding(extent, destination, exclusions)) return false;
        foreach (var leaf in _starts.Leaves)
        {
            int first = Math.Clamp((int)((decimal)leaf.MinimumTick * destination.Length / extent), 0, destination.Length - 1);
            int last = Math.Clamp((int)((decimal)Math.Max(leaf.MinimumTick, leaf.MaximumTick - 1) * destination.Length / extent), 0, destination.Length - 1);
            if (first == last && excludedIds is null) { destination[first] = 1; continue; }
            for (int i = 0; i < leaf.Count; i++)
            {
                var value = leaf[i].Value;
                if (excludedIds?.Contains(value.Id) != true) PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
            }
        }
        return true;
    }

    public bool TryAccumulateNoteRasterColumns(TimelineRasterColumnProjection projection, int minimumKey,
        int maximumKey, Span<TimelineRasterColumnSummary> destination, IReadOnlySet<MidoraId>? excludedIds, out int sourceWorkCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlySet<MidoraId> exclusions = excludedIds is null ? this : new PureMidiUnionIdSet(this, excludedIds);
        if (!_baseline.Query.TryAccumulateRasterColumnsExcluding(projection, minimumKey, maximumKey,
            destination, exclusions, out sourceWorkCount, cancellationToken)) return false;
        int scanned = 0;
        foreach (var item in _spatial.Query(projection.StartTick, projection.EndTick, minimumKey, maximumKey, cancellationToken))
        {
            if ((scanned++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = item.Value;
            if (excludedIds?.Contains(value.Id) == true) continue;
            ulong low = value.Key < 64 ? 1UL << value.Key : 0;
            ulong high = value.Key >= 64 ? 1UL << (value.Key - 64) : 0;
            PagedTimelineRasterProjection.IncludeExact(destination, projection, value.StartTick,
                checked(value.StartTick + value.LengthTicks), low, high,
                value.NoteOnVelocity / 127d, value.NoteOnVelocity / 127d, 1);
            if (sourceWorkCount != int.MaxValue) sourceWorkCount++;
        }
        return true;
    }

    internal BoundedDirectMidiNoteSource Apply(BoundedImmutableValueSource<BoundedDirectNoteDelta> patch,
        BoundedEditResources resources, CancellationToken token, int? formalExtent = null, bool reportProgress = false)
    {
        void ReportIndex(int completed)
        {
            if (reportProgress) BulkEditPreparationContext.Current?.Checkpoint(completed, 6, TimelineEditPreparationPhase.BuildingResult);
        }
        ReportIndex(0);
        IEnumerable<BoundedDirectNoteDelta> Normalize()
        {
            foreach (var change in patch)
            {
                token.ThrowIfCancellationRequested();
                yield return _ordinals.TryGet(change, out var existing)
                    ? change with { Original = existing.Original } : change;
            }
        }
        using var byId = BoundedEditSort.Sort(patch.Select(static change =>
            new BoundedDirectNoteId(change.Value.Id, change.Ordinal, change.Deleted)), IdComparer, resources, token);
        IEnumerable<BoundedDirectNoteSpatial> SpatialChanges()
        {
            foreach (var change in patch)
            {
                if (_ordinals.TryGet(change, out var existing) && !existing.Deleted)
                    yield return new(existing.Value, true);
                if (!change.Deleted) yield return new(change.Value, false);
            }
        }
        using var spatial = BoundedEditSort.Sort(SpatialChanges(), SpatialComparer, resources, token);
        IEnumerable<BoundedDirectNoteSpatial> CoalesceSpatial(IEnumerable<BoundedDirectNoteSpatial> input,
            IComparer<BoundedDirectNoteSpatial> comparer)
        {
            using var values = input.GetEnumerator();
            if (!values.MoveNext()) yield break;
            var previous = values.Current;
            while (values.MoveNext())
            {
                if (comparer.Compare(previous, values.Current) == 0)
                    previous = previous.Deleted ? values.Current : previous;
                else { yield return previous; previous = values.Current; }
            }
            yield return previous;
        }
        using var removed = BoundedEditSort.Sort(Normalize().Where(change => change.Ordinal < _baseline.Objects.Count)
            .Select(static change => new BoundedDirectNoteSpatial(change.Original, false)), SpatialComparer, resources, token);
        var ordinals = _ordinals.ApplyPatch(Normalize(), resources, token);
        ReportIndex(1);
        var ids = _ids.ApplyPatch(byId, resources, token);
        ReportIndex(2);
        var newSpatial = _spatial.ApplyPatch(CoalesceSpatial(spatial, SpatialComparer), resources, token, static value => value.Deleted);
        ReportIndex(3);
        var removedSpatial = _removedSpatial.ApplyPatch(removed, resources, token);
        ReportIndex(4);
        var startComparer = EndpointComparer(true);
        var endComparer = EndpointComparer(false);
        using var startChanges = BoundedEditSort.Sort(SpatialChanges(), startComparer, resources, token);
        var starts = _starts.ApplyPatch(CoalesceSpatial(startChanges, startComparer), resources, token, static value => value.Deleted);
        ReportIndex(5);
        using var endChanges = BoundedEditSort.Sort(SpatialChanges(), endComparer, resources, token);
        var ends = _ends.ApplyPatch(CoalesceSpatial(endChanges, endComparer), resources, token, static value => value.Deleted);
        ReportIndex(6);
        return new(_baseline, ordinals, ids, newSpatial, removedSpatial, starts, ends, formalExtent ?? _formalExtent);
    }

    public IEnumerator<MidoraId> GetEnumerator() => _ids.Enumerate().Select(static x => x.Id).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsProperSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsProperSupersetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    public bool IsSupersetOf(IEnumerable<MidoraId> other) => other.All(Contains);
    public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);
    public bool SetEquals(IEnumerable<MidoraId> other) => throw new NotSupportedException();

    private sealed record FrozenBaseline(DirectMidiNoteObjectSource Objects, DirectMidiNoteQuerySnapshot Query,
        bool SourceOrdinalsMatch, string Identity)
    {
        public DirectMidiNoteValue Get(int ordinal) => Objects.GetByOrdinal(ordinal);
    }

    private sealed class CacheOnlyExclusions(BoundedDirectMidiNoteSource owner)
        : IReadOnlySet<MidoraId>, IPureMidiIdRangeSet, IPureMidiCachedOrdinalRangeSet
    {
        public bool HadCacheMiss { get; private set; }
        public int Count => owner.Count;
        public bool Contains(MidoraId id)
        {
            bool available = owner._ids.TryGetCached(new(id, 0, false), out bool found, out _);
            if (!available) HadCacheMiss = true;
            return found;
        }
        public bool MayContain(MidoraId minimumId, MidoraId maximumId) => owner.MayContain(minimumId, maximumId);
        public bool HasUnknownOrdinals => owner.HasUnknownOrdinals;
        public bool MayContainOrdinalRange(int firstOrdinal, int count) => true;
        public bool ContainsAllOrdinals(int firstOrdinal, int count)
        {
            if (owner.HasUnknownOrdinals) return false;
            if (owner._ordinals.TryContainsAllOrdinalsCached(firstOrdinal, count, out bool all)) return all;
            HadCacheMiss = true;
            return false;
        }
        public bool TryGetOrdinalsInRange(int firstOrdinal, int count, out ArraySegment<int> ordinals)
        {
            ordinals = default;
            if (owner.HasUnknownOrdinals) return true;
            bool ready = owner._ordinals.TryGetOrdinalsInRange(firstOrdinal, count, cachedOnly: true, out ordinals);
            HadCacheMiss |= !ready;
            return ready;
        }
        public ArraySegment<int> GetOrdinalsInRange(int firstOrdinal, int count)
        {
            TryGetOrdinalsInRange(firstOrdinal, count, out var result);
            return result;
        }
        public IEnumerator<MidoraId> GetEnumerator() => owner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<MidoraId> other) => other.All(Contains);
        public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);
        public bool SetEquals(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    }
}
