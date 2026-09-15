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
