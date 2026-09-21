using Midora.Domain;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop;

internal static class EventStepSignalSources
{
    // Guides describe the positions of the lane's explicit records, including
    // commands/selectors. They do not claim that MIDI holds the previous state.
    internal static bool Eligible(MidiValueTarget target) => Enum.IsDefined(target.Kind);

    internal static TimelineStepSignalSource? Direct(MidoraId segmentId,
        DirectMidiChannelEventQuerySnapshot source, DirectMidiEventLaneTarget target)
    {
        if (target.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff
            || !Enum.IsDefined(target.Kind)) return null;
        return new($"direct:{segmentId}:{target}", source.GetRangeFingerprint, Query);
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            int count = 0;
            foreach (var point in source.QueryValues(start, end))
            {
                if ((count++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (point.Kind != target.Kind || target.Kind is DirectMidiChannelEventKind.ControlChange
                        or DirectMidiChannelEventKind.PolyphonicKeyPressure && point.Data1 != target.Data1) continue;
                double value = point.Kind == DirectMidiChannelEventKind.PitchBend
                    ? (point.Data1 | point.Data2 << 7) / 16383d
                    : (point.Kind is DirectMidiChannelEventKind.ChannelPressure or DirectMidiChannelEventKind.ProgramChange
                        ? point.Data1 : point.Data2) / 127d;
                yield return new(point.Tick, point.Order, value);
            }
        }
    }

    internal static TimelineStepSignalSource Opaque(MidoraId segmentId, OpaqueMidiEventQuerySnapshot source)
    {
        return new($"opaque:{segmentId}", source.GetRangeFingerprint, Query);
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            int count = 0;
            foreach (var point in source.QueryValues(start, end))
            {
                if ((count++ & 255) == 0) token.ThrowIfCancellationRequested();
                // Match the opaque point layer's fixed y; never decode a payload
                // into a fabricated numeric value or retain/copy it for lines.
                yield return new(point.Tick, point.Order, 1);
            }
        }
    }

    internal static TimelineStepSignalSource? Template(MidoraId voiceId,
        TemplateEventQuerySnapshot source, MidiValueTarget target, long templateEnd)
    {
        if (!Eligible(target)) return null;
        var (min, max) = InstrumentWorkspaceViewModel.MidiValueRange(target);
        return new($"template:{voiceId}:{target}", source.GetEventRangeFingerprint, Query,
            id => source.TryFindOrdinalById(new(id), out int ordinal) ? ordinal : throw new InvalidOperationException("Missing event order."),
            source.ContentFingerprint, source.PrepareOrdinalLookup) { EndTick = templateEnd };
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            int count = 0;
            foreach (var point in source.QuerySignalCandidates(start, end))
            {
                if ((count++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (PagedTemplateEventLaneTimelineItemSource.TryGetTargetValue(point, target, out int value))
                    yield return new(point.Tick, point.Id.Value, Math.Clamp((value - min) / (max - min), 0, 1));
            }
        }
    }

    internal static TimelineStepSignalSource Logical(MidoraId laneId, CurvePointQuerySnapshot source,
        double minimum, double maximum)
    {
        return new(FormattableString.Invariant($"logical:{laneId}:{minimum:R}:{maximum:R}"),
            source.GetRangeFingerprint, Query,
            id => source.TryFindOrdinalById(new(id), out int ordinal) ? ordinal : throw new InvalidOperationException("Missing parameter order."),
            source.ContentFingerprint, source.PrepareOrdinalLookup);
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            int count = 0;
            foreach (var point in source.QuerySignalCandidates(start, end))
            {
                if ((count++ & 255) == 0) token.ThrowIfCancellationRequested();
                yield return new(point.Tick, point.Id.Value, Math.Clamp((point.Value - minimum) / (maximum - minimum), 0, 1));
            }
        }
    }
}
