using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>
/// Resolves Paste routing from the frozen payload and its destination, never
/// from the previous selection or the lane that happens to be visible. Runs
/// during detached preparation, before the constant-cost selection publication.
/// </summary>
internal static class ClipboardSelectionProjection
{
    internal static PreparedWorkspaceSelectionProjection Prepare(
        MidoraProject project,
        IProjectClipboardPasteCommand command,
        IReadOnlyList<MidoraId> resultIds,
        WorkspaceSelection? selection,
        CancellationToken cancellationToken)
    {
        ProjectClipboardPasteTarget target = command.PasteTarget;
        WorkspaceTimelineSelectionSource? source = null;
        WorkspaceTimelineSelectionSource? quantize = null;
        if (resultIds.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (target.Kind)
            {
                case ProjectObjectClipboardKind.Segments:
                case ProjectObjectClipboardKind.MidiSegments:
                case ProjectObjectClipboardKind.ArrangementSegments:
                    source = new(WorkspaceTimelineSelectionKind.ArrangementSegment);
                    break;
                case ProjectObjectClipboardKind.LogicalNotes:
                case ProjectObjectClipboardKind.DirectMidiNotes:
                    source = new(target.TargetIsDirectMidi
                        ? WorkspaceTimelineSelectionKind.DirectMidiNote
                        : WorkspaceTimelineSelectionKind.LogicalNote, target.OwnerId);
                    break;
                case ProjectObjectClipboardKind.LogicalParameterLaneContent:
                    source = LogicalParameterSource(project, target);
                    break;
                case ProjectObjectClipboardKind.DirectMidiEvents:
                {
                    ProjectClipboardPasteSelectionKinds kinds = command.GetPasteSelectionKinds(cancellationToken);
                    if (!kinds.OnlyEventPoints) break;
                    quantize = new(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, target.OwnerId);
                    if (kinds.DirectMidiEventKind is DirectMidiChannelEventKind kind)
                        source = new(WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
                            target.OwnerId, DirectMidiEventKind: kind, DirectMidiData1: kinds.DirectMidiData1,
                            PointMaximum: kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127);
                    break;
                }
                case ProjectObjectClipboardKind.SubVoiceTimelineEvents:
                {
                    ProjectClipboardPasteSelectionKinds kinds = command.GetPasteSelectionKinds(cancellationToken);
                    if (kinds.OnlyNotes)
                        source = new(WorkspaceTimelineSelectionKind.TemplateNote,
                            target.OwnerId, target.SecondaryOwnerId);
                    else if (kinds.OnlyEventPoints)
                    {
                        quantize = new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                            target.OwnerId, target.SecondaryOwnerId);
                        if (kinds.MidiTarget is MidiValueTarget midiTarget)
                        {
                            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(midiTarget);
                            source = new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                                target.OwnerId, target.SecondaryOwnerId, midiTarget,
                                PointMinimum: minimum, PointMaximum: maximum);
                        }
                    }
                    break;
                }
            }
        }
        // Explicit nulls are important for opaque/structure/mixed payloads:
        // adopting those IDs must also discard an unrelated old Note context.
        return (selection ?? new WorkspaceSelection()).PrepareProjection(
            resultIds, source, quantize, cancellationToken);
    }

    private static WorkspaceTimelineSelectionSource? LogicalParameterSource(
        MidoraProject project, ProjectClipboardPasteTarget target)
    {
        if (target.OwnerId is not MidoraId segmentId || target.SecondaryOwnerId is not MidoraId laneId)
            return null;
        var location = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
        if (location is null) return null;
        LogicalParameterLane? lane = location.Value.Segment.ParameterLanes.FirstOrDefault(value => value.Id == laneId);
        LogicalParameterDefinition? definition = project.FindEventInstrumentDefinition(location.Value.Track)?
            .LogicalParameters.FirstOrDefault(value => value.Id == lane?.ParameterId);
        if (definition is null) return null;
        double minimum = definition.DisplayMinimum, maximum = definition.DisplayMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            (minimum, maximum) = (definition.Minimum, definition.Maximum);
        return new(WorkspaceTimelineSelectionKind.LogicalParameterPoint, segmentId, laneId,
            PointMinimum: minimum, PointMaximum: maximum);
    }
}
