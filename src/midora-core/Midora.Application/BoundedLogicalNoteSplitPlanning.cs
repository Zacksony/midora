using Midora.Domain;

namespace Midora.Application;

internal static class BoundedLogicalNoteSplitPlanning
{
    private readonly record struct Fragment<T>(int Ordinal, int FragmentIndex, T Value) where T : unmanaged;
    internal sealed record Result<T>(BoundedImmutableValueSource<TimelineValueSplice> Splices,
        BoundedIndexedTimelineValueSource<T> Values, bool HasChanges) : IDisposable where T : unmanaged
    {
        public void Dispose() { Splices.Dispose(); Values.Dispose(); }
    }

    internal static Result<T> Split<T>(ITimelineObjectSource<T> source,
        BoundedImmutableValueSource<BoundedLogicalNotePlanning.Selected<T>> selected,
        IReadOnlyList<long> knives, int maximumResults,
        Func<T, BoundedLogicalNotePlanning.NoteKey> key, Func<T, long> end,
        Func<T, long, long, T> fragment, Func<T, MidoraId> id, Func<T, MidoraId, T> setId,
        Func<BoundedLogicalNotePlanning.NoteKey, IEnumerable<T>> existing,
        Func<MidoraId> allocateId, BoundedEditResources resources, CancellationToken token) where T : unmanaged
    {
        using var sorted = BoundedEditSort.Sort(Candidates(), Comparer<Fragment<T>>.Create((a, b) =>
        {
            var x = key(a.Value); var y = key(b.Value);
            int order = x.Tick.CompareTo(y.Tick);
            if (order == 0) order = x.Key.CompareTo(y.Key);
            if (order == 0) order = a.Ordinal.CompareTo(b.Ordinal);
            return order != 0 ? order : a.FragmentIndex.CompareTo(b.FragmentIndex);
        }), resources, token);
        using var winners = new BoundedEditRecordStore<Fragment<T>>(resources);
        using var displaced = new BoundedEditRecordStore<int>(resources);
        BoundedLogicalNotePlanning.NoteKey? prior = null;
        foreach (var item in sorted.ReadValues(token))
        {
            var target = key(item.Value);
            if (prior == target) continue;
            prior = target;
            int winnerOrdinal = item.Ordinal;
            foreach (T old in existing(target))
            {
                token.ThrowIfCancellationRequested();
                if (!source.TryFindOrdinalById(id(old), out int ordinal)) throw new InvalidOperationException("A frozen note identity disappeared.");
                if (!IsSelected(ordinal)) winnerOrdinal = Math.Min(winnerOrdinal, ordinal);
            }
            if (winnerOrdinal == item.Ordinal) winners.Add(item, token);
            foreach (T old in existing(target))
            {
                token.ThrowIfCancellationRequested();
                source.TryFindOrdinalById(id(old), out int ordinal);
                if (!IsSelected(ordinal) && ordinal != winnerOrdinal) displaced.Add(ordinal, token);
            }
        }
        winners.Seal(); displaced.Seal();
        using var finalFragments = BoundedEditSort.Sort(winners.ReadValues(token), Comparer<Fragment<T>>.Create((a, b) =>
        {
            int order = a.Ordinal.CompareTo(b.Ordinal);
            return order != 0 ? order : a.FragmentIndex.CompareTo(b.FragmentIndex);
        }), resources, token);
        using var affected = BoundedEditSort.Sort(selected.Select(v => v.Ordinal).Concat(displaced.ReadValues(token)),
            Comparer<int>.Default, resources, token);
        var valueStore = new BoundedEditRecordStore<T>(resources);
        var spliceStore = new BoundedEditRecordStore<TimelineValueSplice>(resources);
        bool changed = false;
        try
        {
            using var iterator = finalFragments.ReadValues(token).GetEnumerator();
            bool hasNext = iterator.MoveNext();
            foreach (int ordinal in affected.ReadValues(token))
            {
                int first = valueStore.Count;
                T last = default;
                while (hasNext && iterator.Current.Ordinal == ordinal)
                {
                    var item = iterator.Current;
                    last = item.FragmentIndex == 0 ? item.Value : setId(item.Value, allocateId());
                    valueStore.Add(last, token);
                    hasNext = iterator.MoveNext();
                }
                int count = valueStore.Count - first;
                changed |= count != 1 || !EqualityComparer<T>.Default.Equals(last, source.GetByOrdinal(ordinal));
                spliceStore.Add(new(ordinal, first, count), token);
            }
            valueStore.Seal(); spliceStore.Seal();
            valueStore.SpillResidentPages(token); spliceStore.SpillResidentPages(token);
            var values = new BoundedIndexedTimelineValueSource<T>(new(valueStore), id, resources, token);
            return new(new(spliceStore), values, changed);
        }
        catch { valueStore.Dispose(); spliceStore.Dispose(); throw; }

        IEnumerable<Fragment<T>> Candidates()
        {
            int count = 0;
            foreach (var note in selected)
            {
                long start = key(note.Value).Tick, stop = end(note.Value);
                int low = 0, high = knives.Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (knives[middle] <= start) low = middle + 1; else high = middle;
                }
                int fragmentIndex = 0;
                for (int i = low; i < knives.Count && knives[i] < stop; i++)
                {
                    if (++count > maximumResults) throw new InvalidOperationException("Note Split exceeds the configured maximum result-object count.");
                    long knife = knives[i];
                    yield return new(note.Ordinal, fragmentIndex++, fragment(note.Value, start, checked(knife - start)));
                    start = knife;
                }
                if (++count > maximumResults) throw new InvalidOperationException("Note Split exceeds the configured maximum result-object count.");
                yield return new(note.Ordinal, fragmentIndex, fragment(note.Value, start, checked(stop - start)));
            }
        }
        bool IsSelected(int ordinal)
        {
            int low = 0, high = selected.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int found = selected[middle].Ordinal;
                if (found < ordinal) low = middle + 1; else if (found > ordinal) high = middle - 1; else return true;
            }
            return false;
        }
    }
}
