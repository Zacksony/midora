using System.Windows;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    internal static bool IsTimelineNoteGenerationSource(WorkspaceTimelineSelectionSource? source) =>
        source?.Kind is WorkspaceTimelineSelectionKind.LogicalNote
            or WorkspaceTimelineSelectionKind.DirectMidiNote or WorkspaceTimelineSelectionKind.TemplateNote;

    internal static bool IsTimelineGenerationSource(WorkspaceTimelineSelectionSource? source) =>
        IsTimelineNoteGenerationSource(source)
        || source?.Kind is WorkspaceTimelineSelectionKind.LogicalParameterPoint
            or WorkspaceTimelineSelectionKind.DirectMidiEventPoint or WorkspaceTimelineSelectionKind.SubVoiceEventPoint;

    private async void OnBatchCreateTimelineObjectsClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (!_session.CanEditProject || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || _lastTimelineCommandSurface is not { } surface
            || GetTimelineSelectionSource(workspace, surface) is not { } source
            || !IsTimelineGenerationSource(source)) return;
        long baseTick = workspace switch
        {
            TimelineWorkspaceViewModel timeline => timeline.EditCursorTick ?? 0,
            InstrumentWorkspaceViewModel instrument => instrument.EditCursorTick ?? 0,
            _ => 0
        };
        bool notes = IsTimelineNoteGenerationSource(source);
        try
        {
            (double minimum, double maximum) = GetTimelineGenerationRange(_session.Project!, source);
            var dialog = new TimelineGenerationDialog(notes, Math.Max(0, baseTick), minimum, maximum) { Owner = this };
            if (ShowModalDialog(dialog) != true) return;
            IProjectEditCommand command = CreateTimelineGenerationCommand(source, dialog.NoteOptions, dialog.EventOptions);
            await ExecuteStagedProjectOperationAsync(command.Name,
                new TimelineCreationEditCommand(command, source), workspace, surface);
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"Batch Create: {exception.Message}", isError: true);
        }
        finally { RestoreModalCommandFocus(workspace, surface); }
    }

    internal static (double Minimum, double Maximum) GetTimelineGenerationRange(
        MidoraProject project, WorkspaceTimelineSelectionSource source)
    {
        if (source.Kind == WorkspaceTimelineSelectionKind.LogicalParameterPoint
            && source.OwnerId is MidoraId segmentId && source.SecondaryOwnerId is MidoraId laneId
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } location)
        {
            var lane = location.Segment.ParameterLanes.Single(value => value.Id == laneId);
            var definition = project.FindEventInstrumentDefinition(location.Track)?.LogicalParameters
                .Single(value => value.Id == lane.ParameterId)
                ?? throw new InvalidOperationException("The Logical Parameter Lane is not bound to a valid definition.");
            return (definition.Minimum, definition.Maximum);
        }
        if (source.Kind == WorkspaceTimelineSelectionKind.SubVoiceEventPoint && source.MidiTarget is { } target)
            return InstrumentWorkspaceViewModel.MidiValueRange(target);
        return (0, source.DirectMidiEventKind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127);
    }

    internal static IProjectEditCommand CreateTimelineGenerationCommand(WorkspaceTimelineSelectionSource source,
        NoteGenerationOptions? notes, EventGenerationOptions? points) => source.Kind switch
    {
        WorkspaceTimelineSelectionKind.LogicalNote => ProjectDomainEditCommands.GenerateLogicalNotes(
            source.OwnerId!.Value, notes!),
        WorkspaceTimelineSelectionKind.DirectMidiNote => ProjectDomainEditCommands.GenerateDirectMidiNotes(
            source.OwnerId!.Value, notes!),
        WorkspaceTimelineSelectionKind.TemplateNote => ProjectDomainEditCommands.GenerateTemplateNotes(
            source.OwnerId!.Value, source.SecondaryOwnerId!.Value, notes!),
        WorkspaceTimelineSelectionKind.LogicalParameterPoint => ProjectDomainEditCommands.GenerateLogicalParameterPoints(
            source.OwnerId!.Value, source.SecondaryOwnerId!.Value, points!),
        WorkspaceTimelineSelectionKind.DirectMidiEventPoint => ProjectDomainEditCommands.GenerateDirectMidiEventPoints(
            source.OwnerId!.Value, source.DirectMidiEventKind!.Value, source.DirectMidiData1, points!),
        WorkspaceTimelineSelectionKind.SubVoiceEventPoint => ProjectDomainEditCommands.GenerateTemplateEventPoints(
            source.OwnerId!.Value, source.SecondaryOwnerId!.Value, source.MidiTarget!.Value, points!),
        _ => throw new InvalidOperationException("Batch Create requires a Piano Roll or a numeric Event Lane.")
    };
}
