using Midora.Domain;

namespace Midora.Application;

/// <summary>Spill-backed associations and reverse member identities. Untouched
/// pages are shared with Undo roots; no per-association managed tree is retained.</summary>
internal sealed class BoundedInstrumentChangeStorage : IInstrumentChangeStorage
{
    private readonly record struct Row(InstrumentChange Value, bool Removed = false);
    private readonly record struct Member(MidoraId Id, MidoraId Group);
    private static readonly IComparer<Row> RowOrder = Comparer<Row>.Create(static (a, b) => a.Value.Id.CompareTo(b.Value.Id));
    private static readonly IComparer<Member> MemberOrder = Comparer<Member>.Create(static (a, b) => a.Id.CompareTo(b.Id));
    private readonly BoundedDirectMidiIndex<Row> _rows;
    private readonly BoundedDirectMidiIndex<Member> _members;
    private BoundedInstrumentChangeStorage(BoundedDirectMidiIndex<Row> rows, BoundedDirectMidiIndex<Member> members)
        => (_rows, _members) = (rows, members);
    public int Count => _rows.Count;
    public IEnumerable<InstrumentChange> Values => _rows.Enumerate(BulkEditPreparationContext.Current?.Token ?? default).Select(static row => row.Value);
    public bool TryGet(MidoraId id, out InstrumentChange value)
    { bool found = _rows.TryGet(new(new(id, default, null, default)), out var row); value = row.Value; return found; }
    public bool TryGetByMember(MidoraId id, out InstrumentChange value)
    { value = default; return _members.TryGet(new(id, default), out var row) && TryGet(row.Group, out value); }
    public static BoundedInstrumentChangeStorage Create(IEnumerable<InstrumentChange> values)
    {
        using var scope = BulkEditPreparationContext.Enter();
        using var rows = BoundedEditSort.Sort(values.Select(static group => new Row(group)), RowOrder, scope.Resources, scope.Token);
        using var members = BoundedEditSort.Sort(ReadMembers(), MemberOrder, scope.Resources, scope.Token);
        var result = new BoundedInstrumentChangeStorage(
            BoundedDirectMidiIndex<Row>.Empty(RowOrder, static _ => (0, 1, 0), static row => (ulong)row.Value.Id.Value)
                .ApplyPatch(UniqueRows(), scope.Resources, scope.Token),
            BoundedDirectMidiIndex<Member>.Empty(MemberOrder, static _ => (0, 1, 0), static row => (ulong)row.Id.Value)
                .ApplyPatch(UniqueMembers(), scope.Resources, scope.Token));
        return result;
        IEnumerable<Member> ReadMembers()
        { foreach (var row in rows) { row.Value.ValidateShape(row.Value.BankLsbEventId.HasValue); foreach (var id in row.Value.MemberIds) yield return new(id, row.Value.Id); } }
        IEnumerable<Row> UniqueRows()
        { MidoraId prior = default; foreach (var row in rows) { if (prior == row.Value.Id) throw new ArgumentException("Duplicate Instrument Change identity."); prior = row.Value.Id; yield return row; } }
        IEnumerable<Member> UniqueMembers()
        { MidoraId prior = default; foreach (var row in members) { if (prior == row.Id) throw new ArgumentException("An event belongs to more than one Instrument Change."); prior = row.Id; yield return row; } }
    }
    public IInstrumentChangeStorage Add(InstrumentChange value)
    {
        using var scope = BulkEditPreparationContext.Enter();
        return new BoundedInstrumentChangeStorage(_rows.ApplyPatch([new(value)], scope.Resources, scope.Token),
            _members.ApplyPatch(value.MemberIds.Order().Select(id => new Member(id, value.Id)), scope.Resources, scope.Token));
    }
    public IInstrumentChangeStorage Remove(MidoraId id) => RemoveMany([id]);
    internal static BoundedInstrumentChangeStorage AddMany(InstrumentChangeSet existing, IEnumerable<InstrumentChange> values)
    {
        using var scope = BulkEditPreparationContext.Enter();
        var source = existing.Storage as BoundedInstrumentChangeStorage ?? Create(existing.Values);
        using var rows = BoundedEditSort.Sort(values.Select(static value => new Row(value)), RowOrder, scope.Resources, scope.Token);
        using var members = BoundedEditSort.Sort(rows.SelectMany(static row => row.Value.MemberIds.Select(id => new Member(id, row.Value.Id))),
            MemberOrder, scope.Resources, scope.Token);
        MidoraId previousId = default;
        foreach (var row in rows)
        { row.Value.ValidateShape(row.Value.BankLsbEventId.HasValue); if (previousId == row.Value.Id || source.TryGet(row.Value.Id, out _)) throw new InvalidOperationException("Duplicate Instrument Change identity."); previousId = row.Value.Id; }
        MidoraId prior = default;
        foreach (var member in members)
        {
            if (member.Id == prior || source.TryGetByMember(member.Id, out _)) throw new InvalidOperationException("An event already belongs to an Instrument Change.");
            prior = member.Id;
        }
        return new(source._rows.ApplyPatch(rows, scope.Resources, scope.Token), source._members.ApplyPatch(members, scope.Resources, scope.Token));
    }
    internal BoundedInstrumentChangeStorage RemoveMany(IEnumerable<MidoraId> ids)
    {
        using var scope = BulkEditPreparationContext.Enter();
        using var groups = BoundedEditSort.Sort(ReadGroups(), RowOrder, scope.Resources, scope.Token);
        using var members = BoundedEditSort.Sort(groups.SelectMany(static row => row.Value.MemberIds.Select(id => new Member(id, default))),
            MemberOrder, scope.Resources, scope.Token);
        return new(_rows.ApplyPatch(groups, scope.Resources, scope.Token, static row => row.Removed),
            _members.ApplyPatch(members, scope.Resources, scope.Token, static row => row.Group == default));
        IEnumerable<Row> ReadGroups()
        { foreach (var id in ids) { scope.Token.ThrowIfCancellationRequested(); if (TryGet(id, out var group)) yield return new(group, true); } }
    }
    internal static InstrumentChangeSet RemoveAffected(InstrumentChangeSet groups, IEnumerable<MidoraId> invalid)
    {
        using var scope = BulkEditPreparationContext.Enter();
        using var sorted = BoundedEditSort.Sort(invalid, Comparer<MidoraId>.Default, scope.Resources, scope.Token);
        if (sorted.Count == 0) return groups;
        IEnumerable<MidoraId> Unique()
        { MidoraId previous = default; foreach (var id in sorted) { if (id != previous) yield return id; previous = id; } }
        var source = groups.Storage as BoundedInstrumentChangeStorage ?? Create(groups.Values);
        var removed = Create(Unique().Select(id => { source.TryGet(id, out var value); return value; }).Concat(groups.PendingReconciliation));
        return groups.WithDeferredRemovals(source.RemoveMany(Unique()), removed);
    }
}
