using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;

namespace Midora.Application;

/// <summary>Opaque payload bytes have their own immutable, shared storage.
/// Time/ID/order edits only replace scalar event-index pages.</summary>
internal sealed class BoundedOpaqueMidiSource : IPureMidiSegmentContentSource,
    IPureMidiContentRangeFingerprintSource, IPureMidiCachedContentSource, IPureMidiContentOverviewSource
{
    private readonly OpaqueMidiEventObjectSource _baseline;
    private readonly ImmutableList<PayloadBank> _payloads;
    internal BoundedDirectMidiEventSource Metadata { get; }
    private readonly int _payloadCount;
    private readonly string _payloadIdentity;
    private readonly long _baselinePayloadCacheIdentity;
    private BoundedOpaqueMidiSource(OpaqueMidiEventObjectSource baseline, BoundedDirectMidiEventSource metadata,
        ImmutableList<PayloadBank> payloads, int payloadCount, string payloadIdentity, long baselinePayloadCacheIdentity)
    {
        _baseline = baseline; Metadata = metadata; _payloads = payloads; _payloadCount = payloadCount;
        _payloadIdentity = payloadIdentity;
        _baselinePayloadCacheIdentity = baselinePayloadCacheIdentity;
        ContentFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"opaque-root-v1|{metadata.ContentFingerprint}|{payloadIdentity}")));
    }

    internal static BoundedOpaqueMidiSource Capture(MidoraProject project, OpaqueMidiEventCollection events)
    {
        if (events.IsPristinePagedSource && events.PagedSource is BoundedOpaqueMidiSource pristine) return pristine;
        var formal = events.CreateFormalSequenceSnapshot();
        if (!formal.ClearsSource && formal.Source is BoundedOpaqueMidiSource edited)
        {
            using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
            using var payloads = edited.CreatePayloadWriter(scope);
            using var sorted = BoundedEditSort.Sort(Overlay(), BoundedDirectMidiEventSource.OrdinalComparer, scope.Resources, scope.Token);
            using var patch = new BoundedImmutableValueSource<BoundedDirectEventDelta>(sorted);
            return edited.Apply(patch, scope, payloads, checked(edited.Metadata.FormalExtent + formal.Added.Count));
            IEnumerable<BoundedDirectEventDelta> Overlay()
            {
                foreach (var id in formal.RemovedSourceIds)
                {
                    if (!edited.Metadata.TryGetById(id, out int ordinal, out var old)) throw new InvalidOperationException("Invalid opaque tombstone.");
                    yield return new(ordinal, true, old, old);
                }
                foreach (var value in formal.Replacements.Values)
                {
                    if (!edited.Metadata.TryGetById(value.Id, out int ordinal, out var old)) throw new InvalidOperationException("Invalid opaque replacement.");
                    yield return new(ordinal, false, old, edited.Encode(value, payloads));
                }
                int ordinalAdded = edited.Metadata.FormalExtent;
                foreach (var value in formal.Added.Enumerate(scope.Token))
                    yield return new(ordinalAdded++, false, default, edited.Encode(value, payloads));
            }
        }
        var baseline = events.CreateObjectSource();
        var query = events.CreateQuerySnapshot();
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        BoundedEditValueHash overlay = default;
        foreach (var id in formal.RemovedSourceIds) overlay += Hash(-1, new(id, 0, 0, 0, default, 0));
        foreach (var value in formal.Replacements.Values) overlay += Hash(-2, value);
        int addedOrdinal = formal.Source?.OpaqueEventCount ?? 0;
        foreach (var value in formal.Added.Enumerate(BulkEditPreparationContext.Current?.Token ?? default))
            overlay += Hash(addedOrdinal++, value);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"opaque-baseline-v1|{formal.Source?.ContentFingerprint}|{formal.ClearsSource}|{formal.Count}|{overlay}")));
        long payloadCache = BoundedOpaquePayloadCache.AllocateIdentity();
        var metadata = new DirectMidiChannelEventCollection(project);
        metadata.AdoptContentSource(new OpaqueMetadataSource(baseline, query, fingerprint, payloadCache), events.Generation);
        return new(baseline, BoundedDirectMidiEventSource.Capture(metadata, requireOrderedChannelEvents: false),
            ImmutableList<PayloadBank>.Empty, baseline.Count, fingerprint, payloadCache);
        BoundedEditValueHash Hash(int ordinal, OpaqueMidiEventValue value)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            identity.AppendData(Encoding.UTF8.GetBytes($"{ordinal}|{value.Id.Value}|{value.Tick}|{value.Kind}|{value.MetaType}|{value.Order}|{value.Payload.Length}:"));
            identity.AppendData(value.Payload.Span);
            Span<byte> digest = stackalloc byte[32];
            identity.GetHashAndReset(digest);
            return BoundedEditValueHash.Compute(digest);
        }
    }

    internal PayloadWriter CreatePayloadWriter(BulkEditPreparationContext scope) => new(_payloadCount, scope);
    internal DirectMidiChannelEventValue Encode(OpaqueMidiEventValue value, PayloadWriter writer)
    {
        if (Metadata.TryGetById(value.Id, out _, out var original)
            && Payload(original.Data2).Span.SequenceEqual(value.Payload.Span))
            return new(value.Id, value.Tick, (DirectMidiChannelEventKind)value.Kind, value.MetaType, original.Data2, value.Order);
        return new(value.Id, value.Tick, (DirectMidiChannelEventKind)value.Kind, value.MetaType, writer.Add(value.Payload), value.Order);
    }
    internal BoundedOpaqueMidiSource Apply(BoundedImmutableValueSource<BoundedDirectEventDelta> patch,
        BulkEditPreparationContext scope, PayloadWriter? payloads = null, int? extent = null)
    {
        var metadata = Metadata.Apply(patch, scope.Resources, scope.Token, extent);
        var bank = payloads?.Publish();
        return new(_baseline, metadata, bank is null ? _payloads : _payloads.Add(bank),
            bank is null ? _payloadCount : checked(bank.FirstOrdinal + bank.Descriptors.Count),
            bank is null ? _payloadIdentity : Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(_payloadIdentity + ":" + bank.Fingerprint))), _baselinePayloadCacheIdentity);
    }
    private ReadOnlyMemory<byte> Payload(int ordinal)
    {
        if (ordinal < _baseline.Count)
        {
            if (BoundedOpaquePayloadCache.TryGet(_baselinePayloadCacheIdentity, ordinal, out var cached)) return cached;
            var payload = _baseline.GetByOrdinal(ordinal).Payload;
            BoundedOpaquePayloadCache.Add(_baselinePayloadCacheIdentity, ordinal, payload); return payload;
        }
        int low = 0, high = _payloads.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_payloads[middle].FirstOrdinal <= ordinal) low = middle + 1; else high = middle;
        }
        if (low == 0) throw new InvalidOperationException("Opaque payload reference is invalid.");
        return _payloads[low - 1].Read(ordinal);
    }
    private OpaqueMidiEventValue Decode(DirectMidiChannelEventValue value) => new(value.Id, value.Tick,
        (OpaqueMidiEventKind)value.Kind, checked((byte)value.Data1), Payload(value.Data2), value.Order);
    private bool TryDecodeCached(DirectMidiChannelEventValue value, out OpaqueMidiEventValue result)
    {
        ReadOnlyMemory<byte> payload;
        bool hit = value.Data2 < _baseline.Count
            ? BoundedOpaquePayloadCache.TryGet(_baselinePayloadCacheIdentity, value.Data2, out payload)
            : TryReadBank(value.Data2, out payload);
        result = hit ? new(value.Id, value.Tick, (OpaqueMidiEventKind)value.Kind, checked((byte)value.Data1), payload, value.Order) : default;
        return hit;
        bool TryReadBank(int ordinal, out ReadOnlyMemory<byte> bytes)
        {
            foreach (var bank in _payloads)
                if (ordinal >= bank.FirstOrdinal && ordinal - bank.FirstOrdinal < bank.Descriptors.Count)
                    return BoundedOpaquePayloadCache.TryGet(bank.CacheIdentity, ordinal, out bytes);
            bytes = default; return false;
        }
    }
    public int NoteCount => 0;
    public int ChannelEventCount => 0;
    public int OpaqueEventCount => Metadata.ChannelEventCount;
    public string ContentFingerprint { get; }
    public OpaqueMidiEventValue GetOpaqueEvent(int index) => Decode(Metadata.GetChannelEvent(index));
    public int FindOpaqueEventIndex(MidoraId id) => Metadata.FindChannelEventIndex(id);
    public IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(IReadOnlySet<MidoraId> ids)
    {
        foreach (var item in Metadata.QueryChannelEventsByIds(ids)) yield return new(item.Index, Decode(item.Value));
    }
    public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) =>
        Metadata.QueryChannelEvents(startTick, endTick).Select(Decode);
    public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) => Metadata.GetChannelEventRangeFingerprint(startTick, endTick);
    public bool TryQueryCachedOpaqueEvents(long startTick, long endTick, List<OpaqueMidiEventValue> destination)
    {
        List<DirectMidiChannelEventValue> values = [];
        if (!Metadata.TryQueryCachedChannelEvents(startTick, endTick, values)) return false;
        int initial = destination.Count;
        foreach (var value in values)
        {
            if (!TryDecodeCached(value, out var decoded))
            { destination.RemoveRange(initial, destination.Count - initial); return false; }
            destination.Add(decoded);
        }
        return true;
    }
    public bool TryQueryCachedOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, List<OpaqueMidiEventSourceMatch> destination)
    {
        List<DirectMidiChannelEventSourceMatch> values = [];
        if (!Metadata.TryQueryCachedChannelEventsByIds(ids, values)) return false;
        int initial = destination.Count;
        foreach (var value in values)
        {
            if (!TryDecodeCached(value.Value, out var decoded))
            { destination.RemoveRange(initial, destination.Count - initial); return false; }
            destination.Add(new(value.Index, decoded));
        }
        return true;
    }
    public void PrefetchOpaqueEvents(long startTick, long endTick, CancellationToken token)
    { foreach (var _ in QueryOpaqueEvents(startTick, endTick)) token.ThrowIfCancellationRequested(); }
    public void PrefetchOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token)
    {
        Metadata.PrefetchChannelEventsByIds(ids, token);
        foreach (var item in Metadata.ResolveIds(ids, token, requireAll: false)) _ = Decode(item.Value);
    }
    public IEnumerable<PureMidiContentRangeSummary> GetOpaqueEventRangeSummaries() => Metadata.GetChannelEventRangeSummaries();
    public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() => [];
    public DirectMidiNoteValue GetNote(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public int FindNoteIndex(MidoraId id) => -1;
    public int FindChannelEventIndex(MidoraId id) => -1;
    public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
    public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
    public ulong GetNoteRangeFingerprint(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => 0;
    public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) => 0;
    public bool TryQueryCachedNotes(long startTick, long endTick, int minimumKey, int maximumKey, List<DirectMidiNoteValue> destination) => true;
    public bool TryQueryCachedChannelEvents(long startTick, long endTick, List<DirectMidiChannelEventValue> destination) => true;
    public bool TryQueryCachedNotesByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiNoteSourceMatch> destination) => true;
    public bool TryQueryCachedChannelEventsByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiChannelEventSourceMatch> destination) => true;
    public void PrefetchNotes(long startTick, long endTick, int minimumKey, int maximumKey, CancellationToken token) { }
    public void PrefetchChannelEvents(long startTick, long endTick, CancellationToken token) { }
    public void PrefetchNotesByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }
    public void PrefetchChannelEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }

    [InlineArray(64)] internal struct PayloadBlock { private byte _first; }
    internal readonly record struct PayloadDescriptor(int FirstBlock, int Length);
    internal sealed record PayloadBank(int FirstOrdinal, BoundedImmutableValueSource<PayloadDescriptor> Descriptors,
        BoundedImmutableValueSource<PayloadBlock> Blocks, string Fingerprint)
    {
        internal long CacheIdentity { get; } = BoundedOpaquePayloadCache.AllocateIdentity();
        public ReadOnlyMemory<byte> Read(int ordinal)
        {
            if (BoundedOpaquePayloadCache.TryGet(CacheIdentity, ordinal, out var cached)) return cached;
            var descriptor = Descriptors[checked(ordinal - FirstOrdinal)];
            byte[] result = new byte[descriptor.Length];
            using IDisposable? payloadLease = BulkEditPreparationContext.Current?.Resources.BorrowPayload(result);
            for (int offset = 0; offset < result.Length; offset += 64)
            {
                if ((offset & 0x3fff) == 0) BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                var block = Blocks[descriptor.FirstBlock + offset / 64];
                ((ReadOnlySpan<byte>)block)[..Math.Min(64, result.Length - offset)].CopyTo(result.AsSpan(offset));
            }
            BoundedOpaquePayloadCache.Add(CacheIdentity, ordinal, result); return result;
        }
    }
    internal sealed class PayloadWriter : IDisposable
    {
        private readonly BoundedEditRecordStore<PayloadDescriptor> _descriptors;
        private readonly BoundedEditRecordStore<PayloadBlock> _blocks;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly BulkEditPreparationContext _scope;
        private readonly int _first;
        private bool _published;
        public PayloadWriter(int first, BulkEditPreparationContext scope)
        { _first = first; _scope = scope; _descriptors = new(scope.Resources); _blocks = new(scope.Resources); }
        public int Add(ReadOnlyMemory<byte> bytes)
        {
            _scope.Token.ThrowIfCancellationRequested();
            // The input is one borrowed immutable event, not a newly allocated
            // working buffer. A legal large message must not be rejected only
            // because it exceeds a page. Output blocks still use the shared
            // resident/spill budget and cancellation gates.
            using IDisposable payloadLease = _scope.Resources.BorrowPayload(bytes);
            int ordinal = checked(_first + _descriptors.Count);
            _descriptors.Add(new(_blocks.Count, bytes.Length), _scope.Token);
            _hash.AppendData(BitConverter.GetBytes(bytes.Length)); _hash.AppendData(bytes.Span);
            for (int offset = 0; offset < bytes.Length; offset += 64)
            {
                PayloadBlock block = default;
                bytes.Span.Slice(offset, Math.Min(64, bytes.Length - offset)).CopyTo(block);
                _blocks.Add(block, _scope.Token);
            }
            return ordinal;
        }
        public PayloadBank? Publish()
        {
            if (_descriptors.Count == 0) return null;
            _descriptors.Seal(); _blocks.Seal();
            _descriptors.SpillResidentPages(_scope.Token); _blocks.SpillResidentPages(_scope.Token);
            var bank = new PayloadBank(_first, new(_descriptors), new(_blocks), Convert.ToHexStringLower(_hash.GetHashAndReset()));
            _published = true; return bank;
        }
        public void Dispose()
        { _hash.Dispose(); if (!_published) { _descriptors.Dispose(); _blocks.Dispose(); } }
    }

    private sealed class OpaqueMetadataSource(OpaqueMidiEventObjectSource objects, OpaqueMidiEventQuerySnapshot query,
        string fingerprint, long payloadCache) : IPureMidiSegmentContentSource, IPureMidiCachedContentSource,
        IPureMidiContentRangeFingerprintSource, IPureMidiContentOverviewSource
    {
        public int NoteCount => 0;
        public int ChannelEventCount => objects.Count;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => fingerprint;
        private DirectMidiChannelEventValue Convert(OpaqueMidiEventValue value, int ordinal)
        {
            BoundedOpaquePayloadCache.Add(payloadCache, ordinal, value.Payload);
            return new(value.Id, value.Tick, (DirectMidiChannelEventKind)value.Kind, value.MetaType, ordinal, value.Order);
        }
        public DirectMidiChannelEventValue GetChannelEvent(int index) => Convert(objects.GetByOrdinal(index), index);
        public int FindChannelEventIndex(MidoraId id) => objects.TryFindOrdinalById(id, out int index) ? index : -1;
        public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(IReadOnlySet<MidoraId> ids)
        {
            foreach (var match in objects.QueryByIds(ids)) yield return new(match.Index, Convert(match.Value, match.Index));
        }
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick)
        {
            HashSet<MidoraId> batch = [];
            foreach (var value in query.QueryValues(startTick, endTick))
            {
                batch.Add(value.Id);
                if (batch.Count < 4096) continue;
                foreach (var match in QueryChannelEventsByIds(batch)) yield return match.Value;
                batch.Clear();
            }
            if (batch.Count != 0)
                foreach (var match in QueryChannelEventsByIds(batch)) yield return match.Value;
        }
        public DirectMidiNoteValue GetNote(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public OpaqueMidiEventValue GetOpaqueEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int FindNoteIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];
        public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() => [];
        public IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries() => query.GetRangeSummaries();
        public ulong GetNoteRangeFingerprint(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => 0;
        public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) => 0;
        public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) => query.GetRangeFingerprint(startTick, endTick);
        public bool TryQueryCachedChannelEvents(long startTick, long endTick, List<DirectMidiChannelEventValue> destination)
        {
            List<OpaqueMidiEventValue> values = [];
            if (!query.TryQueryValuesCached(startTick, endTick, values)) return false;
            HashSet<MidoraId> batch = [];
            List<DirectMidiChannelEventSourceMatch> matches = [];
            foreach (var value in values)
            {
                batch.Add(value.Id);
                if (batch.Count < 4096) continue;
                if (!TryQueryCachedChannelEventsByIds(batch, matches)) return false;
                batch.Clear();
            }
            if (batch.Count != 0 && !TryQueryCachedChannelEventsByIds(batch, matches)) return false;
            foreach (var value in matches) destination.Add(value.Value);
            return true;
        }
        public bool TryQueryCachedChannelEventsByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiChannelEventSourceMatch> destination)
        {
            List<OpaqueMidiEventSourceMatch> matches = [];
            if (!objects.TryQueryByIdsCached(ids, matches)) return false;
            foreach (var match in matches)
            {
                var value = match.Value;
                BoundedOpaquePayloadCache.Add(payloadCache, match.Index, value.Payload);
                destination.Add(new(match.Index, new(value.Id, value.Tick, (DirectMidiChannelEventKind)value.Kind,
                    value.MetaType, match.Index, value.Order)));
            }
            return true;
        }
        public bool TryQueryCachedNotes(long a, long b, int c, int d, List<DirectMidiNoteValue> target) => true;
        public bool TryQueryCachedOpaqueEvents(long a, long b, List<OpaqueMidiEventValue> target) => true;
        public bool TryQueryCachedNotesByIds(IReadOnlySet<MidoraId> ids, List<DirectMidiNoteSourceMatch> target) => true;
        public bool TryQueryCachedOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, List<OpaqueMidiEventSourceMatch> target) => true;
        public void PrefetchChannelEvents(long a, long b, CancellationToken token) { foreach (var _ in QueryChannelEvents(a, b)) token.ThrowIfCancellationRequested(); }
        public void PrefetchChannelEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { foreach (var value in objects.QueryByIds(ids)) { token.ThrowIfCancellationRequested(); BoundedOpaquePayloadCache.Add(payloadCache, value.Index, value.Value.Payload); } }
        public void PrefetchNotes(long a, long b, int c, int d, CancellationToken token) { }
        public void PrefetchOpaqueEvents(long a, long b, CancellationToken token) { }
        public void PrefetchNotesByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }
        public void PrefetchOpaqueEventsByIds(IReadOnlySet<MidoraId> ids, CancellationToken token) { }
    }
}
