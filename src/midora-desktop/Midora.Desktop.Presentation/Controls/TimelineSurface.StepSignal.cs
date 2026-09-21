using System.Windows;
using System.Windows.Media;
using Midora.Domain;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Controls;

public sealed partial class TimelineSurface
{
    public static readonly DependencyProperty ShowStepSignalProperty = DependencyProperty.Register(
        nameof(ShowStepSignal), typeof(bool), typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
            static (owner, _) => ((TimelineSurface)owner).CancelStepSignalWork()));
    public bool ShowStepSignal
    {
        get => (bool)GetValue(ShowStepSignalProperty);
        set => SetValue(ShowStepSignalProperty, value);
    }

    private CancellationTokenSource? _stepSignalCancellation;

    private CancellationToken GetRasterRequestToken(TimelineRasterLayer layer)
    {
        if (layer == TimelineRasterLayer.EventSignal)
            return (_stepSignalCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(
                _rasterRequestCancellation.Token)).Token;
        return IsSelectionDependentRasterLayer(layer)
            ? _selectionRasterRequestCancellation.Token : _rasterRequestCancellation.Token;
    }

    private void CancelStepSignalWork()
    {
        _stepSignalCancellation?.Cancel();
        _stepSignalCancellation?.Dispose();
        _stepSignalCancellation = null;
        _pendingTileFingerprints.RemoveWhere(static key => key.Layer == TimelineRasterLayer.EventSignal);
        _requestedRasterKeys.RemoveWhere(static key => key.Layer == TimelineRasterLayer.EventSignal);
    }

    private bool MatchesRasterProjection(TimelineRenderSnapshot snapshot, TimelineRasterCacheKey key) =>
        key.Layer == TimelineRasterLayer.EventSignal
            ? ShowStepSignal && snapshot.StepSignalSource is { } source
                && key.ProjectionKey == snapshot.ProjectionKey + ":step:" + source.Identity
            : string.Equals(snapshot.ProjectionKey, key.ProjectionKey, StringComparison.Ordinal);

    private void DrawStepSignal(DrawingContext context, TimelineViewport viewport, double header, double ruler)
    {
        if (!ShowStepSignal || Snapshot is not { StepSignalSource: { } source } snapshot) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int dx = (int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero);
        int dy = (int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero);
        double rasterX = dx / 1024d, rasterY = dy / 1024d;
        double ppt = viewport.PixelsPerTick * dpi.DpiScaleX;
        double ppv = Math.Max(1, ActualHeight - ruler) * dpi.DpiScaleY / Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        long hx = BitConverter.DoubleToInt64Bits(ppt), vy = BitConverter.DoubleToInt64Bits(ppv);
        BeginExactRasterProjection(hx, vy);
        const int size = TimelineEventPointTileRasterizer.TileSize;
        long firstX = Math.Max(0, FloorToLong(viewport.StartTick * ppt / size));
        long lastX = Math.Max(firstX, FloorToLong(Math.Max(viewport.StartTick, viewport.EndTick - 1) * ppt / size));
        long firstY = Math.Max(0, FloorToLong((1 - _valueViewMaximum) * ppv / size));
        long lastY = Math.Max(firstY, FloorToLong(Math.BitDecrement((1 - _valueViewMinimum) * ppv) / size));
        Color color = GetSolidColor(Brush("Brush.Info", Color.FromRgb(98, 166, 246)), Color.FromRgb(98, 166, 246));
        color.A = 120;
        // SubVoice reuses one Surface/projection name for its different targets.
        // Range fingerprints describe the source root, so target identity must
        // also be part of both preparation and completed-bitmap cache keys.
        string projection = snapshot.ProjectionKey + ":step:" + source.Identity;
        context.PushClip(new RectangleGeometry(new Rect(header, ruler,
            Math.Max(0, Math.Min(ActualWidth - header, (source.EndTick - (double)viewport.StartTick) * viewport.PixelsPerTick)),
            Math.Max(0, ActualHeight - ruler))));
        var token = GetRasterRequestToken(TimelineRasterLayer.EventSignal);
        DrawingGroup signal = new();
        using (DrawingContext signalContext = signal.Open())
        {
            foreach (long y in TimelineTickMath.InclusiveIndices(firstY, lastY))
            foreach (long x in TimelineTickMath.InclusiveIndices(firstX, lastX))
            {
                long tileX = x, tileY = y;
                var preparation = new TileFingerprintRequestKey(TimelineRasterLayer.EventSignal,
                    snapshot.SemanticRevision, projection, -1, hx, vy, tileX, tileY, dx, dy);
                if (!TryGetPreparedTileFingerprint(preparation, snapshot,
                    () => TimelineStepSignalRasterizer.Fingerprint(source, ppt, tileX, rasterX, token), false, out ulong fingerprint)) continue;
                var key = new TimelineRasterCacheKey(TimelineRasterLayer.EventSignal, projection,
                    fingerprint, hx, vy, tileX, tileY, ColorToArgb(color), 0, 0, dx, dy);
                if (TimelineRasterCache.Shared.TryGet(key, out var bitmap) && bitmap is not null)
                    DrawEventPointTile(signalContext, viewport, new(key, bitmap), ppt, ppv, header, ruler, rasterX, rasterY);
                else RequestRaster(key, cancellation => TimelineStepSignalRasterizer.Rasterize(source, ppt, ppv,
                    tileX, tileY, rasterX, rasterY, color, cancellation), TimelineRasterRequestPriority.Normal);
            }
        }
        if (RangeStartTick is long rangeStart && RangeEndTick is long rangeEnd && rangeEnd > rangeStart)
        {
            double left = Math.Clamp(header + viewport.TickToX(rangeStart), header, ActualWidth);
            double right = Math.Clamp(header + viewport.TickToX(rangeEnd), left, ActualWidth);
            Present(header, left, .4); Present(left, right, 1); Present(right, ActualWidth, .4);
        }
        else context.DrawDrawing(signal);
        context.Pop();

        void Present(double left, double right, double opacity)
        {
            if (right <= left) return;
            context.PushClip(new RectangleGeometry(new Rect(left, ruler, right - left, Math.Max(0, ActualHeight - ruler))));
            context.PushOpacity(opacity); context.DrawDrawing(signal); context.Pop(); context.Pop();
        }
    }
}
