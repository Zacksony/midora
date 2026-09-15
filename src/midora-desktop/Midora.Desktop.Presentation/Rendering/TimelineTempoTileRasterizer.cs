using System.Windows.Media;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

/// <summary>
/// Device-column projection of a step signal. Only raster summaries are reduced;
/// the immutable source remains the authority for hit tests and editing.
/// </summary>
public static class TimelineTempoTileRasterizer
{
    public static TimelineRasterBuffer Rasterize(TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection, double pixelsPerTick, double pixelsPerValue,
        long tileX, long tileY, double dpiX, double dpiY, Color normal, Color primary,
        Color border, bool selectionOnly, CancellationToken cancellationToken)
    {
        ConductorRenderItemSource source = snapshot.ConductorSource
            ?? throw new ArgumentException("A Tempo projection is required.", nameof(snapshot));
        int gutterX = TimelineEventPointTileRasterizer.GetGutter(dpiX);
        int gutterY = TimelineEventPointTileRasterizer.GetGutter(dpiY);
        int width = TimelineEventPointTileRasterizer.TileSize + gutterX * 2;
        int height = TimelineEventPointTileRasterizer.TileSize + gutterY * 2;
        double left = tileX * (double)TimelineEventPointTileRasterizer.TileSize - gutterX;
        double top = tileY * (double)TimelineEventPointTileRasterizer.TileSize - gutterY;
        long start = TimelineTickMath.RasterQueryStart(left / pixelsPerTick);
        long end = TimelineTickMath.RasterQueryEnd((left + width) / pixelsPerTick, start);
        var columns = new TempoColumn[width];
        int count = 0;
        source.Visit(start, end, 0, 1, item =>
        {
            if ((count++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            int x = (int)Math.Floor(item.StartTick * pixelsPerTick - left);
            if ((uint)x >= (uint)width) return;
            bool selected = selection?.Contains(item.Id) ?? item.State.HasFlag(TimelineItemState.Selected);
            columns[x].Add(item.Value, selected, selection?.Primary == item.Id);
        }, cancellationToken);

        byte[] pixels = new byte[checked(width * height * 4)];
        double radiusX = Math.Max(1, TimelineEventPointTileRasterizer.PointRadius * dpiX);
        double radiusY = Math.Max(1, TimelineEventPointTileRasterizer.PointRadius * dpiY);
        Color signal = Color.FromArgb(175, normal.R, normal.G, normal.B);
        TimelineRenderItem previous = default;
        bool hasPrevious = !selectionOnly && source.TryGetTempoBefore(start, out previous);
        double lastValue = hasPrevious ? previous.Value : 0;
        int previousX = 0;
        int previousPointX = -100000;
        for (int x = 0; x < columns.Length; x++)
        {
            if ((x & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            TempoColumn column = columns[x];
            if (column.Count == 0) continue;
            if (!selectionOnly)
            {
                if (hasPrevious)
                {
                    Horizontal(previousX, x, Y(lastValue), signal);
                    Vertical(x, Y(lastValue), Y(column.First), signal);
                }
                if (column.Count > 1) Vertical(x, Y(column.Minimum), Y(column.Maximum), normal);
                hasPrevious = true;
                lastValue = column.Last;
                previousX = x;
            }
            if (column.SelectedCount > 0)
            {
                int halfWidth = Math.Max(1, (int)Math.Ceiling(dpiX));
                for (int offset = -halfWidth; offset <= halfWidth; offset++)
                    Vertical(x + offset, Y(column.SelectedMinimum), Y(column.SelectedMaximum), primary);
            }

            int nextX = x + 1;
            while (nextX < columns.Length && columns[nextX].Count == 0) nextX++;
            bool separated = column.Count == 1 && x - previousPointX >= radiusX * 2
                && nextX - x >= radiusX * 2;
            if (separated && (!selectionOnly || column.SelectedCount != 0))
            {
                double y = Y(column.First);
                if (y + radiusY >= 0 && y - radiusY < height)
                {
                    if (column.SelectedCount > 0)
                        TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, y,
                            radiusX + 2 * dpiX, radiusY + 2 * dpiY, primary);
                    TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, y, radiusX, radiusY, border);
                    TimelineEventPointTileRasterizer.FillEllipse(pixels, width, height, x, y,
                        Math.Max(0.5, radiusX - dpiX), Math.Max(0.5, radiusY - dpiY), column.SelectedCount > 0 ? primary : normal);
                }
            }
            previousPointX = x;
        }
        if (hasPrevious && !selectionOnly) Horizontal(previousX, width - 1, Y(lastValue), signal);
        cancellationToken.ThrowIfCancellationRequested();
        return new(width, height, pixels, count);

        double Y(double value) => (1 - value) * pixelsPerValue - top;
        void Horizontal(int x0, int x1, double y, Color color)
        {
            if (!double.IsFinite(y) || y < 0 || y >= height) return;
            int row = (int)Math.Floor(y);
            for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++) Pixel(x, row, color);
        }
        void Vertical(int x, double y0, double y1, Color color)
        {
            double low = Math.Max(0, Math.Min(y0, y1));
            double high = Math.Min(height - 1, Math.Max(y0, y1));
            if (!double.IsFinite(low) || !double.IsFinite(high) || low > high) return;
            for (int y = (int)Math.Floor(low); y <= (int)Math.Ceiling(high); y++) Pixel(x, y, color);
        }
        void Pixel(int x, int y, Color color)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)((color.B * color.A + 127) / 255);
            pixels[offset + 1] = (byte)((color.G * color.A + 127) / 255);
            pixels[offset + 2] = (byte)((color.R * color.A + 127) / 255);
            pixels[offset + 3] = color.A;
        }
    }

    private struct TempoColumn
    {
        public int Count, SelectedCount;
        public double First, Last, Minimum, Maximum, SelectedMinimum, SelectedMaximum;
        public bool Primary;
        public void Add(double value, bool selected, bool primary)
        {
            if (Count++ == 0) First = Minimum = Maximum = value;
            else { Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value); }
            Last = value;
            if (!selected) return;
            if (SelectedCount++ == 0) SelectedMinimum = SelectedMaximum = value;
            else { SelectedMinimum = Math.Min(SelectedMinimum, value); SelectedMaximum = Math.Max(SelectedMaximum, value); }
            Primary |= primary;
        }
    }
}
