using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    internal void BindEventLaneLines(TimelineSurface surface) =>
        surface.SetBinding(TimelineSurface.ShowStepSignalProperty,
            new Binding(nameof(DesktopSessionController.ShowEventLaneLines))
            { Source = _session, Mode = BindingMode.OneWay });

    internal (MidoraId Owner, IReadOnlyList<LaneTabDescriptor> Descriptors)? GetLaneTabDescriptors(WorkspaceViewModel workspace)
    {
        if (_session.Project is not { } project || workspace.ObjectId is not { } owner) return null;
        List<LaneTabDescriptor> result = [];
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, owner) is { } midi)
            {
                var segment = midi.Segment;
                result.Add(new(LaneTabKey.Velocity, "Vel.", segment.Notes.Count));
                result.Add(new(LaneTabKey.Instrument, "Inst.", segment.InstrumentChanges.Count, "Instrument changes"));
                foreach (var option in timeline.ParameterLaneOptions)
                    result.Add(new(LaneTabKey.From(option), option.Label, option.IsOpaqueMidiLane ? segment.OpaqueEvents.Count
                        : option.DirectMidiTarget is { } target ? timeline.GetMidiLaneCount(owner, segment.ChannelEvents.Generation, target) : 0,
                        CountError: timeline.LaneCountError));
            }
            else if (TimelineWorkspaceViewModel.FindSegment(project, owner) is { } logical)
            {
                result.Add(new(LaneTabKey.Velocity, "Vel.", logical.Segment.Notes.Count));
                var lanes = logical.Segment.ParameterLanes.ToDictionary(static lane => lane.ParameterId);
                foreach (var option in timeline.ParameterLaneOptions)
                    result.Add(new(LaneTabKey.From(option), option.Label, lanes.GetValueOrDefault(option.ParameterId)?.Points.Count ?? 0, "Parameter"));
            }
            else return null;
        }
        else if (workspace is InstrumentWorkspaceViewModel instrument
                 && project.EventInstruments.FirstOrDefault(value => value.Id == owner)?
                     .SubVoices.FirstOrDefault(value => value.Id == instrument.ActiveSubVoiceId) is { } voice)
        {
            owner = voice.Id;
            var snapshot = voice.Events.CreateQuerySnapshot();
            result.Add(new(LaneTabKey.Velocity, "Vel.", snapshot.NoteCount));
            result.Add(new(LaneTabKey.Instrument, "Inst.", voice.InstrumentChanges.Count, "Instrument changes"));
            foreach (var lane in instrument.RenderLanes)
            {
                if (lane.Target is not { } target) continue;
                bool curve = voice.Curves.Any(value => value.Target == target);
                string owners = lane.EventMappingChainId is not null ? curve ? "Mapping · Curve" : "Mapping" : curve ? "Curve" : "";
                result.Add(new(LaneTabKey.From(target), lane.Label,
                    snapshot.DiscoveryCounts.GetValueOrDefault(TemplateEventMidiTargets.EncodeDiscoveryKey(target)), owners));
            }
        }
        else return null;
        return (owner, result);
    }

    internal void ActivateLaneTarget(WorkspaceViewModel workspace, LaneTabKey key, bool explicitNavigation)
    {
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            int index = timeline.ParameterLaneOptions.ToList().FindIndex(value => LaneTabKey.From(value) == key);
            if (index < 0) return;
            bool changed = timeline.ActiveParameterLaneIndex != index;
            timeline.ActiveParameterLaneIndex = index;
            timeline.PreferCurrentParameterLaneOnNextRebuild();
            if (changed || explicitNavigation) _session.RefreshWorkspace(workspace);
        }
        else if (workspace is InstrumentWorkspaceViewModel instrument)
        {
            int index = instrument.RenderLanes.ToList().FindIndex(value => value.Target is { } target && LaneTabKey.From(target) == key);
            if (index < 0) return;
            bool changed = instrument.ActiveRenderLaneIndex != index;
            instrument.ActiveRenderLaneIndex = index;
            instrument.PreferCurrentRenderLaneOnNextRebuild();
            if (changed || explicitNavigation) _session.RefreshWorkspace(workspace);
        }
    }
    internal void AddLaneFromHeader(LaneTabHeader header)
    {
        if (!_session.CanEditProject) return;
        var args = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
        if (header.DataContext is TimelineWorkspaceViewModel) OnAddParameterLaneClick(header, args);
        else OnAddTemplateEventClick(header, args);
    }
    internal void CommitLaneSubdivision(object sender, KeyboardFocusChangedEventArgs e) => OnSubdivisionComboBoxLostKeyboardFocus(sender, e);
    private void ShowActiveEventLaneTab(WorkspaceViewModel workspace)
    {
        var host = FindDescendant<LaneTabHost>(WorkspaceTabs, value => value.IsVisible && ReferenceEquals(value.DataContext, workspace));
        host?.ShowCurrentEventTarget();
    }
}
