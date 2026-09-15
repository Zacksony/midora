using Midora.Domain;

namespace Midora.Application;

internal readonly record struct BoundedCurveRow(int Ordinal, CurvePointSnapshotValue Value);
internal readonly record struct BoundedCurveChange(int Ordinal, CurvePointSnapshotValue Old,
    CurvePointSnapshotValue Value, bool IsDeleted, bool IsCreated);

/// <summary>One lane's globally resolved scalar plan; no mutable point objects.</summary>
internal sealed class BoundedCurvePointPlan : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private bool _transferred;
    public required IImmutableTimelineValueSource<TimelineValueEdit<CurvePointSnapshotValue>> Changes { get; init; }
    public required IImmutableTimelineValueSource<CurvePointSnapshotValue> Appended { get; init; }
    public required IReadOnlyList<MidoraId> OriginalSelection { get; init; }
    public required IReadOnlyList<MidoraId> ResultSelection { get; init; }
    public required IReadOnlyList<MidoraId> AffectedIds { get; init; }
    public required long MinimumResultTick { get; init; }
    public bool HasChanges => Changes.Count != 0 || Appended.Count != 0;

    public static BoundedCurvePointPlan Transform(CurvePointQuerySnapshot source,
        IReadOnlyCollection<MidoraId> selection,
        Func<IReadOnlyList<BoundedCurveRow>, Func<CurvePointSnapshotValue, CurvePointSnapshotValue?>> transform,
        bool resolveCollisions, bool duplicate, Func<MidoraId> allocateId,
        Action<CurvePointSnapshotValue> validate, bool rejectCollisions = false, bool formalOrderWins = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0) throw new ArgumentException("Select at least one point.", nameof(selection));
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, scope.Token);
        using var selected = BoundedEditSort.Sort(ReadSelection(),
            Comparer<BoundedCurveRow>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), scope.Resources, scope.Token);
        using var selectedView = new BoundedImmutableValueSource<BoundedCurveRow>(selected);
        int previousOrdinal = -1;
        foreach (BoundedCurveRow row in selected.ReadValues(scope.Token))
        {
            if (row.Ordinal == previousOrdinal) throw new ArgumentException("A point is selected more than once.", nameof(selection));
            previousOrdinal = row.Ordinal;
        }
        Func<CurvePointSnapshotValue, CurvePointSnapshotValue?> convert = transform(selectedView);
        using var planned = new BoundedEditRecordStore<BoundedCurveChange>(scope.Resources);
        using var deleted = new BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>>(scope.Resources);
        using var original = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        int creationOrdinal = source.Count;
        int processed = 0;
        foreach (BoundedCurveRow row in selected.ReadValues(scope.Token))
        {
            scope.Checkpoint(processed++, selected.Count);
            original.Add(row.Value.Id, scope.Token);
            affected.Add(row.Value.Id, scope.Token);
            CurvePointSnapshotValue? converted = convert(row.Value);
            if (converted is not { } value)
            {
                if (!duplicate) deleted.Add(new(row.Ordinal, true, row.Value), scope.Token);
                continue;
            }
            validate(value);
            if (duplicate) value = value with { Id = allocateId() };
            affected.Add(value.Id, scope.Token);
            planned.Add(new(duplicate ? creationOrdinal++ : row.Ordinal, row.Value, value, false, duplicate), scope.Token);
        }
        planned.Seal();
        original.Seal();
        return Resolve(source, selectedView, planned, deleted, original, affected,
            resolveCollisions, duplicate, scope, rejectCollisions, formalOrderWins);

        IEnumerable<BoundedCurveRow> ReadSelection()
        {
            int count = 0;
            foreach (MidoraId id in selection)
            {
                scope.Checkpoint(count++, selection.Count, TimelineEditPreparationPhase.ResolvingSelection);
                if (id == default || !source.TryFindOrdinalById(id, out int ordinal))
                    throw new ArgumentException("A selected point is not present in this lane.", nameof(selection));
                yield return new(ordinal, source.GetByOrdinal(ordinal));
            }
        }
    }

    public static BoundedCurvePointPlan Append(CurvePointQuerySnapshot source,
        IEnumerable<CurvePointSnapshotValue> values, Func<MidoraId> allocateId,
        Action<CurvePointSnapshotValue> validate)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, scope.Token);
        using var empty = new BoundedEditRecordStore<BoundedCurveRow>(scope.Resources);
        empty.Seal();
        using var selectedView = new BoundedImmutableValueSource<BoundedCurveRow>(empty);
        using var planned = new BoundedEditRecordStore<BoundedCurveChange>(scope.Resources);
        using var deleted = new BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>>(scope.Resources);
        using var original = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        int ordinal = source.Count;
        foreach (CurvePointSnapshotValue candidate in values)
        {
            scope.Token.ThrowIfCancellationRequested();
            validate(candidate);
            CurvePointSnapshotValue value = candidate with { Id = allocateId() };
            planned.Add(new(ordinal++, default, value, false, true), scope.Token);
            affected.Add(value.Id, scope.Token);
        }
        planned.Seal();
        original.Seal();
        return Resolve(source, selectedView, planned, deleted, original, affected,
            resolveCollisions: true, duplicate: true, scope);
    }

    private static BoundedCurvePointPlan Resolve(CurvePointQuerySnapshot source,
        IReadOnlyList<BoundedCurveRow> selected,
        BoundedEditRecordStore<BoundedCurveChange> planned,
        BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>> deleted,
        BoundedEditRecordStore<MidoraId> original,
        BoundedEditRecordStore<MidoraId> affected,
        bool resolveCollisions, bool duplicate, BulkEditPreparationContext scope,
        bool rejectCollisions = false, bool formalOrderWins = false)
    {
        using var ordered = BoundedEditSort.Sort(planned,
            Comparer<BoundedCurveChange>.Create((a, b) =>
            {
                int comparison = a.Value.Tick.CompareTo(b.Value.Tick);
                return comparison == 0 ? a.Ordinal.CompareTo(b.Ordinal) : comparison;
            }), scope.Resources, scope.Token);
        using var retained = new BoundedEditRecordStore<BoundedCurveChange>(scope.Resources);
        BoundedCurveChange? pending = null;
        int processed = 0;
        foreach (BoundedCurveChange candidate in ordered.ReadValues(scope.Token))
        {
            scope.Checkpoint(processed++, ordered.Count, TimelineEditPreparationPhase.ResolvingCollisions);
            if (!resolveCollisions) { retained.Add(candidate, scope.Token); continue; }
            if (pending is not { } prior) { pending = candidate; continue; }
            if (prior.Value.Tick != candidate.Value.Tick)
            {
                KeepWinner(prior);
                pending = candidate;
                continue;
            }
            // The last edited point in formal collection order wins.
            if (rejectCollisions) throw new InvalidOperationException("The point edit would create an overlap.");
            if (!prior.IsCreated) deleted.Add(new(prior.Ordinal, true, prior.Old), scope.Token);
            pending = candidate;
        }
        if (pending is { } final) KeepWinner(final);
        scope.Checkpoint(ordered.Count, ordered.Count, TimelineEditPreparationPhase.ResolvingCollisions);
        scope.Token.ThrowIfCancellationRequested();
        retained.Seal();
        deleted.Seal();
        affected.Seal();
        using var formal = BoundedEditSort.Sort(retained,
            Comparer<BoundedCurveChange>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), scope.Resources, scope.Token);
        using var updates = new BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>>(scope.Resources);
        using var appended = new BoundedEditRecordStore<CurvePointSnapshotValue>(scope.Resources);
        using var result = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        long minimum = long.MaxValue;
        foreach (BoundedCurveChange value in formal.ReadValues(scope.Token))
        {
            result.Add(value.Value.Id, scope.Token);
            minimum = Math.Min(minimum, value.Value.Tick);
            if (value.IsCreated) appended.Add(value.Value, scope.Token);
            else if (value.Old != value.Value) updates.Add(new(value.Ordinal, false, value.Value), scope.Token);
        }
        updates.AddRange(deleted, scope.Token);
        updates.Seal();
        appended.Seal();
        result.Seal();
        using var finalChanges = BoundedEditSort.Sort(updates,
            Comparer<TimelineValueEdit<CurvePointSnapshotValue>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            scope.Resources, scope.Token);
        // These small copies are page streams, never full result arrays. They
        // transfer ownership out of the intermediate stores' using scopes.
        List<IDisposable> owned = [];
        try
        {
            var changeStore = new BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>>(scope.Resources);
            BoundedTimelineEditValueSource<CurvePointSnapshotValue> changesSource;
            try
            {
                changeStore.AddRange(finalChanges.ReadValues(scope.Token), scope.Token);
                changeStore.Seal(); changeStore.SpillResidentPages(scope.Token);
                changesSource = new(changeStore); owned.Add(changesSource);
            }
            catch { changeStore.Dispose(); throw; }
            var appendedValues = Freeze(appended);
            var appendedSource = new BoundedIndexedTimelineValueSource<CurvePointSnapshotValue>(
                appendedValues, static point => point.Id, scope.Resources, scope.Token);
            owned.Add(appendedSource);
            var originalSource = Freeze(original);
            var resultSource = Freeze(result);
            var affectedSource = Freeze(affected);
            var plan = new BoundedCurvePointPlan
            {
                Changes = changesSource, Appended = appendedSource,
                OriginalSelection = originalSource, ResultSelection = resultSource,
                AffectedIds = affectedSource, MinimumResultTick = minimum
            };
            plan._owned.AddRange(owned);
            return plan;
        }
        catch { foreach (IDisposable value in owned) value.Dispose(); throw; }

        BoundedImmutableValueSource<T> Freeze<T>(IEnumerable<T> values) where T : unmanaged
        {
            var store = new BoundedEditRecordStore<T>(scope.Resources);
            try
            {
                store.AddRange(values, scope.Token);
                store.Seal();
                store.SpillResidentPages(scope.Token);
                var provider = new BoundedImmutableValueSource<T>(store);
                owned.Add(provider);
                return provider;
            }
            catch { store.Dispose(); throw; }
        }

        void KeepWinner(BoundedCurveChange winner)
        {
            int winningOrdinal = winner.Ordinal;
            if (formalOrderWins)
            {
                foreach (CurvePointSnapshotValue current in source.EnumerateExactTick(winner.Value.Tick))
                {
                    scope.Token.ThrowIfCancellationRequested();
                    if (!source.TryFindOrdinalById(current.Id, out int ordinal))
                        throw new InvalidOperationException("A point's immutable ordinal is missing.");
                    if (ContainsOrdinal(selected, ordinal)) continue;
                    winningOrdinal = Math.Max(winningOrdinal, ordinal);
                }
            }
            foreach (CurvePointSnapshotValue current in source.EnumerateExactTick(winner.Value.Tick))
            {
                scope.Token.ThrowIfCancellationRequested();
                if (!source.TryFindOrdinalById(current.Id, out int ordinal))
                    throw new InvalidOperationException("A point's immutable ordinal is missing.");
                if (!duplicate && ContainsOrdinal(selected, ordinal)) continue;
                if (formalOrderWins && ordinal == winningOrdinal) continue;
                if (rejectCollisions) throw new InvalidOperationException("The point edit would create an overlap.");
                deleted.Add(new(ordinal, true, current), scope.Token);
                affected.Add(current.Id, scope.Token);
            }
            if (winningOrdinal == winner.Ordinal) retained.Add(winner, scope.Token);
            else if (!winner.IsCreated) deleted.Add(new(winner.Ordinal, true, winner.Old), scope.Token);
        }
    }

    private static bool ContainsOrdinal(IReadOnlyList<BoundedCurveRow> values, int ordinal)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (values[middle].Ordinal < ordinal) low = middle + 1;
            else high = middle;
        }
        return low < values.Count && values[low].Ordinal == ordinal;
    }

    public IPreparedProjectEdit Own(IPreparedProjectEdit prepared)
    {
        _transferred = true;
        return new OwnedBoundedPreparedEdit(prepared, _owned);
    }

    public void Dispose()
    {
        if (!_transferred) foreach (IDisposable value in _owned) value.Dispose();
    }
}

internal sealed class OwnedBoundedPreparedEdit(IPreparedProjectEdit source, IReadOnlyList<IDisposable> resources)
    : IPreparedProjectEdit, IPreparedProjectEditPublicationGate, IDisposable
{
    private bool _published;
    public bool HasChanges => source.HasChanges;
    public ProjectChangeSet Changes => source.Changes;
    public void ValidateForPublication(MidoraProject project)
    {
        if (source is IPreparedProjectEditPublicationGate gate) gate.ValidateForPublication(project);
    }
    public void Apply(MidoraProject project) { source.Apply(project); _published = true; }
    public void Undo(MidoraProject project) => source.Undo(project);
    public void Dispose()
    {
        if (source is IDisposable disposable) disposable.Dispose();
        if (!_published) foreach (IDisposable resource in resources) resource.Dispose();
    }
}
