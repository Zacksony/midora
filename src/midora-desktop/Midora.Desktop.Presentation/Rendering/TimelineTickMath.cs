namespace Midora.Desktop.Presentation.Rendering;

/// <summary>
/// Finite-domain display/navigation arithmetic only. Musical edit results must
/// use checked arithmetic instead of these saturating projections.
/// </summary>
public static class TimelineTickMath
{
    public static int RoundRasterBoundary(double pixels)
    {
        // Off-tile edges need only preserve side/order, not an unbounded pixel
        // index. Headroom also protects the outline's +1 / -1 arithmetic.
        const int limit = 1 << 29;
        return pixels <= -limit ? -limit : pixels >= limit ? limit : (int)Math.Floor(pixels + 0.5);
    }

    // Cache queries may include an off-domain tile/prefetch gutter. Keep the
    // query interval nonempty and inside Int64; do not use these for edits.
    public static long RasterQueryStart(double tick)
    {
        if (tick > (1L << 52)) tick = Math.BitDecrement(Math.BitDecrement(tick));
        return tick <= 0 ? 0 : tick >= long.MaxValue ? long.MaxValue - 1 : (long)Math.Floor(tick);
    }

    public static long RasterQueryEnd(double tick, long start)
    {
        if (tick > (1L << 52)) tick = Math.BitIncrement(Math.BitIncrement(tick));
        return Math.Max(start + 1, tick >= long.MaxValue ? long.MaxValue
            : tick <= 0 ? 0 : (long)Math.Ceiling(tick));
    }

    public static TickIndexRange InclusiveIndices(long first, long last) => new(first, last);

    public readonly struct TickIndexRange(long first, long last)
    {
        public Enumerator GetEnumerator() => new(first, last);
        public struct Enumerator(long first, long last)
        {
            private bool _started;
            public long Current { get; private set; } = first;
            public bool MoveNext()
            {
                if (!_started) { _started = true; return Current <= last; }
                if (Current >= last) return false;
                Current++;
                return true;
            }
        }
    }

    public static long Clamp(Int128 tick) => tick <= 0 ? 0
        : tick >= long.MaxValue ? long.MaxValue : (long)tick;

    public static long CeilingDistance(double ticks)
    {
        if (double.IsNaN(ticks)) throw new ArgumentOutOfRangeException(nameof(ticks));
        return ticks <= 1 ? 1 : ticks >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(ticks);
    }

    public static long RoundSignedDistance(double ticks)
    {
        if (!double.IsFinite(ticks)) throw new ArgumentOutOfRangeException(nameof(ticks));
        return ticks <= long.MinValue ? long.MinValue : ticks >= long.MaxValue ? long.MaxValue
            : (long)Math.Round(ticks, MidpointRounding.AwayFromZero);
    }

    public static long Pan(long startTick, long delta) =>
        Math.Min(long.MaxValue - 1, Clamp((Int128)startTick + delta));

    public static long ScaleSpan(long span, double factor)
    {
        if (span <= 0 || !double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(span));
        return Math.Clamp(RoundSignedDistance(span * factor), 16, 1L << 50);
    }

    public static long ProjectFraction(long start, long end, double fraction, bool containing = false)
    {
        if (start < 0 || end <= start || !double.IsFinite(fraction))
            throw new ArgumentOutOfRangeException(nameof(end));
        fraction = Math.Clamp(fraction, 0, 1);
        long length = end - start;
        double relative = fraction * length;
        long offset = relative >= length ? length
            : (long)(containing ? Math.Floor(relative) : Math.Round(relative, MidpointRounding.AwayFromZero));
        if (containing) offset = Math.Min(offset, length - 1);
        return start + offset;
    }
}
