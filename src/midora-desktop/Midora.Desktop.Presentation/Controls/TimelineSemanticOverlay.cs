using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Midora.Desktop.Presentation.Typography;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// Read-only Definition timing decoration. It owns no source, selection or raster
/// cache; changing a loop boundary cannot invalidate note/event tiles.
/// </summary>
public sealed class TimelineSemanticOverlay : FrameworkElement
{
    private Typeface? _labelTypeface;

    public static readonly DependencyProperty StartTickProperty = Register(nameof(StartTick), typeof(long), 0L);
    public static readonly DependencyProperty TickSpanProperty = Register(nameof(TickSpan), typeof(long), 1L);
    public static readonly DependencyProperty PreRollTicksProperty = Register(nameof(PreRollTicks), typeof(long), 0L);
    public static readonly DependencyProperty LoopStartTickProperty = Register(nameof(LoopStartTick), typeof(long?), null);
    public static readonly DependencyProperty LoopEndTickProperty = Register(nameof(LoopEndTick), typeof(long?), null);
    public static readonly DependencyProperty LaneHeaderWidthProperty = Register(nameof(LaneHeaderWidth), typeof(double), 52d);
    public static readonly DependencyProperty RulerHeightProperty = Register(nameof(RulerHeight), typeof(double), 24d);
    public static readonly DependencyProperty ShowRulerLabelsProperty = Register(nameof(ShowRulerLabels), typeof(bool), true);

