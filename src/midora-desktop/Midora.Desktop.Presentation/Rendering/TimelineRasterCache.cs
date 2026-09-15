using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

internal enum TimelineRasterLayer
{
    ArrangementSegmentPreview,
    ArrangementSegmentNotePreview,
    ArrangementSegmentEventPreview,
    ArrangementConductorPreview,
    ConductorMeta,
    PianoNotes,
    PianoSelection,
    PianoDragPreview,
    ResizePreview,
    VelocityBars,
    EventPoints,
    EventPointSelection,
    Onion
}

internal readonly record struct TimelineRasterCacheKey(
    TimelineRasterLayer Layer,
    string ProjectionKey,
    ulong ContentFingerprint,
    long HorizontalScaleKey,
    long VerticalScaleKey,
    long TileX,
    long TileY,
    uint NormalColor,
    uint WarningColor,
    uint OutlineColor,
    int DpiX,
    int DpiY);

public sealed class TimelineRasterBuffer
{
    public TimelineRasterBuffer(int width, int height, byte[] pixels, int candidateCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The Pbgra32 payload length does not match its dimensions.", nameof(pixels));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        Width = width;
        Height = height;
        Pixels = pixels;
        CandidateCount = candidateCount;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public byte[] Pixels { get; }
    public int CandidateCount { get; }
    public long ByteSize => Pixels.LongLength;

    internal BitmapSource CreateFrozenBitmap()
    {
        BitmapSource bitmap = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            Pixels,
            Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

public static class TimelineRasterLod
{
    private const int LevelsPerOctave = 2;

    public static int Quantize(double devicePixelsPerUnit)
    {
        if (!double.IsFinite(devicePixelsPerUnit) || devicePixelsPerUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerUnit));
        }
        return checked((int)Math.Round(
            Math.Log2(devicePixelsPerUnit) * LevelsPerOctave,
            MidpointRounding.AwayFromZero));
    }

    public static double GetScale(int level) => Math.Pow(2, level / (double)LevelsPerOctave);
}

public static class TimelineRasterPlacement
{
    public static void BuildUncoveredHorizontalGaps(
        Rect visibleBounds,
        IReadOnlyList<Rect> coveredBounds,
        List<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(coveredBounds);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (visibleBounds.IsEmpty
            || !double.IsFinite(visibleBounds.Left)
            || !double.IsFinite(visibleBounds.Top)
            || !double.IsFinite(visibleBounds.Width)
            || !double.IsFinite(visibleBounds.Height)
            || visibleBounds.Width <= 0
            || visibleBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleBounds));
        }

        double cursor = visibleBounds.Left;
        foreach (Rect covered in coveredBounds)
        {
            if (covered.IsEmpty || covered.Width <= 0) continue;
            double coveredLeft = Math.Clamp(
                covered.Left,
                visibleBounds.Left,
                visibleBounds.Right);
            double coveredRight = Math.Clamp(
                covered.Right,
                visibleBounds.Left,
                visibleBounds.Right);
            if (coveredRight <= cursor) continue;
            if (coveredLeft > cursor)
            {
                destination.Add(new(
                    cursor,
                    visibleBounds.Top,
                    coveredLeft - cursor,
                    visibleBounds.Height));
            }
            cursor = Math.Max(cursor, coveredRight);
            if (cursor >= visibleBounds.Right) return;
        }
        if (cursor < visibleBounds.Right)
        {
            destination.Add(new(
                cursor,
                visibleBounds.Top,
                visibleBounds.Right - cursor,
                visibleBounds.Height));
        }
    }

    public static Rect GetUnclippedItemBounds(
        TimelineViewport viewport,
        TimelineRenderItem item,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }
        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double top = rulerHeight + (item.Lane - viewport.FirstLane) * laneHeight;
        return new(left, top, Math.Max(1, right - left), laneHeight);
    }

    public static Rect GetSegmentPreviewTileDestination(
        Rect fullSegmentBounds,
        long contentWidth,
        long tileLeft,
        int tileWidth,
        DpiScale dpi)
    {
        if (!fullSegmentBounds.IsEmpty
            && double.IsFinite(fullSegmentBounds.Left)
            && double.IsFinite(fullSegmentBounds.Top)
            && double.IsFinite(fullSegmentBounds.Width)
            && double.IsFinite(fullSegmentBounds.Height)
            && fullSegmentBounds.Width > 0
            && fullSegmentBounds.Height > 0
            && contentWidth > 0
            && tileLeft >= 0
            && tileWidth > 0
            && tileLeft <= contentWidth - tileWidth
            && double.IsFinite(dpi.DpiScaleX)
            && dpi.DpiScaleX > 0
            && double.IsFinite(dpi.DpiScaleY)
            && dpi.DpiScaleY > 0)
        {
            double fullLeftDevice = Math.Round(
                fullSegmentBounds.Left * dpi.DpiScaleX,
                MidpointRounding.AwayFromZero);
            double fullTopDevice = Math.Round(
                fullSegmentBounds.Top * dpi.DpiScaleY,
                MidpointRounding.AwayFromZero);
            double fullWidthDevice = Math.Max(
                1,
                Math.Round(
                    fullSegmentBounds.Width * dpi.DpiScaleX,
                    MidpointRounding.AwayFromZero));
            double fullHeightDevice = Math.Max(
                1,
                Math.Round(
                    fullSegmentBounds.Height * dpi.DpiScaleY,
                    MidpointRounding.AwayFromZero));
            double tileLeftDevice = fullLeftDevice + Math.Round(
                tileLeft / (double)contentWidth * fullWidthDevice,
                MidpointRounding.AwayFromZero);
            double tileRightDevice = fullLeftDevice + Math.Round(
                (tileLeft + tileWidth) / (double)contentWidth * fullWidthDevice,
                MidpointRounding.AwayFromZero);
            tileRightDevice = Math.Max(tileLeftDevice + 1, tileRightDevice);
            return new(
                tileLeftDevice / dpi.DpiScaleX,
                fullTopDevice / dpi.DpiScaleY,
                (tileRightDevice - tileLeftDevice) / dpi.DpiScaleX,
                fullHeightDevice / dpi.DpiScaleY);
        }
        throw new ArgumentOutOfRangeException(nameof(fullSegmentBounds));
    }

    public static Rect GetPianoTileDestination(
        TimelineViewport viewport,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
        => GetPianoTileDestination(
            viewport,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            laneHeaderWidth,
            rulerHeight,
            laneHeight);

    public static Rect GetPianoTileDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0
            || !double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0
            || !double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }

        double dpiScaleX = devicePixelsPerTick / viewport.PixelsPerTick;
        double dpiScaleY = devicePixelsPerLane / laneHeight;
        double tileDeviceX = tileX * TimelinePianoTileRasterizer.TileSize
            - TimelinePianoTileRasterizer.Gutter;
        double tileDeviceY = tileY * TimelinePianoTileRasterizer.TileSize
            - TimelinePianoTileRasterizer.Gutter;
        return new(
            laneHeaderWidth + (tileDeviceX - viewport.StartTick * devicePixelsPerTick) / dpiScaleX,
            rulerHeight + (tileDeviceY - viewport.FirstLane * devicePixelsPerLane) / dpiScaleY,
            TimelinePianoTileRasterizer.RasterSize / dpiScaleX,
            TimelinePianoTileRasterizer.RasterSize / dpiScaleY);
    }

    public static Rect GetPianoTileCoreDestination(
        TimelineViewport viewport,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
        => GetPianoTileCoreDestination(
            viewport,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            laneHeaderWidth,
            rulerHeight,
            laneHeight);

    public static Rect GetPianoTileCoreDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0
            || !double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0
            || !double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }

        double dpiScaleX = devicePixelsPerTick / viewport.PixelsPerTick;
        double dpiScaleY = devicePixelsPerLane / laneHeight;
        double tileDeviceX = tileX * TimelinePianoTileRasterizer.TileSize;
        double tileDeviceY = tileY * TimelinePianoTileRasterizer.TileSize;
        return new(
            laneHeaderWidth + (tileDeviceX - viewport.StartTick * devicePixelsPerTick) / dpiScaleX,
            rulerHeight + (tileDeviceY - viewport.FirstLane * devicePixelsPerLane) / dpiScaleY,
            TimelinePianoTileRasterizer.TileSize / dpiScaleX,
            TimelinePianoTileRasterizer.TileSize / dpiScaleY);
    }

    public static Rect GetVelocityTileDestination(
        TimelineViewport viewport,
        int horizontalLod,
        long tileX,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        viewport.Validate();
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double tileTick = (tileX * TimelineVelocityTileRasterizer.TileSize
            - TimelineVelocityTileRasterizer.Gutter) / lodPixelsPerTick;
        return new(
            laneHeaderWidth + (tileTick - viewport.StartTick) * viewport.PixelsPerTick,
            valueTop,
            TimelineVelocityTileRasterizer.RasterWidth / lodPixelsPerTick * viewport.PixelsPerTick,
            valueBottom - valueTop);
    }

    public static Rect GetVelocityTileCoreDestination(
        TimelineViewport viewport,
        int horizontalLod,
        long tileX,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double tileTick = tileX * TimelineVelocityTileRasterizer.TileSize / lodPixelsPerTick;
        return new(
            laneHeaderWidth + (tileTick - viewport.StartTick) * viewport.PixelsPerTick,
            valueTop,
            TimelineVelocityTileRasterizer.TileSize / lodPixelsPerTick * viewport.PixelsPerTick,
            valueBottom - valueTop);
    }

    public static Rect GetEventPointTileDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidateEventPointPlacement(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            valueMinimum,
            valueMaximum,
            dpiScaleX,
            dpiScaleY);
        int gutterX = TimelineEventPointTileRasterizer.GetGutter(dpiScaleX);
        int gutterY = TimelineEventPointTileRasterizer.GetGutter(dpiScaleY);
        double valueRange = valueMaximum - valueMinimum;
        double contentHeight = Math.Max(1, viewport.Height);
        double worldLeft = tileX * TimelineEventPointTileRasterizer.TileSize - gutterX;
        double worldTop = tileY * TimelineEventPointTileRasterizer.TileSize - gutterY;
        double sourceTopValue = 1 - worldTop / devicePixelsPerValue;
        return new(
            laneHeaderWidth
                + (worldLeft / devicePixelsPerTick - viewport.StartTick) * viewport.PixelsPerTick,
            rulerHeight + (valueMaximum - sourceTopValue) / valueRange * contentHeight,
            TimelineEventPointTileRasterizer.GetRasterSize(dpiScaleX)
                / devicePixelsPerTick * viewport.PixelsPerTick,
            TimelineEventPointTileRasterizer.GetRasterSize(dpiScaleY)
                / devicePixelsPerValue / valueRange * contentHeight);
    }

    public static Rect GetEventPointTileCoreDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidateEventPointPlacement(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            valueMinimum,
            valueMaximum,
            dpiScaleX,
            dpiScaleY);
        double valueRange = valueMaximum - valueMinimum;
        double contentHeight = Math.Max(1, viewport.Height);
        double worldLeft = tileX * TimelineEventPointTileRasterizer.TileSize;
        double worldTop = tileY * TimelineEventPointTileRasterizer.TileSize;
        double sourceTopValue = 1 - worldTop / devicePixelsPerValue;
        return new(
            laneHeaderWidth
                + (worldLeft / devicePixelsPerTick - viewport.StartTick) * viewport.PixelsPerTick,
            rulerHeight + (valueMaximum - sourceTopValue) / valueRange * contentHeight,
            TimelineEventPointTileRasterizer.TileSize
                / devicePixelsPerTick * viewport.PixelsPerTick,
            TimelineEventPointTileRasterizer.TileSize
                / devicePixelsPerValue / valueRange * contentHeight);
    }

    private static void ValidateEventPointPlacement(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        viewport.Validate();
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        if (!double.IsFinite(devicePixelsPerValue) || devicePixelsPerValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerValue));
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(laneHeaderWidth));
        if (!double.IsFinite(rulerHeight) || rulerHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(rulerHeight));
        if (!double.IsFinite(valueMinimum) || valueMinimum < 0 || valueMinimum >= 1)
            throw new ArgumentOutOfRangeException(nameof(valueMinimum));
        if (!double.IsFinite(valueMaximum)
            || valueMaximum > 1
            || valueMaximum <= valueMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(valueMaximum));
        }
        if (!double.IsFinite(dpiScaleX) || dpiScaleX <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScaleX));
        if (!double.IsFinite(dpiScaleY) || dpiScaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScaleY));
    }
}

