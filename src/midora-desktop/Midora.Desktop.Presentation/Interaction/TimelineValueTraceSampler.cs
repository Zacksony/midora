using Midora.Domain;

namespace Midora.Desktop.Presentation.Interaction;

public readonly record struct TimelineValueTracePoint(
    long Tick,
    double NormalizedValue);

public static class TimelineValueTraceSampler
{
    /// <summary>Stream samples in gesture order; the command reduces revisited ticks.</summary>
    public static IEnumerable<TimelineValueTracePoint> EnumerateSamples(
        IReadOnlyList<TimelineValueTracePoint> trace, long fixedStepTicks, bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap, long? rangeStartTick = null,
        long? rangeEndTick = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedStepTicks);
        if (rangeStartTick is < 0 || rangeEndTick is < 0 || rangeEndTick < rangeStartTick)
            throw new ArgumentOutOfRangeException(nameof(rangeStartTick));
        foreach (TimelineValueTracePoint point in trace) { Validate(point); _ = Quantize(point.Tick); }
        if (trace.Count == 0) yield break;
        cancellationToken.ThrowIfCancellationRequested();
        long firstTick = Quantize(trace[0].Tick);
        if (InRange(firstTick)) yield return new(firstTick, Math.Clamp(trace[0].NormalizedValue, 0, 1));
        for (int index = 1; index < trace.Count; index++)
        {
            TimelineValueTracePoint from = trace[index - 1], to = trace[index];
            long fromTick = Quantize(from.Tick), toTick = Quantize(to.Tick);
            long tick = fromTick;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double ratio = toTick == fromTick ? 1
                    : Math.Clamp((tick - fromTick) / (double)(toTick - fromTick), 0, 1);
                if (InRange(tick)) yield return new(tick, Math.Clamp(from.NormalizedValue
                    + (to.NormalizedValue - from.NormalizedValue) * ratio, 0, 1));
                if (tick == toTick) break;
                long next;
                next = toTick > fromTick
                    ? ProjectTimelineGrid.TryGetNextGridTick(tick, fixedStepTicks, useBars, timeSignatureMap,
                        out long after) ? after : toTick
                    : TimelineGridQuantization.GetPreviousGridTick(tick, fixedStepTicks, useBars, timeSignatureMap);
                if (toTick > fromTick) { if (next <= tick) break; tick = Math.Min(next, toTick); }
                else { if (next >= tick) break; tick = Math.Max(next, toTick); }
            }
        }
        long Quantize(long tick) => TimelineGridQuantization.SnapAbsolute(
            tick,
            fixedStepTicks, useBars, timeSignatureMap, movementDirection: 0);
        bool InRange(long tick) => (!rangeStartTick.HasValue || tick >= rangeStartTick)
            && (!rangeEndTick.HasValue || tick < rangeEndTick);
    }

    public static void SampleInto(
        IReadOnlyList<TimelineValueTracePoint> trace,
        IDictionary<long, double> destination,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        long? rangeStartTick = null,
        long? rangeEndTick = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(destination);
        if (fixedStepTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStepTicks));
        }
        if (rangeStartTick is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStartTick));
        }
        if (rangeEndTick is < 0
            || rangeStartTick is long start
                && rangeEndTick is long end
                && end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEndTick));
        }

        foreach (TimelineValueTracePoint point in trace)
        {
            Validate(point);
            _ = Quantize(point.Tick);
        }
        destination.Clear();
        if (trace.Count == 0) return;

        if (trace.Count == 1)
        {
            AddSample(Quantize(trace[0].Tick), trace[0].NormalizedValue);
            return;
        }

        for (int index = 1; index < trace.Count; index++)
        {
            TimelineValueTracePoint from = trace[index - 1];
            TimelineValueTracePoint to = trace[index];
            long fromTick = Quantize(from.Tick);
            long toTick = Quantize(to.Tick);
            if (fromTick == toTick)
            {
                AddSample(toTick, to.NormalizedValue);
                continue;
            }

            int direction = toTick > fromTick ? 1 : -1;
            long tick = fromTick;
            while (true)
            {
                double ratio = Math.Clamp(
                    (tick - fromTick) / (double)(toTick - fromTick),
                    0,
                    1);
                AddSample(
                    tick,
                    from.NormalizedValue
                        + (to.NormalizedValue - from.NormalizedValue) * ratio);
                if (tick == toTick) break;

                long next;
                next = direction > 0
                    ? ProjectTimelineGrid.TryGetNextGridTick(tick, fixedStepTicks, useBars, timeSignatureMap,
                        out long after) ? after : toTick
                    : TimelineGridQuantization.GetPreviousGridTick(tick, fixedStepTicks, useBars, timeSignatureMap);
                if (direction > 0)
                {
                    if (next <= tick) break;
                    tick = Math.Min(next, toTick);
                }
                else
                {
                    if (next >= tick) break;
                    tick = Math.Max(next, toTick);
                }
            }
        }

        return;

        long Quantize(long tick)
        {
            return TimelineGridQuantization.SnapAbsolute(
                tick,
                fixedStepTicks,
                useBars,
                timeSignatureMap,
                movementDirection: 0);
        }

        void AddSample(long tick, double value)
        {
            if (rangeStartTick is long rangeStart && tick < rangeStart) return;
            if (rangeEndTick is long rangeEnd && tick >= rangeEnd) return;
            destination[tick] = Math.Clamp(value, 0, 1);
        }
    }

    private static void Validate(TimelineValueTracePoint point)
    {
        if (point.Tick < 0
            || !double.IsFinite(point.NormalizedValue))
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }
}
