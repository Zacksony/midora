using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop.Presentation.Controls;

public sealed class TimelineSelectionActionEventArgs(TimelineSelectionAction action, Point position) : RoutedEventArgs
{
    public TimelineSelectionAction Action { get; } = action;
    public Point Position { get; } = position;
}

public sealed partial class TimelineSurface
{
    public static readonly RoutedEvent SelectionActionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SelectionActionRequested), RoutingStrategy.Bubble, typeof(EventHandler<TimelineSelectionActionEventArgs>), typeof(TimelineSurface));
    public event EventHandler<TimelineSelectionActionEventArgs> SelectionActionRequested
    {
        add => AddHandler(SelectionActionRequestedEvent, value);
        remove => RemoveHandler(SelectionActionRequestedEvent, value);
    }
    private readonly Rect[] _selectionActionBounds = new Rect[TimelineSelectionActions.All.Count];
    private TimelineSelectionIconCache? _selectionToolIcons;
    private int? _selectionActionPress;
    private Rendering.TimelineRenderSnapshot? _actionPressSource;
    private long? _actionPressRevision;
    private bool _selectionToolCanDrag;
    private double _selectionToolScrollOffset;
    private double _selectionToolContentHeight;
    private Rect _selectionToolBodyBounds = Rect.Empty;
    private string _selectionToolHoverName = "Selection";
    private int _selectionToolHoverIndex = -1;
    public Func<TimelineSurface, CancellationToken, Task<IReadOnlySet<TimelineSelectionAction>>>? SelectionActionAvailabilityProvider { get; set; }
    private (Rendering.TimelineRenderSnapshot? Source, Rendering.TimelineSelectionSnapshot? Selection, bool Editable)? _actionAvailabilityKey;
    private IReadOnlySet<TimelineSelectionAction>? _availableSelectionActions;
    private CancellationTokenSource? _actionAvailabilityCancellation;

    private void EnsureSelectionActionAvailability()
    {
        if (SelectionActionAvailabilityProvider is null) return;
        var key = (Snapshot, SelectionSnapshot, CanEdit);
        if (_actionAvailabilityKey == key) return;
        CancelSelectionActionAvailability();
        _actionAvailabilityKey = key;
        var cancellation = _actionAvailabilityCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var actions = await SelectionActionAvailabilityProvider(this, token);
                if (!token.IsCancellationRequested && _actionAvailabilityKey == key)
                { _availableSelectionActions = actions; InvalidateVisual(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Selection capabilities: {ex}"); }
        }));
    }

    private void CancelSelectionActionAvailability()
    {
        _actionAvailabilityCancellation?.Cancel();
        _actionAvailabilityCancellation?.Dispose();
        _actionAvailabilityCancellation = null;
        _actionAvailabilityKey = null;
        _availableSelectionActions = null;
    }

    private bool SelectionActionEnabled(int index) => SelectionSnapshot is { Count: > 0 } selection
        && (TimelineSelectionActions.All[index].Action == TimelineSelectionAction.DeselectAll || (SelectionActionAvailabilityProvider is not null
            ? _actionAvailabilityKey == (Snapshot, SelectionSnapshot, CanEdit)
                && _availableSelectionActions?.Contains(TimelineSelectionActions.All[index].Action) == true
            : TimelineSelectionActions.CanOffer(TimelineSelectionActions.All[index].Action, selection, CanEdit)));

    private void DrawSelectionActions(DrawingContext context, Brush foreground, Brush background)
    {
        EnsureSelectionActionAvailability();
        for (int i = 0; i < _selectionActionBounds.Length; i++)
        {
            Rect cell = _selectionActionBounds[i];
            if (!cell.IntersectsWith(_selectionToolBodyBounds)) continue;
            bool enabled = SelectionActionEnabled(i);
            if (enabled && _hoverPoint is { } pointer && cell.Contains(pointer))
                context.DrawRoundedRectangle(background, null, cell, 3, 3);
            context.PushOpacity(enabled ? 1 : .32);
            DrawSelectionToolIcon(context, TimelineSelectionActions.All[i].ToolbarIcon, cell, foreground);
            context.Pop();
        }
    }

    private void DrawSelectionToolIcon(DrawingContext context, string key, Rect cell, Brush foreground)
    {
        if (!cell.IsEmpty && TryFindResource("Fluent." + key) is Geometry geometry)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _selectionToolIcons ??= new(InvalidateVisual);
            var image = _selectionToolIcons.Get(key, geometry, foreground, dpi);
            context.DrawImage(image, TimelineSelectionIconCache.Destination(image, cell, dpi));
        }
    }

    internal static Rect SelectionToolIconBounds(Geometry geometry, Rect cell)
    {
        Rect ink = geometry.Bounds;
        if (ink.IsEmpty || ink.Width <= 0 || ink.Height <= 0) return Rect.Empty;
        double scale = TimelineSelectionActions.IconSize / Math.Max(ink.Width, ink.Height);
        return new(cell.X + (cell.Width - ink.Width * scale) / 2,
            cell.Y + (cell.Height - ink.Height * scale) / 2, ink.Width * scale, ink.Height * scale);
    }

    internal static void DrawSelectionToolGeometry(DrawingContext context, Geometry geometry, Rect cell, Brush foreground)
    {
        Rect target = SelectionToolIconBounds(geometry, cell);
        if (target.IsEmpty) return;
        Rect ink = geometry.Bounds;
        double scale = target.Width / ink.Width;
        context.PushTransform(new MatrixTransform(scale, 0, 0, scale, target.X - ink.X * scale, target.Y - ink.Y * scale));
        context.DrawGeometry(foreground, null, geometry);
        context.Pop();
    }

    private bool TryPressSelectionAction(Point point)
    {
        for (int i = 0; i < _selectionActionBounds.Length; i++)
        {
            if (!_selectionActionBounds[i].Contains(point)) continue;
            if (SelectionActionEnabled(i))
            { _selectionActionPress = i; _actionPressSource = Snapshot; _actionPressRevision = SelectionSnapshot?.Revision; CaptureMouse(); }
            return true;
        }
        return false;
    }

    private void UpdateSelectionToolTip(Point point)
    {
        string? text = null;
        string name = "Selection";
        int hoverIndex = -1;
        if (IsSelectionFloatingToolEnabled && _selectionToolBounds.Contains(point))
        {
            if (_selectionToolGripBounds.Contains(point)) name = "Move selection toolbar";
            else if (_selectionToolPinBounds.Contains(point)) { name = _selectionToolPinned ? "Follow viewport" : "Pin toolbar position"; hoverIndex = -2; }
            else if (_selectionToolResizeStartBounds.Contains(point)) { name = "Drag left boundaries"; hoverIndex = -3; }
            else if (_selectionToolResizeEndBounds.Contains(point)) { name = "Drag right boundaries"; hoverIndex = -4; }
            else if (_selectionToolMoveBounds.Contains(point)) { name = "Move (Ctrl+drag: copy)"; hoverIndex = -5; }
            else for (int i = 0; i < _selectionActionBounds.Length; i++)
                if (_selectionActionBounds[i].Contains(point)) { name = TimelineSelectionActions.All[i].Label; hoverIndex = i; break; }
        }
        else if (point.X >= GetLaneHeaderWidth() && point.Y < GetRulerHeight())
            text = "Click: playback cursor · Ctrl+click: edit cursor (keep selection)";
        if (!Equals(ToolTip, text)) ToolTip = text;
        if (name != _selectionToolHoverName || hoverIndex != _selectionToolHoverIndex)
        {
            _selectionToolHoverName = name;
            _selectionToolHoverIndex = hoverIndex;
            InvalidateVisual();
        }
    }
}
