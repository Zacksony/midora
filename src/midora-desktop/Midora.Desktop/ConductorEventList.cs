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
using Midora.Domain;

namespace Midora.Desktop;

public sealed class ConductorListSelectionEventArgs(int first, int last, ModifierKeys modifiers,
    ConductorEventRow? primary, bool forceSingle = false) : EventArgs
{
    public int First { get; } = first;
    public int Last { get; } = last;
    public ModifierKeys Modifiers { get; } = modifiers;
    public ConductorEventRow? Primary { get; } = primary;
    public bool ForceSingle { get; } = forceSingle;
}

/// <summary>Virtual drawing-based rows; WPF never receives the million-item collection.</summary>
public sealed class ConductorEventList : Grid
{
    public static readonly DependencyProperty FirstRowProperty = DependencyProperty.Register(
        nameof(FirstRow), typeof(int), typeof(ConductorEventList),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => { var list = (ConductorEventList)d; if (list.Source is not null) list._scroll.Value = Math.Max(0, (int)e.NewValue); }));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ConductorEventListSource), typeof(ConductorEventList),
        new PropertyMetadata(null, (d, _) => ((ConductorEventList)d).SourceChanged()));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(TimelineSelectionSnapshot), typeof(ConductorEventList),
        new PropertyMetadata(null, (d, _) => ((ConductorEventList)d)._rows.InvalidateVisual()));

    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Width = 12, SmallChange = 1 };
    private readonly Rows _rows;
    private CancellationTokenSource _request = new();
    private int _first;
    private int _anchor = -1;
    private int _down = -1;
    private ModifierKeys _modifiers;
    private ConductorEventRow[] _visible = [];
    private string _message = "";
    private const double RowHeight = 28;
    private readonly DispatcherTimer _dragScroll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };

    public ConductorEventList()
    {
        Focusable = true;
        ClipToBounds = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _rows = new(this);
        Children.Add(_rows);
        SetColumn(_scroll, 1);
        Children.Add(_scroll);
        _scroll.ValueChanged += (_, _) =>
        {
            if (IsLoaded && Source is not null) SetCurrentValue(FirstRowProperty, (int)_scroll.Value);
            RequestRows();
        };
        SizeChanged += (_, _) => RequestRows();
        Loaded += (_, _) => RequestRows();
        Unloaded += (_, _) => { _request.Cancel(); CancelDrag(); };
        _dragScroll.Tick += (_, _) =>
        {
            if (_down < 0 || !_rows.IsMouseCaptured) { _dragScroll.Stop(); return; }
            double y = Mouse.GetPosition(_rows).Y;
            double delta = y < 0 ? -Math.Min(8, 1 + -y / RowHeight)
                : y >= _rows.ActualHeight ? Math.Min(8, 1 + (y - _rows.ActualHeight) / RowHeight) : 0;
            if (delta != 0) _scroll.Value = Math.Clamp(_scroll.Value + delta, 0, _scroll.Maximum);
        };
        _rows.LostMouseCapture += (_, _) => { _down = -1; _dragScroll.Stop(); _rows.InvalidateVisual(); };
        PreviewMouseWheel += (_, e) =>
        {
            _scroll.Value = Math.Clamp(_scroll.Value - e.Delta / 120d * 3, 0, _scroll.Maximum);
            e.Handled = true;
        };
        _rows.MouseLeftButtonDown += OnPointerDown;
        _rows.MouseLeftButtonUp += OnPointerUp;
        _rows.MouseMove += (_, e) =>
        {
            int index = Hit(e.GetPosition(_rows));
            ToolTip = VisibleRow(index)?.Value;
            if (_down >= 0 && e.LeftButton == MouseButtonState.Pressed) _rows.InvalidateVisual();
        };
        _rows.MouseRightButtonDown += (_, e) =>
        {
            Focus();
            int index = Hit(e.GetPosition(_rows));
            if (VisibleRow(index) is { } row && !(Selection?.Contains(row.Id) ?? false))
                SelectionRequested?.Invoke(this, new(index, index, ModifierKeys.None, row));
        };
    }

    public ConductorEventListSource? Source { get => (ConductorEventListSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int FirstRow { get => (int)GetValue(FirstRowProperty); set => SetValue(FirstRowProperty, value); }
    public TimelineSelectionSnapshot? Selection { get => (TimelineSelectionSnapshot?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public event EventHandler<ConductorListSelectionEventArgs>? SelectionRequested;
    public event EventHandler<ConductorEventRow>? PropertiesRequested;
    public event EventHandler<Exception>? ReadFailed;

    private void SourceChanged()
    {
        _anchor = -1;
        CancelDrag();
        _visible = [];
        RequestRows();
    }

    private async void RequestRows()
    {
        _request.Cancel();
        _request.Dispose();
        _request = new();
        CancellationToken token = _request.Token;
        ConductorEventListSource? source = Source;
        int capacity = Math.Max(1, (int)Math.Ceiling(Math.Max(0, ActualHeight) / RowHeight));
        int requestedFirst = FirstRow;
        _scroll.Maximum = Math.Max(0, (source?.Count ?? 0) - capacity);
        _scroll.Value = Math.Clamp(requestedFirst, 0, _scroll.Maximum);
        _scroll.ViewportSize = capacity;
        _scroll.LargeChange = capacity;
        _scroll.IsEnabled = _scroll.Maximum > 0;
        _first = Math.Clamp((int)_scroll.Value, 0, source?.Count ?? 0);
        int first = _first;
        _visible = [];
        if (source is null || !IsLoaded) { _visible = []; _rows.InvalidateVisual(); return; }
        _message = "Loading events…";
        _rows.InvalidateVisual();
        try
        {
            var rows = await source.ReadRowsAsync(first, capacity, token);
            if (token.IsCancellationRequested || !ReferenceEquals(Source, source)) return;
            _visible = rows;
            _message = rows.Length == 0 ? "No events" : "";
            _rows.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _message = "Unable to load events";
            _visible = [];
            _rows.InvalidateVisual();
            ReadFailed?.Invoke(this, ex);
        }
    }

    private int Hit(Point point)
    {
        if (Source is not { Count: > 0 } source) return -1;
        int ordinal = _first + (int)Math.Floor(point.Y / RowHeight);
        if (_down >= 0) return Math.Clamp(ordinal, 0, source.Count - 1);
        return point.Y >= 0 && point.Y < _rows.ActualHeight && ordinal < source.Count && VisibleRow(ordinal) is not null
            ? ordinal : -1;
    }
    private ConductorEventRow? VisibleRow(int ordinal) => ordinal >= _first && ordinal - _first < _visible.Length
        ? _visible[ordinal - _first] : null;

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _down = Hit(e.GetPosition(_rows));
        _modifiers = Keyboard.Modifiers;
        if (_down < 0)
        {
            if (_modifiers == ModifierKeys.None) SelectionRequested?.Invoke(this, new(-1, -1, _modifiers, null));
            return;
        }
        if (e.ClickCount == 2 && VisibleRow(_down) is { } row)
        {
            SelectionRequested?.Invoke(this, new(_down, _down, ModifierKeys.None, row, forceSingle: true));
            PropertiesRequested?.Invoke(this, row);
            _down = -1;
        }
        else { _rows.CaptureMouse(); _dragScroll.Start(); }
        e.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_down < 0) return;
        int end = Hit(e.GetPosition(_rows));
        int start = _modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0 ? _anchor : _down;
        if (!_modifiers.HasFlag(ModifierKeys.Shift)) _anchor = _down;
        _down = -1;
        _dragScroll.Stop();
        _rows.ReleaseMouseCapture();
        SelectionRequested?.Invoke(this, new(Math.Min(start, end), Math.Max(start, end), _modifiers, VisibleRow(end)));
        _rows.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _down >= 0) { CancelDrag(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void CancelDrag()
    {
        _down = -1;
        _dragScroll.Stop();
        if (_rows.IsMouseCaptured) _rows.ReleaseMouseCapture();
        _rows.InvalidateVisual();
    }

    private sealed class Rows(ConductorEventList owner) : FrameworkElement
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
                var row = owner._visible[i];
                double y = i * RowHeight;
                if (owner.Selection?.Contains(row.Id) == true) dc.DrawRectangle(selected, null, new(0, y, ActualWidth, RowHeight));
                Text(row.Tick.ToString(CultureInfo.InvariantCulture), 8, y, 86, muted);
                Text(row.Type, 96, y, 112, normal);
                Text(row.Value, 213, y, Math.Max(1, ActualWidth - 221), muted);
            }
            if (owner._visible.Length == 0) Text(owner._message, 8, 0, Math.Max(1, ActualWidth - 16), muted);
            if (owner._down >= 0)
            {
                int end = owner.Hit(Mouse.GetPosition(this));
                int first = Math.Min(owner._down, end) - owner._first;
                double height = (Math.Abs(end - owner._down) + 1) * RowHeight;
                dc.DrawRectangle(null, new Pen(normal, 1), new(1, first * RowHeight, Math.Max(0, ActualWidth - 2), height));
            }
            dc.Pop();
            void Text(string value, double x, double y, double width, Brush brush)
            {
                FormattedText text = new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(EmbeddedFontFamilies.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                    { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new(x, y + (RowHeight - text.Height) / 2));
            }
        }
    }
}