public readonly struct TimelinePianoTileRasterRequest
{
    public TimelinePianoTileRasterRequest(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        Color? normalOutlineColor = null)
        : this(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            normalColor,
            warningColor,
            normalOutlineColor)
    {
    }

    public TimelinePianoTileRasterRequest(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        Color? normalOutlineColor = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerLane));
        }
        Snapshot = snapshot;
        DevicePixelsPerTick = devicePixelsPerTick;
        DevicePixelsPerLane = devicePixelsPerLane;
        TileX = tileX;
        TileY = tileY;
        NormalColor = normalColor;
        WarningColor = warningColor;
        NormalOutlineColor = normalOutlineColor;
    }

    public TimelineRenderSnapshot Snapshot { get; }
    public double DevicePixelsPerTick { get; }
    public double DevicePixelsPerLane { get; }
    public long TileX { get; }
    public long TileY { get; }
    public Color NormalColor { get; }
    public Color WarningColor { get; }
    public Color? NormalOutlineColor { get; }

    public TimelineRasterBuffer Rasterize() => TimelinePianoTileRasterizer.Rasterize(
        Snapshot,
        DevicePixelsPerTick,
        DevicePixelsPerLane,
        TileX,
        TileY,
        NormalColor,
        WarningColor,
        normalOutlineColor: NormalOutlineColor);

    public TimelineRasterBuffer Rasterize(CancellationToken cancellationToken) =>
        TimelinePianoTileRasterizer.Rasterize(
            Snapshot,
            DevicePixelsPerTick,
            DevicePixelsPerLane,
            TileX,
            TileY,
            NormalColor,
            WarningColor,
            normalOutlineColor: NormalOutlineColor,
            cancellationToken: cancellationToken);
}

public static class TimelinePianoTileRasterizer
{
    // Below this scale a single raster column covers enough ticks that exact
    // object visitation makes tile cost proportional to project density.  The
    // immutable presentation sources expose a conservative fixed-width
    // occupancy envelope for this case.  Hit testing and editing never consume
    // this raster-only path.
    private const double AggregatePixelsPerTickThreshold = 0.125;
    public const int TileSize = 256;
    public const int Gutter = 1;
    public const int RasterSize = TileSize + Gutter * 2;

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY)
        => ComputeContentFingerprint(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY);

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryGetTileBounds(
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY,
                out long startTick,
                out long endTick,
                out int firstLane,
                out int lastLaneExclusive))
        {
            return TimelineContentFingerprint.ForPianoTileItems([]);
        }
        List<TimelineRenderItem> materialized = [];
        snapshot.QueryMaterializedInto(
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive,
            materialized);
        return TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.ForPianoTileItems(materialized),
            snapshot.GetExternalRangeFingerprint(
                startTick,
                endTick,
                firstLane,
                lastLaneExclusive));
    }

    public static ulong ComputeSelectionFingerprint(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);
        if (!TryGetTileBounds(
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY,
                out long startTick,
                out long endTick,
                out int firstLane,
                out int lastLaneExclusive))
        {
            return TimelineContentFingerprint.ForLocalSelection([], selection);
        }
        if (selection.HasRenderIndex)
        {
            // Once the complete immutable selection geometry is available, its
            // tile-local fingerprint avoids touching the dense base source.
            return selection.GetRangeContentFingerprint(
                startTick,
                endTick,
                firstLane,
                lastLaneExclusive);
        }

        // A large formal selection is published before its complete, potentially
        // out-of-core render index has finished.  Visible tiles must not remain
        // unselected while unrelated offscreen IDs are being resolved.  The
        // regular piano fingerprint is range-local and uses prepared page
        // summaries for external content; combine it with the immutable formal
        // selection generation and let the selection-only raster pass filter the
        // visible source range by ID.
        ulong sourceFingerprint = snapshot.GetPianoTileContentFingerprint(
            devicePixelsPerTick,
            devicePixelsPerLane,
            tileX,
            tileY);
        ulong selectionGeneration = TimelineContentFingerprint.Combine(
            unchecked((ulong)selection.Revision),
            unchecked((ulong)selection.Count));
        return TimelineContentFingerprint.Combine(selectionGeneration, sourceFingerprint);
    }

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        TimelineSelectionSnapshot? selection = null,
        bool selectionOnly = false,
        Color? outlineColor = null,
        Color? normalOutlineColor = null,
        CancellationToken cancellationToken = default)
        => Rasterize(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            normalColor,
            warningColor,
            selection,
            selectionOnly,
            outlineColor,
            normalOutlineColor,
            cancellationToken);

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        TimelineSelectionSnapshot? selection = null,
        bool selectionOnly = false,
        Color? outlineColor = null,
        Color? normalOutlineColor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryGetTileBounds(
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY,
                out long startTick,
                out long endTick,
                out int firstLane,
                out int lastLaneExclusive))
        {
            return new(RasterSize, RasterSize, new byte[RasterSize * RasterSize * 4]);
        }

        double worldLeft = tileX * (double)TileSize - Gutter;
        double worldTop = tileY * (double)TileSize - Gutter;
        byte[] pixels = new byte[RasterSize * RasterSize * 4];
        int candidateCount = 0;
        if (!selectionOnly
            && selection is null
            && devicePixelsPerTick <= AggregatePixelsPerTickThreshold)
        {
            TimelineRasterColumnSummary[] summaries =
                new TimelineRasterColumnSummary[RasterSize];
            var projection = new TimelineRasterColumnProjection(
                startTick,
                endTick,
                startTick,
                -(worldLeft / devicePixelsPerTick - startTick) * devicePixelsPerTick,
                devicePixelsPerTick,
                RasterSize);
            if (snapshot.TryAccumulateRasterColumns(
                    TimelineRasterAggregateKind.PianoNotes,
                    projection,
                    firstLane,
                    lastLaneExclusive,
                    summaries,
                    out int sourceWorkCount,
                    cancellationToken))
            {
                DrawAggregate(summaries);
                cancellationToken.ThrowIfCancellationRequested();
                return new(RasterSize, RasterSize, pixels, sourceWorkCount);
            }
        }
        if (selectionOnly)
        {
            if (selection?.HasRenderIndex == true)
            {
                selection.VisitRenderItems(
                    startTick,
                    endTick,
                    firstLane,
                    lastLaneExclusive,
                    Draw);
            }
            else
            {
                // Small or directly-constructed selections may not need an
                // auxiliary selection index. Preserve the exact legacy path
                // for those snapshots while large paged selections use the
                // selection-owned index above and never scan the dense source.
                snapshot.VisitInto(
                    startTick,
                    endTick,
                    firstLane,
                    lastLaneExclusive,
                    Draw);
            }
        }
        else
        {
            snapshot.VisitInto(
                startTick,
                endTick,
                firstLane,
                lastLaneExclusive,
                Draw);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(RasterSize, RasterSize, pixels, candidateCount);

        void DrawAggregate(ReadOnlySpan<TimelineRasterColumnSummary> summaries)
        {
            Color outline = normalOutlineColor ?? Darken(normalColor);
            for (int lane = firstLane; lane < lastLaneExclusive; lane++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rawTop = RoundPixelBoundary(
                    lane * devicePixelsPerLane - worldTop);
                int rawBottom = Math.Max(
                    rawTop + 1,
                    RoundPixelBoundary((lane + 1d) * devicePixelsPerLane - worldTop));
                int top = Math.Clamp(rawTop, 0, RasterSize);
                int bottom = Math.Clamp(rawBottom, 0, RasterSize);
                if (bottom <= top) continue;

                int runStart = -1;
                for (int column = 0; column <= summaries.Length; column++)
                {
                    bool occupied = column < summaries.Length && (lane < 64
                        ? (summaries[column].LaneMaskLow & (1UL << lane)) != 0
                        : (summaries[column].LaneMaskHigh & (1UL << (lane - 64))) != 0);
                    if (occupied)
                    {
                        if (runStart < 0) runStart = column;
                        continue;
                    }
                    if (runStart < 0) continue;

                    FillRectangle(
                        pixels,
                        RasterSize,
                        runStart,
                        top,
                        column,
                        bottom,
                        normalColor,
                        0.78);
                    DrawRectangleOutline(
                        pixels,
                        RasterSize,
                        runStart,
                        top,
                        column,
                        bottom,
                        outline,
                        0.82,
                        drawLeft: false,
                        drawTop: rawTop >= 0,
                        drawRight: false,
                        drawBottom: rawBottom <= RasterSize);
                    for (int boundaryColumn = runStart;
                         boundaryColumn < column;
                         boundaryColumn++)
                    {
                        bool startsHere = lane < 64
                            ? (summaries[boundaryColumn].StartLaneMaskLow
                                & (1UL << lane)) != 0
                            : (summaries[boundaryColumn].StartLaneMaskHigh
                                & (1UL << (lane - 64))) != 0;
                        bool endsHere = lane < 64
                            ? (summaries[boundaryColumn].EndLaneMaskLow
                                & (1UL << lane)) != 0
                            : (summaries[boundaryColumn].EndLaneMaskHigh
                                & (1UL << (lane - 64))) != 0;
                        if (!startsHere && !endsHere) continue;
                        FillRectangle(
                            pixels,
                            RasterSize,
                            boundaryColumn,
                            top,
                            boundaryColumn + 1,
                            bottom,
                            outline,
                            0.82);
                    }
                    runStart = -1;
                }
            }
        }

        void Draw(TimelineRenderItem item)
        {
            if (item.Kind is not (TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote))
            {
                return;
            }
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            if (selectionOnly && !selected)
            {
                return;
            }

            if (candidateCount < int.MaxValue) candidateCount++;
            if ((candidateCount & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int rawLeft = RoundPixelBoundary(item.StartTick * devicePixelsPerTick - worldLeft);
            int rawRight = Math.Max(
                rawLeft + 1,
                RoundPixelBoundary(item.EndTick * devicePixelsPerTick - worldLeft));
            int rawTop = RoundPixelBoundary(
                item.Lane * devicePixelsPerLane - worldTop);
            int rawBottom = Math.Max(
                rawTop + 1,
                RoundPixelBoundary(
                    (item.Lane + 1d) * devicePixelsPerLane - worldTop));
            int left = Math.Clamp(rawLeft, 0, RasterSize);
            int right = Math.Clamp(rawRight, 0, RasterSize);
            int top = Math.Clamp(rawTop, 0, RasterSize);
            int bottom = Math.Clamp(rawBottom, 0, RasterSize);
            if (right <= left || bottom <= top)
            {
                return;
            }

            bool warning = item.State.HasFlag(TimelineItemState.Invalid)
                || item.State.HasFlag(TimelineItemState.Broken);
            Color color = warning
                ? warningColor
                : normalColor;
            double opacity = selectionOnly
                ? item.State.HasFlag(TimelineItemState.OutsideActiveRange) ? 0.7 : 0.94
                : item.State.HasFlag(TimelineItemState.OutsideActiveRange) ? 0.35 : 0.78;
            if (color.A != 0)
            {
                FillRectangle(pixels, RasterSize, left, top, right, bottom, color, opacity);
            }
            DrawRectangleOutline(
                pixels,
                RasterSize,
                left,
                top,
                right,
                bottom,
                outlineColor ?? (!warning && normalOutlineColor.HasValue
                    ? normalOutlineColor.Value
                    : Darken(color)),
                selectionOnly ? 1 : 0.82,
                drawLeft: rawLeft >= 0,
                drawTop: rawTop >= 0,
                drawRight: rawRight <= RasterSize,
                drawBottom: rawBottom <= RasterSize);
        }
    }

    private static bool TryGetTileBounds(
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        out long startTick,
        out long endTick,
        out int firstLane,
        out int lastLaneExclusive)
    {
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerLane));
        }
        double worldLeft = tileX * (double)TileSize - Gutter;
        double worldTop = tileY * (double)TileSize - Gutter;
        if (worldLeft + RasterSize <= 0 || worldTop + RasterSize <= 0)
        {
            startTick = 0;
            endTick = 1;
            firstLane = 0;
            lastLaneExclusive = 1;
            return false;
        }

        startTick = TimelineTickMath.RasterQueryStart(worldLeft / devicePixelsPerTick);
        endTick = TimelineTickMath.RasterQueryEnd((worldLeft + RasterSize) / devicePixelsPerTick, startTick);
        firstLane = Math.Max(0, FloorToInt(worldTop / devicePixelsPerLane));
        lastLaneExclusive = Math.Min(
            128,
            Math.Max(
                firstLane + 1,
                CeilingToInt((worldTop + RasterSize) / devicePixelsPerLane)));
        return firstLane < 128;
    }

    private static int RoundPixelBoundary(double value) => TimelineTickMath.RoundRasterBoundary(value);

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);

    private static int FloorToInt(double value) => value <= int.MinValue
        ? int.MinValue
        : value >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Floor(value);

    private static int CeilingToInt(double value) => value <= int.MinValue
        ? int.MinValue
        : value >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling(value);

    internal static void FillRectangle(
        byte[] pixels,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        Color color,
        double opacity)
    {
        byte alpha = (byte)Math.Clamp(
            (int)Math.Round(color.A * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero),
            0,
            255);
        byte blue = Premultiply(color.B, alpha);
        byte green = Premultiply(color.G, alpha);
        byte red = Premultiply(color.R, alpha);
        for (int y = top; y < bottom; y++)
        {
            int offset = checked((y * width + left) * 4);
            for (int x = left; x < right; x++, offset += 4)
            {
                if (pixels[offset + 3] > alpha)
                {
                    continue;
                }
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }
        }
    }

    internal static void DrawRectangleOutline(
        byte[] pixels,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        Color color,
        double opacity,
        bool drawLeft = true,
        bool drawTop = true,
        bool drawRight = true,
        bool drawBottom = true)
    {
        if (right <= left || bottom <= top) return;
        if (drawTop)
            FillRectangle(pixels, width, left, top, right, Math.Min(top + 1, bottom), color, opacity);
        if (drawBottom)
            FillRectangle(pixels, width, left, Math.Max(top, bottom - 1), right, bottom, color, opacity);
        if (drawLeft)
            FillRectangle(pixels, width, left, top, Math.Min(left + 1, right), bottom, color, opacity);
        if (drawRight)
            FillRectangle(pixels, width, Math.Max(left, right - 1), top, right, bottom, color, opacity);
    }

    private static Color Darken(Color color) => Color.FromArgb(
        color.A,
        (byte)(color.R * 0.42),
        (byte)(color.G * 0.42),
        (byte)(color.B * 0.42));

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);
}

