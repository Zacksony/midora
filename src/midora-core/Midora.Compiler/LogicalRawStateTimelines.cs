using Midora.Domain;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private readonly record struct OrderedTemplateEvent(TemplateEventSnapshotValue Event, long Ordinal);

    private sealed class OrderedTemplateEventComparer : IComparer<OrderedTemplateEvent>
    {
        public static readonly OrderedTemplateEventComparer Instance = new();
        public int Compare(OrderedTemplateEvent x, OrderedTemplateEvent y)
        {
            int tick = x.Event.Tick.CompareTo(y.Event.Tick);
            return tick != 0 ? tick : x.Ordinal.CompareTo(y.Ordinal);
        }
    }

    private static CompilerValueStore<EventMappingStatePoint> BuildBoundedEnvelopeRawState(
        RawEventBuffer output, EventInstrument instrument, SubVoice voice,
        TemplateEventMappingTarget target, long projectStart, long gateEnd, long actualEnd,
        long instanceGateEndLocalTick, bool shortNote, bool looping)
    {
        CancellationToken token = output.CancellationToken;
        using CompilerExternalSorter<EventMappingStatePoint> sorter = new(output.Storage,
            EventMappingStatePointComparer.Instance, token);
        int eventOrder = 0;
        foreach (TemplateEventSnapshotValue templateEvent in voice.Events.CreateQuerySnapshot().EnumerateAll())
        {
            int currentEventOrder = eventOrder++;
            if (!TemplateEventMappingTarget.Enumerate(templateEvent).Contains(target)) continue;
            foreach (EventOccurrence occurrence in EnumerateOccurrences(instrument, templateEvent.Tick,
                instanceGateEndLocalTick, looping, actualEnd - projectStart))
            {
                token.ThrowIfCancellationRequested();
                if (occurrence.LocalTick >= actualEnd - projectStart) continue;
                long tick = projectStart + occurrence.LocalTick;
                if (shortNote && instrument.ShortLifecycle == ShortNoteLifecycle.CutAtNoteOff && tick >= gateEnd)
                    continue;
                sorter.Add(new(occurrence.LocalTick, GetOriginalEventMappingValue(target, templateEvent),
                    templateEvent.Id, currentEventOrder));
            }
        }
        CompilerValueStore<EventMappingStatePoint> result = new(output.Storage, token);
        try
        {
            foreach (EventMappingStatePoint value in sorter.ReadSorted(token)) result.Add(value);
            result.Seal();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed class EventMappingStatePointComparer : IComparer<EventMappingStatePoint>
    {
        public static readonly EventMappingStatePointComparer Instance = new();
        public int Compare(EventMappingStatePoint x, EventMappingStatePoint y)
        {
            int tick = x.LocalTick.CompareTo(y.LocalTick);
            return tick != 0 ? tick : x.EventOrder.CompareTo(y.EventOrder);
        }
    }

    private readonly record struct TargetRawStateRecord(long Tick, long Sequence, long Group,
        long Ordinal, int Data1, int Data2, RawMessageKind Kind, CanonicalEventRole Role);
    private readonly record struct OrderedTargetStatePoint(long Tick, double Value,
        CanonicalEventRole Role, long Sequence, long Ordinal);

    private static CompilerValueStore<TargetStatePoint> BuildBoundedTargetStateTimeline(
        RawEventBuffer events, MidiValueTarget target)
    {
        CancellationToken token = events.CancellationToken;
        using CompilerExternalSorter<TargetRawStateRecord> groups = new(events.Storage,
            TargetRawStateRecordComparer.Instance, token);
        using CompilerExternalSorter<OrderedTargetStatePoint> points = new(events.Storage,
            OrderedTargetStatePointComparer.Instance, token);
        long targetKey = SemanticTargetForTarget(target);
        long ordinal = 0;
        foreach (RawMidiEvent value in events)
        {
            if (value.SemanticTargetKey == targetKey)
                groups.Add(new(value.Tick, value.Sequence, value.SemanticGroup,
                    ordinal, value.Data1, value.Data2, value.Kind, value.Role));
            ordinal++;
        }

        bool active = false;
        TargetStateAccumulator accumulator = default;
        foreach (TargetRawStateRecord record in groups.ReadSorted(token))
        {
            if (!active || record.Group != accumulator.Group)
            {
                if (active) points.Add(accumulator.Finish(target));
                accumulator = new(record);
                active = true;
            }
            accumulator.Add(record);
        }
        if (active) points.Add(accumulator.Finish(target));
        CompilerValueStore<TargetStatePoint> result = new(events.Storage, token);
        try
        {
            foreach (OrderedTargetStatePoint value in points.ReadSorted(token))
                result.Add(new(value.Tick, value.Value));
            result.Seal();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private struct TargetStateAccumulator
    {
        public long Group;
        private long _tick, _sequence, _ordinal;
        private CanonicalEventRole _role;
        private int? _control, _program, _pitch, _data6, _data38;

        public TargetStateAccumulator(TargetRawStateRecord first)
        {
            Group = first.Group;
            _tick = first.Tick;
            _sequence = first.Sequence;
            _ordinal = first.Ordinal;
            _role = first.Role;
        }

        public void Add(TargetRawStateRecord record)
        {
            _sequence = Math.Max(_sequence, record.Sequence);
            if (record.Role > _role) _role = record.Role;
            if (record.Kind == RawMessageKind.ControlChange) _control = record.Data2;
            if (record.Kind == RawMessageKind.ProgramChange) _program = record.Data1;
            if (record.Kind == RawMessageKind.PitchBend) _pitch = record.Data1;
            // The reference decoder selects by controller number for compound
            // targets; preserve that predicate exactly, not an inferred kind.
            if (record.Data1 == 6) _data6 = record.Data2;
            if (record.Data1 == 38) _data38 = record.Data2;
        }

        public readonly OrderedTargetStatePoint Finish(MidiValueTarget target)
        {
            double value = target.Kind switch
            {
                MidiValueKind.ControlChange or MidiValueKind.BankMsb or MidiValueKind.BankLsb => Required(_control),
                MidiValueKind.Program => Required(_program),
                MidiValueKind.PitchBend => Required(_pitch),
                MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                    (Required(_data6) << 7) | Required(_data38),
                MidiValueKind.PitchBendRangeSemitones => Required(_data6),
                MidiValueKind.PitchBendRangeCents => Required(_data38),
                _ => throw new InvalidOperationException($"Unsupported target {target}.")
            };
            return new(_tick, value, _role, _sequence, _ordinal);
        }

        private static int Required(int? value) => value
            ?? throw new InvalidOperationException("A raw MIDI target group is incomplete.");
    }

    private sealed class TargetRawStateRecordComparer : IComparer<TargetRawStateRecord>
    {
        public static readonly TargetRawStateRecordComparer Instance = new();
        public int Compare(TargetRawStateRecord x, TargetRawStateRecord y)
        {
            int group = x.Group.CompareTo(y.Group);
            return group != 0 ? group : x.Ordinal.CompareTo(y.Ordinal);
        }
    }

    private sealed class OrderedTargetStatePointComparer : IComparer<OrderedTargetStatePoint>
    {
        public static readonly OrderedTargetStatePointComparer Instance = new();
        public int Compare(OrderedTargetStatePoint x, OrderedTargetStatePoint y)
        {
            int result = x.Tick.CompareTo(y.Tick);
            if (result == 0) result = x.Role.CompareTo(y.Role);
            if (result == 0) result = x.Sequence.CompareTo(y.Sequence);
            return result != 0 ? result : x.Ordinal.CompareTo(y.Ordinal);
        }
    }
}
