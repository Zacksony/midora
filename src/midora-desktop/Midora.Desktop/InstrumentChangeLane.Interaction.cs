using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop;

public partial class InstrumentChangeLane
{
    private Point? _pressPoint;
    private Point _current;
    private Point? _hoverPoint;
    private Point? _panOrigin;
    private long _panStartTick;
    private double _panPixelsPerTick;
    private InstrumentChangeValue? _pressedHit;
    private Task _pressWork = Task.CompletedTask;
    private bool _dragging, _copy, _releasing;
    private int _clickCount;
    private long _delta;
    private long _pressTick, _pressSnappedTick;
    private long _minimumDragTick;
    private ModifierKeys _modifiers;
    private MouseButton _pressButton;
    private bool _floatingMove;
    private long _pressSelectionRevision;
    private Exception? _rightPressError;
    private CompressedMidoraIdSet _pressSelection = CompressedMidoraIdSet.Empty;
    private CancellationTokenSource _interaction = new();
    private void ResetInteractionLifetime()
    { _interaction.Cancel(); _interaction.Dispose(); _interaction = new(); }
    internal static IEnumerable<MidoraId> MemberIds(InstrumentChangeValue value)
    {
        yield return value.BankEventId;
        if (value.BankLsbEventId is { } lsb) yield return lsb;
        yield return value.ProgramEventId;
    }
    private TimelineToolMode Mode => DataContext switch { TimelineWorkspaceViewModel t => t.ToolMode,
        InstrumentWorkspaceViewModel v => v.ToolMode, _ => TimelineToolMode.Select };
    private bool IsContentPoint(Point point) => point.X >= 64 && point.X < Points.ActualWidth
        && point.Y >= 24 && point.Y < Points.ActualHeight;
    internal Point? CreationPreviewPoint => Mode == TimelineToolMode.Draw && _pressPoint is null && _panOrigin is null
        && Host?.DataContext is DesktopSessionController { CanEditProject: true }
        && _hoverPoint is { } point && IsContentPoint(point)
            ? new Point(TickX(PointTick(point.X)), 24 + (Points.ActualHeight - 24) * .5) : null;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (_instrumentToolbar?.IsMouseOver == true) return;
        if (e.ChangedButton == MouseButton.Right)
        {
            var point = e.GetPosition(Points);
            if (_pressPoint is not null || _panOrigin is not null || !IsContentPoint(point) || _workspace is null)
            { e.Handled = true; return; }
            ResetInteractionLifetime(); ResetGesture(); Focus();
            _pressButton = MouseButton.Right;
            _rightPressError = null;
            _pressPoint = _current = point; _modifiers = Keyboard.Modifiers;
            _pressTick = PointTick(point.X, false); _pressSnappedTick = PointTick(point.X);
            _pressSelection = _workspace.Selection.SharedIds; _pressSelectionRevision = _workspace.Selection.Revision;
            _pressWork = ReadRightPressAsync(point, _interaction.Token);
            Points.CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Middle || !IsContentPoint(e.GetPosition(Points)) || _workspace is null) return;
        if (_pressPoint is not null || _panOrigin is not null) { e.Handled = true; return; }
        BeginPan(e.GetPosition(Points));
        if (!Points.CaptureMouse()) ResetGesture();
        e.Handled = true;
    }
    private void BeginPan(Point point)
    {
        ResetInteractionLifetime(); ResetGesture(); Focus();
        _panOrigin = point; _panStartTick = Backdrop.StartTick;
        _panPixelsPerTick = Math.Max(1, Points.ActualWidth - 64) / Math.Max(1, (double)Backdrop.TickSpan);
        GestureOverlay.InvalidateVisual();
    }
    private void UpdatePan(Point point)
    {
        if (_panOrigin is not { } origin) return;
        long delta = TimelineTickMath.RoundSignedDistance((point.X - origin.X) / _panPixelsPerTick);
        long start = Math.Min(long.MaxValue - 1, TimelineTickMath.Clamp((Int128)_panStartTick - delta));
        // Fixed-y Inst. has no vertical axis. Use the same horizontal source as
        // the piano, numeric events, ruler and overview; no independent offset.
        if (_workspace is TimelineWorkspaceViewModel timeline) timeline.StartTick = start;
        else if (_workspace is InstrumentWorkspaceViewModel voice) voice.TimelineStartTick = start;
    }
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || _panOrigin is null) return;
        UpdatePan(e.GetPosition(Points)); ResetGesture(); e.Handled = true;
    }
    private async Task<T> ReadIndex<T>(Func<InstrumentChangeProjection, T> read, CancellationToken token)
    {
        var expected = _latest;
        return await Task.Run(() =>
        {
            lock (_projectionGate)
            {
                token.ThrowIfCancellationRequested();
                if (expected is null || _indexed?.Owner != expected.Owner || _indexed.Revision != expected.Revision
                    || !ReferenceEquals(_indexed.Root, expected.Root)) throw new OperationCanceledException();
                if (_index is null) throw new InvalidOperationException("Instrument Changes are still loading. Try again shortly.");
                return read(_index);
            }
        }, token);
    }
    private Task<InstrumentChangeValue?> HitAsync(Point point, CancellationToken token)
    {
        if (Math.Abs(point.Y - (24 + (Points.ActualHeight - 24) * .5)) > 12)
            return Task.FromResult<InstrumentChangeValue?>(null);
        long tick = PointTick(point.X, false);
        long tolerance = Math.Max(1, (long)Math.Min(long.MaxValue / 2d, 12 * (double)Backdrop.TickSpan / Math.Max(1, Points.ActualWidth - 64)));
        return ReadIndex(index => index.Hit(tick, tolerance, token), token);
    }
    private async Task ReadRightPressAsync(Point point, CancellationToken token)
    {
        try
        {
            var workspace = _workspace;
            var hit = await HitAsync(point, token);
            token.ThrowIfCancellationRequested();
            if (_workspace == workspace && workspace?.Selection.Revision == _pressSelectionRevision) _pressedHit = hit;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { if (!token.IsCancellationRequested) _rightPressError = exception; }
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_instrumentToolbar?.IsMouseOver == true) return;
        var point = e.GetPosition(Points);
        if (!IsContentPoint(point) || _workspace is null || _panOrigin is not null || _pressPoint is not null) return;
        Focus(); e.Handled = true;
        ResetInteractionLifetime();
        _pressedHit = null;
        _pressButton = MouseButton.Left;
        _pressSelection = _workspace.Selection.SharedIds;
        _pressPoint = _current = point; _clickCount = e.ClickCount; _modifiers = Keyboard.Modifiers;
        _pressTick = PointTick(point.X, false); _pressSnappedTick = PointTick(point.X);
        _copy = _modifiers.HasFlag(ModifierKeys.Control); _dragging = false; _delta = 0; _minimumDragTick = 0;
        Points.CaptureMouse(); _pressWork = ReadPressAsync(point, _interaction.Token);
        GestureOverlay.InvalidateVisual();
    }
    private async Task ReadPressAsync(Point point, CancellationToken token)
    {
        try
        {
            var workspace = _workspace; long revision = workspace?.Selection.Revision ?? 0;
            var hit = await HitAsync(point, token); token.ThrowIfCancellationRequested();
            if (_workspace != workspace || workspace is null || workspace.Selection.Revision != revision) return;
            _pressedHit = hit;
            if (hit is { } value && Mode != TimelineToolMode.Select && !IsSelected(value))
            {
                var ids = CompressedMidoraIdSet.Create(MemberIds(value));
                if (_modifiers.HasFlag(ModifierKeys.Control)) ids = workspace.Selection.SharedIds.Union(ids);
                Host?.PublishInstrumentSelection(workspace, ids);
            }
            if (hit is not null && Mode != TimelineToolMode.Select)
            {
                var ids = workspace.Selection.SharedIds;
                long selectionRevision = workspace.Selection.Revision;
                long minimum = await ReadIndex(index => index.MinimumSelectedTick(ids, token) ?? 0, token);
                token.ThrowIfCancellationRequested();
                if (_workspace != workspace || workspace.Selection.Revision != selectionRevision) return;
                _minimumDragTick = minimum;
                _pressSelectionRevision = selectionRevision;
                _delta = Math.Max(PointTick(_current.X) - _pressSnappedTick, -minimum);
                if (_dragging) Refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ResetGesture(); Host?.ReportInstrumentLaneFailure(exception); }
    }
    private void OnPointMove(object sender, MouseEventArgs e)
    {
        _current = e.GetPosition(Points);
        _hoverPoint = _current;
        GestureOverlay.InvalidateVisual();
        if (_panOrigin is not null)
        {
            if (e.MiddleButton == MouseButtonState.Pressed) UpdatePan(_current);
            else ResetGesture();
            PointerPositionText = $"({PointTick(_current.X)})";
            e.Handled = true; return;
        }
        PointerPositionText = $"({PointTick(_current.X)})";
        if (_pressPoint is not { } press || (_pressButton == MouseButton.Right ? e.RightButton : e.LeftButton) != MouseButtonState.Pressed) return;
        if (Math.Abs(_current.X - press.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(_current.Y - press.Y) >= SystemParameters.MinimumVerticalDragDistance) _dragging = true;
        if (_pressButton == MouseButton.Right) return;
        long delta = _modifiers.HasFlag(ModifierKeys.Shift) ? 0 : Math.Max(PointTick(_current.X) - _pressSnappedTick, -_minimumDragTick);
        if (_delta != delta) { _delta = delta; if (_pressedHit is not null) Refresh(); }
        TrackInstrumentAutoScroll();
    }
    private async void OnPointClick(object sender, MouseButtonEventArgs e)
    {
        if (_pressButton != MouseButton.Left) { e.Handled = true; return; }
        var lifetime = _interaction;
        e.Handled = true; var press = _pressPoint; var point = e.GetPosition(Points);
        try
        {
            await _pressWork;
            if (lifetime.IsCancellationRequested || !ReferenceEquals(lifetime, _interaction)) return;
            if (press is null || _pressPoint is null || _workspace is not { } workspace) return;
            bool dragged = _dragging; var hit = _pressedHit;
            long delta = _modifiers.HasFlag(ModifierKeys.Shift) ? 0 : Math.Max(PointTick(point.X) - _pressSnappedTick, -_minimumDragTick);
            if (dragged && hit is not null && (Mode != TimelineToolMode.Select || _floatingMove))
            {
                if (workspace.Selection.Revision == _pressSelectionRevision)
                    Host?.RunInstrumentAction(workspace, "Move", RefreshAndFocus, delta, _copy);
                return;
            }
            if (dragged)
            {
                long a = _pressTick, b = PointTick(point.X, false);
                double y = 24 + (Points.ActualHeight - 24) * .5;
                if (Math.Min(press.Value.Y, point.Y) <= y + 4 && Math.Max(press.Value.Y, point.Y) >= y - 4)
                    await SelectRangeAsync(Math.Min(a, b), Math.Max(a, b) + 1, _modifiers);
                else if (!_modifiers.HasFlag(ModifierKeys.Control)) Host?.PublishInstrumentSelection(workspace, CompressedMidoraIdSet.Empty);
            }
            else if (hit is { } value)
            {
                var ids = CompressedMidoraIdSet.Create(MemberIds(value));
                if (_modifiers.HasFlag(ModifierKeys.Control)) ids = _pressSelection.SymmetricExcept(ids);
                Host?.PublishInstrumentSelection(workspace, ids); _selectedValue = value;
                if (Mode == TimelineToolMode.Erase) Host?.RunInstrumentAction(workspace, "Delete", RefreshAndFocus);
                else if (_clickCount >= 2) Host?.RunInstrumentAction(workspace, "Properties", RefreshAndFocus);
            }
            else if (Mode == TimelineToolMode.Draw) Host?.EditInstrumentChange(this, PointTick(point.X), null);
            else Host?.PublishInstrumentSelection(workspace, CompressedMidoraIdSet.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Host?.ReportInstrumentLaneFailure(exception); }
        finally { if (ReferenceEquals(lifetime, _interaction)) ResetGesture(); }
    }
    private void ResetGesture()
    {
        bool resetPreview = _dragging && _pressedHit is not null;
        _pressPoint = null; _pressedHit = null; _pressSelection = CompressedMidoraIdSet.Empty; _delta = 0; _dragging = false;
        _panOrigin = null;
        _floatingMove = false;
        StopInstrumentAutoScroll();
        _releasing = true; if (Points.IsMouseCaptured) Points.ReleaseMouseCapture(); _releasing = false;
        if (!Points.IsMouseOver) PointerPositionText = "";
        Points.InvalidateVisual();
        GestureOverlay.InvalidateVisual();
        if (resetPreview && IsLoaded) Refresh();
    }
    private async Task SelectRangeAsync(long start, long end, ModifierKeys modifiers)
    {
        if (_workspace is not { } workspace) return;
        var token = _interaction.Token; long revision = workspace.Selection.Revision;
        var basis = workspace.Selection.SharedIds;
        var ids = await ReadIndex(index => CompressedMidoraIdSet.Create(
            index.ReadRange(start, end, token).SelectMany(MemberIds), token), token);
        token.ThrowIfCancellationRequested();
        if (workspace != _workspace || workspace.Selection.Revision != revision) return;
        ids = TimelineToolPolicy.ResolveMarqueeSelectionMode(modifiers) switch
        {
            WorkspaceSelectionRangeMode.Add => basis.Union(ids),
            WorkspaceSelectionRangeMode.Remove => basis.Except(ids),
            WorkspaceSelectionRangeMode.Toggle => basis.SymmetricExcept(ids),
            _ => ids
        };
        Host?.PublishInstrumentSelection(workspace, ids); Points.InvalidateVisual();
    }
    private async void OnContextClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_pressButton != MouseButton.Right || _pressPoint is not { } origin || _workspace is not { } workspace) return;
        var point = e.GetPosition(Points); var lifetime = _interaction; var token = lifetime.Token;
        bool dragged = _dragging;
        _releasing = true; if (Points.IsMouseCaptured) Points.ReleaseMouseCapture(); _releasing = false;
        try
        {
            if (workspace.Selection.Revision != _pressSelectionRevision) return;
            if (dragged)
            {
                double y = 24 + (Points.ActualHeight - 24) * .5;
                long end = Math.Max(_pressTick, PointTick(point.X, false));
                if (Math.Min(origin.Y, point.Y) <= y + 4 && Math.Max(origin.Y, point.Y) >= y - 4)
                    await SelectRangeAsync(Math.Min(_pressTick, PointTick(point.X, false)), end == long.MaxValue ? end : end + 1, _modifiers);
                else await SelectRangeAsync(0, 0, _modifiers);
                return;
            }
            var menu = new ContextMenu { PlacementTarget = Points, Placement = PlacementMode.RelativePoint,
                HorizontalOffset = origin.X, VerticalOffset = origin.Y };
            menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
            menu.Items.Add(new MenuItem { Header = "Locating…", IsEnabled = false });
            var openProperty = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            void Closed(object? sender, EventArgs args)
            {
                if (menu.IsOpen) return;
                openProperty.RemoveValueChanged(menu, Closed);
                if (ReferenceEquals(lifetime, _interaction)) ResetInteractionLifetime();
            }
            openProperty.AddValueChanged(menu, Closed);
            ContextMenu = menu; menu.IsOpen = true;
            await _pressWork; token.ThrowIfCancellationRequested();
            if (_rightPressError is { } error)
            { menu.IsOpen = false; Host?.ReportInstrumentLaneFailure(error); return; }
            if (!menu.IsOpen || !IsLoaded || workspace != _workspace || workspace.Selection.Revision != _pressSelectionRevision) { menu.IsOpen = false; return; }
            if (_pressedHit is { } value && !IsSelected(value)) Select(value);
            var ids = workspace.Selection.SharedIds; long revision = workspace.Selection.Revision;
            var groups = _latest?.Root ?? InstrumentChangeSet.Empty;
            bool hasSelection = await Task.Run(
                () => InstrumentChangeSelectionQuery.EnumerateGroups(groups, ids, token).Any(), token);
            token.ThrowIfCancellationRequested();
            if (!IsLoaded || workspace != _workspace || workspace.Selection.Revision != revision) { menu.IsOpen = false; return; }
            if (!menu.IsOpen) return;
            menu.Items.Clear();
            if (Host?.InstrumentMenu(workspace, hasSelection, RefreshAndFocus) is { } commands)
                foreach (object item in commands.Items.Cast<object>().ToArray()) { commands.Items.Remove(item); menu.Items.Add(item); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Host?.ReportInstrumentLaneFailure(exception); }
        finally { if (ReferenceEquals(lifetime, _interaction)) ResetGesture(); }
    }
    internal bool HandleShortcut(KeyEventArgs e)
    {
        if (_workspace is not { } workspace) return false;
        if (e.Key == Key.Escape && (_panOrigin is not null || _pressPoint is not null))
        { ResetInteractionLifetime(); ResetGesture(); e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.A && Settings is { } settings)
        { settings.SnapEnabled = !settings.SnapEnabled; e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        { SelectAll(); e.Handled = true; return true; }
        string? action = e.Key == Key.Delete ? "Delete" : Keyboard.Modifiers == ModifierKeys.Control ? e.Key switch
        { Key.C => "Copy", Key.X => "Cut", Key.V => "Paste", Key.P => "Properties", Key.Q => "Scale", _ => null } : null;
        if (action is not null) { Host?.RunInstrumentAction(workspace, action, RefreshAndFocus); e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.T) { e.Handled = true; return true; }
        return false;
    }
    private async void SelectAll()
    { try { await SelectRangeAsync(0, long.MaxValue, ModifierKeys.None); } catch (OperationCanceledException) { } catch (Exception e) { Host?.ReportInstrumentLaneFailure(e); } }
    private void RefreshAndFocus() { Refresh(); if (IsVisible) Focus(); }
    internal void DrawGesture(DrawingContext dc, Typeface typeface, double dpi)
    {
        var info = TryFindResource("Brush.Info") as Brush ?? Brushes.DodgerBlue;
        if (CreationPreviewPoint is { } preview)
            dc.DrawEllipse(null, new Pen(info, 1) { DashStyle = DashStyles.Dash }, preview, 5, 5);
        if (!_dragging || _pressPoint is not { } start) return;
        if (_pressButton == MouseButton.Right || _pressedHit is null || Mode == TimelineToolMode.Select && !_floatingMove)
        {
            var rectangle = new Rect(new Point(TickX(_pressTick), start.Y), _current);
            dc.PushOpacity(.22); dc.DrawRectangle(info, null, rectangle); dc.Pop();
            dc.DrawRectangle(null, new Pen(info, 1) { DashStyle = DashStyles.Dash }, rectangle);
        }
        else
        {
            var text = new FormattedText($"{_delta:+0;-0;0} Ticks", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.White, dpi);
            dc.DrawRoundedRectangle(Brushes.Black, new Pen(Brushes.DimGray, 1), new(_current.X + 12, _current.Y + 12, text.Width + 8, text.Height + 4), 3, 3);
            dc.DrawText(text, new(_current.X + 16, _current.Y + 14));
        }
    }
}
