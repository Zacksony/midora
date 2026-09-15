using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedOpaqueTransform(MidoraProject project, MidoraId segmentId,
        IReadOnlyCollection<MidoraId> ids, Func<long, long?> transform, bool duplicate = false)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedOpaqueMidiSource.Capture(project, location.Segment.OpaqueEvents);
        int extent = source.Metadata.FormalExtent;
        long nextId = project.NextStableId;
        var selectionOrder = duplicate
            ? Comparer<BoundedDirectEventDelta>.Create(static (a, b) => a.Value.Id.CompareTo(b.Value.Id))
            : BoundedDirectMidiEventSource.OrdinalComparer;
        using var selection = BoundedEditSort.Sort(Select(), selectionOrder, scope.Resources, scope.Token);
        using var planStore = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        int previous = -1;
        foreach (var item in selection)
        {
            scope.Token.ThrowIfCancellationRequested();
            if (previous == item.Ordinal) throw new ArgumentException("Opaque MIDI IDs must be distinct.", nameof(ids));
            previous = item.Ordinal;
            long? tick = transform(item.Value.Tick);
            if (tick is < 0) throw new InvalidOperationException("Imported MIDI Events cannot move before tick 0.");
            if (duplicate)
            {
                if (tick.HasValue) planStore.Add(new(extent++, false, default,
                    item.Value with { Id = new(nextId++), Tick = tick.Value }), scope.Token);
            }
            else if (!tick.HasValue || tick.Value != item.Value.Tick)
                planStore.Add(item with { Deleted = !tick.HasValue, Value = item.Value with { Tick = tick ?? item.Value.Tick } }, scope.Token);
        }
        planStore.Seal();
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(planStore);
        return PublishBoundedOpaqueRoot(project, location, source, plan, scope, null, extent,
            duplicate ? nextId : null, stamp, duplicate ? ids : null);
        IEnumerable<BoundedDirectEventDelta> Select()
        {
            if (ids.Count == 0) throw new ArgumentException("At least one imported MIDI Event is required.", nameof(ids));
            int count = 0;
            foreach (var value in source.Metadata.ResolveIds(ids, scope.Token))
            {
                if ((count++ & 255) == 0) scope.Checkpoint(count, ids.Count);
                yield return value;
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedOpaqueMidiAppend(MidoraProject project, MidoraId segmentId,
        Func<long, IEnumerable<Midora.Domain.OpaqueMidiEventValue>> createValues)
    {
        var location = FindMidiSegment(project, segmentId);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedOpaqueMidiSource.Capture(project, location.Segment.OpaqueEvents);
        using var payloads = source.CreatePayloadWriter(scope);
        using var store = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        int extent = source.Metadata.FormalExtent;
        long nextId = project.NextStableId;
        foreach (var value in createValues(nextId))
        {
            scope.Token.ThrowIfCancellationRequested();
            if (value.Id.Value != nextId || value.Tick < 0)
                throw new InvalidOperationException("Opaque append values or Stable ID reservation are invalid.");
            store.Add(new(extent++, false, default, source.Encode(value, payloads)), scope.Token);
            nextId = checked(nextId + 1);
        }
        store.Seal();
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
        return PublishBoundedOpaqueRoot(project, location, source, plan, scope, payloads, extent, nextId, stamp);
    }

    private static void AdoptBoundedOpaqueMidiEvents(MidoraProject project, MidiSegment target,
        IEnumerable<Midora.Domain.OpaqueMidiEventValue> values)
    {
        if (target.OpaqueEvents.Count != 0) throw new InvalidOperationException("Detached opaque content must start empty.");
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedOpaqueMidiSource.Capture(project, target.OpaqueEvents);
        using var payloads = source.CreatePayloadWriter(scope);
        using var store = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        int ordinal = 0;
        foreach (var value in values)
        {
            if (value.Tick < 0) throw new ArgumentOutOfRangeException(nameof(values));
            store.Add(new(ordinal++, false, default, source.Encode(value, payloads)), scope.Token);
        }
        store.Seal();
        if (store.Count == 0) return;
        using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
        target.OpaqueEvents.AdoptContentSource(source.Apply(plan, scope, payloads, ordinal), target.OpaqueEvents.Generation + 1);
    }

    private static IPreparedProjectEdit PublishBoundedOpaqueRoot(MidoraProject project, MidiSegmentLocation location,
        BoundedOpaqueMidiSource source, BoundedImmutableValueSource<BoundedDirectEventDelta> plan,
        BulkEditPreparationContext scope, BoundedOpaqueMidiSource.PayloadWriter? payloads, int extent,
        long? nextStableId, ProjectTimelineOwnerSourceStamp stamp, IEnumerable<MidoraId>? originalSelection = null)
    {
        if (plan.Count == 0) return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
        var revised = source.Apply(plan, scope, payloads, extent);
        MidiSegment replacement = new(project, location.Segment.Id)
        { ProjectStartTick = location.Segment.ProjectStartTick, LengthTicks = location.Segment.LengthTicks,
            ContentOffsetTick = location.Segment.ContentOffsetTick };
        replacement.OpaqueEvents.AdoptContentSource(revised, location.Segment.OpaqueEvents.Generation + 1);
        location.Segment.Notes.CloneTo(replacement.Notes, scope.Token);
        location.Segment.ChannelEvents.CloneTo(replacement.ChannelEvents, scope.Token);
        var prepared = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(project, location.Track, location.Segment,
            replacement, PureMidiTrackChange(location.Track.Id), nextStableId.HasValue ? project.NextStableId : null,
            nextStableId, stamp);
        return nextStableId.HasValue ? PublishBoundedNoteSelection(prepared, originalSelection ?? [],
            plan.Where(static item => !item.Deleted).Select(static item => item.Value.Id), static ids => ids, scope) : prepared;
    }
}
