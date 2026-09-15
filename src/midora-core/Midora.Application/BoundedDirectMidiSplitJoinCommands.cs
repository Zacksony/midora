using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct DirectSplitFragment(int Ordinal, int FragmentIndex,
        DirectMidiNoteValue Original, DirectMidiNoteValue Value);
    private readonly record struct DirectSplitBoundary(long Tick, int Ordinal, int RightFragment,
        long SourceOrder, MidoraId SourceId);
    private readonly record struct DirectSplitEndpoint(int Ordinal, int FragmentIndex, bool NoteOn, long Order);
    private readonly record struct DirectSplitCollision(DirectSplitFragment Fragment, bool Incumbent);
    private readonly record struct DirectSplitTickOrder(long Tick, long Order);

    private static int DirectFormalCompare(DirectMidiNoteValue a, DirectMidiNoteValue b)
    {
        int order = a.NoteOnOrder.CompareTo(b.NoteOnOrder);
        return order != 0 ? order : a.Id.CompareTo(b.Id);
    }

    private static BoundedImmutableValueSource<BoundedDirectNoteDelta> SelectBoundedDirectNotes(
        BoundedDirectMidiNoteSource source, IReadOnlyCollection<MidoraId> ids, BulkEditPreparationContext scope)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one Direct MIDI Note must be selected.", nameof(ids));
        var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiNoteSource.OrdinalComparer, scope.Resources, scope.Token);
        try
        {
            int previous = -1;
            foreach (var value in store.ReadValues(scope.Token))
            {
                if (value.Ordinal == previous) throw new ArgumentException("Direct MIDI Note IDs must be distinct.", nameof(ids));
                previous = value.Ordinal;
            }
            return new(store);
        }
        catch { store.Dispose(); throw; }
        IEnumerable<BoundedDirectNoteDelta> Read()
        {
            int completed = 0;
            foreach (var value in source.ResolveIds(ids, scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                if ((++completed & 255) == 0) scope.Checkpoint(completed, ids.Count);
                yield return value;
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedDirectJoin(MidoraProject project, MidiSegmentLocation location,
        IReadOnlyCollection<MidoraId> ids, NoteJoinOptions options, SelectionPublisher publisher,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumGapTicks);
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
        using var selected = SelectBoundedDirectNotes(source, ids, scope);
        using var ordered = BoundedEditSort.Sort(selected, Comparer<BoundedDirectNoteDelta>.Create((a, b) =>
        {
            int order = a.Value.Key.CompareTo(b.Value.Key);
            if (order == 0) order = a.Value.StartTick.CompareTo(b.Value.StartTick);
            return order != 0 ? order : DirectFormalCompare(a.Value, b.Value);
        }), scope.Resources, token);
        using var changes = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        using var retained = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        BoundedDirectNoteDelta? first = null;
        DirectMidiNoteValue last = default;
        long end = 0;
        foreach (var item in ordered.ReadValues(token))
        {
            if (first is { } run && item.Value.Key == run.Value.Key
                && checked(item.Value.StartTick - end) <= options.MaximumGapTicks)
            {
                changes.Add(item with { Deleted = true }, token);
                end = Math.Max(end, checked(item.Value.StartTick + item.Value.LengthTicks));
                last = item.Value;
            }
            else { Finish(); first = item; last = item.Value; end = checked(item.Value.StartTick + item.Value.LengthTicks); }
        }
        Finish(); changes.Seal(); retained.Seal();
        using var plannedStore = BoundedEditSort.Sort(changes.ReadValues(token), BoundedDirectMidiNoteSource.OrdinalComparer, scope.Resources, token);
        using var planned = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(plannedStore);
        IPreparedProjectEdit prepared = planned.Count == 0
            ? ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegmentRevisionGate(project, location.Track, location.Segment,
                PureMidiTrackChange(location.Track.Id), stamp)
            : PrepareBoundedDirectMidiNotes(project, location, source, planned, scope,
                resolveCollisions: false, expectedSourceStamp: stamp);
        using var before = BoundedEditSort.Sort(selected, Comparer<BoundedDirectNoteDelta>.Create((a, b) => DirectFormalCompare(a.Value, b.Value)), scope.Resources, token);
        using var after = BoundedEditSort.Sort(retained.ReadValues(token), Comparer<BoundedDirectNoteDelta>.Create((a, b) => DirectFormalCompare(a.Value, b.Value)), scope.Resources, token);
        return PublishBoundedNoteSelection(prepared, before.Select(v => v.Value.Id), after.Select(v => v.Value.Id), publisher, scope);
        void Finish()
        {
            if (first is not { } value) return;
            var result = value.Value with { LengthTicks = checked(end - value.Value.StartTick),
                NoteOffVelocity = last.NoteOffVelocity, NoteOffOrder = last.NoteOffOrder };
            retained.Add(value with { Value = result }, token);
            if (result != value.Original) changes.Add(value with { Value = result }, token);
        }
    }

    private static IPreparedProjectEdit PrepareBoundedDirectSplit(MidoraProject project, MidiSegmentLocation location,
        IReadOnlyCollection<MidoraId> ids, NoteSplitOptions options, SelectionPublisher publisher,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress, DetachedStableIdAllocator? allocator)
    {
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
        using var selected = SelectBoundedDirectNotes(source, ids, scope);
        long left = selected.Min(v => v.Value.StartTick), right = selected.Max(v => checked(v.Value.StartTick + v.Value.LengthTicks));
        using var knives = CreateBoundedSplitKnives(left, checked(right - left), options, scope);
        using var boundaries = new BoundedEditRecordStore<DirectSplitBoundary>(scope.Resources);
        using var candidates = BoundedEditSort.Sort(Candidates(), Comparer<DirectSplitFragment>.Create((a, b) =>
        {
            int order = a.Value.StartTick.CompareTo(b.Value.StartTick);
            if (order == 0) order = a.Value.Key.CompareTo(b.Value.Key);
            if (order == 0) order = DirectFormalCompare(a.Original, b.Original);
            return order != 0 ? order : a.FragmentIndex.CompareTo(b.FragmentIndex);
        }), scope.Resources, token);
        boundaries.Seal();
        using var winners = new BoundedEditRecordStore<DirectSplitFragment>(scope.Resources);
        using var displaced = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        using var collisions = BoundedEditSort.Sort(CollisionRows(), Comparer<DirectSplitCollision>.Create((a, b) =>
        {
            int order = a.Fragment.Value.StartTick.CompareTo(b.Fragment.Value.StartTick);
            if (order == 0) order = a.Fragment.Value.Key.CompareTo(b.Fragment.Value.Key);
            if (order == 0) order = DirectFormalCompare(a.Fragment.Original, b.Fragment.Original);
            return order != 0 ? order : a.Fragment.FragmentIndex.CompareTo(b.Fragment.FragmentIndex);
        }), scope.Resources, token);
        DirectMidiNoteStartKey? previousKey = null;
        foreach (var row in collisions.ReadValues(token))
        {
            var candidate = row.Fragment;
            var key = new DirectMidiNoteStartKey(candidate.Value.StartTick, candidate.Value.Key);
            if (previousKey != key)
            {
                previousKey = key;
                if (!row.Incumbent) winners.Add(candidate, token);
            }
            else if (row.Incumbent)
                displaced.Add(new(candidate.Ordinal, true, candidate.Original, candidate.Original), token);
        }
        winners.Seal(); displaced.Seal();

        // Reserve every cut endpoint, including a fragment later removed by
        // collision, exactly as the pre-existing split contract does.
        using var orderedBoundaries = BoundedEditSort.Sort(boundaries.ReadValues(token), Comparer<DirectSplitBoundary>.Create((a, b) =>
        {
            int order = a.Tick.CompareTo(b.Tick);
            if (order == 0) order = a.SourceOrder.CompareTo(b.SourceOrder);
            if (order == 0) order = a.SourceId.CompareTo(b.SourceId);
            return order != 0 ? order : a.RightFragment.CompareTo(b.RightFragment);
        }), scope.Resources, token);
        using var cutTicks = new BoundedEditRecordStore<long>(scope.Resources);
        long previousCut = -1;
        foreach (var boundary in orderedBoundaries.ReadValues(token))
            if (boundary.Tick != previousCut) { cutTicks.Add(boundary.Tick, token); previousCut = boundary.Tick; }
        cutTicks.Seal();
        using var endpointHighWater = BoundedEditSort.Sort(OriginalEndpointOrders(), Comparer<DirectSplitTickOrder>.Create((a, b) =>
        { int order = a.Tick.CompareTo(b.Tick); return order != 0 ? order : b.Order.CompareTo(a.Order); }), scope.Resources, token);
        using var highWater = endpointHighWater.ReadValues(token).GetEnumerator();
        bool hasHighWater = highWater.MoveNext();
        using var endpointStore = new BoundedEditRecordStore<DirectSplitEndpoint>(scope.Resources);
        long priorTick = -1, nextOrder = -1;
        foreach (var boundary in orderedBoundaries.ReadValues(token))
        {
            if (priorTick != boundary.Tick)
            {
                priorTick = boundary.Tick;
                while (hasHighWater && highWater.Current.Tick < boundary.Tick) hasHighWater = highWater.MoveNext();
                nextOrder = hasHighWater && highWater.Current.Tick == boundary.Tick ? highWater.Current.Order : -1;
            }
            long off = checked(nextOrder + 1), on = checked(off + 1); nextOrder = on;
            endpointStore.Add(new(boundary.Ordinal, boundary.RightFragment - 1, false, off), token);
            endpointStore.Add(new(boundary.Ordinal, boundary.RightFragment, true, on), token);
        }
        endpointStore.Seal();
        using var endpointOrder = BoundedEditSort.Sort(endpointStore.ReadValues(token), Comparer<DirectSplitEndpoint>.Create((a, b) =>
        {
            int order = a.Ordinal.CompareTo(b.Ordinal);
            if (order == 0) order = a.FragmentIndex.CompareTo(b.FragmentIndex);
            return order != 0 ? order : a.NoteOn.CompareTo(b.NoteOn);
        }), scope.Resources, token);
        using var winnersBySource = BoundedEditSort.Sort(winners.ReadValues(token), Comparer<DirectSplitFragment>.Create((a, b) =>
        { int order = a.Ordinal.CompareTo(b.Ordinal); return order != 0 ? order : a.FragmentIndex.CompareTo(b.FragmentIndex); }), scope.Resources, token);
        using var finalFragments = BoundedEditSort.Sort(WithEndpoints(), Comparer<DirectSplitFragment>.Create((a, b) =>
        { int order = DirectFormalCompare(a.Original, b.Original); return order != 0 ? order : a.FragmentIndex.CompareTo(b.FragmentIndex); }), scope.Resources, token);
        using var formalSelection = BoundedEditSort.Sort(selected, Comparer<BoundedDirectNoteDelta>.Create((a, b) => DirectFormalCompare(a.Value, b.Value)), scope.Resources, token);
        using var output = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        using var resultIds = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        int nextOrdinal = source.FormalExtent;
        long nextId = project.NextStableId;
        using (var iterator = finalFragments.ReadValues(token).GetEnumerator())
        {
            bool more = iterator.MoveNext();
            foreach (var old in formalSelection.ReadValues(token))
            {
                bool retainedFirst = more && iterator.Current.Ordinal == old.Ordinal && iterator.Current.FragmentIndex == 0;
                if (!retainedFirst) output.Add(old with { Deleted = true }, token);
                while (more && iterator.Current.Ordinal == old.Ordinal)
                {
                    var fragment = iterator.Current;
                    var value = fragment.Value;
                    int ordinal = old.Ordinal;
                    if (fragment.FragmentIndex != 0)
                    {
                        MidoraId id = allocator is null ? MidoraId.FromSequence(nextId) : allocator.ReserveNext();
                        if (allocator is null) nextId = checked(nextId + 1);
                        value = value with { Id = id }; ordinal = nextOrdinal; nextOrdinal = checked(nextOrdinal + 1);
                    }
                    output.Add(new(ordinal, false, fragment.FragmentIndex == 0 ? old.Value : default, value), token);
                    resultIds.Add(value.Id, token);
                    more = iterator.MoveNext();
                }
            }
        }
        output.AddRange(displaced.ReadValues(token), token); output.Seal(); resultIds.Seal();
        using var planStore = BoundedEditSort.Sort(output.ReadValues(token), BoundedDirectMidiNoteSource.OrdinalComparer, scope.Resources, token);
        using var plan = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(planStore);
        IPreparedProjectEdit prepared = boundaries.Count == 0 && displaced.Count == 0 && resultIds.Count == selected.Count
            ? ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegmentRevisionGate(project, location.Track, location.Segment, PureMidiTrackChange(location.Track.Id), stamp)
            : PrepareBoundedDirectMidiNotes(project, location, source, plan, scope, nextOrdinal,
                allocator is null ? nextId : null, resolveCollisions: false, expectedSourceStamp: stamp);
        return PublishBoundedNoteSelection(prepared, formalSelection.Select(v => v.Value.Id), resultIds.ReadValues(token), publisher, scope);

        bool IsSelected(int ordinal)
        {
            int low = 0, high = selected.Count - 1;
            while (low <= high)
            { int mid = low + (high - low) / 2; int found = selected[mid].Ordinal;
                if (found < ordinal) low = mid + 1; else if (found > ordinal) high = mid - 1; else return true; }
            return false;
        }
        IEnumerable<DirectSplitFragment> Candidates()
        {
            int count = 0;
            foreach (var note in selected)
            {
                long start = note.Value.StartTick, stop = checked(start + note.Value.LengthTicks);
                int low = 0, high = knives.Count, fragmentIndex = 0;
                while (low < high) { int middle = low + (high - low) / 2; if (knives[middle] <= start) low = middle + 1; else high = middle; }
                for (int index = low; index < knives.Count && knives[index] < stop; index++)
                {
                    if (++count > options.MaximumResultObjects) throw new InvalidOperationException("Note Split exceeds the configured maximum result-object count.");
                    long cut = knives[index];
                    yield return new(note.Ordinal, fragmentIndex++, note.Value, note.Value with { StartTick = start, LengthTicks = checked(cut - start) });
                    boundaries.Add(new(cut, note.Ordinal, fragmentIndex, note.Value.NoteOnOrder, note.Value.Id), token);
                    start = cut;
                }
                if (++count > options.MaximumResultObjects) throw new InvalidOperationException("Note Split exceeds the configured maximum result-object count.");
                yield return new(note.Ordinal, fragmentIndex, note.Value, note.Value with { StartTick = start, LengthTicks = checked(stop - start) });
            }
        }
        IEnumerable<DirectSplitCollision> CollisionRows()
        {
            using var budget = scope.Resources.ReserveWorking(4096L * 64);
            HashSet<DirectMidiNoteStartKey> keys = [];
            DirectMidiNoteStartKey? lastKey = null;
            foreach (var value in candidates.ReadValues(token))
            {
                yield return new(value, false);
                var key = new DirectMidiNoteStartKey(value.Value.StartTick, value.Value.Key);
                if (key == lastKey) continue;
                lastKey = key; keys.Add(key);
                if (keys.Count < 4096) continue;
                foreach (var row in Incumbents()) yield return row;
                keys.Clear();
            }
            foreach (var row in Incumbents()) yield return row;
            IEnumerable<DirectSplitCollision> Incumbents()
            {
                if (keys.Count == 0) yield break;
                foreach (var existing in source.ResolveIds(source.QueryStartKeys(keys).Select(static v => v.Id), token))
                    if (!IsSelected(existing.Ordinal))
                        yield return new(new(existing.Ordinal, -1, existing.Value, existing.Value), true);
            }
        }
        IEnumerable<DirectSplitTickOrder> OriginalEndpointOrders()
        {
            if (cutTicks.Count == 0) yield break;
            long first = cutTicks[0], endTick = checked(cutTicks[cutTicks.Count - 1] + 1);
            foreach (var value in source.QueryNoteStarts(first, endTick))
            { token.ThrowIfCancellationRequested(); if (IsCut(value.StartTick)) yield return new(value.StartTick, value.NoteOnOrder); }
            foreach (var value in source.QueryNoteEnds(first, endTick))
            { token.ThrowIfCancellationRequested(); long tick = checked(value.StartTick + value.LengthTicks); if (IsCut(tick)) yield return new(tick, value.NoteOffOrder); }
            foreach (var value in location.Segment.ChannelEvents.QueryValues(first, endTick))
            { token.ThrowIfCancellationRequested(); if (IsCut(value.Tick)) yield return new(value.Tick, value.Order); }
            foreach (var value in location.Segment.OpaqueEvents.QueryValues(first, endTick))
            { token.ThrowIfCancellationRequested(); if (IsCut(value.Tick)) yield return new(value.Tick, value.Order); }
            bool IsCut(long tick)
            {
                int low = 0, high = cutTicks.Count - 1;
                while (low <= high)
                { int mid = low + (high - low) / 2; long found = cutTicks[mid];
                    if (found < tick) low = mid + 1; else if (found > tick) high = mid - 1; else return true; }
                return false;
            }
        }
        IEnumerable<DirectSplitFragment> WithEndpoints()
        {
            using var ends = endpointOrder.ReadValues(token).GetEnumerator();
            bool more = ends.MoveNext();
            foreach (var candidate in winnersBySource.ReadValues(token))
            {
                var value = candidate.Value;
                while (more && (ends.Current.Ordinal < candidate.Ordinal || ends.Current.Ordinal == candidate.Ordinal && ends.Current.FragmentIndex < candidate.FragmentIndex)) more = ends.MoveNext();
                while (more && ends.Current.Ordinal == candidate.Ordinal && ends.Current.FragmentIndex == candidate.FragmentIndex)
                {
                    value = ends.Current.NoteOn ? value with { NoteOnOrder = ends.Current.Order } : value with { NoteOffOrder = ends.Current.Order };
                    more = ends.MoveNext();
                }
                yield return candidate with { Value = value };
            }
        }
    }
}