public static class TimelineSegmentPreviewRasterizer
{
    private const ulong FingerprintOffset = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;
    public const int TileSize = 256;
    public const int FixedPreviewTileSize = 256;
    public const int FixedPreviewPixelsPerQuarterNote = 96;
    public const int FixedPreviewLodLevelsPerOctave = 2;
    public const int MaximumFixedPreviewLod = 124;
    public const int MaximumWarmupTilesPerSegment = 4;
    public const int MaximumFallbackTilesPerSegment = MaximumWarmupTilesPerSegment;
    public const int ContentWidth = 512;
    public const int Width = ContentWidth;
    public const int Height = 64;

    public static TimelineRasterBuffer RasterizeFixedPreviewTile(
        TimelineSegmentPreview preview,
        long segmentLengthTicks,
        int ticksPerQuarterNote,
        int lod,
        long tileX,
        Color noteColor,
        Color eventColor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        long contentWidth = GetFixedPreviewContentWidth(
            segmentLengthTicks,
            ticksPerQuarterNote,
            lod);
        long tileCount = 1 + ((contentWidth - 1) / FixedPreviewTileSize);
        if (tileX < 0 || tileX >= tileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tileX));
        }
        long tileLeft = checked(tileX * FixedPreviewTileSize);
        int tileWidth = checked((int)Math.Min(
            FixedPreviewTileSize,
            contentWidth - tileLeft));
        long tileRight = checked(tileLeft + tileWidth);
        byte[] pixels = new byte[checked(tileWidth * Height * 4)];
        int rendered = 0;
        int visited = 0;
        double normalizedStart = Math.Max(0, (tileLeft - 1d) / contentWidth);
        double normalizedEnd = Math.Min(
            Math.BitIncrement(1d),
            (tileRight + 1d) / contentWidth);

        preview.VisitNotes(normalizedStart, normalizedEnd, note =>
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            long left = Math.Clamp(
                RoundNormalizedBoundary(note.NormalizedStart, contentWidth),
                0,
                contentWidth - 1);
            long right = Math.Clamp(
                Math.Max(left + 1, RoundNormalizedBoundary(note.NormalizedEnd, contentWidth)),
                1,
                contentWidth);
            if (right <= tileLeft || left >= tileRight) return;
            int localLeft = checked((int)Math.Clamp(left - tileLeft, 0, tileWidth));
            int localRight = checked((int)Math.Clamp(right - tileLeft, 0, tileWidth));
            if (localRight <= localLeft) return;
            int top = Math.Clamp(
                (int)Math.Round(
                    (127 - note.Pitch) / 127d * (Height - 1),
                    MidpointRounding.AwayFromZero),
                0,
                Height - 1);
            int bottom = Math.Min(Height, top + 2);
            if (bottom - top < 2) top = Math.Max(0, bottom - 2);
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                tileWidth,
                localLeft,
                top,
                localRight,
                bottom,
                noteColor,
                0.72);
            if (rendered < int.MaxValue) rendered++;
        });

        double[] eventHeights = new double[tileWidth];
        Array.Fill(eventHeights, -1);
        preview.VisitEvents(normalizedStart, normalizedEnd, value =>
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            long column = Math.Clamp(
                RoundNormalizedBoundary(value.NormalizedTick, contentWidth),
                0,
                contentWidth - 1);
            if (column < tileLeft || column >= tileRight) return;
            int local = checked((int)(column - tileLeft));
            eventHeights[local] = Math.Max(eventHeights[local], value.NormalizedValue);
        });
        for (int x = 0; x < eventHeights.Length; x++)
        {
            if (eventHeights[x] < 0) continue;
            int top = Math.Clamp(
                (int)Math.Floor((1 - eventHeights[x]) * Height),
                0,
                Height - 1);
            CompositeRectangleOver(
                pixels,
                tileWidth,
                x,
                top,
                x + 1,
                Height,
                eventColor,
                0.5);
            if (rendered < int.MaxValue) rendered++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(tileWidth, Height, pixels, rendered);
    }

    public static long GetFixedPreviewContentWidth(
        long segmentLengthTicks,
        int ticksPerQuarterNote,
        int lod = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentLengthTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarterNote);
        if (lod is < 0 or > MaximumFixedPreviewLod)
        {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }
        double width = Math.Ceiling(
            segmentLengthTicks
            * (double)FixedPreviewPixelsPerQuarterNote
            / ticksPerQuarterNote);
        long baseWidth = width >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1, checked((long)width));
        double scaledWidth = baseWidth / GetFixedPreviewLodDivisor(lod);
        return scaledWidth >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)Math.Ceiling(scaledWidth));
    }

    public static int SelectDisplayLod(
        double currentPixelsPerTick,
        int ticksPerQuarterNote)
    {
        if (!double.IsFinite(currentPixelsPerTick) || currentPixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentPixelsPerTick));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarterNote);
        double referencePixelsPerTick = FixedPreviewPixelsPerQuarterNote
            / (double)ticksPerQuarterNote;
        double oversampling = referencePixelsPerTick / currentPixelsPerTick;
        if (!double.IsFinite(oversampling)) return MaximumFixedPreviewLod;
        if (oversampling <= 1) return 0;
        double exactLod = Math.Log2(oversampling) * FixedPreviewLodLevelsPerOctave;
        return Math.Clamp(
            checked((int)Math.Ceiling(exactLod - 1e-12)),
            0,
            MaximumFixedPreviewLod);
    }

    public static double GetFixedPreviewPixelsPerTick(
        int ticksPerQuarterNote,
        int lod)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarterNote);
        if (lod is < 0 or > MaximumFixedPreviewLod)
        {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }
        return FixedPreviewPixelsPerQuarterNote
            / (double)ticksPerQuarterNote
            / GetFixedPreviewLodDivisor(lod);
    }

    public static int SelectWarmupLod(
        long segmentLengthTicks,
        int ticksPerQuarterNote)
    {
        long maximumWidth = checked(
            (long)FixedPreviewTileSize * MaximumWarmupTilesPerSegment);
        int lod = 0;
        while (GetFixedPreviewContentWidth(
                segmentLengthTicks,
                ticksPerQuarterNote,
                lod) > maximumWidth
            && lod < MaximumFixedPreviewLod)
        {
            lod++;
        }
        return lod;
    }

    private static double GetFixedPreviewLodDivisor(int lod)
    {
        int octave = lod / FixedPreviewLodLevelsPerOctave;
        double divisor = Math.ScaleB(1d, octave);
        return (lod & 1) == 0 ? divisor : divisor * Math.Sqrt(2);
    }

    public static TimelineRasterBuffer RasterizeNoteTile(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        int deviceHeight,
        long tileX,
        Color noteColor)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateTileArguments(deviceSegmentWidth, deviceHeight, tileX);
        byte[] pixels = new byte[checked(TileSize * deviceHeight * 4)];
        (long tileLeft, long tileRight, long totalWidth) = GetTilePixelRange(
            deviceSegmentWidth,
            tileX);
        int rendered = 0;
        VisitNoteCandidates(preview, deviceSegmentWidth, tileLeft, tileRight, Draw);
        return new(TileSize, deviceHeight, pixels, rendered);

        void Draw(TimelineSegmentPreviewNote note)
        {
            (long left, long right) = GetNotePixelRange(note, deviceSegmentWidth, totalWidth);
            if (right <= tileLeft || left >= tileRight) return;
            int localLeft = checked((int)Math.Clamp(left - tileLeft, 0, TileSize));
            int localRight = checked((int)Math.Clamp(right - tileLeft, 0, TileSize));
            if (localRight <= localLeft) return;
            int top = Math.Clamp(
                (int)Math.Round(
                    (127 - note.Pitch) / 127d * (deviceHeight - 1),
                    MidpointRounding.AwayFromZero),
                0,
                deviceHeight - 1);
            int bottom = Math.Min(deviceHeight, top + 2);
            if (bottom - top < 2)
            {
                top = Math.Max(0, bottom - 2);
            }
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                TileSize,
                localLeft,
                top,
                localRight,
                bottom,
                noteColor,
                0.72);
            rendered++;
        }
    }

    public static TimelineRasterBuffer RasterizeEventTile(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        int deviceHeight,
        long tileX,
        Color eventColor)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateTileArguments(deviceSegmentWidth, deviceHeight, tileX);
        byte[] pixels = new byte[checked(TileSize * deviceHeight * 4)];
        (long tileLeft, long tileRight, long totalWidth) = GetTilePixelRange(
            deviceSegmentWidth,
            tileX);
        SortedDictionary<long, double> columns = CollectEventColumns(
            preview,
            deviceSegmentWidth,
            tileLeft,
            tileRight,
            totalWidth);
        foreach ((long worldColumn, double normalizedValue) in columns)
        {
            int x = checked((int)(worldColumn - tileLeft));
            int top = Math.Clamp(
                (int)Math.Floor((1 - normalizedValue) * deviceHeight),
                0,
                deviceHeight - 1);
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                TileSize,
                x,
                top,
                x + 1,
                deviceHeight,
                eventColor,
                0.5);
        }
        return new(TileSize, deviceHeight, pixels, columns.Count);
    }

    public static ulong ComputeNoteTileContentFingerprint(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileX)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateTileArguments(deviceSegmentWidth, 1, tileX);
        (long tileLeft, long tileRight, long totalWidth) = GetTilePixelRange(
            deviceSegmentWidth,
            tileX);
        ulong hash = FingerprintOffset;
        VisitNoteCandidates(preview, deviceSegmentWidth, tileLeft, tileRight, Add);
        return hash;

        void Add(TimelineSegmentPreviewNote note)
        {
            (long left, long right) = GetNotePixelRange(note, deviceSegmentWidth, totalWidth);
            if (right <= tileLeft || left >= tileRight) return;
            AddFingerprint(ref hash, unchecked((ulong)left));
            AddFingerprint(ref hash, unchecked((ulong)right));
            AddFingerprint(ref hash, unchecked((ulong)note.Pitch));
        }
    }

    public static ulong ComputeEventTileContentFingerprint(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileX)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateTileArguments(deviceSegmentWidth, 1, tileX);
        (long tileLeft, long tileRight, long totalWidth) = GetTilePixelRange(
            deviceSegmentWidth,
            tileX);
        SortedDictionary<long, double> columns = CollectEventColumns(
            preview,
            deviceSegmentWidth,
            tileLeft,
            tileRight,
            totalWidth);
        ulong hash = FingerprintOffset;
        foreach ((long worldColumn, double normalizedValue) in columns)
        {
            AddFingerprint(ref hash, unchecked((ulong)worldColumn));
            AddFingerprint(
                ref hash,
                unchecked((ulong)BitConverter.DoubleToInt64Bits(normalizedValue)));
        }
        return hash;
    }

    public static TimelineRasterBuffer Rasterize(TimelineSegmentPreview preview, Color noteColor) =>
        Rasterize(preview, noteColor, Colors.Transparent);

    public static TimelineRasterBuffer Rasterize(
        TimelineSegmentPreview preview,
        Color noteColor,
        Color eventColor)
    {
        ArgumentNullException.ThrowIfNull(preview);
        byte[] pixels = new byte[Width * Height * 4];
        double pitchTravel = Height - 1;
        foreach (TimelineSegmentPreviewNote note in preview.Notes)
        {
            int left = Math.Clamp(
                RoundNormalizedBoundary(note.NormalizedStart),
                0,
                ContentWidth - 1);
            int right = Math.Clamp(
                Math.Max(left + 1, RoundNormalizedBoundary(note.NormalizedEnd)),
                1,
                ContentWidth);
            int top = Math.Clamp(
                (int)Math.Round((127 - note.Pitch) / 127d * pitchTravel, MidpointRounding.AwayFromZero),
                0,
                Height - 1);
            int bottom = Math.Min(Height, top + 2);
            if (bottom - top < 2)
            {
                top = Math.Max(0, bottom - 2);
            }
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                Width,
                left,
                top,
                right,
                bottom,
                noteColor,
                0.72);
        }
        // Direct MIDI events intentionally sit above the Note preview. At 50% opacity,
        // dense Note material remains readable while event activity is still visible.
        foreach (TimelineSegmentPreviewEvent value in preview.Events)
        {
            int x = Math.Clamp(
                RoundNormalizedBoundary(value.NormalizedTick),
                0,
                ContentWidth - 1);
            int top = Math.Clamp(
                (int)Math.Floor((1 - value.NormalizedValue) * Height),
                0,
                Height - 1);
            CompositeRectangleOver(
                pixels,
                Width,
                x,
                top,
                Math.Min(Width, x + 1),
                Height,
                eventColor,
                0.5);
        }
        return new(Width, Height, pixels, checked(preview.Notes.Count + preview.Events.Count));
    }

    private static void CompositeRectangleOver(
        byte[] pixels,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        Color color,
        double opacity)
    {
        byte sourceAlpha = (byte)Math.Clamp(
            (int)Math.Round(
                color.A * Math.Clamp(opacity, 0, 1),
                MidpointRounding.AwayFromZero),
            0,
            255);
        if (sourceAlpha == 0) return;

        int inverseSourceAlpha = 255 - sourceAlpha;
        byte sourceBlue = Premultiply(color.B, sourceAlpha);
        byte sourceGreen = Premultiply(color.G, sourceAlpha);
        byte sourceRed = Premultiply(color.R, sourceAlpha);
        for (int y = top; y < bottom; y++)
        {
            int offset = checked((y * width + left) * 4);
            for (int x = left; x < right; x++, offset += 4)
            {
                pixels[offset] = CompositeChannel(
                    sourceBlue,
                    pixels[offset],
                    inverseSourceAlpha);
                pixels[offset + 1] = CompositeChannel(
                    sourceGreen,
                    pixels[offset + 1],
                    inverseSourceAlpha);
                pixels[offset + 2] = CompositeChannel(
                    sourceRed,
                    pixels[offset + 2],
                    inverseSourceAlpha);
                pixels[offset + 3] = CompositeChannel(
                    sourceAlpha,
                    pixels[offset + 3],
                    inverseSourceAlpha);
            }
        }
    }

    private static byte CompositeChannel(
        byte source,
        byte destination,
        int inverseSourceAlpha) =>
        checked((byte)(source + ((destination * inverseSourceAlpha + 127) / 255)));

    private static byte Premultiply(byte value, byte alpha) =>
        checked((byte)((value * alpha + 127) / 255));

    private static int RoundNormalizedBoundary(double value) =>
        checked((int)Math.Floor(value * ContentWidth + 0.5));

    private static long RoundNormalizedBoundary(double value, long contentWidth)
    {
        double scaled = value * contentWidth + 0.5;
        return scaled >= long.MaxValue
            ? long.MaxValue
            : scaled <= long.MinValue
                ? long.MinValue
                : checked((long)Math.Floor(scaled));
    }

    private static void ValidateTileArguments(
        double deviceSegmentWidth,
        int deviceHeight,
        long tileX)
    {
        if (!double.IsFinite(deviceSegmentWidth)
            || deviceSegmentWidth <= 0
            || deviceSegmentWidth > long.MaxValue - TileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceSegmentWidth));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        _ = checked(tileX * TileSize);
    }

    private static (long TileLeft, long TileRight, long TotalWidth) GetTilePixelRange(
        double deviceSegmentWidth,
        long tileX)
    {
        long tileLeft = checked(tileX * TileSize);
        long tileRight = checked(tileLeft + TileSize);
        long totalWidth = Math.Max(1, checked((long)Math.Ceiling(deviceSegmentWidth)));
        return (tileLeft, tileRight, totalWidth);
    }

    private static void QueryNoteCandidates(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileLeft,
        long tileRight,
        List<TimelineSegmentPreviewNote> destination)
    {
        double normalizedStart = Math.Max(0, (tileLeft - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(1, (tileRight + 1d) / deviceSegmentWidth);
        preview.QueryNotes(normalizedStart, normalizedEnd, destination);
    }

    private static void VisitNoteCandidates(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileLeft,
        long tileRight,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        double normalizedStart = Math.Max(0, (tileLeft - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(1, (tileRight + 1d) / deviceSegmentWidth);
        preview.VisitNotes(normalizedStart, normalizedEnd, visitor);
    }

    private static void QueryEventCandidates(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileLeft,
        long tileRight,
        List<TimelineSegmentPreviewEvent> destination)
    {
        double normalizedStart = Math.Max(0, (tileLeft - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(
            Math.BitIncrement(1d),
            (tileRight + 1d) / deviceSegmentWidth);
        preview.QueryEvents(normalizedStart, normalizedEnd, destination);
    }

    private static (long Left, long Right) GetNotePixelRange(
        TimelineSegmentPreviewNote note,
        double deviceSegmentWidth,
        long totalWidth)
    {
        long left = Math.Clamp(
            RoundDeviceBoundary(note.NormalizedStart, deviceSegmentWidth),
            0,
            totalWidth - 1);
        long right = Math.Clamp(
            Math.Max(left + 1, RoundDeviceBoundary(note.NormalizedEnd, deviceSegmentWidth)),
            1,
            totalWidth);
        return (left, right);
    }

    private static SortedDictionary<long, double> CollectEventColumns(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileLeft,
        long tileRight,
        long totalWidth)
    {
        SortedDictionary<long, double> columns = [];
        VisitEventCandidates(preview, deviceSegmentWidth, tileLeft, tileRight, Add);
        return columns;

        void Add(TimelineSegmentPreviewEvent value)
        {
            long x = Math.Clamp(
                RoundDeviceBoundary(value.NormalizedTick, deviceSegmentWidth),
                0,
                totalWidth - 1);
            if (x < tileLeft || x >= tileRight) return;
            if (!columns.TryGetValue(x, out double maximum)
                || value.NormalizedValue > maximum)
            {
                columns[x] = value.NormalizedValue;
            }
        }
    }

    private static void VisitEventCandidates(
        TimelineSegmentPreview preview,
        double deviceSegmentWidth,
        long tileLeft,
        long tileRight,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        double normalizedStart = Math.Max(0, (tileLeft - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(
            Math.BitIncrement(1d),
            (tileRight + 1d) / deviceSegmentWidth);
        preview.VisitEvents(normalizedStart, normalizedEnd, visitor);
    }

    private static long RoundDeviceBoundary(double normalized, double deviceSegmentWidth) =>
        checked((long)Math.Floor(normalized * deviceSegmentWidth + 0.5));

    private static void AddFingerprint(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FingerprintPrime;
    }
}

public static class TimelineEventPointTileRasterizer
{
    private const double AggregatePixelsPerTickThreshold = 0.125;
    public const int TileSize = 256;
    public const double PointRadius = 4;
    public const double SelectionRadius = 6;

    public static int GetGutter(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }
        return checked((int)Math.Ceiling(SelectionRadius * dpiScale) + 1);
    }

    public static int GetRasterSize(double dpiScale) =>
        checked(TileSize + GetGutter(dpiScale) * 2);

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double dpiScaleX,
        double dpiScaleY,
        Color normalColor,
        Color primaryColor,
        Color borderColor,
        bool selectionOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerValue) || devicePixelsPerValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerValue));
        }
        if (snapshot.IsTempoProjection)
            return TimelineTempoTileRasterizer.Rasterize(snapshot, selection,
                devicePixelsPerTick, devicePixelsPerValue, tileX, tileY, dpiScaleX, dpiScaleY,
                normalColor, primaryColor, borderColor, selectionOnly, cancellationToken);
        int gutterX = GetGutter(dpiScaleX);
        int gutterY = GetGutter(dpiScaleY);
        int width = checked(TileSize + gutterX * 2);
        int height = checked(TileSize + gutterY * 2);
        double worldLeft = tileX * (double)TileSize - gutterX;
        double worldTop = tileY * (double)TileSize - gutterY;
        long startTick = TimelineTickMath.RasterQueryStart(worldLeft / devicePixelsPerTick);
        long endTick = TimelineTickMath.RasterQueryEnd((worldLeft + width) / devicePixelsPerTick, startTick);
        byte[] pixels = new byte[checked(width * height * 4)];
        double pointRadiusX = Math.Max(1, PointRadius * dpiScaleX);
        double pointRadiusY = Math.Max(1, PointRadius * dpiScaleY);
        double selectionRadiusX = Math.Max(pointRadiusX + 1, SelectionRadius * dpiScaleX);
        double selectionRadiusY = Math.Max(pointRadiusY + 1, SelectionRadius * dpiScaleY);
        double outlineX = Math.Max(1, dpiScaleX);
        double outlineY = Math.Max(1, dpiScaleY);
        if (!selectionOnly
            && (selection is null || selection.Count == 0)
            && devicePixelsPerTick <= AggregatePixelsPerTickThreshold)
        {
            TimelineRasterColumnSummary[] summaries =
                new TimelineRasterColumnSummary[width];
            var projection = new TimelineRasterColumnProjection(
                startTick,
                endTick,
                startTick,
                -(worldLeft / devicePixelsPerTick - startTick) * devicePixelsPerTick,
                devicePixelsPerTick,
                width);
            if (snapshot.TryAccumulateRasterColumns(
                    TimelineRasterAggregateKind.EventPoints,
                    projection,
                    0,
                    1,
                    summaries,
                    out int sourceWorkCount,
                    cancellationToken))
            {
                DrawAggregate(summaries);
                cancellationToken.ThrowIfCancellationRequested();
                return new(width, height, pixels, sourceWorkCount);
            }
        }
        Dictionary<(int X, int Y, bool Selected, bool Primary), TimelineRenderItem> points = [];
        snapshot.VisitInto(startTick, endTick, 0, 1, Collect);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (TimelineRenderItem item in points.Values
            .OrderBy(value => value.StartTick)
            .ThenBy(value => value.Value)
            .ThenBy(value => value.Id))
        {
            double worldY = (1 - Math.Clamp(item.Value, 0, 1)) * devicePixelsPerValue;
            double centerX = item.StartTick * devicePixelsPerTick - worldLeft;
            double centerY = worldY - worldTop;
            if (centerX + selectionRadiusX < 0 || centerX - selectionRadiusX >= width
                || centerY + selectionRadiusY < 0 || centerY - selectionRadiusY >= height)
            {
                continue;
            }

            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            bool primary = selection is not null
                ? selection.Primary == item.Id
                : item.State.HasFlag(TimelineItemState.Primary);
            if (selected)
            {
                FillEllipse(
                    pixels,
                    width,
                    height,
                    centerX,
                    centerY,
                    selectionRadiusX,
                    selectionRadiusY,
                    primary ? primaryColor : normalColor);
            }
            FillEllipse(
                pixels,
                width,
                height,
                centerX,
                centerY,
                pointRadiusX,
                pointRadiusY,
                borderColor);
            FillEllipse(
                pixels,
                width,
                height,
                centerX,
                centerY,
                Math.Max(0.5, pointRadiusX - outlineX),
                Math.Max(0.5, pointRadiusY - outlineY),
                normalColor);
        }
        return new(width, height, pixels, points.Count);

        void DrawAggregate(ReadOnlySpan<TimelineRasterColumnSummary> summaries)
        {
            double radiusX = Math.Max(0.75, dpiScaleX);
            double radiusY = Math.Max(0.75, dpiScaleY);
            for (int x = 0; x < summaries.Length; x++)
            {
                if ((x & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                TimelineRasterColumnSummary summary = summaries[x];
                if (!summary.HasContent) continue;
                DrawValue(summary.MinimumValue);
                if (summary.MaximumValue != summary.MinimumValue)
                    DrawValue(summary.MaximumValue);

                void DrawValue(double value)
                {
                    double centerY = (1 - Math.Clamp(value, 0, 1))
                        * devicePixelsPerValue - worldTop;
                    if (centerY + radiusY < 0 || centerY - radiusY >= height) return;
                    FillEllipse(
                        pixels,
                        width,
                        height,
                        x,
                        centerY,
                        radiusX,
                        radiusY,
                        normalColor);
                }
            }
        }

        void Collect(TimelineRenderItem item)
        {
            if (item.Kind is not (
                TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                    or TimelineItemKind.DirectMidiEvent
                    or TimelineItemKind.OpaqueMidiEvent))
            {
                return;
            }
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            if (selectionOnly && !selected) return;
            if ((points.Count & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            bool primary = selection is not null
                ? selection.Primary == item.Id
                : item.State.HasFlag(TimelineItemState.Primary);
            double worldY = (1 - Math.Clamp(item.Value, 0, 1)) * devicePixelsPerValue;
            int centerX = checked((int)Math.Round(
                item.StartTick * devicePixelsPerTick - worldLeft,
                MidpointRounding.AwayFromZero));
            int centerY = checked((int)Math.Round(
                worldY - worldTop,
                MidpointRounding.AwayFromZero));
            if (centerX + selectionRadiusX < 0 || centerX - selectionRadiusX >= width
                || centerY + selectionRadiusY < 0 || centerY - selectionRadiusY >= height)
            {
                return;
            }
            var key = (centerX, centerY, selected, primary);
            if (!points.TryGetValue(key, out TimelineRenderItem existing)
                || item.Id.CompareTo(existing.Id) < 0)
                points[key] = item;
        }
    }

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double dpiScaleX,
        double dpiScaleY,
        bool selectionOnly = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        if (!double.IsFinite(devicePixelsPerValue) || devicePixelsPerValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerValue));
        int gutterX = GetGutter(dpiScaleX);
        int gutterY = GetGutter(dpiScaleY);
        int width = checked(TileSize + gutterX * 2);
        int height = checked(TileSize + gutterY * 2);
        double worldLeft = tileX * (double)TileSize - gutterX;
        double worldTop = tileY * (double)TileSize - gutterY;
        long startTick = TimelineTickMath.RasterQueryStart(worldLeft / devicePixelsPerTick);
        long endTick = TimelineTickMath.RasterQueryEnd((worldLeft + width) / devicePixelsPerTick, startTick);
        double selectionRadiusY = Math.Max(1, SelectionRadius * dpiScaleY);
        List<TimelineRenderItem> materialized = [];
        snapshot.QueryMaterializedInto(startTick, endTick, 0, 1, materialized);
        materialized.RemoveAll(item => item.Kind is not (
            TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                or TimelineItemKind.DirectMidiEvent
                or TimelineItemKind.OpaqueMidiEvent));
        ulong content = TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.ForRenderItems(materialized),
            snapshot.GetExternalRangeFingerprint(startTick, endTick, 0, 1));
        return selection is null
            ? content
            : TimelineContentFingerprint.Combine(
                content,
                selection.GetRangeRevisionFingerprint(startTick, endTick, 0, 1));
    }

    internal static void FillEllipse(
        byte[] pixels,
        int width,
        int height,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        Color color)
    {
        int left = Math.Max(0, checked((int)Math.Floor(centerX - radiusX)));
        int right = Math.Min(width, checked((int)Math.Ceiling(centerX + radiusX)));
        int top = Math.Max(0, checked((int)Math.Floor(centerY - radiusY)));
        int bottom = Math.Min(height, checked((int)Math.Ceiling(centerY + radiusY)));
        byte alpha = color.A;
        byte blue = Premultiply(color.B, alpha);
        byte green = Premultiply(color.G, alpha);
        byte red = Premultiply(color.R, alpha);
        for (int y = top; y < bottom; y++)
        {
            double normalizedY = (y + 0.5 - centerY) / radiusY;
            double normalizedYSquared = normalizedY * normalizedY;
            if (normalizedYSquared > 1) continue;
            int offset = checked((y * width + left) * 4);
            for (int x = left; x < right; x++, offset += 4)
            {
                double normalizedX = (x + 0.5 - centerX) / radiusX;
                if (normalizedX * normalizedX + normalizedYSquared > 1) continue;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }
        }
    }

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
}

public static class TimelineConductorTileRasterizer
{
    private const ulong FingerprintOffset = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;
    public const int TileSize = 256;
    public const double PointRadius = 4;
    public const double OutlineThickness = 1;

    public static int GetGutter(double dpiScaleX)
    {
        if (!double.IsFinite(dpiScaleX) || dpiScaleX <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScaleX));
        return checked((int)Math.Ceiling((PointRadius + OutlineThickness) * dpiScaleX) + 1);
    }

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double deviceLaneHeight,
        long tileX,
        double dpiScaleX,
        double dpiScaleY,
        Color fallbackColor,
        Color borderColor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        if (!double.IsFinite(deviceLaneHeight) || deviceLaneHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(deviceLaneHeight));
        int gutterX = GetGutter(dpiScaleX);
        int gutterY = checked((int)Math.Ceiling((PointRadius + OutlineThickness) * dpiScaleY) + 1);
        int width = checked(TileSize + gutterX * 2);
        int height = checked(Math.Max(1, (int)Math.Ceiling(deviceLaneHeight)) + gutterY * 2);
        double worldLeft = tileX * (double)TileSize - gutterX;
        ConductorTilePoint[] points = CollectPoints(
            snapshot,
            devicePixelsPerTick,
            tileX,
            dpiScaleX,
            cancellationToken);

        byte[] pixels = new byte[checked(width * height * 4)];
        double radiusX = Math.Max(1, PointRadius * dpiScaleX);
        double radiusY = Math.Max(1, PointRadius * dpiScaleY);
        double outlineX = Math.Max(0.5, OutlineThickness * dpiScaleX);
        double outlineY = Math.Max(0.5, OutlineThickness * dpiScaleY);
        foreach (ConductorTilePoint point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double centerX = point.DeviceColumn;
            double normalizedY = 0.2 + Math.Clamp(point.Type, 0, 3) * 0.15;
            double centerY = gutterY + normalizedY * deviceLaneHeight;
            if (centerX + radiusX < 0 || centerX - radiusX >= width) continue;
            Color fill = point.AccentColor == 0
                ? fallbackColor
                : Color.FromArgb(
                    (byte)(point.AccentColor >> 24),
                    (byte)(point.AccentColor >> 16),
                    (byte)(point.AccentColor >> 8),
                    (byte)point.AccentColor);
            TimelineEventPointTileRasterizer.FillEllipse(
                pixels, width, height, centerX, centerY, radiusX, radiusY, borderColor);
            TimelineEventPointTileRasterizer.FillEllipse(
                pixels, width, height, centerX, centerY,
                Math.Max(0.5, radiusX - outlineX),
                Math.Max(0.5, radiusY - outlineY),
                fill);
        }
        return new(width, height, pixels, points.Length);
    }

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        long tileX,
        double dpiScaleX)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        ConductorTilePoint[] points = CollectPoints(
            snapshot,
            devicePixelsPerTick,
            tileX,
            dpiScaleX);
        ulong hash = FingerprintOffset;
        foreach (ConductorTilePoint point in points)
        {
            AddFingerprint(ref hash, unchecked((ulong)point.DeviceColumn));
            AddFingerprint(ref hash, unchecked((ulong)point.Type));
            AddFingerprint(ref hash, point.AccentColor);
        }
        return hash;
    }

    private static ConductorTilePoint[] CollectPoints(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        long tileX,
        double dpiScaleX,
        CancellationToken cancellationToken = default)
    {
        int gutterX = GetGutter(dpiScaleX);
        double worldLeft = tileX * (double)TileSize - gutterX;
        double worldRight = worldLeft + TileSize + gutterX * 2;
        long startTick = TimelineTickMath.RasterQueryStart(worldLeft / devicePixelsPerTick);
        long endTick = TimelineTickMath.RasterQueryEnd(worldRight / devicePixelsPerTick, startTick);
        Dictionary<(long DeviceColumn, int Type), ConductorTilePoint> aggregated = [];
        int visited = 0;
        snapshot.VisitConductorPreview(startTick, endTick, item =>
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind is not (TimelineItemKind.ConductorEvent or TimelineItemKind.Marker))
            {
                return;
            }
            double column = Math.Round(item.StartTick * devicePixelsPerTick,
                MidpointRounding.AwayFromZero) - worldLeft;
            if (column + PointRadius * dpiScaleX < 0
                || column - PointRadius * dpiScaleX >= TileSize + gutterX * 2)
            {
                return;
            }
            long deviceColumn = checked((long)Math.Round(column, MidpointRounding.AwayFromZero));
            (long, int) key = (deviceColumn, item.ZIndex);
            ConductorTilePoint point = new(deviceColumn, item.ZIndex, item.AccentColor);
            if (!aggregated.TryGetValue(key, out ConductorTilePoint existing)
                || point.AccentColor < existing.AccentColor)
            {
                aggregated[key] = point;
            }
        }, cancellationToken);
        return aggregated.Values
            .OrderBy(value => value.Type)
            .ThenBy(value => value.DeviceColumn)
            .ThenBy(value => value.AccentColor)
            .ToArray();
    }

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

    private static void AddFingerprint(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FingerprintPrime;
    }

    private readonly record struct ConductorTilePoint(
        long DeviceColumn,
        int Type,
        uint AccentColor);
}