    static TimelineSemanticOverlay()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(TimelineSemanticOverlay),
            new UIPropertyMetadata(false, null, static (_, _) => false));
        FocusableProperty.OverrideMetadata(typeof(TimelineSemanticOverlay),
            new UIPropertyMetadata(false, null, static (_, _) => false));
    }

    public TimelineSemanticOverlay()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    public long StartTick { get => (long)GetValue(StartTickProperty); set => SetValue(StartTickProperty, value); }
    public long TickSpan { get => (long)GetValue(TickSpanProperty); set => SetValue(TickSpanProperty, value); }
    public long PreRollTicks { get => (long)GetValue(PreRollTicksProperty); set => SetValue(PreRollTicksProperty, value); }
    public long? LoopStartTick { get => (long?)GetValue(LoopStartTickProperty); set => SetValue(LoopStartTickProperty, value); }
    public long? LoopEndTick { get => (long?)GetValue(LoopEndTickProperty); set => SetValue(LoopEndTickProperty, value); }
    public double LaneHeaderWidth { get => (double)GetValue(LaneHeaderWidthProperty); set => SetValue(LaneHeaderWidthProperty, value); }
    public double RulerHeight { get => (double)GetValue(RulerHeightProperty); set => SetValue(RulerHeightProperty, value); }
    public bool ShowRulerLabels { get => (bool)GetValue(ShowRulerLabelsProperty); set => SetValue(ShowRulerLabelsProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        TimelineSemanticOverlayLayout layout = GetLayout(ActualWidth, ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        if (layout.Content.IsEmpty) return;
        Brush outside = TryFindResource("Brush.Segment.PianoOutside") as Brush
            ?? new SolidColorBrush(Color.FromRgb(2, 3, 4));
        Brush warning = TryFindResource("Brush.Warning") as Brush
            ?? new SolidColorBrush(Color.FromRgb(232, 179, 75));
        Brush preRoll = TryFindResource("Brush.Timeline.PreRoll") as Brush
            ?? new SolidColorBrush(Color.FromRgb(199, 138, 255));

        drawingContext.PushClip(new RectangleGeometry(layout.Content));
        // Match the existing outside-active-range tint without hiding notes or
        // event points which the user may still edit in the pre-roll interval.
        if (!layout.PreRollShade.IsEmpty)
        {
            drawingContext.PushOpacity(.72);
            drawingContext.DrawRectangle(outside, null, layout.PreRollShade);
            drawingContext.Pop();
        }
        DrawBoundary(layout.LoopStartX);
        DrawBoundary(layout.LoopEndX);
        drawingContext.Pop();

        if (!ShowRulerLabels || RulerHeight <= 0) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(LaneHeaderWidth, 0,
            ActualWidth - LaneHeaderWidth, Math.Min(RulerHeight, ActualHeight))));
        if (!layout.LoopBand.IsEmpty)
        {
            drawingContext.PushOpacity(.35);
            drawingContext.DrawRectangle(warning, null, layout.LoopBand);
            drawingContext.Pop();
        }
        // At low zoom multiple endpoints can share one pixel. Combine colliding
        // labels rather than drawing text on top of other endpoint text.
        List<(double X, string Text, bool IsPreRoll)> labels = [];
        AddLabel(layout.PreRollEndX, $"Pre-Roll {PreRollTicks}", isPreRoll: true);
        AddLabel(layout.LoopStartX, $"Start {LoopStartTick}");
        AddLabel(layout.LoopEndX, $"End {LoopEndTick}");
        labels.Sort(static (left, right) => left.X.CompareTo(right.X));
        for (int i = 0; i < labels.Count; i++)
        {
            (double anchor, string text, bool isPreRoll) = labels[i];
            int preRollStart = isPreRoll ? 0 : -1;
            int preRollLength = isPreRoll ? text.Length : 0;
            FormattedText formatted = Format(text);
            while (i + 1 < labels.Count && anchor + formatted.WidthIncludingTrailingWhitespace + 8 >= labels[i + 1].X)
            {
                var next = labels[++i];
                if (next.IsPreRoll)
                {
                    preRollStart = text.Length + 3;
                    preRollLength = next.Text.Length;
                }
                text += " · " + next.Text;
                formatted = Format(text);
            }
            if (preRollStart >= 0)
                formatted.SetForegroundBrush(preRoll, preRollStart, preRollLength);
            DrawLabel(anchor, formatted);
        }
        drawingContext.Pop();

        void DrawBoundary(double? x)
        {
            if (x is not double left) return;
            drawingContext.DrawRectangle(warning, null,
                new Rect(left, layout.Content.Top, 1 / dpi.DpiScaleX, layout.Content.Height));
        }

        void AddLabel(double? x, string text, bool isPreRoll = false)
        {
            if (x is double anchor) labels.Add((anchor, text, isPreRoll));
        }

        FormattedText Format(string text)
        {
            _labelTypeface ??= new(EmbeddedFontFamilies.Ui,
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _labelTypeface, 9, warning, dpi.PixelsPerDip);
        }

        void DrawLabel(double anchor, FormattedText formatted)
        {
            double width = Math.Min(formatted.WidthIncludingTrailingWhitespace, layout.Content.Width - 4);
            if (width <= 0) return;
            formatted.MaxTextWidth = width;
            formatted.MaxTextHeight = Math.Max(1, RulerHeight - 3);
            double left = Math.Clamp(anchor + 3, LaneHeaderWidth + 2, Math.Max(LaneHeaderWidth + 2, ActualWidth - width - 2));
            drawingContext.DrawRectangle(outside, null, new Rect(left, 0, width, Math.Min(formatted.Height, RulerHeight)));
            drawingContext.DrawText(formatted, new Point(left, 0));
        }
    }

    internal TimelineSemanticOverlayLayout GetLayout(double width, double height, double dpiX, double dpiY)
    {
        if (StartTick < 0 || StartTick == long.MaxValue || TickSpan <= 0
            || !double.IsFinite(width) || !double.IsFinite(height)
            || !double.IsFinite(LaneHeaderWidth) || LaneHeaderWidth < 0
            || !double.IsFinite(RulerHeight) || RulerHeight < 0
            || width <= LaneHeaderWidth || height <= RulerHeight
            || !double.IsFinite(dpiX) || dpiX <= 0 || !double.IsFinite(dpiY) || dpiY <= 0)
            return TimelineSemanticOverlayLayout.Empty;
        long span = Math.Min(TickSpan, long.MaxValue - StartTick);
        long end = StartTick + span;
        double contentWidth = width - LaneHeaderWidth;
        Rect content = new(LaneHeaderWidth, RulerHeight, contentWidth, height - RulerHeight);
        Rect shade = Rect.Empty;
        long preRollEnd = Math.Min(Math.Max(0, PreRollTicks), end);
        if (preRollEnd > StartTick)
            shade = new(content.Left, content.Top, Math.Max(0, X(preRollEnd) - content.Left), content.Height);
        Rect band = Rect.Empty;
        if (LoopStartTick is long start && LoopEndTick is long finish && start >= 0 && finish > start)
        {
            long left = Math.Max(start, StartTick), right = Math.Min(finish, end);
            if (right > left && RulerHeight > 0)
            {
                double y = Math.Max(0, Math.Round((RulerHeight - 3) * dpiY) / dpiY);
                band = new(X(left), y, Math.Max(0, X(right) - X(left)), Math.Min(3, RulerHeight));
            }
        }
        return new(content, shade, band, VisibleX(PreRollTicks > 0 ? PreRollTicks : null),
            VisibleX(LoopStartTick), VisibleX(LoopEndTick));

        double X(long tick) => Math.Clamp(Math.Round((LaneHeaderWidth
            + (tick - StartTick) / (double)span * contentWidth) * dpiX) / dpiX, LaneHeaderWidth, width);
        double? VisibleX(long? tick) => tick is long value && value >= StartTick && value < end ? X(value) : null;
    }

    private static DependencyProperty Register(string name, Type type, object? initial) =>
        DependencyProperty.Register(name, type, typeof(TimelineSemanticOverlay),
            new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));
}

internal readonly record struct TimelineSemanticOverlayLayout(
    Rect Content, Rect PreRollShade, Rect LoopBand, double? PreRollEndX, double? LoopStartX, double? LoopEndX)
{
    public static TimelineSemanticOverlayLayout Empty => new(Rect.Empty, Rect.Empty, Rect.Empty, null, null, null);
}
