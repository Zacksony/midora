using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private sealed partial class PureMidiPagedCanonicalSource
    {
        private static bool IsNoteOff(MidiMessage message) =>
            message.MessageType == MidiMessageType.NoteOff
            || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0;

        private void AppendRootRangeState(
            PureMidiRootPlan rootPlan, byte port, byte channel, long startTick,
            bool includeStateAtStart, IReadOnlySet<MidoraId>? demandedMonitoringSourceIds,
            ICollection<CanonicalMidiEvent> output, CancellationToken cancellationToken)
        {
            // A compiled range's own restore is canonical content, not an optional
            // query decoration (SMF and sequential page readers need it as well).
            if ((!includeStateAtStart && startTick != _compiledStartTick) || startTick <= 0) return;
            PureMidiRootInterval? interval = rootPlan.Intervals.SingleOrDefault(value =>
                value.StartTick < startTick && value.EndTick > startTick);
            if (interval is null) return;
            List<CanonicalMidiEvent> initial = [];
            AppendNaturalStart(rootPlan, interval, port, channel, initial);
            Dictionary<long, CanonicalMidiEvent> state = [];
            foreach (CanonicalMidiEvent value in initial) state[value.SemanticTargetKey] = value;
            Dictionary<long, RootStateCandidate> candidates = [];
            foreach ((PureMidiTrackPlan track, MidiSegment segment) in EnumerateAnalysisSegments(rootPlan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.ProjectStartTick >= startTick
                    || segment.ProjectStartTick + segment.LengthTicks <= interval.StartTick
                    || demandedMonitoringSourceIds is not null && !demandedMonitoringSourceIds.Contains(track.Track.Id)) continue;
                long contentEnd = ContentBoundary(segment, startTick);
                foreach ((long target, DirectMidiChannelEventValue value) in
                    _channelAnalysis[segment.Id].GetStateAt(segment.ChannelEvents, contentEnd))
                {
                    long tick = segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick;
                    RootStateCandidate candidate = new(track, segment, value, tick);
                    if (tick < interval.StartTick || tick >= startTick) continue;
                    if (!candidates.TryGetValue(target, out RootStateCandidate previous)
                        || CompareRootStateCandidate(candidate, previous) > 0) candidates[target] = candidate;
                }
            }
            foreach ((long target, RootStateCandidate candidate) in candidates)
            {
                DirectMidiChannelEventValue value = candidate.Value;
                long order = EncodeDirectMidiStableOrder(ToMidiMessage(value, channel), value.Id);
                state[target] = new(candidate.AbsoluteTick, port, channel, ToMidiMessage(value, channel),
                    CanonicalEventRole.DirectMidi, order, target, order,
                    DirectSource(rootPlan.Root.Id, candidate.TrackPlan.Track.Id, candidate.Segment.Id,
                        value.Id, candidate.AbsoluteTick, SourceOrigin.DirectMidiChannelEvent),
                    candidate.TrackPlan.Track.Id, candidate.TrackPlan.TrackOrder, value.Order);
            }
            long restoreOrder = long.MinValue;
            foreach (CanonicalMidiEvent previous in state.Values.OrderBy(RestoreCategory).ThenBy(value => value.SemanticTargetKey))
            {
                output.Add(previous with { Tick = startTick, Role = CanonicalEventRole.RangeRestore,
                    StableOrder = restoreOrder++, Source = previous.Source with { Origin = SourceOrigin.RangeRestore },
                    SmfTrackOrder = int.MinValue + 1, SmfEventOrder = restoreOrder });
            }
        }

        private static int RestoreCategory(CanonicalMidiEvent value) => value.Role == CanonicalEventRole.Reset ? 0 : value.Message.MessageType switch
        {
            MidiMessageType.ControlChange when value.Message.Byte1 == 0 => 1,
            MidiMessageType.ControlChange when value.Message.Byte1 == 32 => 2,
            MidiMessageType.ProgramChange => 3,
            MidiMessageType.ControlChange => 6,
            MidiMessageType.PitchWheelChange => 7,
            _ => 8
        };

        private void AppendNaturalStart(PureMidiRootPlan root, PureMidiRootInterval interval,
            byte port, byte channel, ICollection<CanonicalMidiEvent> output)
        {
            SourceReference source = RootLifecycleSource(root.Root.Id, interval.StartOwnerTrackId,
                interval.StartOwnerSegmentId, interval.StartTick, interval.StartOwnerTrackId);
            int trackOrder = root.Tracks.Single(value => value.Track.Id == interval.StartOwnerTrackId).TrackOrder;
            long order = long.MinValue / 4 + interval.StartTick;
            output.Add(new(interval.StartTick, port, channel, MidiMessage.ControlChange(channel, 121, 0),
                CanonicalEventRole.Reset, order++, ControlTargetKey(121), order, source,
                interval.StartOwnerTrackId, trackOrder, order - 1));
            AppendDefaults(output, interval.StartTick, port, channel,
                _analysis[(root.Root.Id, interval.GroupId)].UsedTargets, source,
                interval.StartOwnerTrackId, trackOrder, ref order, CanonicalEventRole.Reset);
        }

        private void AppendDefaults(ICollection<CanonicalMidiEvent> output, long tick, byte port, byte channel,
            IEnumerable<long> targets, SourceReference source, MidoraId trackId, int trackOrder,
            ref long order, CanonicalEventRole role)
        {
            List<CanonicalMidiEvent> values = [];
            AppendPureRootDefaults(values, tick, port, channel, targets, _resetDefaults,
                source, trackId, trackOrder, ref order, role);
            foreach (CanonicalMidiEvent value in values) output.Add(value with { SmfEventOrder = value.StableOrder });
        }

        private void AppendLifecycleRange(PureMidiRootPlan root, byte port, byte channel,
            long startTick, long endTick, bool includeStateAtStart,
            ICollection<CanonicalMidiEvent> output, CancellationToken cancellationToken,
            bool emitTerminalNotes = true, MidoraId requiredExportTrackId = default)
        {
            foreach (PureMidiRootInterval interval in root.Intervals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (interval.StartTick >= startTick && interval.StartTick < endTick)
                    AppendNaturalStart(root, interval, port, channel, output);
                if (interval.EndTick < startTick || interval.StartTick >= endTick) continue;
                bool terminal = endTick == _compiledEndTick && interval.EndTick >= endTick;
                if (!terminal && interval.EndTick >= endTick) continue;
                long tick = terminal ? endTick : interval.EndTick;
                (PureMidiTrackPlan TrackPlan, MidiSegment Segment) owner = terminal
                    ? EnumerateAnalysisSegments(root).Where(value => value.Segment.ProjectStartTick < tick
                        && value.Segment.ProjectStartTick + value.Segment.LengthTicks >= tick)
                        .OrderBy(value => value.TrackPlan.TrackOrder).ThenBy(value => value.Segment.Id).First()
                    : (root.Tracks.Single(value => value.Track.Id == interval.EndOwnerTrackId),
                        root.Tracks.SelectMany(value => value.Track.Segments).Single(value => value.Id == interval.EndOwnerSegmentId));
                if (requiredExportTrackId != default && owner.TrackPlan.Track.Id != requiredExportTrackId) continue;
                if (terminal && emitTerminalNotes && _terminalNotes.TryGetValue(root.Root.Id, out long[]? remaining))
                    AppendRemainingNoteOffs(root.Root, EnumerateAnalysisSegments(root).Where(value =>
                        value.Segment.ProjectStartTick < tick && value.Segment.ProjectStartTick + value.Segment.LengthTicks > interval.StartTick),
                        remaining, tick, port, channel, output, cancellationToken, rawOnly: false,
                        boundaryExportTrackId: owner.TrackPlan.Track.Id);
                SourceReference source = RootLifecycleSource(root.Root.Id, owner.TrackPlan.Track.Id,
                    owner.Segment.Id, tick, owner.TrackPlan.Track.Id);
                long order = terminal ? long.MaxValue / 2 : long.MaxValue / 4 + tick;
                if (!terminal || HasSoundSinceLastReset(root, interval, tick, cancellationToken))
                    output.Add(new(tick, port, channel, MidiMessage.ControlChange(channel, AllSoundOffController, 0),
                        CanonicalEventRole.RootBoundaryCleanup, order++, ControlTargetKey(AllSoundOffController),
                        order, source, owner.TrackPlan.Track.Id, owner.TrackPlan.TrackOrder, order - 1));
                IEnumerable<long> targets = terminal ? GetPollutedTargets(root, port, channel, cancellationToken)
                    : _analysis[(root.Root.Id, interval.GroupId)].UsedTargets;
                AppendDefaults(output, tick, port, channel, targets, source, owner.TrackPlan.Track.Id,
                    owner.TrackPlan.TrackOrder, ref order, CanonicalEventRole.RootBoundaryCleanup);
            }
        }

        private HashSet<long> GetPollutedTargets(PureMidiRootPlan root, byte port, byte channel, CancellationToken ct)
        {
            HashSet<long> result = [];
            AppendRootRangeState(root, port, channel, _compiledStartTick, true, null,
                new ActionSink(value => result.Add(value.SemanticTargetKey)), ct);
            foreach ((PureMidiTrackPlan _, MidiSegment segment) in EnumerateAnalysisSegments(root))
            {
                long left = ContentBoundary(segment, _compiledStartTick);
                long right = ContentBoundary(segment, _compiledEndTick);
                if (right <= left) continue;
                foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(left, right))
                {
                    ct.ThrowIfCancellationRequested();
                    long target = SemanticTargetForMessage(ToMidiMessage(value, channel));
                    if (target != long.MinValue && target != ControlTargetKey(AllSoundOffController)) result.Add(target);
                }
            }
            foreach (PureMidiRootInterval interval in root.Intervals)
                if (interval.EndTick >= _compiledStartTick && interval.EndTick < _compiledEndTick)
                    result.UnionWith(_analysis[(root.Root.Id, interval.GroupId)].UsedTargets);
            return result;
        }

        private bool HasSoundSinceLastReset(PureMidiRootPlan root, PureMidiRootInterval interval, long end, CancellationToken ct)
        {
            CanonicalMidiEvent? lastReset = null;
            foreach ((PureMidiTrackPlan track, MidiSegment segment) in EnumerateAnalysisSegments(root))
            {
                long left = ContentBoundary(segment, interval.StartTick);
                long right = ContentBoundary(segment, end);
                if (right <= left) continue;
                foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(left, right))
                {
                    ct.ThrowIfCancellationRequested();
                    if (value.Kind != DirectMidiChannelEventKind.ControlChange || value.Data1 != AllSoundOffController) continue;
                    List<CanonicalMidiEvent> one = [];
                    AddDirect(segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick, value.Order, 1, value.Id,
                        ToMidiMessage(value, 0), DirectSource(root.Root.Id, track.Track.Id, segment.Id, value.Id, value.Tick,
                            SourceOrigin.DirectMidiChannelEvent), track, one, 0, 0);
                    if (lastReset is null || CanonicalComparer.Instance.Compare(one[0], lastReset.Value) > 0) lastReset = one[0];
                }
            }
            long start = lastReset?.Tick ?? interval.StartTick;
            foreach ((PureMidiTrackPlan track, MidiSegment segment) in EnumerateAnalysisSegments(root))
            {
                if (CountStarts(segment, Math.Min(end, start + 1), end, ct) != 0) return true;
                foreach (CanonicalMidiEvent value in QueryNoteOns(root.Root, track, segment, start, Math.Min(start + 1, end), false, ct))
                    if (lastReset is null || CompareNoteWithReset(value, lastReset.Value) > 0) return true;
                long left = ContentBoundary(segment, start);
                long right = ContentBoundary(segment, end);
                if (right <= left) continue;
                foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(left, right))
                    if (value.Kind == DirectMidiChannelEventKind.NoteOn && value.Data2 > 0
                        && segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick > start) return true;
            }
            return false;
        }

        private static int CompareNoteWithReset(CanonicalMidiEvent note, CanonicalMidiEvent reset) =>
            CanonicalComparer.Instance.Compare(note with { ZeroBasedPort = 0, ZeroBasedChannel = 0 }, reset);

        private void BuildTerminalNoteCounts(PureMidiEndpointPrefixCache prefixCache, CancellationToken ct)
        {
            foreach (PureMidiRootPlan root in _plan.Roots)
            {
                if (!_unitByRoot.ContainsKey(root.Root.Id)) continue;
                PureMidiRootInterval? interval = root.Intervals.SingleOrDefault(value =>
                    value.StartTick < _compiledEndTick && value.EndTick >= _compiledEndTick);
                if (interval is null) continue;
                long[] counts = new long[128];
                // Every owned Note has a closing endpoint by the natural Root end.
                if (interval.EndTick == _compiledEndTick)
                {
                    _terminalNotes.Add(root.Root.Id, counts);
                    continue;
                }
                var segments = EnumerateAnalysisSegments(root).Where(value => value.Segment.ProjectStartTick < _compiledEndTick
                    && value.Segment.ProjectStartTick + value.Segment.LengthTicks > interval.StartTick).ToArray();
                bool hasRawPrefix = segments.Any(value => _channelAnalysis[value.Segment.Id].HasRawNotes
                    && _channelAnalysis[value.Segment.Id].FirstRawNoteTick <= ContentBoundary(value.Segment, _compiledEndTick));
                if (hasRawPrefix && !TryCountIndependentRawPrefix(root.Root, segments, interval.StartTick,
                    _compiledEndTick, counts, ct))
                {
                    // Raw endpoints may underflow FIFO. Count the actual ordered stream,
                    // with bounded external sorting, never a queue of source objects.
                    PureMidiRootPlan prefixRoot = root with { Tracks = root.Tracks.Select(track =>
                        track with { Segments = track.Track.Segments.Where(IsRepresentableMidiSegment).ToArray() }).ToArray() };
                    string key = CreateAudioFragmentFingerprint(prefixRoot, interval);
                    counts = prefixCache.GetCounts(key, interval.StartTick, _compiledEndTick, ReadPrefix, ct);
                    IEnumerable<CanonicalMidiRenderEventPage> ReadPrefix(long from)
                    {
                        using BoundedCanonicalMidiRenderEventSorter sorter = new();
                        ActionSink notes = new(value => { if (value.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff) sorter.Add(value); });
                        foreach (var (track, segment) in segments)
                            AppendSegmentRange(root.Root, track, segment, 0, 0, from,
                                _compiledEndTick, false, notes, ct);
                        foreach (CanonicalMidiRenderEventPage page in sorter.ReadPages(ct)) yield return page;
                    }
                }
                else foreach (var (_, segment) in segments)
                    foreach (DirectMidiNoteValue value in ActivePairedNotes(segment, _compiledEndTick, inclusive: false, ct)) counts[value.Key]++;
                _terminalNotes.Add(root.Root.Id, counts);
            }
        }

        private bool TryCountIndependentRawPrefix(MidiChannelRoot root,
            (PureMidiTrackPlan TrackPlan, MidiSegment Segment)[] segments,
            long start, long end, long[] counts, CancellationToken ct)
        {
            using BoundedCanonicalMidiRenderEventSorter sorter = new();
            ActionSink raw = new(value =>
            {
                if (value.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff) sorter.Add(value);
            });
            foreach (var (track, segment) in segments)
                AppendSegmentRange(root, track, segment, 0, 0, start, end, false, raw, ct, includePairedNotes: false);
            foreach (CanonicalMidiEventPage page in sorter.ReadCanonicalPages(ct))
                foreach (CanonicalMidiEvent value in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    byte key = value.Message.Byte1;
                    if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0) counts[key]++;
                    else if (counts[key] != 0) counts[key]--;
                    else if (HasPairedNoteBefore(value, segments, end, ct)) return false;
                }
            // Paired endpoints have a nonnegative prefix balance. Raw balance
            // can be counted independently iff every raw-only underflow also
            // occurs while that paired balance is zero. Otherwise use the exact
            // bounded merged-prefix index; never approximate the FIFO count.
            return true;
        }

        private static bool HasPairedNoteBefore(CanonicalMidiEvent raw,
            (PureMidiTrackPlan TrackPlan, MidiSegment Segment)[] segments, long consumerEnd, CancellationToken ct)
        {
            foreach (var (track, segment) in segments)
            {
                foreach (DirectMidiNoteValue note in ActivePairedNotes(segment, raw.Tick, inclusive: true, ct))
                {
                    if (note.Key != raw.Message.Byte1) continue;
                    long noteEnd = Math.Min(segment.ProjectStartTick + segment.LengthTicks,
                        SaturatingAdd(segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick, note.LengthTicks));
                    if (noteEnd > raw.Tick || CompareEndpoint(note, track, on: false) > 0) return true;
                }
                if (raw.Tick >= consumerEnd || raw.Tick < segment.ProjectStartTick
                    || raw.Tick >= segment.ProjectStartTick + segment.LengthTicks) continue;
                long tick = ContentBoundary(segment, raw.Tick);
                foreach (DirectMidiNoteValue note in segment.Notes.QueryStartValues(tick, tick + 1))
                {
                    ct.ThrowIfCancellationRequested();
                    if (note.Key == raw.Message.Byte1 && CompareEndpoint(note, track, on: true) < 0) return true;
                }
            }
            return false;

            int CompareEndpoint(DirectMidiNoteValue note, PureMidiTrackPlan track, bool on)
            {
                MidiMessage message = on ? MidiMessage.NoteOn(0, (byte)note.Key, (byte)note.NoteOnVelocity)
                    : MidiMessage.NoteOff(0, (byte)note.Key, (byte)note.NoteOffVelocity);
                long stable = EncodeDirectMidiStableOrder(message, note.Id);
                CanonicalMidiEvent endpoint = new(raw.Tick, 0, 0, message, CanonicalEventRole.DirectMidi,
                    stable, long.MinValue, stable, default, track.Track.Id, track.TrackOrder, on ? note.NoteOnOrder : note.NoteOffOrder);
                return CanonicalComparer.Instance.Compare(endpoint, raw);
            }
        }

        private static long CountStarts(MidiSegment segment, long start, long end, CancellationToken ct)
        {
            long left = ContentBoundary(segment, start);
            long right = ContentBoundary(segment, end);
            return right <= left ? 0 : segment.Notes.CreateQuerySnapshot().CountStarts(left, right, ct);
        }

        private static long ContentBoundary(MidiSegment segment, long tick) => tick <= segment.ProjectStartTick
            ? segment.ContentOffsetTick : tick >= segment.ProjectStartTick + segment.LengthTicks
                ? segment.ContentOffsetTick + segment.LengthTicks
                : segment.ContentOffsetTick + (tick - segment.ProjectStartTick);

        private static IEnumerable<DirectMidiNoteValue> ActivePairedNotes(MidiSegment segment, long tick,
            bool inclusive, CancellationToken ct)
        {
            long end = segment.ProjectStartTick + segment.LengthTicks;
            if (tick <= segment.ProjectStartTick || tick > end || !inclusive && tick == end) yield break;
            long local = segment.ContentOffsetTick + (tick - segment.ProjectStartTick);
            foreach (DirectMidiNoteValue value in segment.Notes.QueryActiveValues(local))
            {
                ct.ThrowIfCancellationRequested();
                if (value.StartTick >= segment.ContentOffsetTick && value.StartTick < local) yield return value;
            }
            if (inclusive)
                foreach (DirectMidiNoteValue value in local < long.MaxValue
                    ? segment.Notes.QueryEndValues(local, local + 1)
                    : segment.Notes.QueryActiveValues(local - 1).Concat(segment.Notes.QueryStartValues(local - 1, local)))
                {
                    ct.ThrowIfCancellationRequested();
                    if (value.StartTick >= segment.ContentOffsetTick && value.StartTick < local
                        && SaturatingAdd(value.StartTick, value.LengthTicks) == local) yield return value;
                }
        }

        private void AppendRemainingNoteOffs(MidiChannelRoot root,
            IEnumerable<(PureMidiTrackPlan TrackPlan, MidiSegment Segment)> input,
            long[] counts, long end, byte port, byte channel, ICollection<CanonicalMidiEvent> output,
            CancellationToken ct, bool rawOnly, MidoraId boundaryExportTrackId = default)
        {
            if (!counts.Any(value => value != 0)) return;
            var segments = input.ToArray(); // Track/Segment descriptors only, never notes.
            long[] remaining = (long[])counts.Clone();
            long[] prefix = new long[128];
            for (int key = 1; key < 128; key++) prefix[key] = prefix[key - 1] + counts[key - 1];
            long right = end;
            long width = 1024;
            long minimum = segments.Min(value => value.Segment.ProjectStartTick);
            while (right > minimum && remaining.Any(value => value != 0))
            {
                ct.ThrowIfCancellationRequested();
                long left = Math.Max(minimum, right - width);
                using BoundedCanonicalMidiRenderEventSorter sorter = new(131_072, 64, descending: true);
                foreach (var (track, segment) in segments)
                    foreach (CanonicalMidiEvent value in QueryNoteOns(root, track, segment, left, right, rawOnly, ct))
                        if (remaining[value.Message.Byte1] != 0) sorter.Add(value);
                foreach (CanonicalMidiEventPage page in sorter.ReadCanonicalPages(ct))
                    foreach (CanonicalMidiEvent value in page.Items)
                    {
                        byte key = value.Message.Byte1;
                        if (remaining[key] == 0) continue;
                        long rank = --remaining[key];
                        long order = (rawOnly ? long.MaxValue / 8 : long.MaxValue / 2) + prefix[key] + rank;
                        // FIFO source identity and SMF ownership are different:
                        // the surviving On can belong to a Track whose own EOT
                        // already passed. SRS 23.7.5 assigns final Root output to
                        // a Track participating at this boundary, without losing
                        // the original Note's identity/monitoring source.
                        MidoraId exportTrackId = boundaryExportTrackId != default ? boundaryExportTrackId : value.ExportTrackId;
                        SourceReference source = value.Source with { Tick = end, Origin = SourceOrigin.CompilerBoundaryCleanup,
                            ExportTrackId = exportTrackId };
                        output.Add(new(end, port, channel, MidiMessage.NoteOff(channel, key, 0),
                            rawOnly ? CanonicalEventRole.DirectMidi : CanonicalEventRole.NoteOff,
                            order, long.MinValue, rawOnly ? order : long.MinValue, source,
                            exportTrackId, rawOnly ? value.SmfTrackOrder : int.MaxValue, order));
                    }
                right = left;
                width = width <= long.MaxValue / 2 ? width * 2 : long.MaxValue;
            }
            if (remaining.Any(value => value != 0)) throw new InvalidOperationException("Pure MIDI FIFO boundary source accounting is inconsistent.");
        }

        private static IEnumerable<CanonicalMidiEvent> QueryNoteOns(MidiChannelRoot root, PureMidiTrackPlan track,
            MidiSegment segment, long start, long end, bool rawOnly, CancellationToken ct)
        {
            long left = ContentBoundary(segment, start);
            long right = ContentBoundary(segment, end);
            if (right <= left) yield break;
            if (!rawOnly) foreach (DirectMidiNoteValue note in segment.Notes.QueryStartValues(left, right))
            {
                ct.ThrowIfCancellationRequested();
                long tick = segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick;
                long order = EncodeDirectMidiStableOrder(MidiMessage.NoteOn(0, (byte)note.Key, (byte)note.NoteOnVelocity), note.Id);
                yield return new(tick, 0, 0, MidiMessage.NoteOn(0, (byte)note.Key, (byte)note.NoteOnVelocity),
                    CanonicalEventRole.DirectMidi, order, long.MinValue, order,
                    DirectSource(root.Id, track.Track.Id, segment.Id, note.Id, tick, SourceOrigin.DirectMidiNote),
                    track.Track.Id, track.TrackOrder, note.NoteOnOrder);
            }
            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(left, right))
            {
                ct.ThrowIfCancellationRequested();
                if (value.Kind != DirectMidiChannelEventKind.NoteOn || value.Data2 == 0) continue;
                long tick = segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick;
                long order = EncodeDirectMidiStableOrder(ToMidiMessage(value, 0), value.Id);
                yield return new(tick, 0, 0, ToMidiMessage(value, 0), CanonicalEventRole.DirectMidi,
                    order, long.MinValue, order, DirectSource(root.Id, track.Track.Id, segment.Id,
                        value.Id, tick, SourceOrigin.DirectMidiChannelEvent), track.Track.Id, track.TrackOrder, value.Order);
            }
        }

        private (long Events, long NoteOns) CountEvents(CancellationToken ct)
        {
            long events = 0, noteOns = 0;
            foreach (PureMidiRootPlan root in _plan.Roots)
            {
                if (!_unitByRoot.TryGetValue(root.Root.Id, out int unit)) continue;
                foreach (var (_, segment) in EnumerateAnalysisSegments(root))
                {
                    long starts = CountStarts(segment, _compiledStartTick, _compiledEndTick, ct);
                    long beforeEnd = CountStarts(segment, 0, _compiledEndTick, ct);
                    long beforeStart = CountStarts(segment, 0, _compiledStartTick, ct);
                    long ends = beforeEnd - ActivePairedNotes(segment, _compiledEndTick, false, ct).LongCount()
                        - beforeStart + ActivePairedNotes(segment, _compiledStartTick, true, ct).LongCount();
                    events = checked(events + starts + ends);
                    noteOns = checked(noteOns + starts);
                    long left = ContentBoundary(segment, _compiledStartTick);
                    long right = ContentBoundary(segment, _compiledEndTick);
                    if (right < segment.ContentOffsetTick + segment.LengthTicks) right++;
                    if (right > left) foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(left, right))
                    {
                        ct.ThrowIfCancellationRequested();
                        long tick = segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick;
                        MidiMessage message = ToMidiMessage(value, 0);
                        if (tick >= _compiledEndTick && !IsNoteOff(message)) continue;
                        events++;
                        if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0) noteOns++;
                    }
                    long end = segment.ProjectStartTick + segment.LengthTicks;
                    if (end >= _compiledStartTick && end <= _compiledEndTick)
                        events = checked(events + _channelAnalysis[segment.Id].RawBoundaryNoteKeys.Sum());
                }
                ActionSink counter = new(_ => events++);
                AppendRootRangeState(root, (byte)(unit >> 4), (byte)(unit & 15), _compiledStartTick, true, null, counter, ct);
                AppendLifecycleRange(root, (byte)(unit >> 4), (byte)(unit & 15), _compiledStartTick,
                    _compiledEndTick, true, counter, ct, emitTerminalNotes: false);
                if (_terminalNotes.TryGetValue(root.Root.Id, out long[]? remaining)) events = checked(events + remaining.Sum());
            }
            return (events, noteOns);
        }

        private class ActionSink(Action<CanonicalMidiEvent> add) : ICollection<CanonicalMidiEvent>
        {
            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;
            public void Add(CanonicalMidiEvent item) => add(item);
            public void Clear() => throw new NotSupportedException();
            public bool Contains(CanonicalMidiEvent item) => throw new NotSupportedException();
            public void CopyTo(CanonicalMidiEvent[] array, int index) => throw new NotSupportedException();
            public bool Remove(CanonicalMidiEvent item) => throw new NotSupportedException();
            public IEnumerator<CanonicalMidiEvent> GetEnumerator() => throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class FilteringSink(ICollection<CanonicalMidiEvent> output, Func<CanonicalMidiEvent, bool> predicate)
            : ActionSink(value => { if (predicate(value)) output.Add(value); });
    }
}
