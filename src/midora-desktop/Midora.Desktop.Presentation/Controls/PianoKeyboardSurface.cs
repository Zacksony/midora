using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Typography;

namespace Midora.Desktop.Presentation.Controls;

public sealed class PianoKeyEventArgs(int note, int velocity) : EventArgs
{
    public int Note { get; } = note;
    public int Velocity { get; } = velocity;
}

public sealed class PianoKeyboardSurface : Control
{
    public static readonly DependencyProperty StartNoteProperty = DependencyProperty.Register(
        nameof(StartNote), typeof(int), typeof(PianoKeyboardSurface),
        new FrameworkPropertyMetadata(48, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutValueChanged));
    public static readonly DependencyProperty NoteCountProperty = DependencyProperty.Register(
        nameof(NoteCount), typeof(int), typeof(PianoKeyboardSurface),
        new FrameworkPropertyMetadata(37, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutValueChanged));

    private static readonly Brush WhiteKey = FrozenBrush(0xffd7dbe2);
    private static readonly Brush WhiteHover = FrozenBrush(0xffffd6d9);
    private static readonly Brush BlackKey = FrozenBrush(0xff151820);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "PianoKeyboardSurface");
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ = UIElementAutomationPeer.CreatePeerForElement(this);
    }
    private static readonly Brush BlackHover = FrozenBrush(0xffe0444e);
    private static readonly Brush LabelBrush = FrozenBrush(0xff657083);
    private static readonly Pen BorderPen = FrozenPen(0xff343a46);
    private readonly List<KeyLayout> _keys = [];
    private readonly Dictionary<int, FormattedText> _labelCache = [];
    private double _layoutWidth = -1;
    private double _layoutHeight = -1;
    private double _labelPixelsPerDip = -1;
    private int _layoutStart = -1;
    private int _layoutCount = -1;
    private int? _pressedNote;
    private int _pressedVelocity;

    public PianoKeyboardSurface()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    public int StartNote
    {
        get => (int)GetValue(StartNoteProperty);
        set => SetValue(StartNoteProperty, Math.Clamp(value, 0, 127));
    }

    public int NoteCount
    {
        get => (int)GetValue(NoteCountProperty);
        set => SetValue(NoteCountProperty, Math.Clamp(value, 1, 128));
    }

    public event EventHandler<PianoKeyEventArgs>? NotePressed;
    public event EventHandler<PianoKeyEventArgs>? NoteReleased;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        EnsureLayout();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labelPixelsPerDip != pixelsPerDip)
        {
            _labelPixelsPerDip = pixelsPerDip;
            _labelCache.Clear();
        }
        foreach (KeyLayout key in _keys)
        {
            if (key.IsBlack) continue;
            drawingContext.DrawRectangle(
                _pressedNote == key.Note ? WhiteHover : WhiteKey,
                BorderPen,
                key.Bounds);
            if (key.Note % 12 == 0)
            {
                if (!_labelCache.TryGetValue(key.Note, out FormattedText? label))
                {
                    label = new(
                        $"C{key.Note / 12 - 1}",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(
                            EmbeddedFontFamilies.Ui,
                            FontStyles.Normal,
                            FontWeights.Normal,
                            FontStretches.Normal),
                        9,
                        LabelBrush,
                        pixelsPerDip);
                    _labelCache[key.Note] = label;
                }
                drawingContext.DrawText(label, new Point(key.Bounds.Left + 3, Math.Max(1, key.Bounds.Bottom - 15)));
            }
        }
        foreach (KeyLayout key in _keys)
        {
            if (!key.IsBlack) continue;
            drawingContext.DrawRectangle(
                _pressedNote == key.Note ? BlackHover : BlackKey,
                BorderPen,
                key.Bounds);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;
        Focus();
        EnsureLayout();
        Point point = e.GetPosition(this);
        KeyLayout? hit = null;
        for (int index = _keys.Count - 1; index >= 0; index--)
        {
            if (_keys[index].IsBlack && _keys[index].Bounds.Contains(point))
            {
                hit = _keys[index];
                break;
            }
        }
        if (hit is null)
        {
            for (int index = 0; index < _keys.Count; index++)
            {
                if (!_keys[index].IsBlack && _keys[index].Bounds.Contains(point))
                {
                    hit = _keys[index];
                    break;
                }
            }
        }
        if (hit is not KeyLayout key) return;
        _pressedNote = key.Note;
        _pressedVelocity = CalculatePreviewVelocity(
            point.Y - key.Bounds.Top,
            key.Bounds.Height);
        CaptureMouse();
        InvalidateVisual();
        NotePressed?.Invoke(this, new(key.Note, _pressedVelocity));
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left) ReleasePressedNote();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        ReleasePressedNote();
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int delta = e.Delta > 0 ? 12 : -12;
        StartNote = Math.Clamp(StartNote + delta, 0, Math.Max(0, 128 - NoteCount));
        e.Handled = true;
        base.OnMouseWheel(e);
    }

    private void ReleasePressedNote()
    {
        if (_pressedNote is not int note) return;
        int velocity = _pressedVelocity;
        _pressedNote = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        NoteReleased?.Invoke(this, new(note, velocity));
    }

    internal static int CalculatePreviewVelocity(double y, double height)
    {
        double normalizedY = Math.Clamp(y / Math.Max(1, height), 0, 1);
        return Math.Clamp((int)Math.Round(31 + normalizedY * 96), 1, 127);
    }

    private void EnsureLayout()
    {
        int requestedStart = Math.Clamp(StartNote, 0, 127);
        int requestedCount = Math.Clamp(NoteCount, 1, 128);
        if (_layoutWidth == ActualWidth
            && _layoutHeight == ActualHeight
            && _layoutStart == requestedStart
            && _layoutCount == requestedCount)
        {
            return;
        }
        _layoutWidth = ActualWidth;
        _layoutHeight = ActualHeight;
        _layoutStart = requestedStart;
        _layoutCount = requestedCount;
        _keys.Clear();
        int start = requestedStart;
        int end = Math.Min(128, start + requestedCount);
        int whiteCount = 0;
        for (int note = start; note < end; note++)
        {
            if (!IsBlack(note)) whiteCount++;
        }
        if (whiteCount == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        double whiteWidth = ActualWidth / whiteCount;
        int whiteIndex = 0;
        for (int note = start; note < end; note++)
        {
            if (IsBlack(note)) continue;
            Rect bounds = new(whiteIndex * whiteWidth, 0, whiteWidth, ActualHeight);
            _keys.Add(new(note, false, bounds));
            whiteIndex++;
        }
        for (int note = start; note < end; note++)
        {
            if (!IsBlack(note)) continue;
            int previousWhiteCount = 0;
            for (int candidate = start; candidate < note; candidate++)
            {
                if (!IsBlack(candidate)) previousWhiteCount++;
            }
            double center = previousWhiteCount * whiteWidth;
            double width = Math.Max(5, whiteWidth * 0.62);
            _keys.Add(new(note, true, new(center - width / 2, 0, width, ActualHeight * 0.62)));
        }
    }

    private static bool IsBlack(int note) => note % 12 is 1 or 3 or 6 or 8 or 10;
    private static void OnLayoutValueChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) =>
        ((PianoKeyboardSurface)owner).InvalidateVisual();

    private static SolidColorBrush FrozenBrush(uint argb)
    {
        SolidColorBrush brush = new(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(uint argb)
    {
        Pen pen = new(FrozenBrush(argb), 1);
        pen.Freeze();
        return pen;
    }

    private readonly record struct KeyLayout(int Note, bool IsBlack, Rect Bounds);
}
