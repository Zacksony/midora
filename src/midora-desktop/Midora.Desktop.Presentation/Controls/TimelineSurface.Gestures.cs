using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.ComponentModel;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Controls;

public enum TimelineValueTraceShape { Free, Line, Horizontal }

public sealed partial class TimelineSurface
{
    public static readonly DependencyProperty ValueTraceShapeProperty = DependencyProperty.Register(
        nameof(ValueTraceShape), typeof(TimelineValueTraceShape), typeof(TimelineSurface),
        new FrameworkPropertyMetadata(TimelineValueTraceShape.Free));

    public TimelineValueTraceShape ValueTraceShape
    {
        get => (TimelineValueTraceShape)GetValue(ValueTraceShapeProperty);
        set => SetValue(ValueTraceShapeProperty, value);
    }

    private TimelineValueTraceShape _activeValueTraceShape;
    private MouseButton _marqueeButton = MouseButton.Left;
    private ModifierKeys _marqueeModifiers;
    private long? _marqueeSelectionRevision;
    private TimelineRenderSnapshot? _marqueeSource;
    private TimelineRenderSnapshot? _contextSource;
    private bool _rulerEditCursorRequested;
    private double _dragOriginNormalizedValue;
    private TimelineRenderSnapshot? _editGestureSource;
    private long? _editGestureSelectionRevision;
    private bool _publishingItemEdit;
    private void FreezeEditGesture()
    {
        _editGestureSource = Snapshot;
        _editGestureSelectionRevision = SelectionSnapshot?.Revision;
    }
    private bool EditGestureIsCurrent() => ReferenceEquals(_editGestureSource, Snapshot)
        && _editGestureSelectionRevision == SelectionSnapshot?.Revision;
    public double TimelineRulerHeight => GetRulerHeight();

    private DispatcherTimer? _editAutoScrollTimer;
    private Point _editAutoScrollPointer;
    private TimelineRenderSnapshot? _editAutoScrollSource;

    private void TrackEditAutoScroll(Point point)
    {
        _editAutoScrollPointer = point;
        if (_dragItem is null || !_dragActivated || !CanEdit || !IsMouseCaptured || !IsAtEditScrollEdge(point))
        { StopEditAutoScroll(); return; }
        if (_editAutoScrollTimer is not null) return;
        _editAutoScrollSource = Snapshot;
        _editAutoScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input,
            OnEditAutoScrollTick, Dispatcher);
        _editAutoScrollTimer.Start();
    }

    private bool IsAtEditScrollEdge(Point point) =>
        (!_dragTimeLocked && (point.X < GetLaneHeaderWidth() + 24 || point.X > ActualWidth - 24))
        || point.Y < GetRulerHeight() + 24 || point.Y > ActualHeight - 24;

    private void OnEditAutoScrollTick(object? sender, EventArgs args)
    {
        if (!IsLoaded || !IsMouseCaptured || !CanEdit || _dragItem is null
            || !ReferenceEquals(_editAutoScrollSource, Snapshot) || !EditGestureIsCurrent())
        { StopEditAutoScroll(); return; }
        try
        {
            AutoScrollEditGesture(_editAutoScrollPointer, !_dragTimeLocked);
            if (TryCreateViewport(out TimelineViewport viewport)) UpdateCapturedItemDrag(_editAutoScrollPointer, viewport);
        }
        catch (OverflowException)
        {
            ClearCapturedInteraction(); ReleaseMouseCapture();
            RaiseEvent(new RoutedEventArgs(TickRangeExceededEvent, this));
        }
    }

    private void StopEditAutoScroll()
    {
        if (_editAutoScrollTimer is not null)
        {
            _editAutoScrollTimer.Stop();
            _editAutoScrollTimer.Tick -= OnEditAutoScrollTick;
            _editAutoScrollTimer = null;
        }
        _editAutoScrollSource = null;
    }

    internal static long WorldEditTick(TimelineViewport viewport, double x) =>
        TimelineTickMath.Clamp((Int128)viewport.StartTick + TimelineTickMath.RoundSignedDistance(x / viewport.PixelsPerTick));

    private int WorldEditLane(TimelineViewport viewport, double y)
    {
        if (SurfaceMode != TimelineSurfaceMode.Arrangement)
            return (int)Math.Clamp(viewport.FirstLane + Math.Floor(y / viewport.LaneHeight), int.MinValue, int.MaxValue);
        EnsureArrangementRowLayout();
        int count = _arrangementRowOffsets.Length - 1;
        if (count <= 0) return 0;
        double absolute = _arrangementRowOffsets[Math.Clamp(viewport.FirstLane, 0, count)] + y;
        if (absolute < 0) return -1;
        if (absolute >= _arrangementRowOffsets[count]) return count;
        int low = 0, high = count;
        while (low + 1 < high)
        { int middle = low + (high - low) / 2; if (_arrangementRowOffsets[middle] <= absolute) low = middle; else high = middle; }
        return low;
    }

    // The menu host must use this frozen position, not the later hover position.
    public Point? ContextTargetPosition { get; private set; }
    public bool IsContextTargetPending { get; private set; }
    // null: keyboard/header/command menu; false: frozen content-background menu.
    public bool? ContextTargetHasObject { get; private set; }

    private void OnPendingContextMenuClosed(object? sender, RoutedEventArgs args) => CancelDelayedContextMenu();
    private static readonly DependencyPropertyDescriptor ContextMenuOpenDescriptor =
        DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
    private void OnPendingContextMenuOpenChanged(object? sender, EventArgs args)
    {
        // Closed can be delayed by the popup close animation. Cancellation must
        // follow IsOpen=false immediately, before a late query can reopen it.
        if (sender is ContextMenu { IsOpen: false }) CancelDelayedContextMenu();
    }
    private void OnReadyContextMenuClosed(object? sender, RoutedEventArgs args)
    {
        if (sender is ContextMenu menu) menu.Closed -= OnReadyContextMenuClosed;
        ContextTargetPosition = null;
        ContextTargetHasObject = null;
        _suppressAutomaticContextMenuOpening = false;
    }

    private void CancelInvalidItemGesture()
    {
        if (_publishingItemEdit || _dragItem is null || CanEdit && EditGestureIsCurrent()) return;
        ClearCapturedInteraction();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    internal static bool PassedGestureThreshold(Point origin, Point current) =>
        Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void FreezeMarqueeAnchor(Point point, TimelineViewport viewport,
        MouseButton button, ModifierKeys modifiers)
    {
        _marqueeButton = button;
        _marqueeModifiers = modifiers;
        _marqueeSelectionRevision = SelectionSnapshot?.Revision;
        _marqueeSource = Snapshot;
        _marqueeAnchorTick = viewport.XToTick(point.X - GetLaneHeaderWidth());
        _marqueeAnchorLane = YToLane(viewport, point.Y - GetRulerHeight());
        _marqueeAnchorNormalizedValue = ValueYToNormalized(point.Y, GetRulerHeight());
    }

    private void BeginRightButtonMarquee(Point origin, Point current)
    {
        CancelDelayedContextMenu();
        _pendingRightGestureOrigin = null;
        _pendingRightGestureDragThresholdReached = false;
        _suppressAutomaticContextMenuOpening = true;
        // The semantic anchor was frozen at Down, before any view scrolling.
        _marqueeOrigin = origin;
        _marqueeCurrent = current;
        _marqueeButton = MouseButton.Right;
    }
}
