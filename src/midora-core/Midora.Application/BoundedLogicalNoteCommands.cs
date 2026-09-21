using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private const int BoundedNoteThreshold = 4096;

    private static IPreparedProjectEdit PrepareBoundedLogicalDuplicate(MidoraProject project,
        SegmentLocation sourceLocation, SegmentLocation target, IReadOnlyCollection<MidoraId> ids,
        long earliestTick, int pitchDelta)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = sourceLocation.Segment.Notes.CreateQuerySnapshot();
        var targetSource = target.Segment.Notes.CreateQuerySnapshot();
        var targetStamp = ProjectTimelineOwnerSourceStamp.Capture(target.Segment);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static _ => true, scope.Resources, scope.Token);
        long delta = checked(earliestTick - selected.Min(v => v.Value.StartTick));
        long firstId = project.NextStableId, nextId = firstId;
        var appended = BoundedLogicalNotePlanning.Append(Values(), static v => new(v.StartTick, v.Note),
            key => targetSource.EnumerateExactStart(key.Tick, key.Key), scope.Resources, scope.Token, static v => v.Id);
        try
        {
            if (appended.Count == 0)
            {
                var unchanged = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                    project, target.Track, target.Segment, TrackChange(target.Track.Id), targetStamp);
                appended.Dispose();
                return PublishBoundedNoteSelection(unchanged, selected.Select(v => v.Value.Id), [], static values => values, scope);
            }
            var result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, target.Segment, scope.Token);
            result.Notes.Clear();
            result.Notes.AdoptEditedSnapshot(project, targetSource, EmptyBoundedChanges<LogicalNoteSnapshotValue>(scope), appended, scope.Token);
            var receipt = TrackChange(sourceLocation.Track.Id, target.Track.Id);
            ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(receipt, target.Segment, result, appended.Select(v => v.Id));
            var edit = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, target.Track, target.Segment, result,
                receipt, firstId, nextId, targetStamp);
            return PublishBoundedNoteSelection(edit, selected.Select(v => v.Value.Id), appended.Select(v => v.Id),
                static values => values, scope);
        }
        catch { appended.Dispose(); throw; }
        IEnumerable<LogicalNoteSnapshotValue> Values()
        {
            foreach (var selectedValue in selected)
            {
                var value = selectedValue.Value;
                scope.Token.ThrowIfCancellationRequested();
                if ((long)value.Note + pitchDelta is < 0 or > 127) continue;
                long tick = checked(value.StartTick + delta);
                int pitch = checked(value.Note + pitchDelta);
                ValidateLogicalNote(tick, value.LengthTicks, pitch, value.Velocity);
                MidoraId id = new(nextId); nextId = checked(nextId + 1);
                yield return value with { Id = id, StartTick = tick, Note = pitch };
            }
        }
    }

    private static IPreparedProjectEdit PrepareBoundedTemplateDuplicate(MidoraProject project,
        EventInstrument instrument, SubVoice voice, IReadOnlyCollection<MidoraId> ids,
        long earliestTick, int pitchDelta)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = voice.Events.CreateQuerySnapshot();
        var sourceStamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids,
            static v => v.Kind == TemplateEventKind.Note, scope.Resources, scope.Token);
        long delta = checked(earliestTick - selected.Min(v => v.Value.Tick));
        long firstId = project.NextStableId, nextId = firstId;
        long length = instrument.TemplateLengthTicks;
        var appended = BoundedLogicalNotePlanning.Append(Values(), static v => new(v.Tick, v.Number),
            key => source.EnumerateNoteExactStart(key.Tick, key.Key), scope.Resources, scope.Token, static v => v.Id);
        try
        {
            if (appended.Count == 0)
            {
                var unchanged = ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(
                    project, instrument, voice, EventInstrumentChange(instrument.Id), sourceStamp);
                appended.Dispose();
                return PublishBoundedNoteSelection(unchanged, selected.Select(v => v.Value.Id), [], static values => values, scope);
            }
            var result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
            result.Events.Clear();
            result.Events.AdoptEditedSnapshot(project, source, EmptyBoundedChanges<TemplateEventSnapshotValue>(scope), appended, scope.Token);
            EnsureBoundedTemplateAppendMappings(project, source, result, appended, ref nextId);
            var receipt = EventInstrumentChange(instrument.Id);
            ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(receipt, voice, result, appended.Select(v => v.Id));
            var edit = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project, instrument, voice, result,
                receipt, firstId, nextId, sourceStamp);
            return PublishBoundedNoteSelection(new BoundedTemplateRootEdit(edit, instrument, length),
                selected.Select(v => v.Value.Id), appended.Select(v => v.Id), static values => values, scope);
        }
        catch { appended.Dispose(); throw; }
        IEnumerable<TemplateEventSnapshotValue> Values()
        {
            foreach (var selectedValue in selected)
            {
                var value = selectedValue.Value;
                scope.Token.ThrowIfCancellationRequested();
                if ((long)value.Number + pitchDelta is < 0 or > 127) continue;
                long tick = checked(value.Tick + delta);
                int pitch = checked(value.Number + pitchDelta);
                ValidateLogicalNote(tick, value.LengthTicks, pitch, value.Value);
                length = Math.Max(length, checked(tick + value.LengthTicks));
                MidoraId id = new(nextId); nextId = checked(nextId + 1);
                yield return value with { Id = id, Tick = tick, Number = pitch };
            }
        }
    }

    private static BoundedImmutableValueSource<TimelineValueEdit<T>> EmptyBoundedChanges<T>(BulkEditPreparationContext scope) where T : unmanaged
    {
        var store = new BoundedEditRecordStore<TimelineValueEdit<T>>(scope.Resources);
        store.Seal();
        return new BoundedTimelineEditValueSource<T>(store);
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalNotes(MidoraProject project,
        SegmentLocation location, IReadOnlyCollection<MidoraId> ids,
        Func<IReadOnlyList<BoundedLogicalNotePlanning.Selected<LogicalNoteSnapshotValue>>,
            Func<LogicalNoteSnapshotValue, LogicalNoteSnapshotValue?>> createTransform,
        bool collisions = true, bool expandWindow = false, SelectionPublisher? publishResult = null,
        bool formalCollisions = false)
    {
        publishResult ??= RequestedTimelineSelectionPublisher(new(ProjectTimelineOwnerKind.LogicalSegment, location.Segment.Id));
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = location.Segment.Notes.CreateQuerySnapshot();
        var sourceStamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static _ => true, scope.Resources, scope.Token);
        var transform = createTransform(selected);
        long minimumRetainedTick = location.Segment.ContentOffsetTick;
        using var changes = new OwnedBoundedSource<LogicalNoteSnapshotValue>(BoundedLogicalNotePlanning.Transform(
            source, selected, old =>
            {
                var replacement = transform(old);
                if (replacement is { } value)
                {
                    if (expandWindow && FallsBeforeProjectStart(location.Segment, value.StartTick)) return null;
                    ValidateLogicalNote(value.StartTick, value.LengthTicks, value.Note, value.Velocity);
                    minimumRetainedTick = Math.Min(minimumRetainedTick, value.StartTick);
                }
                return replacement;
            }, static value => new(value.StartTick, value.Note), static value => value.Id,
            key => source.EnumerateExactStart(key.Tick, key.Key), collisions, scope.Resources, scope.Token, formalCollisions));
        if (changes.Value.Count == 0)
        {
            var unchanged = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                project, location.Track, location.Segment, TrackChange(location.Track.Id), sourceStamp);
            return publishResult is null ? unchanged : PublishBoundedNoteSelection(unchanged,
                selected.Select(v => v.Value.Id), selected.Select(v => v.Value.Id), publishResult, scope);
        }
        Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, scope.Token);
        result.Notes.Clear();
        result.Notes.AdoptEditedSnapshot(project, source, changes.Value, cancellationToken: scope.Token);
        if (expandWindow)
        {
            var entry = new SegmentTransformEntry(location.Track, location.Segment, location.Index);
            var window = PlanBatchWindow(entry, [minimumRetainedTick]);
            ValidateSegmentTransformWindows(project, [window]);
            SetWindow(result, window.Replacement);
        }
        var receipt = TrackChange(location.Track.Id);
        if (!ProjectTimelineOwnerChangeSetBuilder.TryAddLogicalNoteValueEdits(receipt, location.Segment, result, changes.Value))
            ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(receipt, location.Segment, result,
                changes.Value.Select(edit => source.GetByOrdinal(edit.Ordinal).Id));
        var prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project,
            location.Track, location.Segment, result, receipt, expectedSourceStamp: sourceStamp);
        changes.Transfer();
        if (publishResult is null) return prepared;
        var final = result.Notes.CreateQuerySnapshot();
        return PublishBoundedNoteSelection(prepared, selected.Select(v => v.Value.Id),
            selected.Select(v => v.Value.Id).Where(id => final.TryFindOrdinalById(id, out _)), publishResult, scope);
    }

    private static IPreparedProjectEdit PrepareBoundedTemplateNotes(MidoraProject project,
        EventInstrument instrument, SubVoice voice, IReadOnlyCollection<MidoraId> ids,
        Func<IReadOnlyList<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>,
            Func<TemplateEventSnapshotValue, TemplateEventSnapshotValue?>> createTransform,
        bool collisions = true, SelectionPublisher? publishResult = null, bool formalCollisions = false)
    {
        publishResult ??= RequestedTimelineSelectionPublisher(new(ProjectTimelineOwnerKind.SubVoice, voice.Id, instrument.Id));
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = voice.Events.CreateQuerySnapshot();
        var sourceStamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids,
            static value => value.Kind == TemplateEventKind.Note, scope.Resources, scope.Token);
        var transform = createTransform(selected);
        long requiredLength = instrument.TemplateLengthTicks;
        using var changes = new OwnedBoundedSource<TemplateEventSnapshotValue>(BoundedLogicalNotePlanning.Transform(
            source, selected, old =>
            {
                var result = transform(old);
                if (result is { } value)
                {
                    ValidateLogicalNote(value.Tick, value.LengthTicks, value.Number, value.Value);
                    requiredLength = Math.Max(requiredLength, checked(value.Tick + value.LengthTicks));
                }
                return result;
            }, static value => new(value.Tick, value.Number), static value => value.Id,
            key => source.EnumerateNoteExactStart(key.Tick, key.Key), collisions, scope.Resources, scope.Token, formalCollisions));
        if (changes.Value.Count == 0)
        {
            var unchanged = ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(
                project, instrument, voice, EventInstrumentChange(instrument.Id), sourceStamp);
            return publishResult is null ? unchanged : PublishBoundedNoteSelection(unchanged,
                selected.Select(v => v.Value.Id), selected.Select(v => v.Value.Id), publishResult, scope);
        }
        SubVoice result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
        result.Events.Clear();
        result.Events.AdoptEditedSnapshot(project, source, changes.Value, cancellationToken: scope.Token);
        var receipt = EventInstrumentChange(instrument.Id);
        if (!ProjectTimelineOwnerChangeSetBuilder.TryAddTemplateNoteValueEdits(receipt, voice, result, changes.Value))
            ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(receipt, voice, result,
                changes.Value.Select(edit => source.GetByOrdinal(edit.Ordinal).Id));
        var root = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project,
            instrument, voice, result, receipt, expectedSourceStamp: sourceStamp);
        var prepared = new BoundedTemplateRootEdit(root, instrument, requiredLength);
        changes.Transfer();
        if (publishResult is null) return prepared;
        var final = result.Events.CreateQuerySnapshot();
        return PublishBoundedNoteSelection(prepared, selected.Select(v => v.Value.Id),
            selected.Select(v => v.Value.Id).Where(id => final.TryFindOrdinalById(id, out _)), publishResult, scope);
    }

    private static IPreparedProjectEdit PublishBoundedNoteSelection(IPreparedProjectEdit edit,
        IEnumerable<MidoraId> original, IEnumerable<MidoraId> result, SelectionPublisher publisher,
        BulkEditPreparationContext scope)
    {
        var before = Freeze(original);
        try
        {
            var after = Freeze(result);
            try { return WithSelectionPublication(edit, publisher(before), after, publisher); }
            catch { after.Dispose(); throw; }
        }
        catch { before.Dispose(); throw; }
        BoundedImmutableValueSource<MidoraId> Freeze(IEnumerable<MidoraId> values)
        {
            var store = new BoundedEditRecordStore<MidoraId>(scope.Resources);
            try
            {
                store.AddRange(values, scope.Token); store.Seal(); store.SpillResidentPages(scope.Token);
                return new(store);
            }
            catch { store.Dispose(); throw; }
        }
    }

    // A failed/canceled detached construction disposes the spill source; after
    // publication the immutable root/history owns it instead of this scope.
    private sealed class OwnedBoundedSource<T>(BoundedImmutableValueSource<TimelineValueEdit<T>> source) : IDisposable where T : unmanaged
    {
        private bool _transferred;
        public BoundedImmutableValueSource<TimelineValueEdit<T>> Value { get; } = source;
        public void Transfer() => _transferred = true;
        public void Dispose() { if (!_transferred) Value.Dispose(); }
    }

    private sealed class BoundedTemplateRootEdit(IPreparedProjectEdit root, EventInstrument instrument, long newLength)
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private readonly long _oldLength = instrument.TemplateLengthTicks;
        public bool HasChanges => root.HasChanges;
        public ProjectChangeSet Changes => root.Changes;
        public void ValidateForPublication(MidoraProject project)
        {
            if (root is IPreparedProjectEditPublicationGate gate) gate.ValidateForPublication(project);
        }
        public void Apply(MidoraProject project) { root.Apply(project); instrument.TemplateLengthTicks = newLength; }
        public void Undo(MidoraProject project) { root.Undo(project); instrument.TemplateLengthTicks = _oldLength; }
    }
}
