using System.Collections;
using System.Runtime.CompilerServices;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    // Pattern identity includes every event/source field. Relative arithmetic is
    // deliberately reversible even for long.MinValue semantic sentinels.
    private readonly record struct CompactRawEvent(
        long Tick, long Sequence, long Target, long Group, long SourceTick,
        int Data1, int Data2, int Source, RawMessageKind Kind, CanonicalEventRole Role, bool GroupIsRelative);

    private readonly record struct RawSourcePattern(SourceReference Source, byte Inherited);

    private sealed record RawVoiceState(MidiInitialState InitialState,
        HashSet<MidiValueTarget> UsedTargets, HashSet<MidiValueTarget> TickZeroTargets,
        MidiValueTarget[] RetainedTargets);

    private sealed class RawEventPatternPool
    {
        private const int MaximumPatterns = 4096;
        private const long MaximumInternedBytes = 16L * 1024 * 1024;
        private readonly Dictionary<RawSourcePattern, int> _sourceIndices = [];
        private readonly List<RawSourcePattern> _sources = [];
        private readonly Dictionary<int, List<CompactRawEvent[]>> _patterns = [];
        private readonly Dictionary<MidoraId, RawVoiceState> _voiceStates = [];
        private readonly Dictionary<MidoraId, CompilerValueStore<TemplateEventSnapshotValue>> _orderedTemplates = [];
        private CompilerValueStore<CompactRawEvent>? _overflowPatterns;
        internal readonly CompilerStorageBudget Storage;
        private long _internedBytes;
        private int _patternCount;

        public RawEventPatternPool() : this(new CompilerStorageBudget()) { }

        public RawEventPatternPool(CompilerStorageBudget storage) => Storage = storage;

        public RawEventBuffer CreateBuffer(SourceReference context, long sequence,
            CancellationToken cancellationToken) => new(this, context, sequence, cancellationToken);

        public RawVoiceState GetVoiceState(MidoraProject project, EventInstrument instrument, SubVoice voice)
        {
            if (_voiceStates.TryGetValue(voice.Id, out RawVoiceState? prepared)) return prepared;
            MidiInitialState state = MergeState(project.GlobalInitialState, instrument.InitialState, voice.InitialState);
            HashSet<MidiValueTarget> targets = CollectUsedTargets(instrument, voice, state);
            prepared = new(state, targets, GetTickZeroTargets(voice), targets
                .Where(static target => target.Kind != MidiValueKind.ControlChange || target.Number != AllSoundOffController)
                .OrderBy(static target => target.Kind).ThenBy(static target => target.Number).ToArray());
            _voiceStates.Add(voice.Id, prepared);
            return prepared;
        }

        public RawEventSequence Freeze(IReadOnlyList<RawMidiEvent> events, SourceReference context, long sequence,
            CancellationToken cancellationToken)
        {
            if (events is RawEventBuffer buffer && buffer.Matches(this, context, sequence))
                return buffer.Freeze();
            using RawEventBuffer converted = CreateBuffer(context, sequence, cancellationToken);
            foreach (RawMidiEvent value in events) converted.Add(value);
            return converted.Freeze();
        }

        internal CompactRawEvent Compact(RawMidiEvent value, SourceReference context, long sequence)
        {
            RawSourcePattern source = NormalizeSource(value.Source, context);
            if (!_sourceIndices.TryGetValue(source, out int sourceIndex))
            {
                sourceIndex = _sources.Count;
                _sources.Add(source);
                _sourceIndices.Add(source, sourceIndex);
            }
            return new(
                unchecked(value.Tick - context.Tick), unchecked(value.Sequence - sequence),
                value.SemanticTargetKey, value.SemanticGroup == long.MinValue
                    ? long.MinValue : unchecked(value.SemanticGroup - sequence),
                unchecked(value.Source.Tick - context.Tick), value.Data1, value.Data2,
                sourceIndex, value.Kind, value.Role, value.SemanticGroup != long.MinValue);
        }

        internal IReadOnlyList<CompactRawEvent> Intern(CompactRawEvent[] records, int key,
            CancellationToken cancellationToken)
        {
            if (_patterns.TryGetValue(key, out List<CompactRawEvent[]>? candidates))
            {
                foreach (CompactRawEvent[] candidate in candidates)
                {
                    if (records.AsSpan().SequenceEqual(candidate))
                        return candidate;
                }
            }
            long bytes = (long)records.Length * Unsafe.SizeOf<CompactRawEvent>() + 24;
            if (_patternCount < MaximumPatterns && bytes <= MaximumInternedBytes - _internedBytes
                && Storage.TryReserveResident(bytes))
            {
                try
                {
                    if (candidates is null) _patterns.Add(key, candidates = []);
                    candidates.Add(records);
                    _internedBytes += bytes;
                    _patternCount++;
                    return records;
                }
                catch { Storage.ReleaseResident(bytes); throw; }
            }
            _overflowPatterns ??= new(Storage, cancellationToken);
            long start = _overflowPatterns.Count;
            foreach (CompactRawEvent record in records) _overflowPatterns.Add(record);
            return new RawCompactStoreList(_overflowPatterns, start, records.Length);
        }

        internal IReadOnlyList<CompactRawEvent> Intern(CompilerValueStore<CompactRawEvent> records,
            int count, int key, CancellationToken cancellationToken)
        {
            if (_patterns.TryGetValue(key, out List<CompactRawEvent[]>? candidates))
            {
                foreach (CompactRawEvent[] candidate in candidates)
                {
                    if (candidate.Length != count) continue;
                    int index = 0;
                    bool equal = true;
                    foreach (CompactRawEvent record in records.Enumerate(false, cancellationToken))
                    {
                        if (record == candidate[index++]) continue;
                        equal = false;
                        break;
                    }
                    if (!equal) continue;
                    records.Dispose();
                    return candidate;
                }
            }
            long bytes = (long)count * Unsafe.SizeOf<CompactRawEvent>() + 24;
            if (_patternCount < MaximumPatterns && bytes <= MaximumInternedBytes - _internedBytes
                && Storage.TryReserveResident(bytes))
            {
                CompactRawEvent[] values;
                try
                {
                    values = new CompactRawEvent[count];
                    int index = 0;
                    foreach (CompactRawEvent value in records.Enumerate(false, cancellationToken))
                        values[index++] = value;
                    if (candidates is null) _patterns.Add(key, candidates = []);
                    candidates.Add(values);
                    _internedBytes += bytes;
                    _patternCount++;
                }
                catch { Storage.ReleaseResident(bytes); throw; }
                records.Dispose();
                return values;
            }
            return new RawCompactStoreList(records, 0, count);
        }

        ~RawEventPatternPool()
        {
            // A frozen sequence retains its pool and therefore the reservation;
            // clearing discovery dictionaries must not release live patterns.
            try { Storage?.ReleaseResident(_internedBytes); }
            catch { /* Cleanup must not terminate a process already under resource pressure. */ }
        }

        public void Seal()
        {
            _overflowPatterns?.Seal();
            foreach (CompilerValueStore<TemplateEventSnapshotValue> ordered in _orderedTemplates.Values)
                ordered.Dispose();
            _orderedTemplates.Clear();
            // Readers need the immutable source table, not either discovery index.
            _sourceIndices.Clear();
            _sourceIndices.TrimExcess();
            _patterns.Clear();
            _patterns.TrimExcess();
            _voiceStates.Clear();
            _voiceStates.TrimExcess();
        }

        public IEnumerable<TemplateEventSnapshotValue> GetOrderedTemplateEvents(SubVoice voice,
            CancellationToken cancellationToken)
        {
            if (!_orderedTemplates.TryGetValue(voice.Id, out CompilerValueStore<TemplateEventSnapshotValue>? ordered))
            {
                ordered = new(Storage, cancellationToken);
                try
                {
                    using CompilerExternalSorter<OrderedTemplateEvent> sorter = new(Storage,
                        OrderedTemplateEventComparer.Instance, cancellationToken);
                    long ordinal = 0;
                    foreach (TemplateEventSnapshotValue value in voice.Events.CreateQuerySnapshot().EnumerateAll())
                        sorter.Add(new(value, ordinal++));
                    foreach (OrderedTemplateEvent value in sorter.ReadSorted(cancellationToken))
                        ordered.Add(value.Event);
                    ordered.Seal();
                    _orderedTemplates.Add(voice.Id, ordered);
                }
                catch { ordered.Dispose(); throw; }
            }
            return ordered.Enumerate(false, cancellationToken);
        }

        public SourceReference RestoreSource(int index, RawEventSequence sequence, long tick)
            => RestoreSource(index, new SourceReference(TrackId: sequence.TrackId,
                SegmentId: sequence.SegmentId, LogicalNoteId: sequence.NoteId,
                EventInstrumentId: sequence.InstrumentId, SubVoiceId: sequence.VoiceId,
                EventInstrumentUsageId: sequence.UsageId), tick);

        public SourceReference RestoreSource(int index, SourceReference context, long tick)
        {
            RawSourcePattern pattern = _sources[index];
            SourceReference source = pattern.Source;
            return source with
            {
                TrackId = (pattern.Inherited & 1) != 0 ? context.TrackId : source.TrackId,
                SegmentId = (pattern.Inherited & 2) != 0 ? context.SegmentId : source.SegmentId,
                LogicalNoteId = (pattern.Inherited & 4) != 0 ? context.LogicalNoteId : source.LogicalNoteId,
                EventInstrumentId = (pattern.Inherited & 8) != 0 ? context.EventInstrumentId : source.EventInstrumentId,
                SubVoiceId = (pattern.Inherited & 16) != 0 ? context.SubVoiceId : source.SubVoiceId,
                EventInstrumentUsageId = (pattern.Inherited & 32) != 0 ? context.EventInstrumentUsageId : source.EventInstrumentUsageId,
                Tick = tick
            };
        }

        private static RawSourcePattern NormalizeSource(SourceReference source, SourceReference context)
        {
            byte inherited = 0;
            if (source.TrackId == context.TrackId) { inherited |= 1; source = source with { TrackId = default }; }
            if (source.SegmentId == context.SegmentId) { inherited |= 2; source = source with { SegmentId = default }; }
            if (source.LogicalNoteId == context.LogicalNoteId) { inherited |= 4; source = source with { LogicalNoteId = default }; }
            if (source.EventInstrumentId == context.EventInstrumentId) { inherited |= 8; source = source with { EventInstrumentId = default }; }
            if (source.SubVoiceId == context.SubVoiceId) { inherited |= 16; source = source with { SubVoiceId = default }; }
            if (source.EventInstrumentUsageId == context.EventInstrumentUsageId) { inherited |= 32; source = source with { EventInstrumentUsageId = default }; }
            return new(source with { Tick = 0 }, inherited);
        }
    }

    private sealed class RawEventSequence : IReadOnlyList<RawMidiEvent>
    {
        private readonly RawEventPatternPool _pool;
        private readonly IReadOnlyList<CompactRawEvent> _events;
        private readonly long _tick, _sequence;
        public readonly MidoraId TrackId, SegmentId, NoteId, InstrumentId, VoiceId, UsageId;

        public RawEventSequence(RawEventPatternPool pool, IReadOnlyList<CompactRawEvent> events, SourceReference context, long sequence)
        {
            _pool = pool; _events = events; _tick = context.Tick; _sequence = sequence;
            TrackId = context.TrackId; SegmentId = context.SegmentId; NoteId = context.LogicalNoteId;
            InstrumentId = context.EventInstrumentId; VoiceId = context.SubVoiceId; UsageId = context.EventInstrumentUsageId;
        }

        public int Count => _events.Count;
        public RawMidiEvent this[int index]
        {
            get => Restore(_events[index]);
        }
        private RawMidiEvent Restore(CompactRawEvent value) =>
                new(unchecked(value.Tick + _tick), value.Kind, value.Data1, value.Data2,
                    value.Role, unchecked(value.Sequence + _sequence), value.Target,
                    value.GroupIsRelative ? unchecked(value.Group + _sequence) : value.Group,
                    _pool.RestoreSource(value.Source, this, unchecked(value.SourceTick + _tick)));
        public IEnumerator<RawMidiEvent> GetEnumerator()
        {
            foreach (CompactRawEvent value in _events) yield return Restore(value);
        }
        public IEnumerable<CompactCanonicalEvent> MaterializeCompact(CanonicalSourceTable sources,
            byte port, byte channel, int trackOrder)
        {
            int previousSource = -1;
            CompactCanonicalSource prepared = default;
            foreach (CompactRawEvent value in _events)
            {
                if (value.Source != previousSource)
                {
                    prepared = sources.Capture(_pool.RestoreSource(value.Source, this, 0));
                    previousSource = value.Source;
                }
                MidiMessage message = value.Kind switch
                {
                    RawMessageKind.NoteOff => MidiMessage.NoteOff(channel, checked((byte)value.Data1), 0),
                    RawMessageKind.NoteOn => MidiMessage.NoteOn(channel, checked((byte)value.Data1), checked((byte)value.Data2)),
                    RawMessageKind.ControlChange => MidiMessage.ControlChange(channel, checked((byte)value.Data1), checked((byte)value.Data2)),
                    RawMessageKind.ProgramChange => MidiMessage.ProgramChange(channel, checked((byte)value.Data1)),
                    RawMessageKind.PitchBend => MidiMessage.PitchWheelChange(channel, checked((ushort)(value.Data1 + 8192))),
                    _ => throw new InvalidOperationException()
                };
                yield return new(unchecked(value.Tick + _tick), unchecked(value.Sequence + _sequence),
                    value.Target, value.GroupIsRelative ? unchecked(value.Group + _sequence) : value.Group,
                    unchecked(value.SourceTick + _tick), prepared.NoteId, prepared.EventId, default,
                    long.MaxValue, prepared.Index, trackOrder, message, port, channel, value.Role);
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