public static class TimelineVelocityTileRasterizer
{
    private const double AggregatePixelsPerTickThreshold = 0.125;
    public const int TileSize = 256;
    public const int StemWidth = 3;
    public const int MarkerSize = 7;
    public const int Gutter = MarkerSize / 2 + 1;
    public const int RasterWidth = TileSize + Gutter * 2;
    public const int RasterHeight = 256;

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        int horizontalLod,
        long tileX,
        Color normalColor,
        Color selectedColor,
        Color borderColor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        double pixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double worldLeft = tileX * (double)TileSize - Gutter;
        long startTick = TimelineTickMath.RasterQueryStart(worldLeft / pixelsPerTick);
        long endTick = TimelineTickMath.RasterQueryEnd((worldLeft + RasterWidth) / pixelsPerTick, startTick);
        byte[] pixels = new byte[RasterWidth * RasterHeight * 4];
        if ((selection is null || selection.Count == 0)
            && pixelsPerTick <= AggregatePixelsPerTickThreshold)
        {
            TimelineRasterColumnSummary[] summaries =
                new TimelineRasterColumnSummary[RasterWidth];
            var projection = new TimelineRasterColumnProjection(
                startTick,
                endTick,
                startTick,
                -(worldLeft / pixelsPerTick - startTick) * pixelsPerTick,
                pixelsPerTick,
                RasterWidth);
            if (snapshot.TryAccumulateRasterColumns(
                    TimelineRasterAggregateKind.Velocity,
                    projection,
                    0,
                    1,
                    summaries,
                    out int sourceWorkCount,
                    cancellationToken))
            {
                DrawAggregate(summaries);
                cancellationToken.ThrowIfCancellationRequested();
                return new(RasterWidth, RasterHeight, pixels, sourceWorkCount);
            }
        }
        Dictionary<(int Center, int Top, bool Selected), TimelineRenderItem> columns = [];
        int visited = 0;
        snapshot.VisitInto(startTick, endTick, 0, 1, Collect);
        foreach (TimelineRenderItem item in columns.Values
            .OrderBy(value => value.StartTick)
            .ThenBy(value => value.ZIndex)
            .ThenBy(value => value.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            int rawCenter = checked((int)Math.Round(
                item.StartTick * pixelsPerTick - worldLeft,
                MidpointRounding.AwayFromZero));
            int rawLeft = rawCenter - StemWidth / 2;
            int rawRight = rawLeft + StemWidth;
            int left = Math.Clamp(rawLeft, 0, RasterWidth);
            int right = Math.Clamp(rawRight, 0, RasterWidth);
            int top = Math.Clamp(
                (int)Math.Round((1 - Math.Clamp(item.Value, 0, 1)) * (RasterHeight - 1), MidpointRounding.AwayFromZero),
                0,
                RasterHeight - 1);
            if (right <= left) continue;
            Color fill = selected ? selectedColor : normalColor;
            TimelinePianoTileRasterizer.FillRectangle(
                pixels, RasterWidth, left, top, right, RasterHeight, fill, selected ? 0.4 : 0.2);
            TimelinePianoTileRasterizer.DrawRectangleOutline(
                pixels, RasterWidth, left, top, right, RasterHeight, borderColor, selected ? 1 : 0.86,
                drawLeft: rawLeft >= 0,
                drawTop: true,
                drawRight: rawRight <= RasterWidth,
                drawBottom: true);
            int rawMarkerLeft = rawCenter - MarkerSize / 2;
            int rawMarkerRight = rawMarkerLeft + MarkerSize;
            int markerLeft = Math.Clamp(rawMarkerLeft, 0, RasterWidth);
            int markerRight = Math.Clamp(rawMarkerRight, 0, RasterWidth);
            int markerBottom = Math.Min(RasterHeight, top + MarkerSize);
            TimelinePianoTileRasterizer.FillRectangle(
                pixels, RasterWidth, markerLeft, top, markerRight, markerBottom, fill, 1);
            TimelinePianoTileRasterizer.DrawRectangleOutline(
                pixels, RasterWidth, markerLeft, top, markerRight, markerBottom, borderColor, 1,
                drawLeft: rawMarkerLeft >= 0,
                drawTop: true,
                drawRight: rawMarkerRight <= RasterWidth,
                drawBottom: true);
        }
        return new(RasterWidth, RasterHeight, pixels, columns.Count);

        void DrawAggregate(ReadOnlySpan<TimelineRasterColumnSummary> summaries)
        {
            for (int x = 0; x < summaries.Length; x++)
            {
                TimelineRasterColumnSummary summary = summaries[x];
                if (!summary.HasContent) continue;
                DrawEnvelopeValue(x, summary.MinimumValue);
                if (summary.MaximumValue != summary.MinimumValue)
                    DrawEnvelopeValue(x, summary.MaximumValue);
            }
        }

        void DrawEnvelopeValue(int center, double value)
        {
            int top = Math.Clamp(
                (int)Math.Round(
                    (1 - Math.Clamp(value, 0, 1)) * (RasterHeight - 1),
                    MidpointRounding.AwayFromZero),
                0,
                RasterHeight - 1);
            int left = Math.Clamp(center, 0, RasterWidth);
            int right = Math.Min(RasterWidth, left + 1);
            if (right <= left) return;
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                RasterWidth,
                left,
                top,
                right,
                RasterHeight,
                normalColor,
                0.32);
            int rawMarkerLeft = center - MarkerSize / 2;
            int rawMarkerRight = rawMarkerLeft + MarkerSize;
            int markerLeft = Math.Clamp(rawMarkerLeft, 0, RasterWidth);
            int markerRight = Math.Clamp(rawMarkerRight, 0, RasterWidth);
            int markerBottom = Math.Min(RasterHeight, top + MarkerSize);
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                RasterWidth,
                markerLeft,
                top,
                markerRight,
                markerBottom,
                normalColor,
                1);
            TimelinePianoTileRasterizer.DrawRectangleOutline(
                pixels,
                RasterWidth,
                markerLeft,
                top,
                markerRight,
                markerBottom,
                borderColor,
                1,
                drawLeft: rawMarkerLeft >= 0,
                drawTop: true,
                drawRight: rawMarkerRight <= RasterWidth,
                drawBottom: true);
        }

        void Collect(TimelineRenderItem item)
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind != TimelineItemKind.Velocity) return;
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            int center = checked((int)Math.Round(
                item.StartTick * pixelsPerTick - worldLeft,
                MidpointRounding.AwayFromZero));
            int top = Math.Clamp(
                (int)Math.Round(
                    (1 - Math.Clamp(item.Value, 0, 1)) * (RasterHeight - 1),
                    MidpointRounding.AwayFromZero),
                0,
                RasterHeight - 1);
            var key = (center, top, selected);
            if (!columns.TryGetValue(key, out TimelineRenderItem existing)
                || item.ZIndex > existing.ZIndex
                || item.ZIndex == existing.ZIndex && item.Id.CompareTo(existing.Id) < 0)
            {
                columns[key] = item;
            }
        }
    }

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        int horizontalLod,
        long tileX)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double pixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double worldLeft = tileX * (double)TileSize - Gutter;
        long startTick = TimelineTickMath.RasterQueryStart(worldLeft / pixelsPerTick);
        long endTick = TimelineTickMath.RasterQueryEnd((worldLeft + RasterWidth) / pixelsPerTick, startTick);
        List<TimelineRenderItem> materialized = [];
        snapshot.QueryMaterializedInto(startTick, endTick, 0, 1, materialized);
        ulong content = TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.ForVelocityTileItems(materialized, selection),
            snapshot.GetExternalRangeFingerprint(startTick, endTick, 0, 1));
        return selection is null
            ? content
            : TimelineContentFingerprint.Combine(
                content,
                selection.GetRangeRevisionFingerprint(startTick, endTick, 0, 1));
    }

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
}

