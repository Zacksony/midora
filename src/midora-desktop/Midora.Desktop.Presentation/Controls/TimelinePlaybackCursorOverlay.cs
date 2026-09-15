using System.Windows;
using System.Windows.Media;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// Lightweight playback cursor layer.  Keeping the high-frequency cursor out of
/// TimelineSurface prevents every timer tick from rebuilding notes, events, grids,
/// and tile composition.
/// </summary>
public sealed class TimelinePlaybackCursorOverlay : FrameworkElement
{
    private Brush? _cachedCursorBrush;
    private Pen? _cachedCursorPen;

    public static readonly DependencyProperty StartTickProperty = DependencyProperty.Register(
        nameof(StartTick),
        typeof(long),
        typeof(TimelinePlaybackCursorOverlay),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickSpanProperty = DependencyProperty.Register(
        nameof(TickSpan),
        typeof(long),
        typeof(TimelinePlaybackCursorOverlay),
        new FrameworkPropertyMetadata(1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaybackCursorTickProperty = DependencyProperty.Register(
        nameof(PlaybackCursorTick),
        typeof(long?),
        typeof(TimelinePlaybackCursorOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceModeProperty = DependencyProperty.Register(
        nameof(SurfaceMode),
        typeof(TimelineSurfaceMode),
        typeof(TimelinePlaybackCursorOverlay),
        new FrameworkPropertyMetadata(
            TimelineSurfaceMode.General,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LaneHeaderWidthOverrideProperty = DependencyProperty.Register(
        nameof(LaneHeaderWidthOverride),
        typeof(double),
        typeof(TimelinePlaybackCursorOverlay),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender),
        value => double.IsNaN((double)value) || (double.IsFinite((double)value) && (double)value >= 0));

    public double LaneHeaderWidthOverride
    {
        get => (double)GetValue(LaneHeaderWidthOverrideProperty);
        set => SetValue(LaneHeaderWidthOverrideProperty, value);
    }

    public TimelinePlaybackCursorOverlay()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public long StartTick
    {
        get => (long)GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, value);
    }

    public long TickSpan
    {
        get => (long)GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, value);
    }

    public long? PlaybackCursorTick
    {
        get => (long?)GetValue(PlaybackCursorTickProperty);
        set => SetValue(PlaybackCursorTickProperty, value);
    }

    public TimelineSurfaceMode SurfaceMode
    {
        get => (TimelineSurfaceMode)GetValue(SurfaceModeProperty);
        set => SetValue(SurfaceModeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (PlaybackCursorTick is not long tick
            || TickSpan <= 0
            || tick < StartTick)
        {
            return;
        }
        long relativeTick = tick - StartTick;
        if (relativeTick >= TickSpan)
        {
            return;
        }
        double headerWidth = !double.IsNaN(LaneHeaderWidthOverride) ? LaneHeaderWidthOverride : SurfaceMode switch
        {
            TimelineSurfaceMode.Arrangement => TimelineSurface.ArrangementLaneHeaderWidth,
            TimelineSurfaceMode.PianoRoll => 52,
            TimelineSurfaceMode.EventLanes => 52,
            TimelineSurfaceMode.Conductor => 130,
            TimelineSurfaceMode.Velocity => 52,
            _ => 0
        };
        double rulerHeight = SurfaceMode == TimelineSurfaceMode.General ? 0 : 24;
        double contentWidth = Math.Max(0, ActualWidth - headerWidth);
        if (contentWidth <= 0 || ActualHeight <= rulerHeight)
        {
            return;
        }
        double x = headerWidth
            + Math.Round(relativeTick / (double)TickSpan * contentWidth)
            + 0.5;
        Pen pen = GetCursorPen();
        drawingContext.DrawLine(
            pen,
            new Point(x, rulerHeight),
            new Point(x, ActualHeight));
    }

    private Pen GetCursorPen()
    {
        Brush brush = TryFindResource("Brush.Red") as Brush
            ?? Brushes.IndianRed;
        if (ReferenceEquals(_cachedCursorBrush, brush) && _cachedCursorPen is not null)
        {
            return _cachedCursorPen;
        }
        Pen pen = new(brush, 1);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        _cachedCursorBrush = brush;
        _cachedCursorPen = pen;
        return pen;
    }
}
