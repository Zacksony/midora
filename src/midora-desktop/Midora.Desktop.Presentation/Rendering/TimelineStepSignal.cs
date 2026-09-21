using System.Windows.Media;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

public readonly record struct TimelineStepPoint(long Tick, long Order, double Value);

/// <summary>First/last refer to musical order, not enumeration order or Stable ID.</summary>
public readonly record struct TimelineStepSummary(long Count, TimelineStepPoint First,
    TimelineStepPoint Last, double Minimum, double Maximum)
{
    public bool OrderSensitive { get; init; }
    public TimelineStepSummary Add(TimelineStepPoint point, Func<long, long>? resolveOrder = null)
    {
        if (Count == 0) return new(1, point, point, point.Value, point.Value);
        return new(checked(Count + 1), Before(point, First, resolveOrder) ? point : First,
            Before(Last, point, resolveOrder) ? point : Last, Math.Min(Minimum, point.Value), Math.Max(Maximum, point.Value))
        { OrderSensitive = OrderSensitive || resolveOrder is not null && (point.Tick == First.Tick || point.Tick == Last.Tick) };
    }
    private static bool Before(TimelineStepPoint a, TimelineStepPoint b, Func<long, long>? resolve) =>
        a.Tick < b.Tick || a.Tick == b.Tick && (resolve?.Invoke(a.Order) ?? a.Order) < (resolve?.Invoke(b.Order) ?? b.Order);
}

