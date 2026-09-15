using Midora.Domain;

namespace Midora.Application;

/// <summary>Scalar-only, globally collision-resolved note plans. Sources and
/// results may spill; no live note facade or selection-sized CLR array is held.</summary>
internal static class BoundedLogicalNotePlanning
{
    internal readonly record struct Selected<T>(int Ordinal, T Value) where T : unmanaged;
    internal readonly record struct Plan<T>(int Ordinal, T Old, T Value, bool Deleted) where T : unmanaged;
    internal readonly record struct NoteKey(long Tick, int Key);

    internal static BoundedImmutableValueSource<Selected<T>> Select<T>(ITimelineObjectSource<T> snapshot,
        IReadOnlyCollection<MidoraId> ids, Func<T, bool> valid, BoundedEditResources resources,
        CancellationToken token) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one note must be selected.", nameof(ids));
        snapshot.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, token);
        var sorted = BoundedEditSort.Sort(Resolve(), Comparer<Selected<T>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), resources, token);
        try
        {
            int previous = -1;
            foreach (var item in sorted.ReadValues(token))
            {
                if (previous == item.Ordinal) throw new ArgumentException("Note selections must contain distinct stable IDs.", nameof(ids));
                previous = item.Ordinal;
            }
            return new(sorted);
        }
        catch { sorted.Dispose(); throw; }

        IEnumerable<Selected<T>> Resolve()
        {
            long read = 0;
            foreach (MidoraId id in ids)
            {
                if ((read++ & 255) == 0) BulkEditPreparationContext.Current?.Checkpoint(read, ids.Count,
                    TimelineEditPreparationPhase.ResolvingSelection);
                token.ThrowIfCancellationRequested();
                if (id == default || !snapshot.TryFindOrdinalById(id, out int ordinal))
                    throw new ArgumentException("Every selected ID must identify a note in the target owner.", nameof(ids));
                T value = snapshot.GetByOrdinal(ordinal);
                if (!valid(value)) throw new ArgumentException("Every selected ID must identify a note in the target owner.", nameof(ids));
                yield return new(ordinal, value);
            }
        }
    }

    internal static BoundedImmutableValueSource<TimelineValueEdit<T>> Transform<T>(
        ITimelineObjectSource<T> snapshot, BoundedImmutableValueSource<Selected<T>> selected,
        Func<T, T?> transform, Func<T, NoteKey> key, Func<T, MidoraId> id,
        Func<NoteKey, IEnumerable<T>> existingAtKey, bool resolveCollisions,
        BoundedEditResources resources, CancellationToken token, bool formalCollisions = false) where T : unmanaged
    {
        using var planStore = new BoundedEditRecordStore<Plan<T>>(resources);
        int count = 0;
        foreach (var item in selected)
        {
            if ((count++ & 255) == 0) BulkEditPreparationContext.Current?.Checkpoint(count, selected.Count);
            token.ThrowIfCancellationRequested();
            T? result = transform(item.Value);
            planStore.Add(new(item.Ordinal, item.Value, result.GetValueOrDefault(), !result.HasValue), token);
        }
        planStore.Seal();
        BulkEditPreparationContext.Current?.Checkpoint(selected.Count, selected.Count);
        using var plans = new BoundedImmutableValueSource<Plan<T>>(planStore);
        var output = new BoundedEditRecordStore<TimelineValueEdit<T>>(resources);
        try
        {
            if (!resolveCollisions)
            {
                foreach (var plan in plans)
                    if (plan.Deleted || !EqualityComparer<T>.Default.Equals(plan.Old, plan.Value))
                        output.Add(new(plan.Ordinal, plan.Deleted, plan.Value), token);
                output.Seal();
                output.SpillResidentPages(token);
                return new BoundedTimelineEditValueSource<T>(output);
            }
            using var byKey = BoundedEditSort.Sort(plans.Where(static p => !p.Deleted),
                Comparer<Plan<T>>.Create((a, b) =>
                {
                    NoteKey x = key(a.Value), y = key(b.Value);
                    int order = x.Tick.CompareTo(y.Tick);
                    if (order == 0) order = x.Key.CompareTo(y.Key);
                    return order != 0 ? order : a.Ordinal.CompareTo(b.Ordinal);
                }), resources, token);
            NoteKey? current = null;
            bool incumbent = false;
            bool newcomerRetained = false;
            int winningOrdinal = -1;
            int examined = 0;
            foreach (var plan in byKey.ReadValues(token))
            {
                if ((++examined & 255) == 0)
                    BulkEditPreparationContext.Current?.Checkpoint(examined, byKey.Count,
                        TimelineEditPreparationPhase.ResolvingCollisions);
                NoteKey target = key(plan.Value);
                if (current != target)
                {
                    current = target;
                    newcomerRetained = false;
                    if (formalCollisions)
                    {
                        winningOrdinal = plan.Ordinal;
                        foreach (T old in existingAtKey(target))
                        {
                            token.ThrowIfCancellationRequested();
                            snapshot.TryFindOrdinalById(id(old), out int ordinal);
                            if (FindSelected(ordinal) is null) winningOrdinal = Math.Min(winningOrdinal, ordinal);
                        }
                        foreach (T old in existingAtKey(target))
                        {
                            token.ThrowIfCancellationRequested();
                            snapshot.TryFindOrdinalById(id(old), out int ordinal);
                            if (ordinal != winningOrdinal && FindSelected(ordinal) is null)
                                output.Add(new(ordinal, true, default), token);
                        }
                    }
                    else incumbent = HasLiveIncumbent(target);
                }
                bool unchangedTarget = key(plan.Old) == target;
                bool deleted = formalCollisions ? plan.Ordinal != winningOrdinal
                    : !unchangedTarget && (incumbent || newcomerRetained);
                if (!deleted && !unchangedTarget) newcomerRetained = true;
                if (deleted || !EqualityComparer<T>.Default.Equals(plan.Old, plan.Value))
                    output.Add(new(plan.Ordinal, deleted, plan.Value), token);
            }
            foreach (var plan in plans)
                if (plan.Deleted) output.Add(new(plan.Ordinal, true, default), token);
            output.Seal();
            BulkEditPreparationContext.Current?.Checkpoint(0, 1, TimelineEditPreparationPhase.BuildingResult);
            var ordered = BoundedEditSort.Sort(output.ReadValues(token),
                Comparer<TimelineValueEdit<T>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), resources, token);
            output.Dispose();
            ordered.SpillResidentPages(token);
            BulkEditPreparationContext.Current?.Checkpoint(1, 1, TimelineEditPreparationPhase.BuildingResult);
            return new BoundedTimelineEditValueSource<T>(ordered);
        }
        catch { output.Dispose(); throw; }

        bool HasLiveIncumbent(NoteKey target)
        {
            foreach (T old in existingAtKey(target))
            {
                token.ThrowIfCancellationRequested();
                if (!snapshot.TryFindOrdinalById(id(old), out int ordinal))
                    throw new InvalidOperationException("The frozen note source lost an identity.");
                Plan<T>? selectedPlan = FindSelected(ordinal);
                if (selectedPlan is not { } p || (!p.Deleted && key(p.Value) == target)) return true;
            }
            return false;
        }

        Plan<T>? FindSelected(int ordinal)
        {
            int low = 0, high = plans.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                var candidate = plans[middle];
                if (candidate.Ordinal < ordinal) low = middle + 1;
                else if (candidate.Ordinal > ordinal) high = middle - 1;
                else return candidate;
            }
            return null;
        }
    }

    internal static BoundedIndexedTimelineValueSource<T> Append<T>(IEnumerable<T> values,
        Func<T, NoteKey> key, Func<NoteKey, IEnumerable<T>> existingAtKey,
        BoundedEditResources resources, CancellationToken token, Func<T, MidoraId> id) where T : unmanaged
    {
        using var sorted = BoundedEditSort.Sort(WithOrdinals(), Comparer<Selected<T>>.Create((a, b) =>
        {
            NoteKey x = key(a.Value), y = key(b.Value);
            int order = x.Tick.CompareTo(y.Tick);
            if (order == 0) order = x.Key.CompareTo(y.Key);
            return order != 0 ? order : a.Ordinal.CompareTo(b.Ordinal);
        }), resources, token);
        using var winners = new BoundedEditRecordStore<Selected<T>>(resources);
        NoteKey? previous = null;
        foreach (var candidate in sorted.ReadValues(token))
        {
            NoteKey target = key(candidate.Value);
            if (previous == target) continue;
            previous = target;
            if (!existingAtKey(target).Any()) winners.Add(candidate, token);
        }
        winners.Seal();
        using var ordered = BoundedEditSort.Sort(winners.ReadValues(token),
            Comparer<Selected<T>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), resources, token);
        var result = new BoundedEditRecordStore<T>(resources);
        try
        {
            foreach (var value in ordered.ReadValues(token)) result.Add(value.Value, token);
            result.Seal();
            result.SpillResidentPages(token);
            return new(new BoundedImmutableValueSource<T>(result), id, resources, token);
        }
        catch { result.Dispose(); throw; }
        IEnumerable<Selected<T>> WithOrdinals()
        {
            int ordinal = 0;
            foreach (T value in values) yield return new(ordinal++, value);
        }
    }

    internal static BoundedImmutableValueSource<TimelineValueEdit<T>> Join<T>(
        BoundedImmutableValueSource<Selected<T>> selected, Func<T, NoteKey> key,
        Func<T, long> end, Func<T, long, T> setEnd, long maximumGap,
        BoundedEditResources resources, CancellationToken token) where T : unmanaged
    {
        if (maximumGap < 0) throw new ArgumentOutOfRangeException(nameof(maximumGap));
        using var ordered = BoundedEditSort.Sort(selected, Comparer<Selected<T>>.Create((a, b) =>
        {
            NoteKey x = key(a.Value), y = key(b.Value);
            int order = x.Key.CompareTo(y.Key);
            if (order == 0) order = x.Tick.CompareTo(y.Tick);
            return order != 0 ? order : a.Ordinal.CompareTo(b.Ordinal);
        }), resources, token);
        using var output = new BoundedEditRecordStore<TimelineValueEdit<T>>(resources);
        Selected<T>? first = null;
        long maximumEnd = 0;
        foreach (var item in ordered.ReadValues(token))
        {
            if (first is { } run && key(run.Value).Key == key(item.Value).Key
                && checked(key(item.Value).Tick - maximumEnd) <= maximumGap)
            {
                maximumEnd = Math.Max(maximumEnd, end(item.Value));
                output.Add(new(item.Ordinal, true, default), token);
            }
            else
            {
                FinishRun();
                first = item;
                maximumEnd = end(item.Value);
            }
        }
        FinishRun();
        output.Seal();
        var result = BoundedEditSort.Sort(output.ReadValues(token),
            Comparer<TimelineValueEdit<T>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), resources, token);
        try { result.SpillResidentPages(token); return new BoundedTimelineEditValueSource<T>(result); }
        catch { result.Dispose(); throw; }
        void FinishRun()
        {
            if (first is not { } run || maximumEnd == end(run.Value)) return;
            output.Add(new(run.Ordinal, false, setEnd(run.Value, maximumEnd)), token);
        }
    }
}
