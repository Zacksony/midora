using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using Midora.Domain;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop;

public partial class InstrumentChangeLane
{
    private InstrumentChangeValue? _floatingAnchor;
    private long _floatingSelectionRevision;
    private Border? _instrumentToolbar;
    private StackPanel? _instrumentToolbarGroups;
    private readonly Dictionary<Button, string> _instrumentToolbarButtons = [];
    private TextBlock? _instrumentToolbarName;
    private Button? _instrumentPinButton;
    private bool _instrumentToolbarPinned;
    private long _instrumentToolbarAnchorTick;
    private Point _instrumentToolbarOffset = new(12, 0);
    private DispatcherTimer? _instrumentAutoScroll;
    private DesktopSessionController? _instrumentToolbarSession;
    private void OnToolbarSessionChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(DesktopSessionController.CanEditProject)) UpdateInstrumentToolbar(); }

    internal static string? InstrumentAction(TimelineSelectionAction action) => action switch
    {
        TimelineSelectionAction.Copy => "Copy", TimelineSelectionAction.Cut => "Cut",
        TimelineSelectionAction.Delete => "Delete", TimelineSelectionAction.FlipHorizontal => "Flip",
        TimelineSelectionAction.Scale => "Scale", TimelineSelectionAction.Quantize => "Quantize",
        TimelineSelectionAction.Properties => "Properties", TimelineSelectionAction.DeselectAll => "Deselect All", _ => null
    };

    private void UpdateInstrumentToolbar()
    {
        if (Host?.DataContext is DesktopSessionController session && !ReferenceEquals(session, _instrumentToolbarSession))
        {
            if (_instrumentToolbarSession is not null) _instrumentToolbarSession.PropertyChanged -= OnToolbarSessionChanged;
            _instrumentToolbarSession = session;
            session.PropertyChanged += OnToolbarSessionChanged;
        }
        if (_instrumentToolbar is null) CreateInstrumentToolbar();
        bool visible = _floatingAnchor is not null && _workspace?.Selection.Revision == _floatingSelectionRevision;
        _instrumentToolbar!.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        bool editable = Host?.DataContext is DesktopSessionController { CanEditProject: true };
        foreach (var button in _instrumentToolbarButtons.Keys)
            button.IsEnabled = button.Tag is TimelineSelectionAction action
                ? InstrumentAction(action) is not null && (editable || action is TimelineSelectionAction.Copy or TimelineSelectionAction.Properties or TimelineSelectionAction.DeselectAll)
                : button == _instrumentPinButton || editable;
        _instrumentToolbarButtons[_instrumentPinButton!] = _instrumentToolbarPinned ? "Follow viewport" : "Pin toolbar position";
        if (_instrumentToolbarPinned) _instrumentPinButton!.SetResourceReference(BackgroundProperty, "Brush.Red.Subtle");
        else _instrumentPinButton!.ClearValue(BackgroundProperty);
        int columns = TimelineSelectionActions.Columns(Points.ActualWidth - 98);
        foreach (var row in _instrumentToolbarGroups!.Children.OfType<WrapPanel>())
            row.Width = Math.Min(columns, row.Children.Count) * TimelineSelectionActions.ButtonSize;
        _instrumentToolbar.MaxHeight = Math.Min(TimelineSelectionActions.HeaderHeight + TimelineSelectionActions.MaximumBodyHeight + 2,
            Math.Max(TimelineSelectionActions.HeaderHeight + TimelineSelectionActions.ButtonSize, Points.ActualHeight - 32));
        double contentHeight = TimelineSelectionActions.HeaderHeight + TimelineSelectionActions.BodyHeight(columns, 2) + 2;
        double height = Math.Min(_instrumentToolbar.MaxHeight, contentHeight);
        double width = columns * TimelineSelectionActions.ButtonSize + TimelineSelectionActions.Padding * 2 + 2
            + (contentHeight > height ? 18 : 0);
        _instrumentToolbar.Width = width;
        double x = TickX(_instrumentToolbarPinned ? _instrumentToolbarAnchorTick : _floatingAnchor!.Value.Tick) + _instrumentToolbarOffset.X;
        double y = 24 + (Points.ActualHeight - 24) / 2 + 12 + _instrumentToolbarOffset.Y;
        if (!_instrumentToolbarPinned)
        {
            x = Math.Clamp(x, 68, Math.Max(68, Points.ActualWidth - width - 4));
            y = Math.Clamp(y, 28, Math.Max(28, Points.ActualHeight - height - 4));
        }
        _instrumentToolbar.Margin = new(x, y, 0, 0);
    }

    private void CreateInstrumentToolbar()
    {
        // Constant-size command UI: never one control per point or selected ID.
        _instrumentToolbarGroups = new StackPanel { Margin = new(0, TimelineSelectionActions.Padding, 0, TimelineSelectionActions.Padding) };
        var grip = new Thumb { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        grip.DragDelta += (_, args) =>
        { _instrumentToolbarOffset += new Vector(args.HorizontalChange, args.VerticalChange); UpdateInstrumentToolbar(); };
        _instrumentToolbarName = new TextBlock { Text = "Selection", FontSize = 11, Margin = new(7, 0, 18, 0),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _instrumentToolbarName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
        var title = new Grid { Height = TimelineSelectionActions.HeaderHeight };
        title.Children.Add(grip); title.Children.Add(_instrumentToolbarName);
        title.Children.Add(new TextBlock { Text = "⋮", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0), IsHitTestVisible = false });
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = _instrumentToolbarGroups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); content.Children.Add(title); content.Children.Add(scroll);
        _instrumentToolbar = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Padding = new(0), CornerRadius = new(4), BorderThickness = new(1), Child = content };
        _instrumentToolbar.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.1");
        _instrumentToolbar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _instrumentToolbar.PreviewMouseMove += (_, args) => UpdateInstrumentToolbarHover(args.GetPosition(_instrumentToolbar));
        _instrumentToolbar.MouseLeave += (_, _) => _instrumentToolbarName.Text = "Selection";
        var row = AddGroup();
        _instrumentPinButton = AddButton(TimelineSelectionActions.PinIcon, "Pin to the selection position");
        _instrumentPinButton.Click += (_, _) =>
        { _instrumentToolbarPinned = !_instrumentToolbarPinned; _instrumentToolbarAnchorTick = _floatingAnchor?.Tick ?? 0; UpdateInstrumentToolbar(); };
        var move = AddButton(TimelineSelectionActions.MoveIcon, "Move (Ctrl+drag: copy)");
        // Existing move preview / bounded command, also when the main mode is Select.
        move.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            if (_workspace is null || _floatingAnchor is not { } anchor || _workspace.Selection.Revision != _floatingSelectionRevision) return;
            ResetInteractionLifetime(); ResetGesture(); Focus();
            _floatingMove = true; _pressButton = MouseButton.Left; _pressedHit = anchor;
            _pressPoint = _current = args.GetPosition(Points); _modifiers = Keyboard.Modifiers;
            _pressTick = PointTick(_current.X, false); _pressSnappedTick = PointTick(_current.X);
            _minimumDragTick = anchor.Tick; _copy = _modifiers.HasFlag(ModifierKeys.Control);
            _pressSelection = _workspace.Selection.SharedIds; _pressSelectionRevision = _workspace.Selection.Revision;
            _pressWork = Task.CompletedTask; Points.CaptureMouse();
        };
        foreach (var action in TimelineSelectionActions.PrimaryActions) AddAction(action);
        foreach (var group in TimelineSelectionActions.Groups)
        {
            row = AddGroup();
            foreach (var action in group) AddAction(action);
        }
        void AddAction(TimelineSelectionAction action)
        {
            var descriptor = TimelineSelectionActions.All[(int)action];
            var button = AddButton(descriptor.ToolbarIcon, descriptor.Label); button.Tag = action;
            button.Click += (_, _) =>
            {
                if (_workspace is { } workspace && InstrumentAction(action) is { } command)
                    Host?.RunInstrumentAction(workspace, command, RefreshAndFocus);
            };
        }
        Panel.SetZIndex(_instrumentToolbar, 10); PointHost.Children.Add(_instrumentToolbar);
        WrapPanel AddGroup()
        {
            var group = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new(0, _instrumentToolbarGroups.Children.Count == 0 ? 0 : TimelineSelectionActions.GroupGap, 0, 0) };
            _instrumentToolbarGroups.Children.Add(group); return group;
        }
        Button AddButton(string iconName, string name)
        {
            var icon = new FluentIcon { Width = TimelineSelectionActions.IconSize, Height = TimelineSelectionActions.IconSize };
            icon.SetResourceReference(FluentIcon.DataProperty, "Fluent." + iconName);
            var button = new Button { Content = icon, Width = TimelineSelectionActions.ButtonSize, Height = TimelineSelectionActions.ButtonSize, MinWidth = 0, MinHeight = 0,
                Margin = new(0), Padding = new(0), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            _instrumentToolbarButtons.Add(button, name); row.Children.Add(button); return button;
        }
    }

    private void UpdateInstrumentToolbarHover(Point point)
    {
        if (point.Y < TimelineSelectionActions.HeaderHeight)
        { _instrumentToolbarName!.Text = "Move selection toolbar"; return; }
        // Use constant-size visual rectangles, including disabled buttons. WPF
        // skips disabled children in input hit testing, but their names remain readable.
        foreach (var pair in _instrumentToolbarButtons)
        {
            Rect bounds = pair.Key.TransformToAncestor(_instrumentToolbar!).TransformBounds(new Rect(pair.Key.RenderSize));
            if (!bounds.Contains(point)) continue;
            _instrumentToolbarName!.Text = pair.Value; return;
        }
        _instrumentToolbarName!.Text = "Selection";
    }

    private void TrackInstrumentAutoScroll()
    {
        if (!_dragging || _pressButton != MouseButton.Left || _pressedHit is null || _modifiers.HasFlag(ModifierKeys.Shift)
            || _current.X >= 88 && _current.X <= Points.ActualWidth - 24)
        { StopInstrumentAutoScroll(); return; }
        if (_instrumentAutoScroll is not null) return;
        _instrumentAutoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, OnInstrumentAutoScroll, Dispatcher);
        _instrumentAutoScroll.Start();
    }
    private void OnInstrumentAutoScroll(object? sender, EventArgs args)
    {
        if (!IsLoaded || !Points.IsMouseCaptured || _pressPoint is null || _workspace?.Selection.Revision != _pressSelectionRevision)
        { StopInstrumentAutoScroll(); return; }
        long step = Math.Max(1, Backdrop.TickSpan / 48);
        long start = TimelineTickMath.Clamp((Int128)Backdrop.StartTick + (_current.X < 88 ? -step : step));
        start = Math.Min(long.MaxValue - 1, start);
        if (_workspace is TimelineWorkspaceViewModel t) t.StartTick = start;
        else if (_workspace is InstrumentWorkspaceViewModel v) v.TimelineStartTick = start;
        _delta = Math.Max(PointTick(_current.X) - _pressSnappedTick, -_minimumDragTick);
        Refresh(); GestureOverlay.InvalidateVisual();
    }
    private void StopInstrumentAutoScroll()
    {
        if (_instrumentAutoScroll is not null)
        { _instrumentAutoScroll.Stop(); _instrumentAutoScroll.Tick -= OnInstrumentAutoScroll; _instrumentAutoScroll = null; }
    }
}