internal static class TimelineResizePreviewRasterizer
{
    public const int TileSize = 256;
    public const int Gutter = 1;
    public const int RasterSize = TileSize + Gutter * 2;

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        MidoraId anchorId,
        TimelineItemKind itemKind,
        TimelineResizeEdge edge,
        long tickDelta,
        long minimumLength,
        long viewportStartTick,
        double devicePixelsPerTick,
        IReadOnlyList<double> laneTopsDevice,
        IReadOnlyList<double> laneHeightsDevice,
        long tileX,
        long tileY,
        Color color,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(laneTopsDevice);
        ArgumentNullException.ThrowIfNull(laneHeightsDevice);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        if (laneTopsDevice.Count != laneHeightsDevice.Count)
            throw new ArgumentException("Lane layout arrays must have equal lengths.");
        minimumLength = Math.Max(1, minimumLength);

        double worldLeft = tileX * (double)TileSize - Gutter;
        double worldTop = tileY * (double)TileSize - Gutter;
        long destinationStart = TimelineTickMath.RasterQueryStart(viewportStartTick + worldLeft / devicePixelsPerTick);
        long destinationEnd = TimelineTickMath.RasterQueryEnd(
            viewportStartTick + (worldLeft + RasterSize) / devicePixelsPerTick, destinationStart);
        long queryStart = destinationStart;
        long queryEnd = destinationEnd;
        if (edge == TimelineResizeEdge.End && tickDelta > 0)
            queryStart = SaturatingAdd(destinationStart, -tickDelta);
        else if (edge == TimelineResizeEdge.Start && tickDelta < 0)
            queryEnd = SaturatingAdd(destinationEnd, -tickDelta);
        queryStart = Math.Max(0, queryStart);
        queryEnd = Math.Max(queryStart + 1, queryEnd);