/// <summary>
/// An immutable lane projection. Queries run only in existing cancellable raster workers.
/// The shared bounded cache retains scalar summaries, never Project/snapshot delegates.
/// Dyadic suffixes find predecessors without rescanning [0, viewportStart) on every frame.
/// </summary>
public sealed class TimelineStepSignalSource(
    string identity, Func<long, long, ulong> fingerprint,
    Func<long, long, CancellationToken, IEnumerable<TimelineStepPoint>> query,
    Func<long, long>? resolveOrder = null, ulong formalOrderRevision = 0,
    Action<CancellationToken>? prepareOrder = null)
{
    public string Identity { get; } = identity;
    public long EndTick { get; init; } = long.MaxValue;
    public const int SharedSummaryCapacity = 16_384;
    private const int LocalCapacity = 1_024;
    private static readonly object SharedLock = new();
    private static readonly Dictionary<CacheKey, (TimelineStepSummary Summary, ulong OrderRevision)> Shared = [];
    private static readonly Queue<CacheKey> SharedOrder = [];
    private readonly object _lock = new();
    private readonly Dictionary<(long, long), TimelineStepSummary> _local = [];
    private readonly Queue<(long, long)> _order = [];
    private readonly Dictionary<(long, long), ulong> _fingerprints = [];
    private readonly Queue<(long, long)> _fingerprintOrder = [];
    private volatile bool _orderReady;
    private readonly record struct CacheKey(string Identity, long Start, long End, ulong Fingerprint);

    public TimelineStepSummary Summarize(long start, long end, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (end <= start) return default;
        lock (_lock) if (_local.TryGetValue((start, end), out var ready)) return ready;
        var key = new CacheKey(Identity, start, end, RangeFingerprint(start, end));
        TimelineStepSummary summary;
        bool found;
        lock (SharedLock)
        {
            found = Shared.TryGetValue(key, out var cached)
                && (!cached.Summary.OrderSensitive || cached.OrderRevision == formalOrderRevision);
            summary = found ? cached.Summary : default;
        }
        if (!found)
        {
            int visited = 0;
            foreach (var point in query(start, end, token))
            {
                if ((visited++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (point.Tick >= start && point.Tick < end)
                {
                    if (!_orderReady && prepareOrder is not null && summary.Count != 0
                        && (point.Tick == summary.First.Tick || point.Tick == summary.Last.Tick))
                    {
                        // Reuse the owner's existing ordinal directory; cold
                        // preparation must observe cancellation too.
                        prepareOrder(token);
                        _orderReady = true;
                    }
                    summary = summary.Add(point, resolveOrder);
                }
            }
            token.ThrowIfCancellationRequested();
            lock (SharedLock)
            {
                if (!Shared.ContainsKey(key)) SharedOrder.Enqueue(key);
                Shared[key] = (summary, formalOrderRevision);
                while (Shared.Count > SharedSummaryCapacity) Shared.Remove(SharedOrder.Dequeue());
            }
        }
        lock (_lock)
        {
            if (_local.TryAdd((start, end), summary)) _order.Enqueue((start, end));
            while (_local.Count > LocalCapacity) _local.Remove(_order.Dequeue());
        }
        return summary;
    }

    public bool TryGetBefore(long tick, out TimelineStepPoint point, CancellationToken token = default)
    {
        long end = tick;
        while (end > 0)
        {
            // Largest aligned suffix: at most 63 metadata/summary probes.
            long size = end & -end;
            var summary = Summarize(end - size, end, token);
            if (summary.Count != 0) { point = summary.Last; return true; }
            end -= size;
        }
        point = default;
        return false;
    }

    public ulong GetFingerprint(long start, long end, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ulong hash = RangeFingerprint(start, end);
        if (TryGetBefore(start, out var previous, token))
        {
            hash = (hash ^ (ulong)previous.Tick) * 1099511628211UL;
            hash = (hash ^ (ulong)previous.Order) * 1099511628211UL;
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(previous.Value)) * 1099511628211UL;
        }
        return hash;
    }

    private ulong RangeFingerprint(long start, long end)
    {
        // This source is immutable. Reusing its bounded metadata result also
        // avoids rebuilding a page-reference list on warm raster preparation.
        lock (_lock) if (_fingerprints.TryGetValue((start, end), out ulong ready)) return ready;
        ulong value = fingerprint(start, end);
        lock (_lock)
        {
            if (_fingerprints.TryAdd((start, end), value)) _fingerprintOrder.Enqueue((start, end));
            while (_fingerprints.Count > LocalCapacity) _fingerprints.Remove(_fingerprintOrder.Dequeue());
        }
        return value;
    }

    public static void ClearSharedCache()
    {
        lock (SharedLock) { Shared.Clear(); SharedOrder.Clear(); }
    }
}

public static class TimelineStepSignalRasterizer
{
    public static ulong Fingerprint(TimelineStepSignalSource source, double pixelsPerTick, long tileX, double dpiX,
        CancellationToken token = default)
    {
        var range = Range(pixelsPerTick, tileX, dpiX);
        ulong hash = source.GetFingerprint(range.Start, range.End, token);
        // Spatial source hashes may deliberately ignore formal-list order.
        // First/last of each device column capture precisely the order which
        // affects this raster, without invalidating unrelated sparse tiles.
        foreach (var column in Columns(source, pixelsPerTick, tileX, dpiX, token))
        {
            hash = (hash ^ (ulong)column.First.Order) * 1099511628211UL;
            hash = (hash ^ (ulong)column.Last.Order) * 1099511628211UL;
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(column.First.Value)) * 1099511628211UL;
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(column.Last.Value)) * 1099511628211UL;
        }
        return hash;
    }

    public static (long Start, long End) Range(double pixelsPerTick, long tileX, double dpiX)
    {
        ValidateScale(pixelsPerTick, nameof(pixelsPerTick));
        ValidateScale(dpiX, nameof(dpiX));
        int gutter = TimelineEventPointTileRasterizer.GetGutter(dpiX);
        double left = tileX * (double)TimelineEventPointTileRasterizer.TileSize - gutter;
        long start = TimelineTickMath.RasterQueryStart(left / pixelsPerTick);
        return (start, TimelineTickMath.RasterQueryEnd((left + TimelineEventPointTileRasterizer.TileSize + 2 * gutter) / pixelsPerTick, start));
    }

    public static TimelineRasterBuffer Rasterize(TimelineStepSignalSource source,
        double pixelsPerTick, double pixelsPerValue, long tileX, long tileY,
        double dpiX, double dpiY, Color color, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateScale(pixelsPerTick, nameof(pixelsPerTick));
        ValidateScale(pixelsPerValue, nameof(pixelsPerValue));
        ValidateScale(dpiX, nameof(dpiX));
        ValidateScale(dpiY, nameof(dpiY));
        int gx = TimelineEventPointTileRasterizer.GetGutter(dpiX), gy = TimelineEventPointTileRasterizer.GetGutter(dpiY);
        int width = TimelineEventPointTileRasterizer.TileSize + gx * 2;
        int height = TimelineEventPointTileRasterizer.TileSize + gy * 2;
        double top = tileY * (double)TimelineEventPointTileRasterizer.TileSize - gy;
        var (start, _) = Range(pixelsPerTick, tileX, dpiX);
        byte[] pixels = new byte[width * height * 4];
        // At most one bounded summary per device column. Exact point records
        // remain exclusively in the separate point/hit/selection pipeline.
        var columns = Columns(source, pixelsPerTick, tileX, dpiX, token);
        bool hasPrevious = source.TryGetBefore(start, out var previous, token);
        double last = previous.Value;
        int previousX = 0;
        int lineWidth = Math.Max(1, (int)Math.Round(dpiX));
        int lineHeight = Math.Max(1, (int)Math.Round(dpiY));
        for (int x = 0; x < width; x++)
        {
            var column = columns[x];
            if (column.Count == 0) continue;
            if (hasPrevious)
            {
                Horizontal(previousX, x, Y(last));
                Vertical(x, Y(last), Y(column.First.Value));
            }
            if (column.Count > 1) Vertical(x, Y(column.Minimum), Y(column.Maximum));
            hasPrevious = true; last = column.Last.Value; previousX = x;
        }
        if (hasPrevious) Horizontal(previousX, width - 1, Y(last));
        return new(width, height, pixels);

        double Y(double value) => (1 - value) * pixelsPerValue - top;
        void Horizontal(int from, int to, double y)
        {
            if (y < -lineHeight || y >= height) return;
            int iy = (int)Math.Floor(y);
            for (int row = Math.Max(0, iy); row < Math.Min(height, iy + lineHeight); row++)
                for (int x = Math.Max(0, from); x <= Math.Min(width - 1, to); x++) Pixel(x, row);
        }
        void Vertical(int x, double from, double to)
        {
            double lo = Math.Max(0, Math.Min(from, to)), hi = Math.Min(height - 1, Math.Max(from, to));
            if (hi < lo) return;
            for (int y = (int)Math.Floor(lo); y <= (int)Math.Floor(hi); y++)
                for (int col = x; col < Math.Min(width, x + lineWidth); col++) Pixel(col, y);
        }
        void Pixel(int x, int y)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)(color.B * color.A / 255);
            pixels[i + 1] = (byte)(color.G * color.A / 255);
            pixels[i + 2] = (byte)(color.R * color.A / 255); pixels[i + 3] = color.A;
        }
    }

    private static void ValidateScale(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static TimelineStepSummary[] Columns(TimelineStepSignalSource source, double pixelsPerTick,
        long tileX, double dpiX, CancellationToken token)
    {
        int gutter = TimelineEventPointTileRasterizer.GetGutter(dpiX);
        int width = TimelineEventPointTileRasterizer.TileSize + gutter * 2;
        double left = tileX * (double)TimelineEventPointTileRasterizer.TileSize - gutter;
        var (start, end) = Range(pixelsPerTick, tileX, dpiX);
        var columns = new TimelineStepSummary[width];
        long cursor = start;
        for (int x = 0; x < width && cursor < end; x++)
        {
            token.ThrowIfCancellationRequested();
            double boundary = Math.Ceiling((left + x + 1) / pixelsPerTick);
            long next = boundary >= long.MaxValue ? long.MaxValue : boundary <= 0 ? 0 : (long)boundary;
            next = Math.Min(end, next);
            if (next > cursor) { columns[x] = source.Summarize(cursor, next, token); cursor = next; }
        }
        return columns;
    }
}
