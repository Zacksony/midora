using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedDirectMidiNoteAppend(MidoraProject project,
        MidoraId segmentId, Func<long, IEnumerable<DirectMidiNoteValue>> createValues,
        IEnumerable<MidoraId>? originalSelection = null)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
        using var store = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        long nextId = project.NextStableId;
        int ordinal = source.FormalExtent;
        foreach (var value in createValues(nextId))
        {
            scope.Token.ThrowIfCancellationRequested();
            if (value.Id.Value != nextId)
                throw new InvalidOperationException("A detached Direct MIDI append must consume its reserved Stable IDs in order.");
            ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            store.Add(new(ordinal++, false, default, value), scope.Token);
            nextId = checked(nextId + 1);
        }
        store.Seal();
        using var planned = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(store);
        return PrepareBoundedDirectMidiNotes(project, location, source, planned, scope, ordinal, nextId,
            publishResult: static ids => ids, expectedSourceStamp: stamp, selectionBefore: originalSelection ?? []);
    }

    private readonly record struct BoundedDirectNoteStatistics(long MinimumTick, long MaximumEndTick,
        int MinimumKey, int MaximumKey, int Count);
    private readonly record struct BoundedDirectNoteCollision(long Tick, int Key, int Ordinal, bool Incumbent,
        long Order = 0, MidoraId Id = default);
    private static readonly IComparer<BoundedDirectNoteCollision> BoundedDirectNoteCollisionComparer =
        Comparer<BoundedDirectNoteCollision>.Create(static (a, b) =>
        {
            int tick = a.Tick.CompareTo(b.Tick);
            if (tick != 0) return tick;
            int key = a.Key.CompareTo(b.Key);
            if (key != 0) return key;
            int incumbent = b.Incumbent.CompareTo(a.Incumbent);
            return incumbent != 0 ? incumbent : a.Ordinal.CompareTo(b.Ordinal);
        });

    private static IProjectEditCommand ChangeBoundedDirectMidiNotes(string name, MidoraId segmentId,
        IReadOnlyCollection<MidoraId> ids,
        Func<BoundedDirectNoteStatistics, Func<DirectMidiNoteValue, DirectMidiNoteValue?>> makeTransform)
    {
        // The small command path already has a fixed upper bound and avoids
        // creating scratch files for a single keypress. Large commands use the
        // same value transform but never build result-sized object arrays.
        return Command(name, project =>
        {
        if (ids.Count <= 4096 && FindMidiSegment(project, segmentId).Segment.Notes.Count <= 4096)
            return ChangeDirectMidiNotes(name, segmentId, ids, values =>
            {
                var transform = makeTransform(new(values.Min(static x => x.StartTick),
                    values.Max(static x => checked(x.StartTick + x.LengthTicks)),
                    values.Min(static x => x.Key), values.Max(static x => x.Key), values.Count));
                return values.Select(value =>
                {
                    var result = transform(new(default, value.StartTick, value.LengthTicks, value.Key,
                        value.NoteOnVelocity, value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder));
                    return result is { } changed ? new DirectNoteValue(changed.StartTick, changed.LengthTicks,
                        changed.Key, changed.NoteOnVelocity, changed.NoteOffVelocity, changed.NoteOnOrder,
                        changed.NoteOffOrder) : value with { Key = -1 };
                }).ToArray();
            }).Prepare(project);
        return PrepareBoundedDirectMidiNoteTransform(project, segmentId, ids, makeTransform);
        });
    }

    private static IPreparedProjectEdit PrepareBoundedDirectMidiNoteTransform(MidoraProject project,
        MidoraId segmentId, IReadOnlyCollection<MidoraId> ids,
        Func<BoundedDirectNoteStatistics, Func<DirectMidiNoteValue, DirectMidiNoteValue?>> makeTransform,
        bool formalCollisions = false, SelectionPublisher? publishResult = null)
    {
            var location = FindMidiSegment(project, segmentId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
            var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
            using var selectedStore = BoundedEditSort.Sort(ReadSelection(),
                BoundedDirectMidiNoteSource.OrdinalComparer, scope.Resources, scope.Token,
                scope.ProgressInRange(0.13, 0.02));
            using var selected = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(selectedStore);
            long minimum = long.MaxValue, maximum = 0;
            int minKey = 127, maxKey = 0, previous = -1;
            int measured = 0;
            foreach (var value in selected)
            {
                if (value.Ordinal == previous) throw new ArgumentException("Direct MIDI Note IDs must be distinct.", nameof(ids));
                previous = value.Ordinal;
                minimum = Math.Min(minimum, value.Value.StartTick);
                maximum = Math.Max(maximum, checked(value.Value.StartTick + value.Value.LengthTicks));
                minKey = Math.Min(minKey, value.Value.Key); maxKey = Math.Max(maxKey, value.Value.Key);
                if ((++measured & 255) == 0)
                    scope.Checkpoint(measured, selected.Count, TimelineEditPreparationPhase.Planning, 0.15, 0.10);
            }
            if (selected.Count == 0) throw new ArgumentException("At least one Direct MIDI Note is required.", nameof(ids));
            var transform = makeTransform(new(minimum, maximum, minKey, maxKey, selected.Count));
            using var plannedStore = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
            int completed = 0;
            foreach (var value in selected)
            {
                var changed = transform(value.Value);
                if (changed is { } next)
                    ValidateDirectMidiNote(next.StartTick, next.LengthTicks, next.Key, next.NoteOnVelocity, next.NoteOffVelocity);
                plannedStore.Add(value with { Deleted = changed is null, Value = changed ?? value.Value }, scope.Token);
                if ((++completed & 255) == 0)
                    scope.Checkpoint(completed, selected.Count, TimelineEditPreparationPhase.Planning, 0.25, 0.30);
            }
            scope.Checkpoint(completed, selected.Count, TimelineEditPreparationPhase.Planning, 0.25, 0.30);
            plannedStore.Seal();
            using var planned = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(plannedStore);
            return PrepareBoundedDirectMidiNotes(project, location, source, planned, scope,
                formalCollisions: formalCollisions, publishResult: publishResult, expectedSourceStamp: stamp);

            IEnumerable<BoundedDirectNoteDelta> ReadSelection()
            {
                ArgumentNullException.ThrowIfNull(ids);
                int count = 0;
                foreach (var value in source.ResolveIds(ids, scope.Token))
                {
                    if ((++count & 255) == 0)
                        scope.Checkpoint(count, ids.Count, TimelineEditPreparationPhase.ResolvingSelection, 0, 0.13);
                    yield return value;
                }
                scope.Checkpoint(count, ids.Count, TimelineEditPreparationPhase.ResolvingSelection, 0, 0.13);
            }
    }
    private static IPreparedProjectEdit PrepareBoundedDirectMidiNotes(MidoraProject project,
        MidiSegmentLocation location, BoundedDirectMidiNoteSource source,
        BoundedImmutableValueSource<BoundedDirectNoteDelta> planned, BulkEditPreparationContext scope,
        int? formalExtent = null, long? nextStableId = null, bool formalCollisions = false,
        SelectionPublisher? publishResult = null, bool resolveCollisions = true,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null, IEnumerable<MidoraId>? selectionBefore = null)
    {
        publishResult ??= RequestedTimelineSelectionPublisher(new(ProjectTimelineOwnerKind.DirectMidiSegment, location.Segment.Id));
        // Candidate and incumbent streams use the same target ordering. Keeping
        // the complete operation in this merge prevents cross-chunk collisions.
        using var collisionStore = BoundedEditSort.Sort(CollisionCandidates(),
            formalCollisions ? Comparer<BoundedDirectNoteCollision>.Create(static (a, b) =>
            {
                int order = a.Tick.CompareTo(b.Tick);
                if (order == 0) order = a.Key.CompareTo(b.Key);
                if (order == 0) order = a.Order.CompareTo(b.Order);
                return order != 0 ? order : a.Id.CompareTo(b.Id);
            }) : BoundedDirectNoteCollisionComparer, scope.Resources, scope.Token,
            scope.ProgressInRange(0.65, 0.025));
        using var discardedStore = BoundedEditSort.Sort(DiscardedOrdinals(),
            Comparer<int>.Default, scope.Resources, scope.Token, scope.ProgressInRange(0.70, 0.025));
        using var patchStore = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        using var discarded = discardedStore.GetEnumerator();
        bool hasDiscard = discarded.MoveNext();
        bool hasSourceChanges = false;
        int patched = 0;
        foreach (var value in planned)
        {
            scope.Token.ThrowIfCancellationRequested();
            while (hasDiscard && discarded.Current < value.Ordinal)
            {
                var removed = source.GetByPhysicalOrdinal(discarded.Current);
                patchStore.Add(new(discarded.Current, true, removed, removed), scope.Token);
                hasSourceChanges = true;
                hasDiscard = discarded.MoveNext();
            }
            bool remove = value.Deleted || hasDiscard && discarded.Current == value.Ordinal;
            if (remove || value.Ordinal >= source.FormalExtent || value.Original != value.Value)
            {
                patchStore.Add(value with { Deleted = remove }, scope.Token);
                // A discarded append still needs a physical ordinal tombstone
                // when other candidates survive. On its own, however, it never
                // changed the source and must not create a root/history entry.
                hasSourceChanges |= value.Ordinal < source.FormalExtent || !remove;
            }
            if (hasDiscard && discarded.Current == value.Ordinal) hasDiscard = discarded.MoveNext();
            if ((++patched & 255) == 0)
                scope.Checkpoint(patched, planned.Count, TimelineEditPreparationPhase.BuildingResult, 0.725, 0.025);
        }
        while (hasDiscard)
        {
            var removed = source.GetByPhysicalOrdinal(discarded.Current);
            patchStore.Add(new(discarded.Current, true, removed, removed), scope.Token);
            hasSourceChanges = true;
            hasDiscard = discarded.MoveNext();
        }
        patchStore.Seal();
        scope.Checkpoint(planned.Count, planned.Count, TimelineEditPreparationPhase.BuildingResult, 0.725, 0.025);
        if (!hasSourceChanges)
        {
            var unchanged = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegmentRevisionGate(
                project, location.Track, location.Segment, PureMidiTrackChange(location.Track.Id), expectedSourceStamp);
            return publishResult is null ? unchanged : PublishBoundedNoteSelection(unchanged,
                selectionBefore ?? planned.Select(static value => value.Value.Id),
                planned.Where(value => value.Ordinal < source.FormalExtent && !value.Deleted)
                    .Select(static value => value.Value.Id), publishResult, scope);
        }
        using var patch = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(patchStore);
        var revisedSource = source.Apply(patch, scope.Resources, scope.Token, formalExtent, reportProgress: true);
        MidiSegment replacement = new(project, location.Segment.Id)
        {
            ProjectStartTick = location.Segment.ProjectStartTick,
            LengthTicks = location.Segment.LengthTicks,
            ContentOffsetTick = location.Segment.ContentOffsetTick
        };
        replacement.Notes.AdoptContentSource(revisedSource, checked(location.Segment.Notes.Generation + 1));
        location.Segment.ChannelEvents.CloneTo(replacement.ChannelEvents, scope.Token);
        location.Segment.OpaqueEvents.CloneTo(replacement.OpaqueEvents, scope.Token);
        ProjectChangeSet changes = PureMidiTrackChange(location.Track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectNotes(changes, location.Segment, replacement,
            patch.Select(static value => value.Value.Id));
        var prepared = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(project, location.Track,
            location.Segment, replacement, changes,
            nextStableId.HasValue ? project.NextStableId : null, nextStableId,
            expectedSourceStamp: expectedSourceStamp);
        return publishResult is null ? prepared : PublishBoundedNoteSelection(prepared,
            selectionBefore ?? planned.Where(value => value.Ordinal < source.FormalExtent).Select(static value => value.Value.Id),
            planned.Select(static value => value.Value.Id).Where(id => revisedSource.FindNoteIndex(id) >= 0),
            publishResult, scope);

        IEnumerable<BoundedDirectNoteCollision> CollisionCandidates()
        {
            if (!resolveCollisions) yield break;
            using var targets = BoundedEditSort.Sort(planned.Where(change => !change.Deleted
                    && (formalCollisions || change.Ordinal < 0 || change.Original.Id == default
                        || change.Original.StartTick != change.Value.StartTick || change.Original.Key != change.Value.Key))
                .Select(static change => new BoundedDirectNoteCollision(change.Value.StartTick, change.Value.Key,
                    change.Ordinal, false, change.Value.NoteOnOrder, change.Value.Id)), BoundedDirectNoteCollisionComparer, scope.Resources, scope.Token);
            HashSet<DirectMidiNoteStartKey> keys = [];
            DirectMidiNoteStartKey? previous = null;
            int queried = 0;
            foreach (var target in targets)
            {
                if ((++queried & 255) == 0)
                    scope.Checkpoint(queried, targets.Count, TimelineEditPreparationPhase.ResolvingCollisions, 0.55, 0.10);
                yield return target;
                var key = new DirectMidiNoteStartKey(target.Tick, target.Key);
                if (previous == key) continue;
                previous = key;
                keys.Add(key);
                if (keys.Count < scope.Resources.Budget.PageRecordCount) continue;
                foreach (var incumbent in Incumbents(keys)) yield return incumbent;
                keys.Clear();
            }
            if (keys.Count != 0)
                foreach (var incumbent in Incumbents(keys)) yield return incumbent;
            scope.Checkpoint(targets.Count, targets.Count, TimelineEditPreparationPhase.ResolvingCollisions, 0.55, 0.10);
        }
        IEnumerable<BoundedDirectNoteCollision> Incumbents(HashSet<DirectMidiNoteStartKey> keys)
        {
            foreach (var resolved in source.ResolveValues(source.QueryStartKeys(keys), scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                var value = resolved.Value;
                int ordinal = resolved.Ordinal;
                if (TryGetPlan(ordinal, out var edit)
                    && (formalCollisions || edit.Deleted || edit.Value.StartTick != value.StartTick || edit.Value.Key != value.Key)) continue;
                yield return new(value.StartTick, value.Key, ordinal, true, value.NoteOnOrder, value.Id);
            }
        }
        bool TryGetPlan(int ordinal, out BoundedDirectNoteDelta value)
        {
            int low = 0, high = planned.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (planned[middle].Ordinal < ordinal) low = middle + 1; else high = middle;
            }
            value = low < planned.Count ? planned[low] : default;
            return low < planned.Count && value.Ordinal == ordinal;
        }
        IEnumerable<int> DiscardedOrdinals()
        {
            DirectMidiNoteStartKey? previous = null;
            bool occupied = false;
            int checkedCandidates = 0;
            foreach (var candidate in collisionStore)
            {
                if ((++checkedCandidates & 255) == 0)
                    scope.Checkpoint(checkedCandidates, collisionStore.Count,
                        TimelineEditPreparationPhase.ResolvingCollisions, 0.675, 0.025);
                var key = new DirectMidiNoteStartKey(candidate.Tick, candidate.Key);
                if (previous != key) { previous = key; occupied = false; }
                if ((formalCollisions || !candidate.Incumbent) && occupied) yield return candidate.Ordinal;
                occupied = true;
            }
        }
    }
}
