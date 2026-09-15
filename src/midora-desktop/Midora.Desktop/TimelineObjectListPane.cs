using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Typography;

namespace Midora.Desktop;

public sealed class TimelineObjectListSelectionEventArgs(int first, int last, ModifierKeys modifiers,
    TimelineObjectListRow? primary, bool forceSingle = false) : EventArgs
{
    public int First { get; } = first;
    public int Last { get; } = last;
    public ModifierKeys Modifiers { get; } = modifiers;
    public TimelineObjectListRow? Primary { get; } = primary;
    public bool ForceSingle { get; } = forceSingle;
}

public sealed class TimelineObjectListContextEventArgs(TimelineObjectListSource source,
    TimelineObjectListRow? row, int ordinal, TimelineSelectionSnapshot? selection, Point position) : EventArgs
{
    public TimelineObjectListSource Source { get; } = source;
    public TimelineObjectListRow? Row { get; } = row;
    public int Ordinal { get; } = ordinal;
    public TimelineSelectionSnapshot? Selection { get; } = selection;
    public Point Position { get; } = position;
}

/// <summary>
/// Shared owner-data list for all three piano rolls. Only a viewport-sized array
/// is retained and painted; selection spans remain ordinal requests bound to the
/// source revision. It uses the Conductor list's theme and row geometry.
/// </summary>
public sealed class TimelineObjectListPane : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source),
        typeof(TimelineObjectListSource), typeof(TimelineObjectListPane), new PropertyMetadata(null, (d, _) => ((TimelineObjectListPane)d).SourceChanged()));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection),
        typeof(TimelineSelectionSnapshot), typeof(TimelineObjectListPane), new PropertyMetadata(null, (d, _) =>
        { var pane = (TimelineObjectListPane)d; pane.CancelPendingInteraction(); pane._rows.InvalidateVisual(); }));
    public static readonly DependencyProperty FirstRowProperty = DependencyProperty.Register(nameof(FirstRow),
        typeof(int), typeof(TimelineObjectListPane), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((TimelineObjectListPane)d).RequestRows()));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive),
        typeof(bool), typeof(TimelineObjectListPane), new PropertyMetadata(false, (d, _) => ((TimelineObjectListPane)d).ActivityChanged()));

    private const double RowHeight = 28;
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Width = 12, SmallChange = 1 };
    private readonly RowSurface _rows;
    private readonly DispatcherTimer _dragScroll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
    private CancellationTokenSource _request = new();
    private CancellationTokenSource _interaction = new();
    private TimelineObjectListRow[] _visible = [];
    private int _first;
    private int _anchor = -1;
    private int _down = -1;
    private int _dragEnd = -1;
    private bool _settingScroll;
    private ModifierKeys _modifiers;
    private string _message = "";
    private TimelineObjectListSource? _gestureSource;
    private Point? _rightDown;
    private bool _rightDragged;
    private bool _waitingForRow;

    public TimelineObjectListPane()
    {
        Focusable = true; ClipToBounds = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _rows = new(this);
        Children.Add(_rows); SetColumn(_scroll, 1); Children.Add(_scroll);
        _scroll.ValueChanged += (_, _) =>
        {
            if (!_settingScroll) SetCurrentValue(FirstRowProperty, (int)_scroll.Value);
        };
        SizeChanged += (_, _) => RequestRows();
        Loaded += (_, _) => RequestRows();
        Unloaded += (_, _) => StopReading();
        IsVisibleChanged += (_, _) => { if (IsVisible) RequestRows(); else StopReading(); };
        _rows.MouseLeftButtonDown += OnPointerDown;
        _rows.MouseLeftButtonUp += OnPointerUp;
        _rows.MouseMove += OnPointerMove;
        _rows.MouseRightButtonDown += OnRightDown;
        _rows.MouseRightButtonUp += OnRightUp;
        _rows.LostMouseCapture += (_, _) => CancelDrag();
        _dragScroll.Tick += (_, _) =>
        {
            if (_down < 0 || !_rows.IsMouseCaptured) { _dragScroll.Stop(); return; }
            double y = Mouse.GetPosition(_rows).Y;
            double delta = y < 0 ? -Math.Min(8, 1 + -y / RowHeight)
                : y >= _rows.ActualHeight ? Math.Min(8, 1 + (y - _rows.ActualHeight) / RowHeight) : 0;
            if (delta != 0) _scroll.Value = Math.Clamp(_scroll.Value + delta, 0, _scroll.Maximum);
            _dragEnd = Hit(Mouse.GetPosition(_rows), allowUnloaded: true);
            _rows.InvalidateVisual();
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (IsActive) _scroll.Value = Math.Clamp(_scroll.Value - e.Delta / 120d * 3, 0, _scroll.Maximum);
            e.Handled = true;
        };
    }

    public TimelineObjectListSource? Source { get => (TimelineObjectListSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public TimelineSelectionSnapshot? Selection { get => (TimelineSelectionSnapshot?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public int FirstRow { get => (int)GetValue(FirstRowProperty); set => SetValue(FirstRowProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public event EventHandler<TimelineObjectListSelectionEventArgs>? SelectionRequested;
    public event EventHandler<TimelineObjectListRow>? PropertiesRequested;
    public event EventHandler<TimelineObjectListContextEventArgs>? ContextRequested;
    public event EventHandler<TimelineObjectListRow>? LocateRequested;
    public event EventHandler<Exception>? ReadFailed;
    public void RequestLocate(TimelineObjectListRow row) => LocateRequested?.Invoke(this, row);

    private void SourceChanged()
    {
        CancelPendingInteraction(); _anchor = -1; _rightDown = null; CancelDrag(); _visible = [];
        RequestRows();
    }
    private void ActivityChanged() { if (IsActive) RequestRows(); else StopReading(); }
    private void StopReading()
    {
        _request.Cancel(); CancelPendingInteraction(); CancelDrag(); _rightDown = null;
        _visible = []; _message = ""; _rows.InvalidateVisual();
        Source?.Cancel();
    }

    private async void RequestRows()
    {
        _request.Cancel(); _request.Dispose(); _request = new();
        CancellationToken token = _request.Token;
        var source = Source;
        int capacity = Math.Clamp((int)Math.Ceiling(Math.Max(0, ActualHeight) / RowHeight) + 1, 1, 256);
        int viewportRows = Math.Max(1, (int)Math.Floor(Math.Max(0, ActualHeight) / RowHeight));
        _settingScroll = true;
        try
        {
            _scroll.Maximum = Math.Max(0, (source?.Count ?? 0) - viewportRows);
            _scroll.Value = Math.Clamp(FirstRow, 0, _scroll.Maximum);
            _scroll.ViewportSize = viewportRows; _scroll.LargeChange = viewportRows;
            _scroll.IsEnabled = _scroll.Maximum > 0;
        }
        finally { _settingScroll = false; }
        int first = (int)_scroll.Value;
        if (source is not null && IsActive && FirstRow != first) { SetCurrentValue(FirstRowProperty, first); return; }
        // Retain the previous page while refreshing exactly the same location;
        // scrolling elsewhere must never display old rows at new ordinals.
        if (first != _first) _visible = [];
        _first = first;
        if (source is null || !IsActive || !IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || source.IsDisposed)
        { _visible = []; _message = ""; _rows.InvalidateVisual(); return; }
        _message = "Loading objects…"; _rows.InvalidateVisual();
        try
        {
            var rows = await source.ReadRowsAsync(first, Math.Min(capacity, source.Count - first), token);
            if (token.IsCancellationRequested || !ReferenceEquals(Source, source) || !IsActive || !IsVisible) return;
            _visible = rows; _message = rows.Length == 0 ? "No objects" : "";
            _rows.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !ReferenceEquals(Source, source)) return;
            _visible = []; _message = "Unable to load objects"; _rows.InvalidateVisual();
            ReadFailed?.Invoke(this, ex);
        }
    }

    private int Hit(Point point, bool allowUnloaded = false)
    {
        if (!IsActive || Source is not { Count: > 0 } source
            || !allowUnloaded && (point.X < 0 || point.X > _rows.ActualWidth)) return -1;
        int ordinal = _first + (int)Math.Floor(point.Y / RowHeight);
        if (allowUnloaded) return Math.Clamp(ordinal, 0, source.Count - 1);
        // The ordinal is authoritative even while its page is cold. Commands
        // resolve the actual row asynchronously instead of treating it as blank.
        return point.Y >= 0 && point.Y < _rows.ActualHeight && ordinal < source.Count ? ordinal : -1;
    }
    private TimelineObjectListRow? VisibleRow(int ordinal) => ordinal >= _first && ordinal - _first < _visible.Length
        ? _visible[ordinal - _first] : null;

    internal bool IsTrueEmptySpace(Point point) => Source is { } source
        && point.X >= 0 && point.X < _rows.ActualWidth && point.Y >= 0 && point.Y < _rows.ActualHeight
        && _first + (long)Math.Floor(point.Y / RowHeight) >= source.Count;

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive) return;
        CancelPendingInteraction();
        Focus();
        _down = Hit(e.GetPosition(_rows)); _modifiers = Keyboard.Modifiers;
        _gestureSource = Source; _dragEnd = _down;
        if (_down < 0)
        {
            // A cold visible page is not empty content: do not clear a valid
            // selection just because its asynchronous rows have not arrived.
            if (IsTrueEmptySpace(e.GetPosition(_rows)) && _modifiers == ModifierKeys.None)
                SelectionRequested?.Invoke(this, new(-1, -1, _modifiers, null));
            return;
        }
        if (e.ClickCount == 2)
        {
            int ordinal = _down;
            CancelDrag(); _ = RequestPropertiesAsync(ordinal);
        }
        else { _rows.CaptureMouse(); _dragScroll.Start(); }
        e.Handled = true;
    }
    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(_rows);
        if (_rightDown is { } right && (Math.Abs(right.X - point.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(right.Y - point.Y) >= SystemParameters.MinimumVerticalDragDistance)) _rightDragged = true;
        int index = Hit(point, _down >= 0);
        ToolTip = VisibleRow(index) is { } row
            ? $"{row.Tick} · {Source?.GetRowTypeLabel(row) ?? TimelineObjectListSource.GetTypeLabel(row)}\n{TimelineObjectListSource.GetValueLabel(row)}" : null;
        if (_down >= 0 && e.LeftButton == MouseButtonState.Pressed) { _dragEnd = index; _rows.InvalidateVisual(); }
    }
    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_down < 0) return;
        int end = Hit(e.GetPosition(_rows), allowUnloaded: true);
        int start = (_modifiers & ModifierKeys.Shift) != 0 && _anchor >= 0 ? _anchor : _down;
        ModifierKeys modifiers = _modifiers;
        var gestureSource = _gestureSource;
        if ((modifiers & ModifierKeys.Shift) == 0) _anchor = _down;
        CancelDrag();
        if (end >= 0 && ReferenceEquals(Source, gestureSource) && Source?.IsDisposed == false)
            _ = RequestSelectionAsync(start, end, modifiers);
        e.Handled = true;
    }
    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive) return;
        CancelPendingInteraction(); Focus(); _rightDown = e.GetPosition(_rows); _rightDragged = false;
        e.Handled = true;
    }
    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_rightDown is null || !IsActive) return;
        _rightDown = null;
        if (_rightDragged || Source is not { IsDisposed: false }) { e.Handled = true; return; }
        _ = RequestContextAsync(e.GetPosition(_rows));
        e.Handled = true;
    }

    internal Task RequestSelectionAsync(int start, int end, ModifierKeys modifiers) => ResolveInteractionRowAsync(end,
        (source, row, _) => SelectionRequested?.Invoke(this,
            new(Math.Min(start, end), Math.Max(start, end), modifiers, row)));

    internal Task RequestPropertiesAsync(int ordinal) => ResolveInteractionRowAsync(ordinal,
        (_, row, _) => PropertiesRequested?.Invoke(this, row));

    internal Task RequestContextAsync(Point position)
    {
        int ordinal = Hit(position);
        if (ordinal >= 0) return ResolveInteractionRowAsync(ordinal, (source, row, selection) =>
            ContextRequested?.Invoke(this, new(source, row, ordinal, selection, position)));
        if (IsActive && Source is { IsDisposed: false } current && IsTrueEmptySpace(position))
            ContextRequested?.Invoke(this, new(current, null, -1, Selection, position));
        return Task.CompletedTask;
    }

    private async Task ResolveInteractionRowAsync(int ordinal,
        Action<TimelineObjectListSource, TimelineObjectListRow, TimelineSelectionSnapshot?> publish)
    {
        CancelPendingInteraction();
        var request = _interaction;
        CancellationToken token = request.Token;
        var source = Source;
        var selection = Selection;
        if (!IsActive || source is null || source.IsDisposed || ordinal < 0 || ordinal >= source.Count) return;
        try
        {
            var row = VisibleRow(ordinal);
            if (row is null)
            {
                _waitingForRow = true;
                var resolved = await source.ReadRowsAsync(ordinal, 1, token);
                if (resolved.Length != 1) return;
                row = resolved[0];
            }
            if (token.IsCancellationRequested || !IsActive || source.IsDisposed
                || !ReferenceEquals(source, Source) || !ReferenceEquals(selection, Selection)) return;
            publish(source, row.Value, selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(Source, source)) ReadFailed?.Invoke(this, ex);
        }
        finally { if (ReferenceEquals(request, _interaction)) _waitingForRow = false; }
    }

    private void CancelPendingInteraction()
    {
        _interaction.Cancel(); _interaction.Dispose(); _interaction = new(); _waitingForRow = false;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_down >= 0 || _rightDown is not null || _waitingForRow))
        { CancelPendingInteraction(); CancelDrag(); _rightDown = null; e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
    private void CancelDrag()
    {
        _down = -1; _dragEnd = -1; _gestureSource = null;
        _dragScroll.Stop();
        if (_rows.IsMouseCaptured) _rows.ReleaseMouseCapture();
        _rows.InvalidateVisual();
    }

    private sealed class RowSurface(TimelineObjectListPane owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            Brush normal = owner.TryFindResource("Brush.Text.Primary") as Brush ?? Brushes.White;
            Brush muted = owner.TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.Gray;
            Brush selected = owner.TryFindResource("Brush.Red.Subtle") as Brush ?? Brushes.DarkRed;
            dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            for (int i = 0; i < owner._visible.Length; i++)
            {
                var row = owner._visible[i]; double y = i * RowHeight;
                bool isSelected = owner.Selection is { } selection && selection.Contains(row.Id)
                    && (!row.IsInstrumentChange || selection.Contains(row.InstrumentChange.BankEventId)
                        && (row.InstrumentChange.BankLsbEventId is not { } lsb || selection.Contains(lsb)));
                if (isSelected) dc.DrawRectangle(selected, null, new(0, y, ActualWidth, RowHeight));
                Text(row.Tick.ToString(CultureInfo.InvariantCulture), 8, y, 78, muted);
                Text(owner.Source?.GetRowTypeLabel(row) ?? TimelineObjectListSource.GetTypeLabel(row), 92, y, 105, normal);
                Text(TimelineObjectListSource.GetValueLabel(row), 203, y, Math.Max(1, ActualWidth - 211), muted);
            }
            if (owner._visible.Length == 0) Text(owner._message, 8, 0, Math.Max(1, ActualWidth - 16), muted);
            if (owner._down >= 0 && owner._dragEnd >= 0)
            {
                int start = (owner._modifiers & ModifierKeys.Shift) != 0 && owner._anchor >= 0 ? owner._anchor : owner._down;
                int first = Math.Min(start, owner._dragEnd) - owner._first;
                double height = (Math.Abs(owner._dragEnd - start) + 1d) * RowHeight;
                dc.DrawRectangle(null, new Pen(normal, 1), new(1, first * RowHeight, Math.Max(0, ActualWidth - 2), height));
            }
            dc.Pop();
            void Text(string value, double x, double y, double width, Brush brush)
            {
                FormattedText text = new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(EmbeddedFontFamilies.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11,
                    brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new(x, y + (RowHeight - text.Height) / 2));
            }
        }
    }
}