        int firstLane = laneTopsDevice.Count;
        int lastLaneExclusive = 0;
        double worldBottom = worldTop + RasterSize;
        for (int lane = 0; lane < laneTopsDevice.Count; lane++)
        {
            double top = laneTopsDevice[lane];
            double bottom = top + laneHeightsDevice[lane];
            if (bottom <= worldTop || top >= worldBottom) continue;
            firstLane = Math.Min(firstLane, lane);
            lastLaneExclusive = lane + 1;
        }
        if (lastLaneExclusive <= firstLane)
            return new(RasterSize, RasterSize, new byte[RasterSize * RasterSize * 4]);

        int stride = RasterSize + 1;
        int[] horizontal = new int[RasterSize * stride];
        int[] vertical = new int[RasterSize * stride];
        int candidateCount = 0;
        snapshot.VisitInto(queryStart, queryEnd, firstLane, lastLaneExclusive, Visit);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] pixels = new byte[RasterSize * RasterSize * 4];
        for (int y = 0; y < RasterSize; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            int active = 0;
            int row = y * stride;
            for (int x = 0; x < RasterSize; x++)
            {
                active += horizontal[row + x];
                if (active > 0) WritePixel(pixels, x, y, color);
            }
        }
        for (int x = 0; x < RasterSize; x++)
        {
            int active = 0;
            int column = x * stride;
            for (int y = 0; y < RasterSize; y++)
            {
                active += vertical[column + y];
                if (active > 0) WritePixel(pixels, x, y, color);
            }
        }
        return new(RasterSize, RasterSize, pixels, candidateCount);

        void Visit(TimelineRenderItem item)
        {
            if (item.Kind != itemKind
                || !(selection?.Contains(item.Id) ?? item.Id == anchorId))
            {
                return;
            }
            if ((candidateCount++ & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            long start = item.StartTick;
            long end = item.EndTick;
            long itemMinimum = Math.Min(Math.Max(1, item.Length), minimumLength);
            if (edge == TimelineResizeEdge.Start)
            {
                long maximumStart = Math.Max(0, end - itemMinimum);
                start = Math.Clamp(SaturatingAdd(start, tickDelta), 0, maximumStart);
            }
            else
            {
                end = Math.Max(
                    SaturatingAdd(start, itemMinimum),
                    SaturatingAdd(end, tickDelta));
            }

            int rawLeft = RoundBoundary((start - viewportStartTick) * devicePixelsPerTick - worldLeft);
            int rawRight = Math.Max(
                rawLeft + 1,
                RoundBoundary((end - viewportStartTick) * devicePixelsPerTick - worldLeft));
            int rawTop = RoundBoundary(laneTopsDevice[item.Lane] - worldTop);
            int rawBottom = Math.Max(
                rawTop + 1,
                RoundBoundary(laneTopsDevice[item.Lane] + laneHeightsDevice[item.Lane] - worldTop));
            int left = Math.Clamp(rawLeft, 0, RasterSize);
            int right = Math.Clamp(rawRight, 0, RasterSize);
            int top = Math.Clamp(rawTop, 0, RasterSize);
            int bottom = Math.Clamp(rawBottom, 0, RasterSize);
            if (right <= left || bottom <= top) return;

            AddHorizontal(Math.Clamp(rawTop, 0, RasterSize - 1), left, right);
            AddHorizontal(Math.Clamp(rawBottom - 1, 0, RasterSize - 1), left, right);
            AddVertical(Math.Clamp(rawLeft, 0, RasterSize - 1), top, bottom);
            AddVertical(Math.Clamp(rawRight - 1, 0, RasterSize - 1), top, bottom);
        }

        void AddHorizontal(int y, int left, int right)
        {
            int offset = y * stride;
            horizontal[offset + left]++;
            horizontal[offset + right]--;
        }

        void AddVertical(int x, int top, int bottom)
        {
            int offset = x * stride;
            vertical[offset + top]++;
            vertical[offset + bottom]--;
        }
    }

    private static void WritePixel(byte[] pixels, int x, int y, Color color)
    {
        int offset = (y * RasterSize + x) * 4;
        byte alpha = color.A;
        pixels[offset] = Premultiply(color.B, alpha);
        pixels[offset + 1] = Premultiply(color.G, alpha);
        pixels[offset + 2] = Premultiply(color.R, alpha);
        pixels[offset + 3] = alpha;
    }

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static int RoundBoundary(double value) => TimelineTickMath.RoundRasterBoundary(value);

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

    private static long SaturatingAdd(long value, long delta)
    {
        if (delta > 0 && value > long.MaxValue - delta) return long.MaxValue;
        if (delta < 0 && value < long.MinValue - delta) return long.MinValue;
        return value + delta;
    }
}

