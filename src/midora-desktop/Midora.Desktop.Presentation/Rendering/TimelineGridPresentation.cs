using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

public enum TimelineGridLineKind
{
    Bar,
    Beat
}

public readonly record struct TimelineGridLine(long Tick, TimelineGridLineKind Kind);

public static class TimelineGridPresentation
{
    /// <summary>Labels use a global bar ordinal phase, never the viewport's left edge.</summary>
    public static void BuildBarRulerLines(long startTick, long endTick,
        ProjectTimeSignatureMap map, List<TimelineGridLine> destination,
        long minimumTickSpacing, long projectTickOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumTickSpacing, 1);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        Int128 first = Int128.Max(0, (Int128)startTick + projectTickOffset);
        Int128 end = Int128.Min((Int128)long.MaxValue + 1, (Int128)endTick + projectTickOffset);
        if (endTick <= startTick || end <= first) return;
        ulong lastBar = map.GetBarBounds((long)(end - 1)).Bar;
        // A map-wide stride remains stable while panning across a time-signature
        // boundary. Powers of two also preserve existing labels while zooming out.
        ulong wanted = (ulong)(1 + (minimumTickSpacing - 1) / map.MinimumFullBarLengthTicks);
        ulong stride = 1;
        while (stride < wanted) stride <<= 1;
        // Abrupt signature changes can create arbitrarily short partial bars.
        // Retain at most the first eligible ordinal in each globally anchored
        // tick bucket. This bounds work by visible label slots without making
        // one short bar suppress labels throughout the entire project.
        Int128 bucketStart = first / minimumTickSpacing * minimumTickSpacing;
        while (bucketStart < end)
        {
            ulong firstBar = map.GetBarBounds((long)bucketStart).Bar;
            UInt128 ordinal = ((UInt128)firstBar - 1 + stride - 1) / stride * stride + 1;
            if (ordinal > lastBar) break;
            long projectTick = map.GetTick(new((ulong)ordinal, 1, 0));
            if (projectTick < bucketStart)
            {
                ordinal += stride;
                if (ordinal > lastBar) break;
                projectTick = map.GetTick(new((ulong)ordinal, 1, 0));
            }
            Int128 local = (Int128)projectTick - projectTickOffset;
            if (local >= startTick && local < endTick)
                destination.Add(new((long)local, TimelineGridLineKind.Bar));
            bucketStart = ((Int128)projectTick / minimumTickSpacing + 1) * minimumTickSpacing;
        }
    }

    public static void BuildArrangementBarGridLines(
        long startTick,
        long endTick,
        ProjectTimeSignatureMap timeSignatureMap,
        List<TimelineGridLine> destination,
        long minimumTickSpacing = 1)
        => BuildBarGridLines(
            startTick,
            endTick,
            timeSignatureMap,
            destination,
            minimumTickSpacing,
            projectTickOffset: 0);

    public static void BuildBarGridLines(
        long startTick,
        long endTick,
        ProjectTimeSignatureMap timeSignatureMap,
        List<TimelineGridLine> destination,
        long minimumTickSpacing = 1,
        long projectTickOffset = 0,
        bool includeBeats = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        ArgumentNullException.ThrowIfNull(timeSignatureMap);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumTickSpacing, 1);
        destination.Clear();
        if (endTick <= startTick)
        {
            return;
        }

        Int128 projectStart = Int128.Max(0, (Int128)startTick + projectTickOffset);
        Int128 projectEnd = Int128.Min((Int128)long.MaxValue + 1, (Int128)endTick + projectTickOffset);
        if (projectEnd <= projectStart)
        {
            return;
        }

        ProjectBarBounds bar = timeSignatureMap.GetBarBounds((long)projectStart);
        Int128 nextEligibleTick = startTick;
        while (bar.StartTick < projectEnd)
        {
            Int128 localBarTick = (Int128)bar.StartTick - projectTickOffset;
            if (localBarTick >= nextEligibleTick && localBarTick < endTick)
            {
                destination.Add(new((long)localBarTick, TimelineGridLineKind.Bar));
                nextEligibleTick = localBarTick + minimumTickSpacing;
            }

            for (int beat = 1; includeBeats && beat < bar.Numerator; beat++)
            {
                Int128 projectBeatTick = (Int128)bar.StartTick + (long)beat * bar.TicksPerBeat;
                if (projectBeatTick >= bar.EndTick || projectBeatTick >= projectEnd)
                {
                    break;
                }
                Int128 localBeatTick = projectBeatTick - projectTickOffset;
                if (localBeatTick >= nextEligibleTick)
                {
                    destination.Add(new((long)localBeatTick, TimelineGridLineKind.Beat));
                    nextEligibleTick = localBeatTick + minimumTickSpacing;
                }
            }

            if (bar.EndTick <= bar.StartTick || bar.EndTick >= projectEnd)
            {
                break;
            }
            Int128 nextBarTick = bar.EndTick;
            Int128 nextEligibleProjectTick = nextEligibleTick + projectTickOffset;
            if (nextEligibleProjectTick >= projectEnd) break;
            if (nextBarTick < nextEligibleProjectTick)
            {
                ProjectBarBounds containing = timeSignatureMap.GetBarBounds(
                    (long)nextEligibleProjectTick);
                nextBarTick = containing.StartTick >= nextEligibleProjectTick
                    ? containing.StartTick
                    : containing.EndTick;
            }
            if (nextBarTick >= projectEnd)
            {
                break;
            }
            bar = timeSignatureMap.GetBarBounds((long)nextBarTick);
        }
    }

    public static void BuildFixedGridLines(long startTick, long endTick,
        long step, List<TimelineGridLine> destination, long minimumTickSpacing = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumTickSpacing, 1);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        // Skip indistinguishable lines without visiting every hidden grid tick.
        Int128 stride = (Int128)step * (1 + (minimumTickSpacing - 1) / step);
        if (!ProjectTimelineGrid.TryGetGridTickAtOrAfter(startTick, step, false, null, out long first)) return;
        for (Int128 tick = first; tick < endTick; tick += stride)
            destination.Add(new((long)tick, TimelineGridLineKind.Beat));
    }
}
