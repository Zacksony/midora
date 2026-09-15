using System.Collections.Immutable;

namespace Midora.Domain;

/// <summary>
/// Explicit editing association, never an additional MIDI event. BankLsbEventId
/// is absent for SubVoice, whose Bank event already owns both bank components.
/// Tick and musical values are deliberately not duplicated here.
/// </summary>
public readonly record struct InstrumentChange(
    MidoraId Id,
    MidoraId BankEventId,
    MidoraId? BankLsbEventId,
    MidoraId ProgramEventId)
{
    public void ValidateShape(bool directMidi)
    {
        if (Id == default || BankEventId == default || ProgramEventId == default
            || directMidi != BankLsbEventId.HasValue || BankLsbEventId == default(MidoraId)
            || Id == BankEventId || Id == ProgramEventId || Id == BankLsbEventId
            || BankEventId == ProgramEventId || BankEventId == BankLsbEventId
            || ProgramEventId == BankLsbEventId)
            throw new ArgumentException("Invalid Instrument Change member identities.");
    }

    public IEnumerable<MidoraId> MemberIds
    {
        get
        {
            yield return BankEventId;
            if (BankLsbEventId is { } lsb) yield return lsb;
            yield return ProgramEventId;
        }
    }
}

/// <summary>
/// Persistent association root with a reverse member index. A one-point edit
/// copies only tree paths, not all associations or any raw event collection.
/// Identity ordering is for lookup/serialization only, never musical ordering.
/// </summary>
public interface IInstrumentChangeStorage
{
    int Count { get; }
    IEnumerable<InstrumentChange> Values { get; }
    bool TryGet(MidoraId id, out InstrumentChange value);
    bool TryGetByMember(MidoraId id, out InstrumentChange value);
    IInstrumentChangeStorage Add(InstrumentChange value);
    IInstrumentChangeStorage Remove(MidoraId id);
}

public sealed class InstrumentChangeSet
{
    public static InstrumentChangeSet Empty { get; } = new(MemoryStorage.Empty);
    public IInstrumentChangeStorage Storage { get; }
    private readonly IInstrumentChangeStorage _pending;
    private readonly long? _validatedRevision;
    public InstrumentChangeSet(IInstrumentChangeStorage storage)
        : this(storage, MemoryStorage.Empty, null) { }
    private InstrumentChangeSet(IInstrumentChangeStorage storage, IInstrumentChangeStorage pending, long? revision)
    { Storage = storage; _pending = pending; _validatedRevision = revision; }
    public int Count => Storage.Count;
    public IEnumerable<InstrumentChange> Values => Storage.Values;
    internal IEnumerable<InstrumentChange> PendingReconciliation => _pending.Values;
    internal bool IsValidatedFor(long revision) => _validatedRevision == revision;
    internal InstrumentChangeSet ValidatedAt(long revision, bool complete = false) =>
        new(Storage, complete ? MemoryStorage.Empty : _pending, revision);
    internal InstrumentChangeSet WithDeferredRemovals(IInstrumentChangeStorage retained, IInstrumentChangeStorage removed) =>
        new(retained, removed, null);
    internal InstrumentChangeSet DeferRemoval(MidoraId id)
    {
        if (!TryGet(id, out var value)) return this;
        return new(Storage.Remove(id), _pending.TryGet(id, out _) ? _pending : _pending.Add(value), null);
    }
    public bool TryGet(MidoraId id, out InstrumentChange value) => Storage.TryGet(id, out value);
    public bool TryGetByMember(MidoraId memberId, out InstrumentChange value) => Storage.TryGetByMember(memberId, out value);
    public InstrumentChangeSet Add(InstrumentChange value, bool directMidi)
    {
        value.ValidateShape(directMidi);
        if (TryGet(value.Id, out _)) throw new ArgumentException("Duplicate Instrument Change identity.");
        foreach (var id in value.MemberIds)
            if (TryGetByMember(id, out _)) throw new ArgumentException("An event already belongs to an Instrument Change.");
        return new(Storage.Add(value), _pending.Remove(value.Id), null);
    }
    public InstrumentChangeSet Remove(MidoraId id) =>
        !TryGet(id, out _) ? this : new(Storage.Remove(id), _pending.Remove(id), null);

    private sealed record MemoryStorage(ImmutableSortedDictionary<MidoraId, InstrumentChange> Groups,
        ImmutableDictionary<MidoraId, MidoraId> Members) : IInstrumentChangeStorage
    {
        internal static readonly MemoryStorage Empty = new(ImmutableSortedDictionary<MidoraId, InstrumentChange>.Empty,
            ImmutableDictionary<MidoraId, MidoraId>.Empty);
        public int Count => Groups.Count;
        public IEnumerable<InstrumentChange> Values => Groups.Values;
        public bool TryGet(MidoraId id, out InstrumentChange value) => Groups.TryGetValue(id, out value);
        public bool TryGetByMember(MidoraId id, out InstrumentChange value)
        { value = default; return Members.TryGetValue(id, out var owner) && TryGet(owner, out value); }
        public IInstrumentChangeStorage Add(InstrumentChange value)
        {
            var members = Members;
            foreach (var id in value.MemberIds) members = members.Add(id, value.Id);
            return new MemoryStorage(Groups.Add(value.Id, value), members);
        }
        public IInstrumentChangeStorage Remove(MidoraId id)
        {
            if (!TryGet(id, out var value)) return this;
            var members = Members;
            foreach (var member in value.MemberIds) members = members.Remove(member);
            return new MemoryStorage(Groups.Remove(id), members);
        }
    }
}
