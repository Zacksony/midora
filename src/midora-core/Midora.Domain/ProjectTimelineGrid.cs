namespace Midora.Domain;

/// <summary>
/// Shared integer-grid arithmetic for timeline presentation and Project edits.
/// Fixed musical subdivisions are anchored at Project tick zero; only the Bar
/// mode follows the effective Time Signature map.
/// </summary>
public static class ProjectTimelineGrid
{
    public readonly record struct SnappedRange(long StartTick, long EndTick);

    public static long ResolveWholeNoteFractionStep(
        int ticksPerQuarterNote,
        int numerator,
        int denominator)
    {
        if (ticksPerQuarterNote < 1)
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        if (numerator < 1)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator < 1)
            throw new ArgumentOutOfRangeException(nameof(denominator));

        Int128 scaled = (Int128)4 * ticksPerQuarterNote * numerator;
        Int128 step = (scaled + denominator - 1) / denominator;
        if (step < 1 || step > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        return (long)step;
    }

    public static long Snap(long tick, long gridStep, int tieDirection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfLessThan(gridStep, 1);
        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));

        long lower = tick / gridStep * gridStep;
        if (lower == tick)
            return lower;

        // The mathematical upper grid point can lie above Int64. Compare in a
        // wider domain first, so an unselected upper candidate cannot fail a
        // valid lower result and a selected unrepresentable result still fails.
        Int128 upperWide = (Int128)lower + gridStep;
        Int128 lowerDistance = (Int128)tick - lower;
        Int128 upperDistance = upperWide - tick;
        if (lowerDistance < upperDistance)
            return lower;
        if (upperDistance < lowerDistance)
            return checked((long)upperWide);
        return tieDirection > 0 ? checked((long)upperWide) : lower;
    }

    /// <summary>
    /// Snaps a signed coordinate to the project-zero grid. This is used when
    /// Segment content outside its active crop window maps before Project tick
    /// zero; the content remains valid even though its projected tick is
    /// negative.
    /// </summary>
    public static long SnapSigned(long tick, long gridStep, int tieDirection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gridStep, 1);
        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));
        if (tick >= 0)
            return Snap(tick, gridStep, tieDirection);

        // Integer division truncates toward zero, so this is the grid point
        // immediately above a negative tick. Compare in Int128 so the lower
        // point may fall outside Int64 without overflowing an unselected path.
        Int128 upperWide = (Int128)(tick / gridStep) * gridStep;
        if (upperWide == tick)
            return tick;
        Int128 lowerWide = upperWide - gridStep;
        Int128 lowerDistance = (Int128)tick - lowerWide;
        Int128 upperDistance = upperWide - tick;
        if (lowerDistance < upperDistance)
            return checked((long)lowerWide);
        if (upperDistance < lowerDistance)
            return checked((long)upperWide);
        return tieDirection > 0
            ? checked((long)upperWide)
            : checked((long)lowerWide);
    }

    public static long SnapAbsolute(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        int tieDirection)
        => TrySnapAbsolute(tick, fixedStepTicks, useBars, timeSignatureMap, tieDirection, out long result)
            ? result : throw new OverflowException("The snapped tick exceeds the Project tick domain.");

    public static bool TrySnapAbsolute(
        long tick, long fixedStepTicks, bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap, int tieDirection, out long result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));
        long lower;
        Int128 upper;
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarBounds bar = timeSignatureMap.GetBarBounds(tick);
            lower = bar.StartTick;
            upper = bar.EndTick;
        }
        else
        {
            long step = Math.Max(1, fixedStepTicks);
            lower = tick / step * step;
            upper = (Int128)lower + step;
        }
        long lowerDistance = tick - lower;
        Int128 upperDistance = upper - tick;
        return TryRepresent(lowerDistance < upperDistance || lowerDistance == upperDistance && tieDirection <= 0
            ? lower : upper, out result);
    }

    public static long SnapDelta(
        long delta,
        long targetTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        if (delta == 0)
            return 0;

        long step = Math.Max(1, fixedStepTicks);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarBounds bar = timeSignatureMap.GetBarBounds(Math.Max(0, targetTick));
            step = Math.Max(1, checked((long)(bar.EndTick - bar.StartTick)));
        }

        // Preserve the magnitude of Int64.MinValue and the existing tie rule
        // (negative movement chooses the smaller magnitude).
        if (delta != long.MinValue && Math.Abs(delta) <= long.MaxValue - step)
        {
            long magnitude64 = Math.Abs(delta);
            long snapped64 = Snap(magnitude64, step, delta > 0 ? 1 : -1);
            return delta < 0 ? -snapped64 : snapped64;
        }
        Int128 magnitude = Int128.Abs((Int128)delta);
        Int128 lower = magnitude / step * step;
        Int128 remainder = magnitude - lower;
        Int128 snapped = remainder * 2 < step || remainder * 2 == step && delta < 0
            ? lower : lower + (remainder == 0 ? 0 : step);
        return checked((long)(delta < 0 ? -snapped : snapped));
    }

    public static long GetGridTickAtOrAfter(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => TryGetGridTickAtOrAfter(tick, fixedStepTicks, useBars, timeSignatureMap, out long result)
            ? result : throw new OverflowException("The next grid tick exceeds the Project tick domain.");

    public static bool TryGetGridTickAtOrAfter(
        long tick, long fixedStepTicks, bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap, out long result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarBounds bar = timeSignatureMap.GetBarBounds(tick);
            return TryRepresent(bar.StartTick == tick ? tick : bar.EndTick, out result);
        }

        long step = Math.Max(1, fixedStepTicks);
        long lower = tick / step * step;
        return TryRepresent(lower == tick ? tick : (Int128)lower + step, out result);
    }

    public static long GetNextGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => TryGetNextGridTick(tick, fixedStepTicks, useBars, timeSignatureMap, out long result)
            ? result : throw new OverflowException("The next grid tick exceeds the Project tick domain.");

    public static bool TryGetNextGridTick(
        long tick, long fixedStepTicks, bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap, out long result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            return TryRepresent(timeSignatureMap.GetBarBounds(tick).EndTick, out result);
        }

        return TryRepresent((Int128)tick + Math.Max(1, fixedStepTicks), out result);
    }

    private static bool TryRepresent(Int128 tick, out long result)
    {
        result = tick <= long.MaxValue ? (long)tick : 0;
        return tick <= long.MaxValue;
    }

    public static long GetPreviousGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (tick == 0)
            return 0;
        if (useBars && timeSignatureMap is not null)
            return timeSignatureMap.GetBarBounds(tick - 1).StartTick;

        return Math.Max(0, tick - Math.Max(1, fixedStepTicks));
    }

    public static SnappedRange SnapRangeFromAnchor(
        long rawAnchorTick,
        long rawMovingTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawAnchorTick);
        ArgumentOutOfRangeException.ThrowIfNegative(rawMovingTick);
        bool movesRight = rawMovingTick >= rawAnchorTick;
        long anchor = SnapAbsolute(
            rawAnchorTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            0);
        long moving = SnapAbsolute(
            rawMovingTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            movesRight ? 1 : -1);

        if (movesRight)
        {
            long end = moving > anchor
                ? moving
                : GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
            return new(anchor, end);
        }

        long start = moving < anchor
            ? moving
            : GetPreviousGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        if (start < anchor)
            return new(start, anchor);

        long fallbackEnd = GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        return new(anchor, fallbackEnd);
    }

    public static SnappedRange SnapPositiveRange(
        long rawStartTick,
        long rawEndTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawStartTick);
        if (rawEndTick <= rawStartTick)
            throw new ArgumentOutOfRangeException(nameof(rawEndTick));
        return SnapRangeFromAnchor(
            rawStartTick,
            rawEndTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);
    }
}
