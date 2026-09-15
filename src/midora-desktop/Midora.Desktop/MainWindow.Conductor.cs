using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    private long _conductorListSelectionGeneration;
    private CancellationTokenSource _conductorListSelectionCancellation = new();

    private void OnConductorWorkspaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ConductorWorkspaceView view || view.HandlersAttached) return;
        view.HandlersAttached = true;
        foreach (TimelineSurface surface in new[] { view.TempoTimeline, view.MetaTimeline })
        {
            surface.ContextMenu = (ContextMenu)FindResource("TimelineContextMenu");
            surface.ItemInvoked += OnTimelineItemInvoked;
            surface.ItemEditCompleted += OnTimelineItemEditCompleted;
            surface.SelectionReplacementStarted += OnTimelineSelectionReplacementStarted;
            surface.MarqueeCompleted += OnTimelineMarqueeCompleted;
            surface.RulerClicked += OnTimelineRulerClicked;
            surface.TimeRangeSelected += OnTimelineTimeRangeSelected;
            surface.PreviewMouseDown += OnFollowViewportPreviewMouseDown;
            surface.PreviewMouseUp += OnFollowViewportPreviewMouseUp;
            surface.LostMouseCapture += OnFollowViewportLostMouseCapture;
        }
        view.Overview.PreviewMouseDown += OnFollowViewportPreviewMouseDown;
        view.Overview.PreviewMouseUp += OnFollowViewportPreviewMouseUp;
        view.Overview.LostMouseCapture += OnFollowViewportLostMouseCapture;
        view.Overview.PreviewMouseWheel += OnFollowOverviewPreviewMouseWheel;
        view.TempoTimeline.EventPointTraceCompleted += OnConductorTempoPointsCompleted;
        view.MetaTimeline.BackgroundInvoked += (s, args) => OnTimelineBackgroundInvoked(s,
            new(args.Tick, args.Lane + 1, args.NormalizedValue, args.Modifiers, args.IsDoubleClick, args.IsEmptyBackground));
        view.TempoTimeline.BackgroundInvoked += (s, args) => OnTimelineBackgroundInvoked(s, args);
        view.EventList.SelectionRequested += OnConductorListSelectionRequested;
        view.Unloaded += (_, _) => _conductorListSelectionCancellation.Cancel();
        view.EventList.PropertiesRequested += (_, _) => TryOpenActiveProperties();
        view.EventList.ReadFailed += (_, error) => _session.SetStatusMessage($"Read Conductor events: {error.Message}", isError: true);
        view.CommandRequested += OnConductorCommandRequested;
        view.EventList.ContextMenu.Opened += (_, _) =>
        {
            if (view.DataContext is not TimelineWorkspaceViewModel workspace) return;
            foreach (var item in view.EventList.ContextMenu.Items.OfType<MenuItem>())
            {
                string header = item.Header as string ?? "";
                item.IsEnabled = header == "Paste" ? _session.CanEditProject : workspace.Selection.Ids.Count > 0;
                if (header is "Cut" or "Delete") item.IsEnabled &= _session.CanEditProject;
                if (header == "Properties…") item.IsEnabled &= workspace.Selection.Ids.Count == 1;
            }
        };
    }

    private async void OnConductorListSelectionRequested(object? sender, ConductorListSelectionEventArgs e)
    {
        if (sender is not ConductorEventList { DataContext: TimelineWorkspaceViewModel workspace, Source: { } source }) return;
        _conductorListSelectionCancellation.Cancel();
        _conductorListSelectionCancellation.Dispose();
        _conductorListSelectionCancellation = new();
        CancellationToken token = _conductorListSelectionCancellation.Token;
        if (FindVisualAncestor<ConductorWorkspaceView>(sender as DependencyObject) is { } view)
            _lastTimelineCommandSurface = view.MetaTimeline;
        long generation = ++_conductorListSelectionGeneration;
        if (e.First < 0)
        {
            workspace.Selection.Clear();
            _session.RefreshWorkspaceSelection(workspace);
            return;
        }
        if (e.First == e.Last && e.Primary is { } row && !e.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (e.Modifiers.HasFlag(ModifierKeys.Control)) workspace.Selection.Toggle(row.Id);
            else if (!e.ForceSingle && workspace.Selection.IdSet.Contains(row.Id)) workspace.Selection.Add(row.Id);
            else workspace.Selection.Replace(row.Id);
            _session.RefreshWorkspaceSelection(workspace);
            return;
        }
        long selectionRevision = workspace.Selection.Revision;
        var basis = workspace.Selection.SharedIds;
        try
        {
            var rows = await source.ReadSelectionAsync(e.First, e.Last, token);
            var prepared = await Task.Run(() =>
            {
                CompressedMidoraIdSet range = CompressedMidoraIdSet.Create(rows, token);
                return e.Modifiers.HasFlag(ModifierKeys.Control)
                    ? e.Modifiers.HasFlag(ModifierKeys.Shift) ? basis.Union(range) : basis.SymmetricExcept(range)
                    : range;
            }, token);
            token.ThrowIfCancellationRequested();
            if (generation != _conductorListSelectionGeneration || !ReferenceEquals(source, workspace.ConductorListSource)
                || workspace.Selection.Revision != selectionRevision || !_session.Workspaces.Contains(workspace)) return;
            MidoraId? primary = e.Primary?.Id;
            if (primary is not MidoraId id || !prepared.Contains(id))
                primary = prepared.TryGetMinimum(out MidoraId minimum) ? minimum : null;
            workspace.Selection.AdoptMaterialized(prepared, primary);
            _session.RefreshWorkspaceSelection(workspace);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _session.SetStatusMessage($"Select Conductor events: {ex.Message}", isError: true); }
    }

    private async void OnConductorTempoPointsCompleted(object? sender, TimelineEventPointTraceEventArgs e)
    {
        if (sender is not TimelineSurface { DataContext: TimelineWorkspaceViewModel workspace } surface || e.Trace.Count == 0) return;
        try
        {
            decimal minimum = (decimal)workspace.TempoAxisMinimum, span = (decimal)workspace.TempoAxisMaximum - minimum;
            await ExecuteStagedProjectOperationAsync("Draw Tempo states",
                ProjectDomainEditCommands.DrawTempoPoints(token => e.Sample(token).Select(p =>
                    new ConductorTempoPoint(checked((long)p.Tick), minimum + (decimal)p.NormalizedValue * span))), workspace, surface);
        }
        catch (Exception ex) { ShowError("Draw Tempo states", ex.Message); }
    }

    private async void OnConductorCommandRequested(object? sender, string command)
    {
        if (sender is not ConductorWorkspaceView { DataContext: TimelineWorkspaceViewModel workspace } view) return;
        try
        {
            switch (command)
            {
                case "Properties": TryOpenActiveProperties(); break;
                case "Copy": OnCopyClick(this, new()); break;
                case "Cut": OnCutClick(this, new()); break;
                case "Paste": OnPasteClick(this, new()); break;
                case "Delete": OnDeleteWorkspaceSelectionClick(this, new()); break;
                case "Locate":
                    if (workspace.Selection.Primary is MidoraId id)
                    {
                        var tempoSource = workspace.ConductorTempoSnapshot;
                        var metaSource = workspace.Snapshot;
                        long selectionRevision = workspace.Selection.Revision;
                        long? tick = await Task.Run(() => tempoSource?.TryGetItem(id, out var tempo) == true
                            ? tempo.StartTick : metaSource?.TryGetItem(id, out var meta) == true ? meta.StartTick : (long?)null);
                        if (tick is long location && _session.Workspaces.Contains(workspace)
                            && ReferenceEquals(tempoSource, workspace.ConductorTempoSnapshot)
                            && ReferenceEquals(metaSource, workspace.Snapshot) && workspace.Selection.Revision == selectionRevision)
                        { workspace.StartTick = Math.Max(0, location - workspace.TickSpan / 2); workspace.EditCursorTick = location; }
                    }
                    break;
                case "Fit": await workspace.FitVisibleTempoAsync(CancellationToken.None); break;
                case "ResetAxis": view.ResetTempoView(); break;
                case "Axis":
                    ObjectPropertiesViewModel fields = new();
                    fields.Replace("Tempo Display Range", "Display settings only; actual Tempo values are unchanged.",
                    [new("min", "Minimum BPM", workspace.TempoAxisMinimum.ToString(CultureInfo.InvariantCulture)),
                     new("max", "Maximum BPM", workspace.TempoAxisMaximum.ToString(CultureInfo.InvariantCulture))]);
                    ObjectPropertiesDialog dialog = new(fields, values =>
                    {
                        workspace.SetTempoAxis(double.Parse(values["min"], CultureInfo.InvariantCulture), double.Parse(values["max"], CultureInfo.InvariantCulture));
                        return true;
                    }) { Owner = this };
                    ShowModalDialog(dialog);
                    break;
            }
        }
        catch (Exception ex) { ShowError("Conductor", ex.Message); }
        if (command is "Fit" or "Axis" or "ResetAxis" or "Locate") view.TempoTimeline.Focus();
    }
}
