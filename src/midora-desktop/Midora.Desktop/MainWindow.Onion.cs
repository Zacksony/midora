using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    private void OnOnionMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _session.ActiveWorkspace is not { } workspace
            || _session.GetOnionControls(workspace) is not { } state
            || !_session.StopEventInstrumentKeyboardPreviewForEditing()) return;
        ContextMenu menu = new() { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        bool StillTargetsSameObject() => ReferenceEquals(_session.ActiveWorkspace, workspace)
            && _session.GetOnionControls(workspace) is { } current
            && current.TargetId == state.TargetId && current.InstrumentId == state.InstrumentId;
        MenuItem enable = new() { Header = state.Enabled ? "Disable" : "Enable" };
        enable.Click += (_, _) => { if (StillTargetsSameObject()) _session.SetOnionEnabled(workspace, !state.Enabled); };
        menu.Items.Add(enable);
        menu.Items.Add(new Separator());
        string kind = state.InstrumentId is null ? "Track" : "SubVoice";
        AddNeighbor($"Show Previous {kind}", state.Previous, previous: true);
        AddNeighbor($"Show Next {kind}", state.Next, previous: false);
        bool openingDialog = false;
        AddDialogItem($"Select {kind}s...", OpenOnionSources);
        menu.Items.Add(new Separator());
        AddDialogItem("Settings...", OpenOnionSettings);
        menu.Closed += (_, _) =>
        {
            button.ContextMenu = null;
            if (!openingDialog) RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
        };
        button.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;

        void AddNeighbor(string title, MidoraId? id, bool previous)
        {
            MenuItem item = new() { Header = title, IsEnabled = id is not null };
            item.Click += (_, _) =>
            {
                if (StillTargetsSameObject()) _session.ShowAdjacentOnionSource(workspace, previous);
            };
            menu.Items.Add(item);
        }

        void AddDialogItem(string title, Action open)
        {
            MenuItem item = new() { Header = title };
            item.Click += (_, _) =>
            {
                openingDialog = true;
                RunAfterMenuClosed(item, () => { if (StillTargetsSameObject()) open(); });
            };
            menu.Items.Add(item);
        }
    }

    private void OnOpenAllTracksClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null || !_session.StopEventInstrumentKeyboardPreviewForEditing()) return;
        _session.OpenAllTracks();
    }
    private void OpenOnionSettings()
    {
        if (_session.ActiveWorkspace is not { } workspace || _session.GetOnionControls(workspace) is not { } state) return;
        OnionSettingsDialog dialog = new(state.Opacity) { Owner = this };
        if (ShowModalDialog(dialog) == true && IsSameOnionTarget(workspace, state))
            _session.SetOnionOpacity(workspace, dialog.OpacityPercent / 100);
        RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
    }

    private bool IsSameOnionTarget(WorkspaceViewModel workspace, DesktopSessionController.OnionControls state)
        => ReferenceEquals(_session.ActiveWorkspace, workspace) && _session.GetOnionControls(workspace) is { } current
            && current.TargetId == state.TargetId && current.InstrumentId == state.InstrumentId;

    private void OpenOnionSources()
    {
        if (_session.Project is not { } project || _session.Persistence is null
            || _session.ActiveWorkspace is not { } workspace || _session.GetOnionControls(workspace) is not { } state) return;
        if (workspace is TimelineWorkspaceViewModel { IsSegment: true }
            && OnionPresentation.Target(project, workspace.ObjectId) is { } target)
        {
            var logical = project.Tracks.ToDictionary(t => t.Id);
            var midi = project.PureMidiTracks.ToDictionary(t => t.Id);
            var choices = project.TracksInArrangementOrder().Where(t => t.TrackId != target.Track)
                .Select(t => logical.TryGetValue(t.TrackId, out var track)
                    ? new OnionSourceChoice(t.TrackId, TimelineWorkspaceViewModel.TrackDisplayName(project, track),
                        OnionPresentation.Color(ProjectTrackColorPolicy.ResolveDisplayColor(project, track)), state.Sources.Contains(t.TrackId))
                    : new OnionSourceChoice(t.TrackId, midi[t.TrackId].Name,
                        OnionPresentation.Color(ProjectTrackColorPolicy.ResolveDisplayColor(midi[t.TrackId])), state.Sources.Contains(t.TrackId)));
            ShowSources(choices, "Select Onion Tracks");
        }
        else if (workspace is InstrumentWorkspaceViewModel { ActiveSubVoiceId: { } voiceId }
            && project.EventInstruments.FirstOrDefault(i => i.Id == workspace.ObjectId) is { } instrument)
        {
            var choices = instrument.SubVoices.Select((voice, index) => new OnionSourceChoice(voice.Id, voice.Name ?? $"SubVoice {index + 1}",
                OnionPresentation.VoiceColors[index % OnionPresentation.VoiceColors.Length], state.Sources.Contains(voice.Id)))
                .Where(s => s.Id != voiceId);
            ShowSources(choices, "Select Onion SubVoices");
        }
        RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);

        void ShowSources(IEnumerable<OnionSourceChoice> choices, string title)
        {
            OnionSourcesDialog dialog = new(choices, title) { Owner = this };
            if (ShowModalDialog(dialog) == true && IsSameOnionTarget(workspace, state))
                _session.SetCustomOnionSources(workspace, dialog.Sources.Where(s => s.Included).Select(s => s.Id).ToArray());
        }
    }
}