internal enum TimelineRasterRequestPriority
{
    Background = 0,
    Normal = 1,
    Visible = 2
}

internal enum TimelineResizeEdge
{
    Start,
    End
}

internal sealed class TimelineRasterCache
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const int MaximumInFlight = 64;
    // Visible piano tiles may use a small, CPU-proportional worker set so a
    // cold viewport converges promptly on machines with spare cores.  Keep the
    // pool bounded; on machines with at least eight logical processors this
    // leaves at least three quarters for UI, compilation, and audio work.
    // Speculative preview/prewarm remains limited separately to one worker.
    internal static readonly int WorkerCount = Math.Clamp(
        Environment.ProcessorCount / 4,
        2,
        4);
    internal const int RecommendedBackgroundConcurrency = 1;
    private const int MaximumSpeculativeInFlight = MaximumInFlight / 2;
    private readonly object _gate = new();
    private readonly Dictionary<TimelineRasterCacheKey, CacheEntry> _completed = [];
    private readonly Dictionary<TimelineRasterCacheKey, PendingWork> _inFlight = [];
    private readonly Dictionary<SubscriptionKey, Completion> _subscriptions = [];
    private readonly LinkedList<TimelineRasterCacheKey> _lru = [];
    private readonly Queue<TimelineRasterCacheKey> _visibleQueue = [];
    private readonly Queue<TimelineRasterCacheKey> _normalQueue = [];
    private readonly Queue<TimelineRasterCacheKey> _backgroundQueue = [];
    private readonly SemaphoreSlim _workAvailable = new(0);
    private long _currentBytes;
    private long _generation;
    private int _speculativeInFlight;
    private int _runningSpeculative;
    private int _runningCount;
    private static long _nextConsumerId;

    public static TimelineRasterCache Shared { get; } = new();

    internal static long CreateConsumerId() => Interlocked.Increment(ref _nextConsumerId);

    public TimelineRasterCache()
    {
        for (int index = 0; index < WorkerCount; index++)
            _ = Task.Run(WorkerLoopAsync);
    }

    public long CurrentBytes
    {
        get { lock (_gate) return _currentBytes; }
    }

    public int CompletedCount
    {
        get { lock (_gate) return _completed.Count; }
    }

    public int InFlightCount
    {
        get { lock (_gate) return _inFlight.Count; }
    }

    public int ActiveSubscriptionCount
    {
        get { lock (_gate) return _subscriptions.Count; }
    }

    public int QueuedCompletionCount
    {
        get { lock (_gate) return _subscriptions.Values.Count(static value => value.Queued); }
    }

    public int RunningCount
    {
        get { lock (_gate) return _runningCount; }
    }

    internal int QueuedKeyCount
    {
        get { lock (_gate) return _visibleQueue.Count + _normalQueue.Count + _backgroundQueue.Count; }
    }

    public bool TryGet(TimelineRasterCacheKey key, out BitmapSource? bitmap)
    {
        lock (_gate)
        {
            if (!_completed.TryGetValue(key, out CacheEntry? entry))
            {
                bitmap = null;
                return false;
            }
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            bitmap = entry.Bitmap;
            return true;
        }
    }

    public bool Request(
        TimelineRasterCacheKey key,
        Func<TimelineRasterBuffer> factory,
        Dispatcher dispatcher,
        Action completion,
        CancellationToken cancellationToken = default,
        TimelineRasterRequestPriority priority = TimelineRasterRequestPriority.Normal,
        long consumerId = 0)
        => Request(
            key,
            _ => factory(),
            dispatcher,
            completion,
            cancellationToken,
            priority,
            consumerId);

    public bool Request(
        TimelineRasterCacheKey key,
        Func<CancellationToken, TimelineRasterBuffer> factory,
        Dispatcher dispatcher,
        Action completion,
        CancellationToken cancellationToken = default,
        TimelineRasterRequestPriority priority = TimelineRasterRequestPriority.Normal,
        long consumerId = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(completion);
        if (cancellationToken.IsCancellationRequested) return false;

        // Legacy callers remain independent. Surface callers supply one stable,
        // opaque identity; the cancellation token identifies their generation.
        if (consumerId == 0) consumerId = CreateConsumerId();
        SubscriptionKey subscriptionKey = new(consumerId, key, cancellationToken);
        Completion? readyCompletion = null;
        bool signalWorker = false;
        bool accepted = true;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (_subscriptions.ContainsKey(subscriptionKey))
            {
                if (_inFlight.TryGetValue(key, out PendingWork? existing))
                    signalWorker = PromoteLocked(key, existing, priority);
            }
            else if (_completed.ContainsKey(key))
            {
                readyCompletion = AddCompletionLocked(subscriptionKey, null, dispatcher, completion);
            }
            else if (_inFlight.TryGetValue(key, out PendingWork? pending))
            {
                if (pending.ExecutionCancellation.IsCancellationRequested)
                {
                    RemovePendingLocked(key, pending);
                    pending = CreatePendingLocked(
                        key,
                        factory,
                        priority);
                    AddCompletionLocked(subscriptionKey, pending, dispatcher, completion);
                    signalWorker = true;
                }
                else
                {
                    AddCompletionLocked(subscriptionKey, pending, dispatcher, completion);
                }
                signalWorker |= PromoteLocked(key, pending, priority);
            }
            else if (_inFlight.Count >= MaximumInFlight
                || priority != TimelineRasterRequestPriority.Visible
                && _speculativeInFlight >= MaximumSpeculativeInFlight)
            {
                accepted = false;
            }
            else
            {
                PendingWork created = CreatePendingLocked(
                    key,
                    factory,
                    priority);
                AddCompletionLocked(subscriptionKey, created, dispatcher, completion);
                signalWorker = true;
            }
        }
        if (signalWorker) _workAvailable.Release();
        if (readyCompletion is not null) QueueCompletion(readyCompletion);
        return accepted;
    }

    private PendingWork CreatePendingLocked(
        TimelineRasterCacheKey key,
        Func<CancellationToken, TimelineRasterBuffer> factory,
        TimelineRasterRequestPriority priority)
    {
        PendingWork work = new(factory, _generation, priority);
        _inFlight[key] = work;
        if (priority != TimelineRasterRequestPriority.Visible)
            _speculativeInFlight++;
        EnqueueLocked(key, priority);
        return work;
    }

    private bool PromoteLocked(
        TimelineRasterCacheKey key,
        PendingWork work,
        TimelineRasterRequestPriority priority)
    {
        if (work.Running || priority <= work.Priority) return false;
        if (work.Priority != TimelineRasterRequestPriority.Visible
            && priority == TimelineRasterRequestPriority.Visible)
            _speculativeInFlight--;
        work.Priority = priority;
        EnqueueLocked(key, priority);
        return true;
    }

    private Completion AddCompletionLocked(
        SubscriptionKey key,
        PendingWork? work,
        Dispatcher dispatcher,
        Action completion)
    {
        Completion consumer = new(key, _generation, dispatcher, completion, work);
        _subscriptions.Add(key, consumer);
        work?.Completions.Add(key, consumer);
        if (key.Token.CanBeCanceled)
        {
            CancellationTokenRegistration registration = key.Token.UnsafeRegister(
                _ => CancelSubscription(consumer), null);
            if (consumer.Action is null) _ = registration.Unregister();
            else consumer.Registration = registration;
        }
        return consumer;
    }

    private void CancelSubscription(Completion consumer)
    {
        DispatcherOperation? operation;
        lock (_gate)
        {
            PendingWork? work = consumer.Work;
            operation = RemoveSubscriptionLocked(consumer);
            if (work is not null && work.Completions.Count == 0
                && _inFlight.TryGetValue(consumer.Key.Key, out PendingWork? current)
                && ReferenceEquals(current, work))
            {
                // CancelAsync marks the token immediately, without executing a
                // factory's registered callbacks under the cache lock.
                _ = work.ExecutionCancellation.CancelAsync();
                RemovePendingLocked(consumer.Key.Key, work);
            }
        }
        operation?.Abort();
    }

    private DispatcherOperation? RemoveSubscriptionLocked(Completion consumer)
    {
        if (_subscriptions.TryGetValue(consumer.Key, out Completion? current)
            && ReferenceEquals(current, consumer))
            _subscriptions.Remove(consumer.Key);
        consumer.Work?.Completions.Remove(consumer.Key);
        consumer.Work = null;
        consumer.Action = null;
        consumer.Dispatcher = null;
        _ = consumer.Registration.Unregister();
        consumer.Registration = default;
        DispatcherOperation? operation = consumer.Operation;
        consumer.Operation = null;
        return operation;
    }

    private void RemovePendingLocked(TimelineRasterCacheKey key, PendingWork work)
    {
        if (!_inFlight.TryGetValue(key, out PendingWork? current)
            || !ReferenceEquals(current, work)) return;
        _inFlight.Remove(key);
        foreach (Completion completion in work.Completions.Values.ToArray())
            RemoveSubscriptionLocked(completion);
        if (work.Priority != TimelineRasterRequestPriority.Visible)
            _speculativeInFlight = Math.Max(0, _speculativeInFlight - 1);
        if (!work.Running) work.ExecutionCancellation.Dispose();
        work.Factory = null;
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            try
            {
                await _workAvailable.WaitAsync().ConfigureAwait(false);
                if (!TryStartNext(out TimelineRasterCacheKey key, out PendingWork? work))
                    continue;
                Execute(key, work!);
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Timeline raster worker failed: {exception}");
            }
        }
    }

    private bool TryStartNext(
        out TimelineRasterCacheKey key,
        out PendingWork? work)
    {
        lock (_gate)
        {
            if (TryDequeueLocked(_visibleQueue, TimelineRasterRequestPriority.Visible, out key, out work)
                || _runningSpeculative < RecommendedBackgroundConcurrency
                && (TryDequeueLocked(
                        _normalQueue,
                        TimelineRasterRequestPriority.Normal,
                        out key,
                        out work)
                    || TryDequeueLocked(
                        _backgroundQueue,
                        TimelineRasterRequestPriority.Background,
                        out key,
                        out work)))
            {
                work!.Running = true;
                _runningCount++;
                if (work.Priority != TimelineRasterRequestPriority.Visible)
                    _runningSpeculative++;
                return true;
            }
        }
        key = default;
        work = null;
        return false;
    }

    private void Execute(
        TimelineRasterCacheKey key,
        PendingWork work)
    {
        BitmapSource? bitmap = null;
        long bytes = 0;
        try
        {
            Func<CancellationToken, TimelineRasterBuffer>? factory;
            lock (_gate)
            {
                factory = _inFlight.TryGetValue(key, out PendingWork? pending)
                    && ReferenceEquals(pending, work)
                    && work.Completions.Count > 0
                        ? work.Factory
                        : null;
            }
            if (factory is not null)
            {
                TimelineRasterBuffer buffer = factory(work.ExecutionCancellation.Token);
                work.ExecutionCancellation.Token.ThrowIfCancellationRequested();
                bool hasLiveConsumer;
                lock (_gate)
                {
                    hasLiveConsumer = _inFlight.TryGetValue(key, out PendingWork? pending)
                        && ReferenceEquals(pending, work)
                        && work.Completions.Count > 0;
                }
                if (hasLiveConsumer)
                {
                    bitmap = buffer.CreateFrozenBitmap();
                    bytes = buffer.ByteSize;
                }
            }
        }
        catch (OperationCanceledException) when (work.ExecutionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Timeline rasterization failed: {exception}");
        }

        Completion[] callbacks;
        bool signalQueuedWork = false;
        lock (_gate)
        {
            callbacks = work.Completions.Values.ToArray();
            work.Completions.Clear();
            foreach (Completion callback in callbacks) callback.Work = null;
            work.Factory = null;
            if (_inFlight.TryGetValue(key, out PendingWork? pending)
                && ReferenceEquals(pending, work))
            {
                _inFlight.Remove(key);
                if (work.Priority != TimelineRasterRequestPriority.Visible)
                    _speculativeInFlight = Math.Max(0, _speculativeInFlight - 1);
                if (bitmap is not null
                    && work.Generation == _generation
                    && !work.ExecutionCancellation.IsCancellationRequested
                    && callbacks.Length > 0
                    && bytes <= MaximumBytes)
                {
                    LinkedListNode<TimelineRasterCacheKey> node = _lru.AddFirst(key);
                    _completed[key] = new(bitmap, bytes, node);
                    _currentBytes = checked(_currentBytes + bytes);
                    TrimLocked();
                }
            }
            if (work.Priority != TimelineRasterRequestPriority.Visible)
                _runningSpeculative = Math.Max(0, _runningSpeculative - 1);
            _runningCount--;
            work.ExecutionCancellation.Dispose();
            signalQueuedWork = HasRunnableQueuedWorkLocked();
        }
        foreach (Completion callback in callbacks)
        {
            QueueCompletion(callback);
        }
        if (signalQueuedWork) _workAvailable.Release();
    }

    public void Clear()
    {
        List<DispatcherOperation> operations = [];
        lock (_gate)
        {
            _generation = checked(_generation + 1);
            foreach (Completion completion in _subscriptions.Values.ToArray())
                if (RemoveSubscriptionLocked(completion) is { } operation) operations.Add(operation);
            foreach (var pair in _inFlight.ToArray())
            {
                _ = pair.Value.ExecutionCancellation.CancelAsync();
                RemovePendingLocked(pair.Key, pair.Value);
            }
            _visibleQueue.Clear();
            _normalQueue.Clear();
            _backgroundQueue.Clear();
            _completed.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
        foreach (DispatcherOperation operation in operations) operation.Abort();
    }

    private void TrimLocked()
    {
        while (_currentBytes > MaximumBytes && _lru.Last is LinkedListNode<TimelineRasterCacheKey> node)
        {
            _lru.RemoveLast();
            if (_completed.Remove(node.Value, out CacheEntry? entry))
            {
                _currentBytes -= entry.Bytes;
            }
        }
    }

    private void EnqueueLocked(
        TimelineRasterCacheKey key,
        TimelineRasterRequestPriority priority)
    {
        switch (priority)
        {
            case TimelineRasterRequestPriority.Visible:
                _visibleQueue.Enqueue(key);
                break;
            case TimelineRasterRequestPriority.Normal:
                _normalQueue.Enqueue(key);
                break;
            default:
                _backgroundQueue.Enqueue(key);
                break;
        }
        if (_visibleQueue.Count + _normalQueue.Count + _backgroundQueue.Count
            > MaximumInFlight * 2)
        {
            CompactQueueLocked(_visibleQueue, TimelineRasterRequestPriority.Visible);
            CompactQueueLocked(_normalQueue, TimelineRasterRequestPriority.Normal);
            CompactQueueLocked(_backgroundQueue, TimelineRasterRequestPriority.Background);
        }
    }

    private void CompactQueueLocked(
        Queue<TimelineRasterCacheKey> queue,
        TimelineRasterRequestPriority priority)
    {
        HashSet<TimelineRasterCacheKey> seen = [];
        int count = queue.Count;
        for (int index = 0; index < count; index++)
        {
            TimelineRasterCacheKey key = queue.Dequeue();
            if (_inFlight.TryGetValue(key, out PendingWork? work)
                && !work.Running && work.Priority == priority && seen.Add(key))
                queue.Enqueue(key);
        }
    }

    private void QueueCompletion(Completion completion)
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (completion.Action is null) return;
            if (completion.Generation != _generation
                || completion.Key.Token.IsCancellationRequested)
            {
                RemoveSubscriptionLocked(completion);
                return;
            }
            dispatcher = completion.Dispatcher;
            completion.Queued = true;
        }
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            CancelSubscription(completion);
            return;
        }
        try
        {
            // The queued delegate holds only a revocable delivery. Cancelling
            // clears its owner callback even while the Dispatcher is blocked.
            DispatcherOperation operation = dispatcher.BeginInvoke(
                () => DeliverCompletion(completion), DispatcherPriority.Render);
            operation.Aborted += (_, _) => CancelSubscription(completion);
            bool revoked;
            lock (_gate)
            {
                revoked = completion.Action is null;
                if (!revoked) completion.Operation = operation;
            }
            if (revoked) operation.Abort();
            if (operation.Status == DispatcherOperationStatus.Aborted)
                CancelSubscription(completion);
        }
        catch (InvalidOperationException)
        {
            CancelSubscription(completion);
        }
    }

    private void DeliverCompletion(Completion completion)
    {
        Action? action;
        lock (_gate)
        {
            action = completion.Generation == _generation
                && !completion.Key.Token.IsCancellationRequested
                    ? completion.Action
                    : null;
            RemoveSubscriptionLocked(completion);
        }
        // Surface cancellation and delivery are serialized on its Dispatcher.
        // The additional token test also closes cancellation during dequeue.
        if (Volatile.Read(ref _generation) == completion.Generation
            && !completion.Key.Token.IsCancellationRequested) action?.Invoke();
    }

    private bool TryDequeueLocked(
        Queue<TimelineRasterCacheKey> queue,
        TimelineRasterRequestPriority priority,
        out TimelineRasterCacheKey key,
        out PendingWork? work)
    {
        while (queue.TryDequeue(out key))
        {
            if (_inFlight.TryGetValue(key, out work)
                && !work.Running
                && work.Priority == priority)
            {
                return true;
            }
        }
        key = default;
        work = null;
        return false;
    }

    private bool HasRunnableQueuedWorkLocked() =>
        _visibleQueue.Count != 0
        || _runningSpeculative < RecommendedBackgroundConcurrency
        && (_normalQueue.Count != 0 || _backgroundQueue.Count != 0);

    private sealed record CacheEntry(
        BitmapSource Bitmap,
        long Bytes,
        LinkedListNode<TimelineRasterCacheKey> Node);

    private readonly record struct SubscriptionKey(
        long ConsumerId,
        TimelineRasterCacheKey Key,
        CancellationToken Token);

    private sealed class Completion(
        SubscriptionKey key,
        long generation,
        Dispatcher dispatcher,
        Action action,
        PendingWork? work)
    {
        public SubscriptionKey Key { get; } = key;
        public long Generation { get; } = generation;
        public Dispatcher? Dispatcher { get; set; } = dispatcher;
        public Action? Action { get; set; } = action;
        public PendingWork? Work { get; set; } = work;
        public bool Queued { get; set; }
        public DispatcherOperation? Operation { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class PendingWork(
        Func<CancellationToken, TimelineRasterBuffer> factory,
        long generation,
        TimelineRasterRequestPriority priority)
    {
        public Func<CancellationToken, TimelineRasterBuffer>? Factory { get; set; } = factory;
        public long Generation { get; } = generation;
        public TimelineRasterRequestPriority Priority { get; set; } = priority;
        public Dictionary<SubscriptionKey, Completion> Completions { get; } = [];
        public CancellationTokenSource ExecutionCancellation { get; } = new();
        public bool Running { get; set; }
    }
}

public static class TimelineRasterCacheSession
{
    public static long CurrentBytes => TimelineRasterCache.Shared.CurrentBytes;
    public static int CompletedCount => TimelineRasterCache.Shared.CompletedCount;
    public static int InFlightCount => TimelineRasterCache.Shared.InFlightCount;
    public static int ActiveSubscriptionCount => TimelineRasterCache.Shared.ActiveSubscriptionCount;
    public static int QueuedCompletionCount => TimelineRasterCache.Shared.QueuedCompletionCount;
    public static int RunningCount => TimelineRasterCache.Shared.RunningCount;
    public static void Clear() => TimelineRasterCache.Shared.Clear();
}
