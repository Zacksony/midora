using System.Windows;
using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Controls;

public sealed partial class TimelineSurface
{
    public static readonly DependencyProperty OnionSnapshotProperty = DependencyProperty.Register(
        nameof(OnionSnapshot), typeof(TimelineOnionSnapshot), typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            static (sender, _) => ((TimelineSurface)sender).CancelOnionRequests()));
    public TimelineOnionSnapshot? OnionSnapshot
    {
        get => (TimelineOnionSnapshot?)GetValue(OnionSnapshotProperty);
        set => SetValue(OnionSnapshotProperty, value);
    }
    public static readonly DependencyProperty OnionIsPrimaryLayerProperty = DependencyProperty.Register(
        nameof(OnionIsPrimaryLayer), typeof(bool), typeof(TimelineSurface), new PropertyMetadata(false));
    public bool OnionIsPrimaryLayer
    {
        get => (bool)GetValue(OnionIsPrimaryLayerProperty);
        set => SetValue(OnionIsPrimaryLayerProperty, value);
    }
    private CancellationTokenSource _onionCancellation = new();
    private TimelineOnionSnapshot? _onionSourceSnapshot;
    private TimelineOnionSnapshot? _onionDrawSnapshot;
    private int _onionMaximumBlocks;
    public int OnionMissingTileCount { get; private set; }
    private (long Start, long End, int First, double Height, double Width) _onionViewport;
    private void CancelOnionRequests()
    {
        _onionCancellation.Cancel();
        _onionCancellation.Dispose();
        _onionCancellation = new();
        // These are source-bearing projections, not the independent bitmap LRU.
        // A disabled/hidden layer must not retain the previous Project graph.
        _onionSourceSnapshot = null;
        _onionDrawSnapshot = null;
        _onionMaximumBlocks = 0;
        OnionMissingTileCount = 0;
    }
    private void DrawOnion(DrawingContext context, TimelineViewport viewport, double header, double ruler)
    {
        OnionMissingTileCount = 0;
        if (_backgroundWorkSuspended) return;
        if (OnionSnapshot is not { Opacity: > 0 } snapshot || snapshot.Blocks.Count == 0)
        {
            if (_onionSourceSnapshot is not null) CancelOnionRequests();
            return;
        }
        var metrics = (viewport.StartTick, viewport.EndTick, viewport.FirstLane, viewport.LaneHeight, viewport.Width);
        if (_onionViewport != metrics || _onionCancellation.IsCancellationRequested)
        { CancelOnionRequests(); _onionViewport = metrics; }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double sx = viewport.PixelsPerTick * dpi.DpiScaleX;
        double sy = viewport.LaneHeight * dpi.DpiScaleY;
        if (!double.IsFinite(sx) || sx <= 0 || !double.IsFinite(sy) || sy <= 0) return;
        // A strip stores exactly one row per key, then scales vertically with nearest-neighbor.
        // Query each source once for all keys instead of decoding the same dense pages again
        // for every vertical tile. This is an independent ghost layer, never the editable notes.
        double visibleTileBytes = (Math.Ceiling(viewport.Width * dpi.DpiScaleX / TimelineOnionRasterizer.Width) + 2)
            * TimelineOnionRasterizer.Width * TimelineOnionRasterizer.Height * 4;
        int maximumBlocks = Math.Max(1, (int)Math.Min(1024, 64L * 1024 * 1024 / visibleTileBytes));
        if (!ReferenceEquals(snapshot, _onionSourceSnapshot) || maximumBlocks != _onionMaximumBlocks)
        {
            _onionSourceSnapshot = snapshot; _onionMaximumBlocks = maximumBlocks;
            _onionDrawSnapshot = snapshot.FitVisibleCacheBudget(maximumBlocks);
        }
        snapshot = _onionDrawSnapshot!;
        long x0 = (long)Math.Floor(viewport.StartTick * sx / TimelineOnionRasterizer.Width);
        long x1 = (long)Math.Floor(viewport.EndTick * sx / TimelineOnionRasterizer.Width);
        context.PushClip(new RectangleGeometry(new Rect(header, ruler, viewport.Width, viewport.Height)));
        // Each block has its own key: edits cannot invalidate other track blocks.
        for (int block = 0; block < snapshot.Blocks.Count; block++)
        for (long x = x0; x <= x1; x++)
        {
            long left = (long)Math.Clamp(Math.Floor(x * (double)TimelineOnionRasterizer.Width / sx) - 1, 0, long.MaxValue);
            long right = (long)Math.Clamp(Math.Ceiling((x + 1d) * TimelineOnionRasterizer.Width / sx) + 1, 0, long.MaxValue);
            var key = new TimelineRasterCacheKey(TimelineRasterLayer.Onion,
                $"{snapshot.Identity}:block:{block}", snapshot.Fingerprint(block, left, right, 0, 128),
                BitConverter.DoubleToInt64Bits(sx), 1, x, 0, 0, 0, 0,
                (int)Math.Round(dpi.DpiScaleX * 96), (int)Math.Round(dpi.DpiScaleY * 96));
            if (TimelineRasterCache.Shared.TryGet(key, out var bitmap))
            {
                var drawing = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
                drawing.Children.Add(new ImageDrawing(bitmap, new Rect(
                    header + (x * (double)TimelineOnionRasterizer.Width - viewport.StartTick * sx) / dpi.DpiScaleX,
                    ruler - viewport.FirstLane * sy / dpi.DpiScaleY,
                    TimelineOnionRasterizer.Width / dpi.DpiScaleX, 128 * sy / dpi.DpiScaleY)));
                drawing.Freeze(); context.DrawDrawing(drawing);
            }
            else
            {
                OnionMissingTileCount++;
                int capturedBlock = block; long capturedX = x;
                TimelineRasterCache.Shared.Request(key,
                    token => TimelineOnionRasterizer.Render(snapshot, capturedBlock, capturedX, 0, sx, 1, token),
                    Dispatcher, InvalidateVisual, _onionCancellation.Token,
                    OnionIsPrimaryLayer ? TimelineRasterRequestPriority.Visible : TimelineRasterRequestPriority.Normal,
                    consumerId: _rasterConsumerId);
            }
        }
        context.Pop();
    }
}
