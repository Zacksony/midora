using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// A rendered, allocation-bounded timeline overview and horizontal navigator.
/// It deliberately exposes one WPF control rather than one element per musical object.
/// </summary>
public sealed class TimelineOverviewSurface : Control
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(TimelineRenderSnapshot), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StartTickProperty = DependencyProperty.Register(
        nameof(StartTick), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TickSpanProperty = DependencyProperty.Register(
        nameof(TickSpan), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(3072L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExtentEndTickProperty = DependencyProperty.Register(
        nameof(ExtentEndTick), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(3072L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlaybackCursorTickProperty = DependencyProperty.Register(
        nameof(PlaybackCursorTick), typeof(long?), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EditCursorTickProperty = DependencyProperty.Register(
        nameof(EditCursorTick), typeof(long?), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private byte[] _noteStartColumns = [];
    private byte[] _eventColumns = [];
    private int[] _density = [];
    private ulong _cachedContentFingerprint;
    private bool _hasCachedOverview;
    private bool _cachedHasDedicatedOverview;
    private long _cachedExtent;
    private int _cachedWidth;
    private int _maximumDensity;
    private readonly object _overviewBuildSync = new();
    private OverviewBuildRequest? _requestedOverviewBuild;
    private Task? _overviewBuildTask;
    private long _overviewBuildRevision;
    private bool _draggingViewport;
    private double _dragOffset;
    private Brush? _cachedBorderBrush;
    private Brush? _cachedInfoBrush;
    private Brush? _cachedRedBrush;
    private Pen? _borderPen;
    private Pen? _infoPen;
    private Pen? _playbackCursorPen;
    private Pen? _editCursorPen;

    public TimelineOverviewSurface()
    {
        Height = 22;
        MinHeight = 22;
        Focusable = true;
        Cursor = Cursors.Arrow;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "TimelineOverviewSurface");

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ = UIElementAutomationPeer.CreatePeerForElement(this);
    }

    public TimelineRenderSnapshot? Snapshot
    {
        get => (TimelineRenderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public long StartTick
    {
        get => (long)GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, Math.Max(0, value));
    }

    public long TickSpan
    {
        get => (long)GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, Math.Max(16, value));
    }

    public long ExtentEndTick
    {
        get => (long)GetValue(ExtentEndTickProperty);
        set => SetValue(ExtentEndTickProperty, Math.Max(1, value));
    }

    public long? PlaybackCursorTick
    {
        get => (long?)GetValue(PlaybackCursorTickProperty);
        set => SetValue(PlaybackCursorTickProperty, value);
    }

    public long? EditCursorTick
    {
        get => (long?)GetValue(EditCursorTickProperty);
        set => SetValue(EditCursorTickProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Brush surface = ResourceBrush("Brush.Surface.1", Color.FromRgb(14, 17, 21));
        Brush border = ResourceBrush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush info = ResourceBrush("Brush.Info", Color.FromRgb(98, 166, 246));
        Brush red = ResourceBrush("Brush.Red", Color.FromRgb(229, 72, 77));
        Brush eventRed = ResourceBrush("Brush.Overview.Event", Color.FromRgb(74, 47, 52));
        EnsurePens(border, info, red);
        drawingContext.DrawRectangle(surface, _borderPen, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 2 || ActualHeight <= 2) return;

        long extent = EffectiveExtent();
        RequestOverviewBuild(ContentExtent());
        double cachedContentWidth = _cachedExtent <= 0
            ? 0
            : Math.Max(0, ActualWidth) * Math.Min(1, _cachedExtent / (double)extent);
        if (_hasCachedOverview && Snapshot is not null && _cachedHasDedicatedOverview)
        {
            double lineHeight = Math.Max(1, ActualHeight - 4);
            for (int x = 0; x < _cachedWidth; x++)
            {
                if (_noteStartColumns[x] != 0)
                    DrawOverviewColumn(drawingContext, border, x, cachedContentWidth, lineHeight);
            }
            for (int x = 0; x < _cachedWidth; x++)
            {
                if (_eventColumns[x] != 0)
                    DrawOverviewColumn(drawingContext, eventRed, x, cachedContentWidth, lineHeight);
            }
        }
        else if (_hasCachedOverview && Snapshot is not null && _maximumDensity > 0)
        {
            double plotHeight = Math.Max(1, ActualHeight - 6);
            for (int x = 0; x < _cachedWidth; x++)
            {
                int count = _density[x];
                if (count == 0) continue;
                double height = Math.Max(
                    1,
                    plotHeight * Math.Log2(count + 1) / Math.Log2(_maximumDensity + 1));
                double projectedX = ProjectOverviewColumn(x, cachedContentWidth);
                drawingContext.DrawRectangle(
                    border,
                    null,
                    new Rect(projectedX, ActualHeight - 3 - height, 1, height));
            }
        }

        Rect thumb = ViewportThumb(extent);
        drawingContext.PushOpacity(IsMouseOver ? 0.28 : 0.20);
        drawingContext.DrawRoundedRectangle(info, null, thumb, 2, 2);
        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, _infoPen, thumb, 2, 2);
        DrawCursor(drawingContext, PlaybackCursorTick, extent, _playbackCursorPen);
        DrawCursor(drawingContext, EditCursorTick, extent, _editCursorPen);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        long extent = EffectiveExtent();
        Point point = e.GetPosition(this);
        Rect thumb = ViewportThumb(extent);
        if (thumb.Contains(point))
        {
            _dragOffset = point.X - thumb.Left;
        }
        else
        {
            _dragOffset = thumb.Width / 2;
            MoveViewport(point.X, extent);
        }
        _draggingViewport = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_draggingViewport || e.LeftButton != MouseButtonState.Pressed) return;
        MoveViewport(e.GetPosition(this).X, EffectiveExtent());
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_draggingViewport) return;
        _draggingViewport = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _draggingViewport = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        long delta = Math.Max(1, TickSpan / 8);
        StartTick = e.Delta > 0
            ? Math.Max(0, StartTick - delta)
            : SafeAdd(StartTick, delta);
        e.Handled = true;
    }

    private void MoveViewport(double pointerX, long extent)
    {
        double width = Math.Max(1, ActualWidth);
        Rect thumb = ViewportThumb(extent);
        double desiredLeft = Math.Clamp(pointerX - _dragOffset, 0, Math.Max(0, width - thumb.Width));
        long maximumStart = Math.Max(0, extent - Math.Min(TickSpan, extent));
        StartTick = maximumStart == 0
            ? 0
            : TimelineTickMath.ProjectFraction(0, maximumStart,
                desiredLeft / Math.Max(1, width - thumb.Width));
    }

    private Rect ViewportThumb(long extent)
    {
        double width = Math.Max(1, ActualWidth);
        long span = Math.Min(Math.Max(1, TickSpan), extent);
        double thumbWidth = Math.Clamp(width * span / extent, Math.Min(18, width), width);
        long maximumStart = Math.Max(0, extent - span);
        double left = maximumStart == 0
            ? 0
            : Math.Clamp(StartTick, 0, maximumStart) / (double)maximumStart * Math.Max(0, width - thumbWidth);
        return new Rect(left, 2, thumbWidth, Math.Max(1, ActualHeight - 4));
    }

    private long EffectiveExtent()
    {
        long viewportEnd = SafeAdd(Math.Max(0, StartTick), Math.Max(1, TickSpan));
        return Math.Max(1, Math.Max(ExtentEndTick, viewportEnd));
    }

    private long ContentExtent() => Math.Max(1, ExtentEndTick);

    private void RequestOverviewBuild(long extent)
    {
        int width = Math.Max(0, (int)Math.Ceiling(ActualWidth));
        TimelineRenderSnapshot? snapshot = Snapshot;
        ulong fingerprint = Snapshot?.ContentFingerprint ?? 0;
        if (_hasCachedOverview
            && fingerprint == _cachedContentFingerprint
            && extent == _cachedExtent
            && width == _cachedWidth)
        {
            return;
        }
        lock (_overviewBuildSync)
        {
            if (_requestedOverviewBuild is { } requested
                && requested.Fingerprint == fingerprint
                && requested.Extent == extent
                && requested.Width == width)
            {
                return;
            }
            long revision = checked(++_overviewBuildRevision);
            _requestedOverviewBuild = new(snapshot, fingerprint, extent, width, revision);
            if (_overviewBuildTask is null || _overviewBuildTask.IsCompleted)
                _overviewBuildTask = Task.Run(ProcessOverviewBuildQueue);
        }
    }

    private async Task ProcessOverviewBuildQueue()
    {
        while (true)
        {
            OverviewBuildRequest request;
            lock (_overviewBuildSync)
            {
                if (_requestedOverviewBuild is not { } pending)
                {
                    _overviewBuildTask = null;
                    return;
                }
                request = pending;
            }
            OverviewBuildResult? result = BuildOverview(request);
            if (result is not null && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() => PublishOverview(result),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            lock (_overviewBuildSync)
            {
                if (_requestedOverviewBuild?.Revision == request.Revision)
                {
                    _overviewBuildTask = null;
                    return;
                }
            }
        }
    }

    private static OverviewBuildResult? BuildOverview(OverviewBuildRequest request)
    {
        try
        {
            byte[] notes = new byte[request.Width];
            byte[] events = new byte[request.Width];
            int[] density = new int[request.Width];
            int maximumDensity = 0;
            if (request.Snapshot is not null && request.Width != 0)
            {
                if (request.Snapshot.HasDedicatedOverview)
                {
                    request.Snapshot.AccumulateOverviewChannels(
                        request.Extent,
                        notes,
                        events);
                }
                else
                {
                    request.Snapshot.AccumulateOverviewDensity(request.Extent, density);
                    for (int x = 0; x < density.Length; x++)
                        maximumDensity = Math.Max(maximumDensity, density[x]);
                }
            }
            return new(
                request,
                notes,
                events,
                density,
                maximumDensity,
                request.Snapshot?.HasDedicatedOverview == true);
        }
        catch (Exception)
        {
            // Overview rendering is cache-only presentation work. Keep the last
            // complete frame; semantic editing and hit testing remain independent.
            return null;
        }
    }

    private void PublishOverview(OverviewBuildResult result)
    {
        lock (_overviewBuildSync)
        {
            if (_requestedOverviewBuild?.Revision != result.Request.Revision) return;
        }
        _noteStartColumns = result.NoteStartColumns;
        _eventColumns = result.EventColumns;
        _density = result.Density;
        _maximumDensity = result.MaximumDensity;
        _cachedHasDedicatedOverview = result.HasDedicatedOverview;
        _hasCachedOverview = true;
        _cachedContentFingerprint = result.Request.Fingerprint;
        _cachedExtent = result.Request.Extent;
        _cachedWidth = result.Request.Width;
        InvalidateVisual();
    }

    private void DrawOverviewColumn(
        DrawingContext context,
        Brush brush,
        int sourceColumn,
        double projectedWidth,
        double lineHeight) => context.DrawRectangle(
            brush,
            null,
            new Rect(ProjectOverviewColumn(sourceColumn, projectedWidth), 2, 1, lineHeight));

    private double ProjectOverviewColumn(int sourceColumn, double projectedWidth) =>
        _cachedWidth <= 0 || projectedWidth <= 0
            ? 0
            : Math.Clamp(
                Math.Floor(sourceColumn / (double)_cachedWidth * projectedWidth),
                0,
                Math.Max(0, ActualWidth - 1));

    private sealed record OverviewBuildRequest(
        TimelineRenderSnapshot? Snapshot,
        ulong Fingerprint,
        long Extent,
        int Width,
        long Revision);

    private sealed record OverviewBuildResult(
        OverviewBuildRequest Request,
        byte[] NoteStartColumns,
        byte[] EventColumns,
        int[] Density,
        int MaximumDensity,
        bool HasDedicatedOverview);

    private static long SafeAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private void DrawCursor(DrawingContext context, long? tick, long extent, Pen? pen)
    {
        if (tick is not long value || value < 0 || value > extent || pen is null) return;
        double contentWidth = Math.Max(0, ActualWidth - 2);
        if (contentWidth <= 0) return;
        double rawX = 1 + value / (double)extent * contentWidth;
        double x = Math.Clamp(Math.Floor(rawX) + 0.5, 1.5, Math.Max(1.5, ActualWidth - 1.5));
        context.DrawLine(pen, new Point(x, 1), new Point(x, Math.Max(1, ActualHeight - 1)));
    }

    private void EnsurePens(Brush border, Brush info, Brush red)
    {
        if (ReferenceEquals(border, _cachedBorderBrush)
            && ReferenceEquals(info, _cachedInfoBrush)
            && ReferenceEquals(red, _cachedRedBrush)) return;
        _cachedBorderBrush = border;
        _cachedInfoBrush = info;
        _cachedRedBrush = red;
        _borderPen = new Pen(border, 1);
        _infoPen = new Pen(info, 1);
        _playbackCursorPen = new Pen(red, 1);
        _editCursorPen = new Pen(info, 1)
        {
            DashStyle = DashStyles.Dash
        };
        if (_borderPen.CanFreeze) _borderPen.Freeze();
        if (_infoPen.CanFreeze) _infoPen.Freeze();
        if (_playbackCursorPen.CanFreeze) _playbackCursorPen.Freeze();
        if (_editCursorPen.CanFreeze) _editCursorPen.Freeze();
    }

    private static Brush ResourceBrush(string key, Color fallback)
    {
        Brush value = Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(fallback);
        if (value.CanFreeze && !value.IsFrozen)
        {
            value = value.Clone();
            value.Freeze();
        }
        return value;
    }
}
