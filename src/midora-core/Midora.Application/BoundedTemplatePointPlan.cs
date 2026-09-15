using Midora.Domain;

namespace Midora.Application;

/// <summary>SubVoice event edit with bounded records and exact multi-target collisions.</summary>
internal sealed class BoundedTemplatePointPlan : IDisposable
{
    internal readonly record struct Row(int Ordinal, TemplateEventSnapshotValue Old,
        TemplateEventSnapshotValue Value, bool Created, bool Incumbent, int First, int Second, int DetailCount);
    private readonly List<IDisposable> _owned;
    private bool _transferred;
    private BoundedTemplatePointPlan(List<IDisposable> owned,
        IImmutableTimelineValueSource<TimelineValueEdit<TemplateEventSnapshotValue>> changes,
        IImmutableTimelineValueSource<TemplateEventSnapshotValue> appended, IReadOnlyList<MidoraId> original,
        IReadOnlyList<MidoraId> result, IReadOnlyList<MidoraId> affected, long maximumEnd)
        => (_owned, Changes, Appended, OriginalSelection, ResultSelection, AffectedIds, MaximumEndTick) =
            (owned, changes, appended, original, result, affected, maximumEnd);
    public IImmutableTimelineValueSource<TimelineValueEdit<TemplateEventSnapshotValue>> Changes { get; }
    public IImmutableTimelineValueSource<TemplateEventSnapshotValue> Appended { get; }
    public IReadOnlyList<MidoraId> OriginalSelection { get; }
    public IReadOnlyList<MidoraId> ResultSelection { get; }
    public IReadOnlyList<MidoraId> AffectedIds { get; }
    public long MaximumEndTick { get; }
    public bool HasChanges => Changes.Count != 0 || Appended.Count != 0;

    public static BoundedTemplatePointPlan Transform(TemplateEventQuerySnapshot source,
        IReadOnlyCollection<MidoraId> ids, Func<TemplateEventSnapshotValue, bool> valid,
        Func<IReadOnlyList<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>,
            Func<TemplateEventSnapshotValue, TemplateEventSnapshotValue?>> transform,
        bool resolveCollisions, bool duplicate, bool formalOrderWins, Func<MidoraId> allocate,
        Action<TemplateEventSnapshotValue> validate, bool rejectCollisions = false)
    {
        var context = BulkEditPreparationContext.Current!;
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, valid, context.Resources, context.Token);
        Func<TemplateEventSnapshotValue, TemplateEventSnapshotValue?> convert = transform(selected);
        using var planned = new BoundedEditRecordStore<Row>(context.Resources);
        using var deleted = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(context.Resources);
        using var original = new BoundedEditRecordStore<MidoraId>(context.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(context.Resources);
        int ordinal = source.Count, processed = 0;
        foreach (var item in selected)
        {
            context.Checkpoint(processed++, selected.Count);
            original.Add(item.Value.Id, context.Token); affected.Add(item.Value.Id, context.Token);
            if (convert(item.Value) is not { } value)
            {
                if (!duplicate) deleted.Add(new(item.Ordinal, true, item.Value), context.Token);
                continue;
            }
            validate(value);
            if (duplicate) value = value with { Id = allocate() };
            var details = TemplateEventExactCollision.Details(value);
            planned.Add(new(duplicate ? ordinal++ : item.Ordinal, item.Value, value, duplicate, false,
                details.First, details.Second, details.Count), context.Token);
            affected.Add(value.Id, context.Token);
        }
        planned.Seal(); original.Seal();
        return Resolve(source, selected, planned, deleted, original, affected,
            resolveCollisions, duplicate, formalOrderWins, rejectCollisions);
    }

