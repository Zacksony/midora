using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    internal const string PureMidiAudioFragmentFingerprintAbi =
        "MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V4";

    private sealed record PureMidiPlan(
        PureMidiRootPlan[] Roots,
        CanonicalSmfTrackDescriptor[] TrackDescriptors,
        CanonicalOpaqueMidiEvent[] OpaqueEvents,
        PureMidiChannelModeSystemExclusiveEvent[] ChannelModeSystemExclusiveEvents)
    {
        public static PureMidiPlan Empty { get; } = new([], [], [], []);
    }

    private sealed record PureMidiRootPlan(
        MidiChannelRoot Root,
        PureMidiTrackPlan[] Tracks,
        PureMidiRootInterval[] Intervals,
        bool ParticipatesInRequest)
    {
        public bool HasParticipatingSegments => Intervals.Length != 0;
    }

    private sealed record PureMidiTrackPlan(
        PureMidiTrack Track,
        int RootOrder,
        int TrackOrder,
        MidiSegment[] Segments,
        long ExportEndTick);

    private sealed record PureMidiRootInterval(
        long StartTick,
        long EndTick,
        MidoraId GroupId,
        MidoraId StartOwnerTrackId,
        MidoraId StartOwnerSegmentId,
        MidoraId EndOwnerTrackId,
        MidoraId EndOwnerSegmentId);


    private readonly record struct PureMidiChannelModeSystemExclusiveEvent(
        MidoraId RootId,
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId ObjectId,
        long Tick,
        MidiChannelModeSystemExclusive Value,
        CanonicalEventRole Role,
        long StableOrder,
        int SmfTrackOrder,
        long SmfEventOrder);

    private static PureMidiPlan BuildPureMidiPlan(
        MidoraProject project,
        CompilationRequest request,
        long startTick,
        long endTick,
        CancellationToken cancellationToken)
    {
        if (project.MidiChannelRoots.Count == 0)
        {
            return PureMidiPlan.Empty;
        }

        PureMidiTrack[] globalTracks = project.PureMidiTracksInArrangementOrder().ToArray();
        Dictionary<MidoraId, int> globalTrackOrder = globalTracks
            .Select((track, index) => (track.Id, index))
            .ToDictionary(value => value.Id, value => value.index);
        List<PureMidiRootPlan> roots = [];
        List<CanonicalSmfTrackDescriptor> descriptors = [];
        List<CanonicalOpaqueMidiEvent> opaque = [];
        int rootOrder = 0;
        foreach (MidiChannelRoot root in project.MidiChannelRootsInOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PureMidiTrack[] rootTracks = globalTracks
                .Where(value => value.MidiChannelRootId == root.Id)
                .ToArray();
            bool participatesInRequest = request.IncludedTrackIds is null
                || rootTracks.Any(value => request.IncludedTrackIds.Contains(value.Id));
            List<PureMidiTrackPlan> tracks = [];
            foreach (PureMidiTrack track in rootTracks)
            {
                if (request.IncludedTrackIds is not null
                    && !request.IncludedTrackIds.Contains(track.Id))
                {
                    continue;
                }
                int childOrder = globalTrackOrder[track.Id];

                MidiSegment[] segments = track.Segments
                    .Where(segment => IsRepresentable(segment)
                        && segment.ProjectStartTick < endTick
                        && segment.ProjectStartTick + segment.LengthTicks > startTick)
                    .OrderBy(segment => segment.ProjectStartTick)
                    .ThenBy(segment => segment.Id)
                    .ToArray();
                long trackEnd = track.Segments
                    .Where(IsRepresentable)
                    .Select(segment => segment.ProjectStartTick + segment.LengthTicks)
                    .DefaultIfEmpty(0)
                    .Max();
                long exportEnd = Math.Max(0, Math.Min(trackEnd, endTick) - startTick);
                PureMidiTrackPlan trackPlan = new(
                    track,
                    rootOrder,
                    childOrder,
                    segments,
                    exportEnd);
                tracks.Add(trackPlan);
                descriptors.Add(new(
                    track.Id,
                    CanonicalSmfTrackKind.PureMidiTrack,
                    track.Name,
                    root.FixedZeroBasedPort,
                    root.FixedZeroBasedChannel,
                    exportEnd,
                    root.Id,
                    track.Id,
                    root.ChannelMode,
                    root.Name,
                    rootOrder,
                    childOrder,
                    root.RoutingMode));
            }

            PureMidiTrackPlan[] rangeTracks = tracks.ToArray();
            PureMidiTrackPlan[] fullTracks = rangeTracks
                .Select(value => value with
                {
                    Segments = value.Track.Segments
                        .Where(IsRepresentable)
                        .OrderBy(segment => segment.ProjectStartTick)
                        .ThenBy(segment => segment.Id)
                        .ToArray()
                })
                .ToArray();
            PureMidiRootInterval[] intervals = BuildRootIntervals(fullTracks, cancellationToken)
                .Where(value => value.StartTick < endTick && value.EndTick > startTick)
                .ToArray();
            roots.Add(new(root, rangeTracks, intervals, participatesInRequest));
            rootOrder++;
        }

        bool usesPagedContent = roots
            .SelectMany(value => value.Tracks)
            .SelectMany(value => value.Segments)
            .Any(value => value.UsesPagedContent);

        // Small in-memory Projects freeze opaque events here. Paged Projects keep
        // them in the immutable source pack and project them by SMF Track on demand.
        foreach (PureMidiRootPlan rootPlan in usesPagedContent
            ? Enumerable.Empty<PureMidiRootPlan>()
            : roots)
        {
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
                    foreach (OpaqueMidiEventValue value in segment.OpaqueEvents.EnumerateValues(cancellationToken)
                        .Where(value => value.Tick >= segment.ContentOffsetTick
                            && value.Tick < contentEnd)
                        .OrderBy(value => value.Tick)
                        .ThenBy(value => value.Order)
                        .ThenBy(value => value.Id))
                    {
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
                        if (absoluteTick < startTick || absoluteTick >= endTick)
                        {
                            continue;
                        }
                        SourceReference source = new(
                            TrackId: trackPlan.Track.Id,
                            SegmentId: segment.Id,
                            SourceEventId: value.Id,
                            Tick: absoluteTick,
                            Origin: SourceOrigin.OpaqueMidiEvent,
                            MidiChannelRootId: rootPlan.Root.Id,
                            PureMidiTrackId: trackPlan.Track.Id,
                            MidiSegmentId: segment.Id,
                            DirectMidiObjectId: value.Id,
                            ExportTrackId: trackPlan.Track.Id);
                        opaque.Add(new(
                            trackPlan.Track.Id,
                            absoluteTick,
                            value.Kind,
                            value.MetaType,
                            value.Payload.ToArray(),
                            value.Order,
                            source,
                            trackPlan.TrackOrder));
                    }
                }
            }
        }

        return new(
            roots.ToArray(),
            descriptors.ToArray(),
            opaque.OrderBy(value => value.Tick)
                .ThenBy(value => value.ExportTrackId)
                .ThenBy(value => value.StableOrder)
                .ToArray(),
            BuildChannelModeSystemExclusiveEvents(
                roots,
                startTick,
                endTick,
                cancellationToken));

        static bool IsRepresentable(MidiSegment segment) =>
            segment.ProjectStartTick >= 0
            && segment.LengthTicks > 0
            && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
            && segment.ContentOffsetTick >= 0
            && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
    }

    private static PureMidiChannelModeSystemExclusiveEvent[]
        BuildChannelModeSystemExclusiveEvents(
            IReadOnlyList<PureMidiRootPlan> roots,
            long startTick,
            long endTick,
            CancellationToken cancellationToken)
    {
        List<PureMidiChannelModeSystemExclusiveEvent> result = [];
        foreach (PureMidiRootPlan rootPlan in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PureMidiChannelModeSystemExclusiveEvent> all = [];
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(IsRepresentableMidiSegment))
                {
                    long contentStart = segment.ContentOffsetTick;
                    long contentEnd = checked(contentStart + segment.LengthTicks);
                    foreach (OpaqueMidiEventValue opaque in segment.OpaqueEvents.QueryValues(
                        contentStart,
                        contentEnd))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (opaque.Kind != OpaqueMidiEventKind.SystemExclusive
                            || !MidiChannelModeSystemExclusive.TryParseF0Payload(
                                opaque.Payload.Span,
                                out MidiChannelModeSystemExclusive parsed))
                        {
                            continue;
                        }
                        long tick = checked(
                            segment.ProjectStartTick + opaque.Tick - contentStart);
                        all.Add(new(
                            rootPlan.Root.Id,
                            trackPlan.Track.Id,
                            segment.Id,
                            opaque.Id,
                            tick,
                            parsed,
                            CanonicalEventRole.DirectMidi,
                            opaque.Order,
                            trackPlan.TrackOrder,
                            opaque.Order));
                    }
                }
            }

            all.Sort(CompareChannelModeSystemExclusive);
            result.AddRange(all.Where(value => value.Tick >= startTick && value.Tick < endTick));

            if (startTick <= 0) continue;
            PureMidiRootInterval? activeInterval = BuildFullRootIntervals(rootPlan)
                .FirstOrDefault(value => value.StartTick < startTick && value.EndTick > startTick);
            if (activeInterval is null) continue;
            PureMidiChannelModeSystemExclusiveEvent[] restoreCandidates = all
                .Where(value => value.Tick >= activeInterval.StartTick && value.Tick < startTick)
                .ToArray();
            if (restoreCandidates.Length != 0)
            {
                PureMidiChannelModeSystemExclusiveEvent restored = restoreCandidates[^1];
                result.Add(restored with
                {
                    Tick = startTick,
                    Role = CanonicalEventRole.RangeRestore
                });
            }
        }
        result.Sort(CompareChannelModeSystemExclusive);
        return result.ToArray();
    }

    private static PureMidiRootInterval[] BuildFullRootIntervals(PureMidiRootPlan rootPlan)
    {
        PureMidiTrackPlan[] fullTracks = rootPlan.Tracks
            .Select(value => value with
            {
                Segments = value.Track.Segments
                    .Where(IsRepresentableMidiSegment)
                    .OrderBy(segment => segment.ProjectStartTick)
                    .ThenBy(segment => segment.Id)
                    .ToArray()
            })
            .ToArray();
        return BuildRootIntervals(fullTracks, CancellationToken.None);
    }

    private static int CompareChannelModeSystemExclusive(
        PureMidiChannelModeSystemExclusiveEvent left,
        PureMidiChannelModeSystemExclusiveEvent right)
    {
        int value = left.Tick.CompareTo(right.Tick);
        if (value != 0) return value;
        value = left.Role.CompareTo(right.Role);
        if (value != 0) return value;
        value = left.SmfTrackOrder.CompareTo(right.SmfTrackOrder);
        if (value != 0) return value;
        value = left.SmfEventOrder.CompareTo(right.SmfEventOrder);
        if (value != 0) return value;
        return left.ObjectId.CompareTo(right.ObjectId);
    }

    private static bool IsRepresentableMidiSegment(MidiSegment segment) =>
        segment.ProjectStartTick >= 0
        && segment.LengthTicks > 0
        && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
        && segment.ContentOffsetTick >= 0
        && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;

    private static CanonicalMidiChannelModeSystemExclusiveEvent[]
        MaterializePureMidiChannelModeSystemExclusiveEvents(
            PureMidiPlan plan,
            IReadOnlyDictionary<MidoraId, int> unitByRoot)
    {
        CanonicalMidiChannelModeSystemExclusiveEvent[] result = new
            CanonicalMidiChannelModeSystemExclusiveEvent[
                plan.ChannelModeSystemExclusiveEvents.Length];
        for (int index = 0; index < result.Length; index++)
        {
            PureMidiChannelModeSystemExclusiveEvent value =
                plan.ChannelModeSystemExclusiveEvents[index];
            int unit = unitByRoot[value.RootId];
            byte port = checked((byte)(unit >> 4));
            byte channel = checked((byte)(unit & 15));
            SourceReference source = new(
                TrackId: value.TrackId,
                SegmentId: value.SegmentId,
                SourceEventId: value.ObjectId,
                Tick: value.Tick,
                Origin: value.Role == CanonicalEventRole.RangeRestore
                    ? SourceOrigin.RangeRestore
                    : SourceOrigin.OpaqueMidiEvent,
                MidiChannelRootId: value.RootId,
                PureMidiTrackId: value.TrackId,
                MidiSegmentId: value.SegmentId,
                DirectMidiObjectId: value.ObjectId,
                ExportTrackId: value.TrackId);
            result[index] = new(
                value.Tick,
                port,
                channel,
                value.Value,
                value.Role,
                value.StableOrder,
                source,
                value.TrackId,
                value.SmfTrackOrder,
                value.SmfEventOrder);
        }
        return result;
    }

    private static void AppendPureMidiExportCompatibilityDiagnostics(
        PureMidiPlan plan,
        CompilationRequest request,
        long startTick,
        long endTick,
        ICollection<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (request.Purpose != CompilationPurpose.MidiExport)
        {
            return;
        }

        foreach (PureMidiRootPlan rootPlan in plan.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Cross-MTrk ordering loss is impossible when the Root exports one MTrk.
            // This is also the dominant imported-MIDI case and must not materialize
            // two compatibility records for every Note in an extreme Track.
            if (rootPlan.Tracks.Length < 2) continue;
            using BoundedCanonicalSmfEventSorter sorter = new();
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
                    long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                    foreach (DirectMidiNoteValue note in segment.Notes.QueryValues(
                        segment.ContentOffsetTick,
                        contentEnd))
                    {
                        if (note.StartTick < segment.ContentOffsetTick || note.StartTick >= contentEnd)
                        {
                            continue;
                        }
                        long absoluteStart = checked(
                            segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick);
                        long absoluteEnd = Math.Min(
                            segmentEnd,
                            checked(absoluteStart + note.LengthTicks));
                        Add(
                            absoluteStart,
                            MidiMessage.NoteOn(
                                0,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOnVelocity)),
                            note.Id);
                        Add(
                            absoluteEnd,
                            MidiMessage.NoteOff(
                                0,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOffVelocity)),
                            note.Id);
                    }
                    foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryValues(
                        segment.ContentOffsetTick,
                        contentEnd))
                    {
                        if (value.Tick < segment.ContentOffsetTick || value.Tick >= contentEnd)
                        {
                            continue;
                        }
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
                        Add(absoluteTick, ToMidiMessage(value, channel: 0), value.Id);
                    }

                    void Add(long tick, MidiMessage message, MidoraId objectId)
                    {
                        if (tick < startTick || tick > endTick)
                        {
                            return;
                        }
                        sorter.Add(new(
                            trackPlan.Track.Id,
                            tick,
                            0,
                            0,
                            message,
                            CanonicalEventRole.DirectMidi,
                            trackPlan.TrackOrder,
                            objectId));
                    }
                }
            }

            long? firstTick = null;
            long currentTick = long.MinValue;
            PureMidiCompatibilityTickState tickState = new();
            foreach (CanonicalSmfTrackChannelEvent value in sorter
                .ReadPages(cancellationToken)
                .SelectMany(page => page.Items))
            {
                if (value.Tick != currentTick)
                {
                    currentTick = value.Tick;
                    tickState.Reset();
                }
                if (!tickState.Add(value.ExportTrackId, value.Message)) continue;
                firstTick = value.Tick;
                break;
            }
            if (!firstTick.HasValue)
            {
                continue;
            }
            diagnostics.Add(new(
                "MIDORA2251",
                DiagnosticSeverity.Warning,
                $"MIDI Channel Root '{rootPlan.Root.Name}' has cross-Track same-tick order-sensitive events; the first combination is at tick {firstTick}. SMF players may not preserve Midora's cross-MTrk order.",
                new(MidiChannelRootId: rootPlan.Root.Id, Tick: firstTick.Value)));
        }
    }

    private sealed class PureMidiCompatibilityTickState
    {
        private readonly TrackWitness[] _noteOns = new TrackWitness[128];
        private readonly TrackWitness[] _noteOffs = new TrackWitness[128];
        private readonly Dictionary<int, StateTargetWitness> _stateTargets = [];
        private TrackWitness _any;
        private TrackWitness _channelMode;
        private TrackWitness _bankProgramOrReset;
        private TrackWitness _anyNoteOn;

        public void Reset()
        {
            _any = default;
            _channelMode = default;
            _bankProgramOrReset = default;
            _anyNoteOn = default;
            Array.Clear(_noteOns);
            Array.Clear(_noteOffs);
            _stateTargets.Clear();
        }

        public bool Add(MidoraId trackId, MidiMessage message)
        {
            bool channelMode = IsChannelModeMessage(message);
            bool bankProgramOrReset = IsBankProgramOrResetMessage(message);
            bool noteOn = IsNoteOnMessage(message);
            bool noteOff = IsNoteOffMessage(message);
            if (channelMode ? _any.HasOther(trackId) : _channelMode.HasOther(trackId))
                return true;
            if (bankProgramOrReset && _anyNoteOn.HasOther(trackId)
                || noteOn && _bankProgramOrReset.HasOther(trackId))
            {
                return true;
            }
            if (noteOn && _noteOffs[message.Byte1].HasOther(trackId)
                || noteOff && _noteOns[message.Byte1].HasOther(trackId))
            {
                return true;
            }
            if (TryGetDirectStateTarget(message, out int target))
            {
                if (!_stateTargets.TryGetValue(target, out StateTargetWitness? witness))
                {
                    witness = new();
                    _stateTargets.Add(target, witness);
                }
                if (witness.Add(trackId, message.PackedValue)) return true;
            }

            _any.Add(trackId);
            if (channelMode) _channelMode.Add(trackId);
            if (bankProgramOrReset) _bankProgramOrReset.Add(trackId);
            if (noteOn)
            {
                _anyNoteOn.Add(trackId);
                _noteOns[message.Byte1].Add(trackId);
            }
            if (noteOff) _noteOffs[message.Byte1].Add(trackId);
            return false;
        }
    }

    private struct TrackWitness
    {
        private MidoraId _firstTrackId;
        private bool _hasMultipleTracks;

        public bool HasOther(MidoraId trackId) => _firstTrackId != default
            && (_hasMultipleTracks || _firstTrackId != trackId);

        public void Add(MidoraId trackId)
        {
            if (_firstTrackId == default) _firstTrackId = trackId;
            else if (_firstTrackId != trackId) _hasMultipleTracks = true;
        }
    }

    private sealed class StateTargetWitness
    {
        private MidoraId _firstTrackId;
        private uint _firstValue;
        private bool _firstTrackHasMultipleValues;
        private bool _hasMultipleTracks;

        public bool Add(MidoraId trackId, uint value)
        {
            if (_firstTrackId == default)
            {
                _firstTrackId = trackId;
                _firstValue = value;
                return false;
            }
            if (_hasMultipleTracks) return value != _firstValue;
            if (trackId == _firstTrackId)
            {
                _firstTrackHasMultipleValues |= value != _firstValue;
                return false;
            }
            if (_firstTrackHasMultipleValues || value != _firstValue) return true;
            _hasMultipleTracks = true;
            return false;
        }
    }

    private static bool IsNoteOnMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.NoteOn && value.Byte2 != 0;

    private static bool IsNoteOffMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.NoteOff
        || value.MessageType == MidiMessageType.NoteOn && value.Byte2 == 0;

    private static bool IsChannelModeMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.ControlChange && value.Byte1 >= 120;

    private static bool IsBankProgramOrResetMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.ProgramChange
        || value.MessageType == MidiMessageType.ControlChange
            && (value.Byte1 is 0 or 32 or 121 || value.Byte1 >= 120);

    private static bool TryGetDirectStateTarget(MidiMessage value, out int target)
    {
        switch (value.MessageType)
        {
            case MidiMessageType.ControlChange:
                target = 0x10000 + value.Byte1;
                return true;
            case MidiMessageType.ProgramChange:
                target = 0x20000;
                return true;
            case MidiMessageType.PitchWheelChange:
                target = 0x30000;
                return true;
            case MidiMessageType.ChannelPressure:
                target = 0x40000;
                return true;
            case MidiMessageType.PolyphonicKeyPressure:
                target = 0x50000 + value.Byte1;
                return true;
            default:
                target = 0;
                return false;
        }
    }

    private static PureMidiRootInterval[] BuildRootIntervals(
        IReadOnlyList<PureMidiTrackPlan> tracks,
        CancellationToken cancellationToken)
    {
        List<(long Start, long End, int TrackOrder, MidoraId TrackId, MidoraId SegmentId)> ranges = [];
        foreach (PureMidiTrackPlan track in tracks)
        {
            foreach (MidiSegment segment in track.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ranges.Add((
                    segment.ProjectStartTick,
                    checked(segment.ProjectStartTick + segment.LengthTicks),
                    track.TrackOrder,
                    track.Track.Id,
                    segment.Id));
            }
        }
        if (ranges.Count == 0)
        {
            return [];
        }

        ranges.Sort(static (left, right) =>
        {
            int value = left.Start.CompareTo(right.Start);
            if (value != 0) return value;
            value = left.TrackOrder.CompareTo(right.TrackOrder);
            return value != 0 ? value : left.SegmentId.CompareTo(right.SegmentId);
        });
        List<PureMidiRootInterval> result = [];
        int index = 0;
        while (index < ranges.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = ranges[index];
            long start = first.Start;
            long end = first.End;
            List<(long Start, long End, int TrackOrder, MidoraId TrackId, MidoraId SegmentId)> members = [first];
            index++;
            while (index < ranges.Count && ranges[index].Start <= end)
            {
                var current = ranges[index++];
                end = Math.Max(end, current.End);
                members.Add(current);
            }
            var startOwner = members
                .Where(value => value.Start == start)
                .OrderBy(value => value.TrackOrder)
                .ThenBy(value => value.SegmentId)
                .First();
            var endOwner = members
                .Where(value => value.End == end)
                .OrderBy(value => value.TrackOrder)
                .ThenBy(value => value.SegmentId)
                .First();
            result.Add(new(
                start,
                end,
                startOwner.SegmentId,
                startOwner.TrackId,
                startOwner.SegmentId,
                endOwner.TrackId,
                endOwner.SegmentId));
        }
        return result.ToArray();
    }


    private static CanonicalSmfTrackDescriptor[] FreezeSmfTrackDescriptors(
        PureMidiPlan plan,
        IReadOnlyDictionary<MidoraId, int> unitByRoot)
    {
        return plan.TrackDescriptors.Select(value =>
        {
            if (!unitByRoot.TryGetValue(value.MidiChannelRootId, out int unit))
            {
                return value;
            }
            return value with
            {
                ZeroBasedPort = checked((byte)(unit >> 4)),
                ZeroBasedChannel = checked((byte)(unit & 15))
            };
        }).ToArray();
    }


    private static MidiMessage ToMidiMessage(DirectMidiChannelEvent value, byte channel) =>
        value.Kind switch
        {
            DirectMidiChannelEventKind.NoteOff => MidiMessage.NoteOff(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.NoteOn => MidiMessage.NoteOn(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.PolyphonicKeyPressure => MidiMessage.PolyphonicKeyPressure(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ControlChange => MidiMessage.ControlChange(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ProgramChange => MidiMessage.ProgramChange(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.ChannelPressure => MidiMessage.ChannelPressure(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.PitchBend => MidiMessage.PitchWheelChange(
                channel, checked((ushort)((value.Data2 << 7) | value.Data1))),
            _ => throw new InvalidDataException($"Unsupported direct MIDI event kind {value.Kind}.")
        };

    private static MidiMessage ToMidiMessage(DirectMidiChannelEventValue value, byte channel) =>
        value.Kind switch
        {
            DirectMidiChannelEventKind.NoteOff => MidiMessage.NoteOff(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.NoteOn => MidiMessage.NoteOn(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.PolyphonicKeyPressure => MidiMessage.PolyphonicKeyPressure(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ControlChange => MidiMessage.ControlChange(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ProgramChange => MidiMessage.ProgramChange(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.ChannelPressure => MidiMessage.ChannelPressure(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.PitchBend => MidiMessage.PitchWheelChange(
                channel, checked((ushort)((value.Data2 << 7) | value.Data1))),
            _ => throw new InvalidDataException($"Unsupported direct MIDI event kind {value.Kind}.")
        };

    private static long EncodeDirectMidiStableOrder(MidiMessage message, MidoraId stableId)
    {
        if (stableId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(stableId));
        }

        // SmfEventOrder already carries the complete, non-negative Int64 explicit
        // order. StableOrder therefore only has to preserve the endpoint class and
        // the stable-object tie break. Mapping NoteOff endpoints to the negative
        // half and all other events to the positive half is injective for every
        // valid MidoraId and cannot overflow, unlike `(explicitOrder << 1)`.
        return CanonicalMidiOrdering.DirectEndpointOrder(message) == 0
            ? checked(long.MinValue + stableId.Value)
            : stableId.Value;
    }

    private static SourceReference DirectSource(
        MidoraId rootId,
        MidoraId trackId,
        MidoraId segmentId,
        MidoraId objectId,
        long tick,
        SourceOrigin origin) => new(
            TrackId: trackId,
            SegmentId: segmentId,
            SourceEventId: objectId,
            Tick: tick,
            Origin: origin,
            MidiChannelRootId: rootId,
            PureMidiTrackId: trackId,
            MidiSegmentId: segmentId,
            DirectMidiObjectId: objectId,
            ExportTrackId: trackId);

    private static SourceReference RootLifecycleSource(
        MidoraId rootId,
        MidoraId trackId,
        MidoraId segmentId,
        long tick,
        MidoraId exportTrackId) => new(
            TrackId: trackId,
            SegmentId: segmentId,
            Tick: tick,
            Origin: SourceOrigin.MidiChannelRootLifecycle,
            MidiChannelRootId: rootId,
            PureMidiTrackId: trackId,
            MidiSegmentId: segmentId,
            ExportTrackId: exportTrackId);

    private static void AppendPureRootDefaults(
        List<CanonicalMidiEvent> output,
        long tick,
        byte port,
        byte channel,
        IEnumerable<long> targets,
        MidiInitialState defaults,
        SourceReference source,
        MidoraId exportTrackId,
        int smfTrackOrder,
        ref long order,
        CanonicalEventRole role = CanonicalEventRole.Reset)
    {
        foreach (long target in targets.Order())
        {
            int before = output.Count;
            AppendCanonicalReset(output, tick, port, channel, target, defaults, ref order);
            for (int index = before; index < output.Count; index++)
            {
                CanonicalMidiEvent generated = output[index];
                output[index] = generated with
                {
                    Role = role,
                    Source = source,
                    ExportTrackId = exportTrackId,
                    SmfTrackOrder = smfTrackOrder,
                    SmfEventOrder = role == CanonicalEventRole.RootBoundaryCleanup
                        ? long.MaxValue
                        : long.MinValue
                };
            }
        }
    }
}
