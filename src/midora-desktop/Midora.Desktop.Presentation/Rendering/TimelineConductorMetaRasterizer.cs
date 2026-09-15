using System.Collections.Concurrent;
using System.Windows.Media;

namespace Midora.Desktop.Presentation.Rendering;

public readonly record struct ConductorMetaLabel(double DeviceX, string Text);

internal static class TimelineConductorMetaRasterizer
{
    private const int MaximumLabelTiles = 2048;
    private static readonly ConcurrentDictionary<TimelineRasterCacheKey, ConductorMetaLabel[]> Labels = [];
    private static readonly ConcurrentQueue<TimelineRasterCacheKey> LabelOrder = [];
    public const int TileSize = 256;

    internal static bool TryGetLabels(TimelineRasterCacheKey key, out ConductorMetaLabel[]? labels) => Labels.TryGetValue(key, out labels);

    internal static (long Start, long End) GetReadTickRange(double pixelsPerTick, long tileX, double dpiX)
    {
        int gutter = TimelineEventPointTileRasterizer.GetGutter(dpiX);
        double cellWidth = 96 * dpiX;
        double left = Math.Floor((tileX * (double)TileSize - gutter) / cellWidth) * cellWidth;
        double right = Math.Ceiling(((tileX + 1) * (double)TileSize + gutter) / cellWidth) * cellWidth;
        return (Math.Max(0, Floor(left / pixelsPerTick)), Ceiling(right / pixelsPerTick));
    }

    public static TimelineRasterBuffer Rasterize(TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection, double pixelsPerTick, double laneHeight,
        long tileX, int lane, double dpiX, double dpiY, Color fallback, Color selectedColor,
        Color border, TimelineRasterCacheKey key, CancellationToken cancellationToken)
    {
        int gutterX = TimelineEventPointTileRasterizer.GetGutter(dpiX);
        int gutterY = TimelineEventPointTileRasterizer.GetGutter(dpiY);
        int width = TileSize + gutterX * 2;
        int height = Math.Max(1, checked((int)Math.Ceiling(laneHeight))) + gutterY * 2;
        double left = tileX * (double)TileSize - gutterX;
        (long start, long end) = GetReadTickRange(pixelsPerTick, tileX, dpiX);
        var columns = new Column[width];
        Dictionary<long, TimelineRenderItem> labelCells = [];
        int count = 0;
        snapshot.ConductorSource!.Visit(start, end, lane, lane + 1, item =>
        {
            if ((count++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            double worldX = item.StartTick * pixelsPerTick;
            long cell = (long)Math.Floor(worldX / (96 * dpiX));
            if (!labelCells.TryGetValue(cell, out TimelineRenderItem labelItem)
                || item.StartTick < labelItem.StartTick
                || item.StartTick == labelItem.StartTick && item.Id.CompareTo(labelItem.Id) < 0)
                labelCells[cell] = item;
            int x = (int)Math.Round(item.StartTick * pixelsPerTick - left, MidpointRounding.AwayFromZero);
            if ((uint)x >= (uint)width) return;
            ref Column column = ref columns[x];
            column.Selected |= selection?.Contains(item.Id) ?? item.State.HasFlag(TimelineItemState.Selected);
            if (!column.HasContent || item.StartTick > column.Item.StartTick
                || item.StartTick == column.Item.StartTick && item.Id.CompareTo(column.Item.Id) < 0)
                column.Item = item;
            column.HasContent = true;
        }, cancellationToken);
        byte[] pixels = new byte[checked(width * height * 4)];
        List<ConductorMetaLabel> labels = [];
        double centerY = gutterY + laneHeight * 0.5;
        double radiusX = 4 * dpiX;
        double radiusY = 4 * dpiY;
        for (int x = 0; x < width; x++)
        {
            if ((x & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            Column column = columns[x];
            if (!column.HasContent) continue;
            uint argb = column.Item.AccentColor;
            Color color = column.Selected ? selectedColor : argb == 0 ? fallback
                : Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            if (column.Selected)
                TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, centerY,
                    radiusX + 2 * dpiX, radiusY + 2 * dpiY, selectedColor);
            TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, centerY, radiusX, radiusY, border);
            TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, centerY,
                Math.Max(.5, radiusX - dpiX), Math.Max(.5, radiusY - dpiY), color);
        }
        // Every occupied label cell has a deterministic representative, irrespective
        // of the point's phase within that cell. Query complete cells on both sides of
        // tile boundaries so the same label is emitted exactly once by its anchor tile.
        foreach ((long cell, TimelineRenderItem item) in labelCells.OrderBy(static x => x.Key))
        {
            double cellStart = cell * 96 * dpiX;
            double worldX = Math.Clamp(item.StartTick * pixelsPerTick - 34 * dpiX,
                cellStart + 4 * dpiX, cellStart + 24 * dpiX);
            if (worldX < tileX * (double)TileSize || worldX >= (tileX + 1) * (double)TileSize) continue;
            string text = ConductorRenderItemSource.GetDisplayLabel(item);
            if (text.Length > 48) text = text[..48] + "…";
            if (text.Length > 0) labels.Add(new(worldX, text));
        }
        if (Labels.TryAdd(key, labels.ToArray())) LabelOrder.Enqueue(key);
        while (Labels.Count > MaximumLabelTiles && LabelOrder.TryDequeue(out var oldest)) Labels.TryRemove(oldest, out _);
        return new(width, height, pixels, count);
    }

    private static long Floor(double value) => value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);
    private static long Ceiling(double value) => value <= 0 ? 1 : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
    private struct Column { public bool HasContent, Selected; public TimelineRenderItem Item; }
}
