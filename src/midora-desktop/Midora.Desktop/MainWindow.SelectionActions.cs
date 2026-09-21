using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

public partial class MainWindow
{
    private void OnSelectionCommandSurfaceLoaded(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is TimelineSurface surface && surface.DataContext is WorkspaceViewModel and not AllTracksWorkspaceViewModel)
        { surface.SelectionActionAvailabilityProvider = ReadSelectionActionAvailabilityAsync; surface.InvalidateVisual(); }
    }

    private async Task<IReadOnlySet<TimelineSelectionAction>> ReadSelectionActionAvailabilityAsync(TimelineSurface surface, CancellationToken token)
    {
        HashSet<TimelineSelectionAction> result = [];
        if (surface.DataContext is not WorkspaceViewModel workspace || !ReferenceEquals(workspace, _session.ActiveWorkspace)) return result;
        if (workspace.Selection.Ids.Count > 0) result.Add(TimelineSelectionAction.DeselectAll);
        ContextMenu menu = new() { PlacementTarget = surface, Tag = TimelineSelectionAction.Properties };
        if (workspace.Selection.Ids.Count > 1 && workspace.Selection.HomogeneousTimelineSource is null && TryGetTimelineObjectOwner(workspace, out _))
            await PopulateObjectListMenuAsync(menu, workspace, requireOpen: false, cancellationToken: token);
        else
        {
            var priorSurface = _lastTimelineCommandSurface;
            var priorOperation = _timelineSelectionOperationContext;
            var priorQuantize = _timelineQuantizeOperationContext;
            try { OnTimelineContextMenuOpened(menu, new RoutedEventArgs(ContextMenu.OpenedEvent, menu)); }
            finally
            { _lastTimelineCommandSurface = priorSurface; _timelineSelectionOperationContext = priorOperation; _timelineQuantizeOperationContext = priorQuantize; }
        }
        token.ThrowIfCancellationRequested();
        Collect(menu.Items, true);
        return result;
        void Collect(ItemCollection items, bool parentEnabled)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (!parentEnabled || !item.IsEnabled) continue;
                string label = (item.Header as string ?? "").TrimEnd('…', '.');
                foreach (var action in TimelineSelectionActions.All)
                    if (label == action.Label || action.Action == TimelineSelectionAction.Delete && label == "Delete All Selected") result.Add(action.Action);
                Collect(item.Items, true);
            }
        }
    }
    private void SetTimelineCommandIcon(MenuItem item)
    {
        string text = (item.Header as string ?? "").TrimEnd('…', '.');
        foreach (var descriptor in TimelineSelectionActions.All)
        {
            if (text != descriptor.Label && !(descriptor.Action == TimelineSelectionAction.Delete && text == "Delete All Selected")) continue;
            FluentIcon icon = new() { Width = 16, Height = 16 };
            icon.SetResourceReference(FluentIcon.DataProperty, "Fluent." + descriptor.Icon);
            item.Icon = icon;
            return;
        }
    }

    private async void OnSelectionActionRequested(object? sender, TimelineSelectionActionEventArgs args)
    {
        args.Handled = true;
        if (args.OriginalSource is not TimelineSurface surface || surface.DataContext is not WorkspaceViewModel workspace
            || !ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
        var selectionRevision = workspace.Selection.Revision;
        var source = surface.Snapshot;
        ContextMenu menu = new() { PlacementTarget = surface, Placement = PlacementMode.RelativePoint,
            HorizontalOffset = args.Position.X, VerticalOffset = args.Position.Y, Tag = args.Action };
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        _lastTimelineCommandSurface = surface;
        try
        {
            if (args.Action == TimelineSelectionAction.DeselectAll)
            {
                OnDeselectAllTimelineObjectsClick(surface, args);
                surface.Focus();
                return;
            }
            bool mixed = workspace.Selection.Ids.Count > 1 && workspace.Selection.HomogeneousTimelineSource is null
                && TryGetTimelineObjectOwner(workspace, out _);
            if (mixed)
            {
                menu.Items.Add(new MenuItem { Header = "Reading selection…", IsEnabled = false });
                menu.IsOpen = true;
                await PopulateObjectListMenuAsync(menu, workspace);
                if (!menu.IsOpen) return;
            }
            else OnTimelineContextMenuOpened(menu, new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            if (workspace.Selection.Revision != selectionRevision || !ReferenceEquals(surface.Snapshot, source)
                || !ReferenceEquals(_session.ActiveWorkspace, workspace)) { menu.IsOpen = false; return; }
            string label = TimelineSelectionActions.All[(int)args.Action].Label;
            if (args.Action == TimelineSelectionAction.Delete
                && menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Header, "Delete All Selected")) is { } deleteAll)
            {
                menu.IsOpen = false;
                if (deleteAll.IsEnabled) deleteAll.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, deleteAll));
                return;
            }
            FilterSelectionActionMenu(menu.Items, label);
            MenuItem[] targets = menu.Items.OfType<MenuItem>().ToArray();
            if (targets.Length == 1 && targets[0].Items.Count == 0 && targets[0].IsEnabled)
            {
                menu.IsOpen = false;
                targets[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, targets[0]));
            }
            else if (targets.Length != 0)
            {
                menu.Closed += (_, _) => { if (ReferenceEquals(_session.ActiveWorkspace, workspace) && surface.IsVisible) surface.Focus(); };
                menu.IsOpen = true;
            }
            else
            {
                menu.IsOpen = false;
                _session.SetStatusMessage($"{label} is not available for the current selection.");
            }
        }
        catch (OperationCanceledException) { menu.IsOpen = false; }
        catch (Exception ex) { menu.IsOpen = false; ShowError("Selection action", ex.Message); }
    }

    internal static void FilterSelectionActionMenu(ItemCollection items, string label)
    {
        foreach (object child in items.Cast<object>().ToArray())
        {
            if (child is not MenuItem item) { items.Remove(child); continue; }
            string header = (item.Header as string ?? "").TrimEnd('…', '.');
            if (header == label) continue;
            if (header.StartsWith("For ", StringComparison.Ordinal))
            {
                FilterSelectionActionMenu(item.Items, label);
                if (item.Items.Count != 0) continue;
            }
            items.Remove(item);
        }
    }
}