    public static BoundedTemplatePointPlan Append(TemplateEventQuerySnapshot source,
        IEnumerable<TemplateEventSnapshotValue> values, Func<MidoraId> allocate, Action<TemplateEventSnapshotValue> validate)
    {
        var context = BulkEditPreparationContext.Current!;
        using var selected = new BoundedEditRecordStore<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>(context.Resources);
        selected.Seal();
        using var view = new BoundedImmutableValueSource<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>(selected);
        using var planned = new BoundedEditRecordStore<Row>(context.Resources);
        using var deleted = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(context.Resources);
        using var original = new BoundedEditRecordStore<MidoraId>(context.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(context.Resources);
        int ordinal = source.Count;
        foreach (var candidate in values)
        {
            context.Token.ThrowIfCancellationRequested(); validate(candidate);
            var value = candidate with { Id = allocate() };
            var details = TemplateEventExactCollision.Details(value);
            planned.Add(new(ordinal++, default, value, true, false, details.First, details.Second, details.Count), context.Token);
            affected.Add(value.Id, context.Token);
        }
        planned.Seal(); original.Seal();
        return Resolve(source, view, planned, deleted, original, affected, true, true, false);
    }

    internal static BoundedTemplatePointPlan Build(TemplateEventQuerySnapshot source, IEnumerable<Row> rows)
    {
        var context = BulkEditPreparationContext.Current!;
        source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
        using var planned = new BoundedEditRecordStore<Row>(context.Resources);
        using var selection = new BoundedEditRecordStore<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>(context.Resources);
        using var deleted = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(context.Resources);
        using var original = new BoundedEditRecordStore<MidoraId>(context.Resources);
        using var affected = new BoundedEditRecordStore<MidoraId>(context.Resources);
        foreach (var row in rows)
        {
            context.Token.ThrowIfCancellationRequested();
            planned.Add(row, context.Token); affected.Add(row.Value.Id, context.Token);
            if (!row.Created) selection.Add(new(row.Ordinal, row.Old), context.Token);
        }
        planned.Seal(); selection.Seal();
        using var sorted = BoundedEditSort.Sort(selection,
            Comparer<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            context.Resources, context.Token);
        using var view = new BoundedImmutableValueSource<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>(sorted);
        foreach (var row in view) original.Add(row.Value.Id, context.Token);
        original.Seal();
        return Resolve(source, view, planned, deleted, original, affected, true, false, false);
    }

    private static BoundedTemplatePointPlan Resolve(TemplateEventQuerySnapshot source,
        IReadOnlyList<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>> selected,
        BoundedEditRecordStore<Row> planned,
        BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>> deleted,
        BoundedEditRecordStore<MidoraId> original, BoundedEditRecordStore<MidoraId> affected,
        bool resolveCollisions, bool duplicate, bool formalOrderWins, bool rejectCollisions = false)
    {
        var context = BulkEditPreparationContext.Current!;
        source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
        using var candidates = new BoundedEditRecordStore<Row>(context.Resources);
        using var sorted = BoundedEditSort.Sort(planned, Comparer<Row>.Create((a, b) =>
        {
            int c = a.Value.Tick.CompareTo(b.Value.Tick);
            return c != 0 ? c : b.Ordinal.CompareTo(a.Ordinal);
        }), context.Resources, context.Token);
        // Only MIDI target kinds at one tick are resident, never that tick's
        // unbounded number of events. The set has at most the MIDI target universe.
        HashSet<int> targets = [];
        long currentTick = -1;
        foreach (Row row in sorted.ReadValues(context.Token))
        {
            if (row.Value.Tick != currentTick)
            {
                AddIncumbents(); targets.Clear(); currentTick = row.Value.Tick;
            }
            if (row.DetailCount > 0) targets.Add(row.First);
            if (row.DetailCount > 1) targets.Add(row.Second);
            candidates.Add(row, context.Token);
        }
        AddIncumbents(); candidates.Seal();
        using var priority = BoundedEditSort.Sort(candidates, Comparer<Row>.Create((a, b) =>
        {
            int c = a.Value.Tick.CompareTo(b.Value.Tick);
            if (c == 0) c = (a.Value.Kind == TemplateEventKind.Note).CompareTo(b.Value.Kind == TemplateEventKind.Note);
            if (c == 0 && a.Value.Kind == TemplateEventKind.Note)
            {
                // Notes keep every existing occupant, then the first newcomer.
                // Non-note events below use the opposite, later-wins rule.
                c = b.Incumbent.CompareTo(a.Incumbent);
                return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
            }
            if (c == 0 && !formalOrderWins) c = a.Incumbent.CompareTo(b.Incumbent);
            return c != 0 ? c : b.Ordinal.CompareTo(a.Ordinal);
        }), context.Resources, context.Token);
        using var retained = new BoundedEditRecordStore<Row>(context.Resources);
        HashSet<int> claimed = [];
        currentTick = -1;
        int processed = 0;
        foreach (Row row in priority.ReadValues(context.Token))
        {
            context.Checkpoint(processed++, priority.Count, TimelineEditPreparationPhase.ResolvingCollisions);
            if (row.Value.Tick != currentTick) { currentTick = row.Value.Tick; claimed.Clear(); }
            bool discard = resolveCollisions && ((row.DetailCount > 0 && claimed.Contains(row.First))
                || (row.DetailCount > 1 && claimed.Contains(row.Second)));
            if (row.Incumbent && row.Value.Kind == TemplateEventKind.Note) discard = false;
            if (discard)
            {
                if (rejectCollisions) throw new InvalidOperationException("The Instrument Change transform would create overlapping points.");
                if (!row.Created) deleted.Add(new(row.Ordinal, true, row.Old), context.Token);
                affected.Add(row.Value.Id, context.Token);
            }
            else
            {
                if (row.DetailCount > 0) claimed.Add(row.First);
                if (row.DetailCount > 1) claimed.Add(row.Second);
                if (!row.Incumbent) retained.Add(row, context.Token);
            }
        }
        context.Checkpoint(priority.Count, priority.Count, TimelineEditPreparationPhase.ResolvingCollisions);
        context.Token.ThrowIfCancellationRequested();
        retained.Seal(); deleted.Seal(); affected.Seal();
        using var formal = BoundedEditSort.Sort(retained, Comparer<Row>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            context.Resources, context.Token);
        using var updates = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(context.Resources);
        using var appended = new BoundedEditRecordStore<TemplateEventSnapshotValue>(context.Resources);
        using var result = new BoundedEditRecordStore<MidoraId>(context.Resources);
        long maximumEnd = 0;
        foreach (Row row in formal.ReadValues(context.Token))
        {
            result.Add(row.Value.Id, context.Token);
            maximumEnd = Math.Max(maximumEnd, checked(row.Value.Tick + (row.Value.Kind == TemplateEventKind.Note ? row.Value.LengthTicks : 1)));
            if (row.Created) appended.Add(row.Value, context.Token);
            else if (row.Old != row.Value) updates.Add(new(row.Ordinal, false, row.Value), context.Token);
        }
        updates.AddRange(deleted, context.Token); updates.Seal(); appended.Seal(); result.Seal();
        using var orderedChanges = BoundedEditSort.Sort(updates,
            Comparer<TimelineValueEdit<TemplateEventSnapshotValue>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), context.Resources, context.Token);
        List<IDisposable> owned = [];
        try
        {
            var changeStore = new BoundedEditRecordStore<TimelineValueEdit<TemplateEventSnapshotValue>>(context.Resources);
            BoundedTimelineEditValueSource<TemplateEventSnapshotValue> changes;
            try
            {
                changeStore.AddRange(orderedChanges.ReadValues(context.Token), context.Token);
                changeStore.Seal(); changeStore.SpillResidentPages(context.Token);
                changes = new(changeStore); owned.Add(changes);
            }
            catch { changeStore.Dispose(); throw; }
            var appendedValues = Freeze(appended);
            var indexed = new BoundedIndexedTimelineValueSource<TemplateEventSnapshotValue>(appendedValues, static v => v.Id, context.Resources, context.Token);
            owned.Add(indexed);
            return new(owned, changes, indexed, Freeze(original), Freeze(result), Freeze(affected), maximumEnd);
        }
        catch { foreach (var value in owned) value.Dispose(); throw; }

        BoundedImmutableValueSource<T> Freeze<T>(BoundedEditRecordStore<T> input) where T : unmanaged
        {
            var store = new BoundedEditRecordStore<T>(context.Resources);
            try
            {
                store.AddRange(input.ReadValues(context.Token), context.Token); store.Seal(); store.SpillResidentPages(context.Token);
                var value = new BoundedImmutableValueSource<T>(store); owned.Add(value); return value;
            }
            catch { store.Dispose(); throw; }
        }
        void AddIncumbents()
        {
            if (!resolveCollisions || currentTick < 0) return;
            foreach (var old in source.EnumerateEventExactTick(currentTick))
                Add(old);
            foreach (int detail in targets)
                if (detail is <= -1 and >= -128)
                    foreach (var old in source.EnumerateNoteExactStart(currentTick, -1 - detail)) Add(old);

            void Add(TemplateEventSnapshotValue old)
            {
                context.Token.ThrowIfCancellationRequested();
                if (!source.TryFindOrdinalById(old.Id, out int ordinal)) throw new InvalidOperationException("Missing event ordinal.");
                if (!duplicate && IsSelected(ordinal)) return;
                var d = TemplateEventExactCollision.Details(old);
                int first = 0, second = 0, count = 0;
                if (d.Count > 0 && targets.Contains(d.First)) { first = d.First; count++; }
                if (d.Count > 1 && targets.Contains(d.Second)) { if (count == 0) first = d.Second; else second = d.Second; count++; }
                if (count > 0) candidates.Add(new(ordinal, old, old, false, true, first, second, count), context.Token);
            }
        }
        bool IsSelected(int ordinal)
        {
            int low = 0, high = selected.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (selected[mid].Ordinal < ordinal) low = mid + 1; else high = mid;
            }
            return low < selected.Count && selected[low].Ordinal == ordinal;
        }
    }

    public IPreparedProjectEdit Own(IPreparedProjectEdit edit)
    {
        _transferred = true; return new OwnedBoundedPreparedEdit(edit, _owned);
    }
    public void Dispose() { if (!_transferred) foreach (var value in _owned) value.Dispose(); }
}
