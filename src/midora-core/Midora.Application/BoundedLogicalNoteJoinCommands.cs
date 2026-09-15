using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedLogicalJoin(MidoraProject project,
        SegmentLocation location, IReadOnlyCollection<MidoraId> ids, NoteJoinOptions options,
        SelectionPublisher publisher, CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var source = location.Segment.Notes.CreateQuerySnapshot();
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static _ => true, scope.Resources, token);
        using var changes = new OwnedBoundedSource<LogicalNoteSnapshotValue>(BoundedLogicalNotePlanning.Join(selected,
            static v => new(v.StartTick, v.Note), static v => checked(v.StartTick + v.LengthTicks),
            static (v, end) => v with { LengthTicks = checked(end - v.StartTick) }, options.MaximumGapTicks, scope.Resources, token));
        var result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, token);
        result.Notes.Clear();
        result.Notes.AdoptEditedSnapshot(project, source, changes.Value, cancellationToken: token);
        var receipt = TrackChange(location.Track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(receipt, location.Segment, result,
            changes.Value.Select(v => source.GetByOrdinal(v.Ordinal).Id));
        IPreparedProjectEdit edit = changes.Value.Count == 0
            ? ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(project, location.Track, location.Segment, TrackChange(location.Track.Id), stamp)
            : ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track, location.Segment, result, receipt, expectedSourceStamp: stamp);
        var final = result.Notes.CreateQuerySnapshot();
        var prepared = PublishBoundedNoteSelection(edit, selected.Select(v => v.Value.Id),
            selected.Select(v => v.Value.Id).Where(id => final.TryFindOrdinalById(id, out _)), publisher, scope);
        changes.Transfer();
        return prepared;
    }

    private static IPreparedProjectEdit PrepareBoundedTemplateJoin(MidoraProject project,
        EventInstrument instrument, SubVoice voice, IReadOnlyCollection<MidoraId> ids, NoteJoinOptions options,
        SelectionPublisher publisher, CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var source = voice.Events.CreateQuerySnapshot();
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static v => v.Kind == TemplateEventKind.Note, scope.Resources, token);
        using var changes = new OwnedBoundedSource<TemplateEventSnapshotValue>(BoundedLogicalNotePlanning.Join(selected,
            static v => new(v.Tick, v.Number), static v => checked(v.Tick + v.LengthTicks),
            static (v, end) => v with { LengthTicks = checked(end - v.Tick) }, options.MaximumGapTicks, scope.Resources, token));
        var result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, token);
        result.Events.Clear();
        result.Events.AdoptEditedSnapshot(project, source, changes.Value, cancellationToken: token);
        var receipt = EventInstrumentChange(instrument.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(receipt, voice, result,
            changes.Value.Select(v => source.GetByOrdinal(v.Ordinal).Id));
        IPreparedProjectEdit edit = changes.Value.Count == 0
            ? ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(project, instrument, voice, EventInstrumentChange(instrument.Id), stamp)
            : ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project, instrument, voice, result, receipt, expectedSourceStamp: stamp);
        var final = result.Events.CreateQuerySnapshot();
        var prepared = PublishBoundedNoteSelection(edit, selected.Select(v => v.Value.Id),
            selected.Select(v => v.Value.Id).Where(id => final.TryFindOrdinalById(id, out _)), publisher, scope);
        changes.Transfer();
        return prepared;
    }
}
