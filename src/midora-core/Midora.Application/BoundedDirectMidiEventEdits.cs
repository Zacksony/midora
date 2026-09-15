using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct BoundedDrawEventPoint(long Tick, int Data1, int Data2);
    private static IPreparedProjectEdit PrepareBoundedDirectMidiEventLine(MidoraProject project, MidoraId segmentId,
        DirectMidiChannelEventKind kind, int laneData1, IReadOnlyCollection<DirectMidiEventPointEdit> points)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
        using var ordered = BoundedEditSort.Sort(points.Select(static value => new BoundedDrawEventPoint(value.Tick, value.Data1, value.Data2)), Comparer<BoundedDrawEventPoint>.Create(
            static (a, b) => a.Tick.CompareTo(b.Tick)), scope.Resources, scope.Token);
        using var incumbents = BoundedEditSort.Sort(ReadIncumbents(), Comparer<BoundedDirectEventDelta>.Create(static (a, b) =>
        {
            int order = a.Value.Tick.CompareTo(b.Value.Tick);
            return order != 0 ? order : a.Ordinal.CompareTo(b.Ordinal);
        }), scope.Resources, scope.Token);
        int extent = source.FormalExtent;
        long nextId = project.NextStableId;
        using var planStore = BoundedEditSort.Sort(Plan(), BoundedDirectMidiEventSource.OrdinalComparer, scope.Resources, scope.Token);
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(planStore);
        return PublishBoundedDirectMidiEvents(project, location, source, plan, scope,
            BoundedEventCollisionMode.Overwrite, extent: extent, nextStableId: nextId, expectedSourceStamp: stamp);
        IEnumerable<BoundedDirectEventDelta> Plan()
        {
            long previous = -1;
            using var old = incumbents.GetEnumerator();
            bool more = old.MoveNext();
            foreach (var point in ordered)
            {
                scope.Token.ThrowIfCancellationRequested();
                ValidateDirectMidiEvent(point.Tick, kind, point.Data1, point.Data2);
                if (point.Tick == previous) throw new ArgumentException("Direct MIDI Event point ticks must be distinct.", nameof(points));
                previous = point.Tick;
                while (more && old.Current.Value.Tick < point.Tick) more = old.MoveNext();
                if (more && old.Current.Value.Tick == point.Tick)
                {
                    var original = old.Current;
                    yield return original with { Value = original.Value with { Data1 = point.Data1, Data2 = point.Data2 } };
                }
                else
                {
                    var value = new DirectMidiChannelEventValue(new(nextId++), point.Tick, kind, point.Data1, point.Data2, nextId);
                    yield return new(extent++, false, default, value);
                }
            }
        }
        IEnumerable<BoundedDirectEventDelta> ReadIncumbents()
        {
            HashSet<DirectMidiEventStartKey> keys = [];
            foreach (var point in ordered)
            {
                scope.Token.ThrowIfCancellationRequested();
                keys.Add(new(point.Tick, kind, DirectMidiEventUsesData1Selector(kind) ? laneData1 : 0));
                if (keys.Count < 4096) continue;
                foreach (var item in source.ResolveValues(source.QueryStartKeys(keys), scope.Token)) yield return item;
                keys.Clear();
            }
            if (keys.Count != 0)
                foreach (var item in source.ResolveValues(source.QueryStartKeys(keys), scope.Token)) yield return item;
        }
    }

    private enum BoundedEventCollisionMode { Overwrite, Reject, FormalLatest, None }
    private static int ValidateBoundedDirectEventLane(IReadOnlyList<BoundedDirectEventDelta> values)
    {
        var expected = BoundedDirectMidiEventSource.Key(values[0].Value) with { Tick = 0 };
        foreach (var value in values)
            if ((BoundedDirectMidiEventSource.Key(value.Value) with { Tick = 0 }) != expected)
                throw new InvalidOperationException("Selected Direct MIDI Event points must belong to one lane.");
        return expected.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127;
    }
    private readonly record struct BoundedDirectEventCollision(DirectMidiEventStartKey Key,
        int Ordinal, bool Edited, long Order, MidoraId Id);
    private static int CompareEventKeys(DirectMidiEventStartKey a, DirectMidiEventStartKey b)
    {
        int tick = a.Tick.CompareTo(b.Tick);
        if (tick != 0) return tick;
        int kind = a.Kind.CompareTo(b.Kind);
        return kind != 0 ? kind : a.Data1.CompareTo(b.Data1);
    }

    private static IPreparedProjectEdit PrepareBoundedDirectMidiEventAppend(MidoraProject project,
        MidoraId segmentId, Func<long, IEnumerable<DirectMidiChannelEventValue>> createValues,
        IEnumerable<MidoraId>? originalSelection = null)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
        using var store = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        long nextId = project.NextStableId;
        int ordinal = source.FormalExtent;
        foreach (var value in createValues(nextId))
        {
            scope.Token.ThrowIfCancellationRequested();
            if (value.Id.Value != nextId) throw new InvalidOperationException("Direct MIDI append IDs must be consecutive.");
            ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            store.Add(new(ordinal++, false, default, value), scope.Token);
            nextId = checked(nextId + 1);
        }
        store.Seal();
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
        return PublishBoundedDirectMidiEvents(project, location, source, plan, scope,
            BoundedEventCollisionMode.Overwrite, publisher: static ids => ids, extent: ordinal, nextStableId: nextId,
            expectedSourceStamp: stamp, selectionBefore: originalSelection ?? []);
    }

    private static IPreparedProjectEdit PrepareBoundedDirectMidiEventTransform(MidoraProject project,
        MidoraId segmentId, IReadOnlyCollection<MidoraId> ids,
        Func<IReadOnlyList<BoundedDirectEventDelta>, Func<DirectMidiChannelEventValue, DirectMidiChannelEventValue?>> createTransform,
        BoundedEventCollisionMode collisionMode = BoundedEventCollisionMode.Overwrite, SelectionPublisher? publisher = null)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
        using var selectedStore = BoundedEditSort.Sort(Select(), BoundedDirectMidiEventSource.OrdinalComparer,
            scope.Resources, scope.Token, scope.ProgressInRange(0.13, 0.02));
        using var selected = new BoundedImmutableValueSource<BoundedDirectEventDelta>(selectedStore);
        if (selected.Count == 0) throw new ArgumentException("At least one Direct MIDI Event is required.", nameof(ids));
        int previous = -1;
        foreach (var item in selected)
        {
            if (item.Ordinal == previous) throw new ArgumentException("Event IDs must be distinct.", nameof(ids));
            previous = item.Ordinal;
        }
        var transform = createTransform(selected);
        using var store = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        int count = 0;
        foreach (var item in selected)
        {
            if ((count++ & 255) == 0) scope.Checkpoint(count, selected.Count);
            var value = transform(item.Value);
            if (value is { } changed) ValidateDirectMidiEvent(changed.Tick, changed.Kind, changed.Data1, changed.Data2);
            store.Add(item with { Deleted = value is null, Value = value ?? item.Value }, scope.Token);
        }
        store.Seal();
        scope.Checkpoint(selected.Count, selected.Count, TimelineEditPreparationPhase.Planning);
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
        return PublishBoundedDirectMidiEvents(project, location, source, plan, scope, collisionMode, publisher, expectedSourceStamp: stamp);

        IEnumerable<BoundedDirectEventDelta> Select()
        {
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

    private static IPreparedProjectEdit PublishBoundedDirectMidiEvents(MidoraProject project,
        MidiSegmentLocation location, BoundedDirectMidiEventSource source,
        BoundedImmutableValueSource<BoundedDirectEventDelta> planned, BulkEditPreparationContext scope,
        BoundedEventCollisionMode mode, SelectionPublisher? publisher = null, int? extent = null, long? nextStableId = null,
        ProjectTimelineOwnerSourceStamp? expectedSourceStamp = null, IEnumerable<MidoraId>? selectionBefore = null)
    {
        var candidateComparer = Comparer<BoundedDirectEventCollision>.Create((a, b) =>
        {
            int key = CompareEventKeys(a.Key, b.Key);
            if (key != 0) return key;
            if (mode != BoundedEventCollisionMode.FormalLatest)
            {
                int edited = b.Edited.CompareTo(a.Edited);
                if (edited != 0) return edited;
                return b.Ordinal.CompareTo(a.Ordinal);
            }
            int order = b.Order.CompareTo(a.Order);
            return order != 0 ? order : b.Id.CompareTo(a.Id);
        });
        using var candidates = BoundedEditSort.Sort(Candidates(), candidateComparer, scope.Resources, scope.Token,
            scope.ProgressInRange(0.65, 0.025));
        using var removed = BoundedEditSort.Sort(Removed(), Comparer<int>.Default, scope.Resources, scope.Token,
            scope.ProgressInRange(0.70, 0.025));
        using var patchStore = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        using var removals = removed.GetEnumerator();
        bool hasRemoval = removals.MoveNext();
        int patched = 0;
        foreach (var item in planned)
        {
            scope.Token.ThrowIfCancellationRequested();
            while (hasRemoval && removals.Current < item.Ordinal)
            {
                var old = source.GetPhysical(removals.Current);
                patchStore.Add(new(removals.Current, true, old, old), scope.Token);
                hasRemoval = removals.MoveNext();
            }
            bool deleted = item.Deleted || hasRemoval && removals.Current == item.Ordinal;
            if (deleted || item.Value != item.Original) patchStore.Add(item with { Deleted = deleted }, scope.Token);
            if (hasRemoval && removals.Current == item.Ordinal) hasRemoval = removals.MoveNext();
            if ((++patched & 255) == 0)
                scope.Checkpoint(patched, planned.Count, TimelineEditPreparationPhase.BuildingResult, 0.725, 0.025);
        }
        while (hasRemoval)
        {
            var old = source.GetPhysical(removals.Current);
            patchStore.Add(new(removals.Current, true, old, old), scope.Token);
            hasRemoval = removals.MoveNext();
        }
        patchStore.Seal();
        if (patchStore.Count == 0)
        {
            var unchanged = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegmentRevisionGate(
                project, location.Track, location.Segment, PureMidiTrackChange(location.Track.Id), expectedSourceStamp);
            return publisher is null ? unchanged : PublishBoundedNoteSelection(unchanged,
                selectionBefore ?? planned.Select(static value => value.Value.Id), planned.Select(static value => value.Value.Id), publisher, scope);
        }
        using var patch = new BoundedImmutableValueSource<BoundedDirectEventDelta>(patchStore);
        scope.Checkpoint(0, 2, TimelineEditPreparationPhase.BuildingResult);
        var revised = source.Apply(patch, scope.Resources, scope.Token, extent);
        scope.Checkpoint(1, 2, TimelineEditPreparationPhase.BuildingResult);
        MidiSegment replacement = new(project, location.Segment.Id)
        {
            ProjectStartTick = location.Segment.ProjectStartTick, ContentOffsetTick = location.Segment.ContentOffsetTick,
            LengthTicks = location.Segment.LengthTicks
        };
        replacement.ChannelEvents.AdoptContentSource(revised, checked(location.Segment.ChannelEvents.Generation + 1));
        location.Segment.Notes.CloneTo(replacement.Notes, scope.Token);
        location.Segment.OpaqueEvents.CloneTo(replacement.OpaqueEvents, scope.Token);
        var changes = PureMidiTrackChange(location.Track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectEvents(changes, location.Segment, replacement, patch.Select(static value => value.Value.Id));
        var prepared = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(project, location.Track,
            location.Segment, replacement, changes, nextStableId.HasValue ? project.NextStableId : null, nextStableId,
            expectedSourceStamp);
        scope.Checkpoint(2, 2, TimelineEditPreparationPhase.BuildingResult);
        return publisher is null ? prepared : PublishBoundedNoteSelection(prepared,
            selectionBefore ?? planned.Where(value => value.Ordinal < source.FormalExtent).Select(static value => value.Value.Id),
            planned.Select(static value => value.Value.Id).Where(id => revised.FindChannelEventIndex(id) >= 0), publisher, scope);

        IEnumerable<BoundedDirectEventCollision> Candidates()
        {
            if (mode == BoundedEventCollisionMode.None) yield break;
            using var keys = BoundedEditSort.Sort(planned.Where(static value => !value.Deleted).Select(static value =>
                new BoundedDirectEventCollision(BoundedDirectMidiEventSource.Key(value.Value), value.Ordinal, true,
                    value.Value.Order, value.Value.Id)), candidateComparer, scope.Resources, scope.Token);
            HashSet<DirectMidiEventStartKey> batch = [];
            DirectMidiEventStartKey? previous = null;
            int queried = 0;
            foreach (var item in keys)
            {
                if ((++queried & 255) == 0)
                    scope.Checkpoint(queried, keys.Count, TimelineEditPreparationPhase.ResolvingCollisions, 0.55, 0.10);
                yield return item;
                if (previous == item.Key) continue;
                previous = item.Key; batch.Add(item.Key);
                if (batch.Count < scope.Resources.Budget.PageRecordCount) continue;
                foreach (var incumbent in Existing(batch)) yield return incumbent;
                batch.Clear();
            }
            if (batch.Count != 0) foreach (var incumbent in Existing(batch)) yield return incumbent;
            scope.Checkpoint(keys.Count, keys.Count, TimelineEditPreparationPhase.ResolvingCollisions, 0.55, 0.10);
        }
        IEnumerable<BoundedDirectEventCollision> Existing(IReadOnlySet<DirectMidiEventStartKey> keys)
        {
            foreach (var resolved in source.ResolveValues(source.QueryStartKeys(keys), scope.Token))
            {
                scope.Token.ThrowIfCancellationRequested();
                var value = resolved.Value;
                int ordinal = resolved.Ordinal;
                int low = 0, high = planned.Count;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    if (planned[middle].Ordinal < ordinal) low = middle + 1; else high = middle;
                }
                if (low < planned.Count && planned[low].Ordinal == ordinal) continue;
                yield return new(BoundedDirectMidiEventSource.Key(value), ordinal, false, value.Order, value.Id);
            }
        }
        IEnumerable<int> Removed()
        {
            DirectMidiEventStartKey? previous = null;
            int examined = 0;
            foreach (var item in candidates)
            {
                if ((++examined & 255) == 0)
                    scope.Checkpoint(examined, candidates.Count, TimelineEditPreparationPhase.ResolvingCollisions, 0.675, 0.025);
                if (previous == item.Key)
                {
                    if (mode == BoundedEventCollisionMode.Reject)
                        throw new InvalidOperationException("The Direct MIDI Event transform would create overlapping points.");
                    yield return item.Ordinal;
                }
                previous = item.Key;
            }
        }
    }
}
