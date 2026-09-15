using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private sealed partial class PureMidiPagedCanonicalSource :
        ICanonicalMidiEventPageSource,
        ICanonicalMidiRenderPageSource,
        ICanonicalDemandFilteredMidiRenderPageSource,
        ICanonicalSmfTrackPageSource,
        ICanonicalPureMidiAudioMetadataSource
    {
        private readonly PureMidiPlan _plan;
        private readonly IReadOnlyDictionary<MidoraId, int> _unitByRoot;
        private readonly MidiInitialState _resetDefaults;
        private readonly long _compiledStartTick;
        private readonly long _compiledEndTick;
        private readonly Dictionary<(MidoraId RootId, MidoraId GroupId), RootIntervalAnalysis> _analysis = [];
        private readonly Dictionary<MidoraId, SegmentChannelAnalysis> _channelAnalysis = [];
        private readonly Dictionary<MidoraId, long[]> _terminalNotes = [];

        public PureMidiPagedCanonicalSource(
            PureMidiPlan plan,
            IReadOnlyDictionary<MidoraId, int> unitByRoot,
            MidiInitialState resetDefaults,
            long compiledStartTick,
            long compiledEndTick,
            CancellationToken cancellationToken,
            PureMidiEndpointPrefixCache prefixCache)
        {
            _plan = plan;
            _unitByRoot = new Dictionary<MidoraId, int>(unitByRoot);
            _resetDefaults = resetDefaults.Clone();
            _compiledStartTick = compiledStartTick;
            _compiledEndTick = compiledEndTick;
            BuildAnalysis(cancellationToken);
            BuildTerminalNoteCounts(prefixCache, cancellationToken);
            (EventCount, NoteOnEventCount) = CountEvents(cancellationToken);
            PureMidiAudioFragments = BuildAudioFragments(cancellationToken);
            ContentFingerprint = CreateContentFingerprint(cancellationToken);
            PureMidiPresetReferences = BuildPresetReferences(cancellationToken);
        }

        public long EventCount { get; }
        public long NoteOnEventCount { get; }
        public string ContentFingerprint { get; }
        public IReadOnlyList<CanonicalPureMidiAudioFragmentDescriptor> PureMidiAudioFragments { get; }
        public IReadOnlyList<CanonicalMidiPresetReference> PureMidiPresetReferences { get; }

        private CanonicalPureMidiAudioFragmentDescriptor[] BuildAudioFragments(
            CancellationToken cancellationToken)
        {
            List<CanonicalPureMidiAudioFragmentDescriptor> result = [];
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                foreach (PureMidiRootInterval interval in rootPlan.Intervals)
                {
                    long start = Math.Max(_compiledStartTick, interval.StartTick);
                    long end = Math.Min(_compiledEndTick, interval.EndTick);
                    if (end <= start) continue;
                    MidoraId[] contributingTrackIds = rootPlan.Tracks
                        .Where(track => track.Track.Segments.Any(segment => IsRepresentableMidiSegment(segment) &&
                            segment.ProjectStartTick < interval.EndTick
                            && checked(segment.ProjectStartTick + segment.LengthTicks) > interval.StartTick))
                        .Select(track => track.Track.Id)
                        .Order()
                        .ToArray();
                    result.Add(new(
                        rootPlan.Root.Id,
                        interval.GroupId,
                        start,
                        end,
                        checked((byte)(unit >> 4)),
                        checked((byte)(unit & 15)),
                        rootPlan.Root.ChannelMode,
                        CreateAudioFragmentFingerprint(rootPlan, interval),
                        contributingTrackIds));
                }
            }
            return result.ToArray();
        }

        private CanonicalMidiPresetReference[] BuildPresetReferences(
            CancellationToken cancellationToken)
        {
            HashSet<CanonicalMidiPresetReference> result = [];
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                HashSet<byte> banks = [0];
                HashSet<byte> programs = [0];
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    foreach (MidiSegment segment in trackPlan.Track.Segments.Where(segment =>
                        _channelAnalysis.ContainsKey(segment.Id)))
                    {
                        SegmentChannelAnalysis analysis = _channelAnalysis[segment.Id];
                        banks.UnionWith(analysis.ReferencedBanks);
                        programs.UnionWith(analysis.ReferencedPrograms);
                    }
                }
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (byte bank in banks)
                    foreach (byte program in programs)
                        result.Add(new(port, channel, bank, program));
            }
            return result
                .OrderBy(value => value.ZeroBasedPort)
                .ThenBy(value => value.ZeroBasedChannel)
                .ThenBy(value => value.Bank)
                .ThenBy(value => value.Program)
                .ToArray();
        }

        private string CreateAudioFragmentFingerprint(
            PureMidiRootPlan rootPlan,
            PureMidiRootInterval interval)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendText(PureMidiAudioFragmentFingerprintAbi);
            AppendLong(rootPlan.Root.Id.Value);
            AppendLong((int)rootPlan.Root.ChannelMode);
            AppendLong(interval.GroupId.Value);
            AppendLong(interval.StartTick);
            AppendLong(interval.EndTick);
            AppendInitialState(_resetDefaults);
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks.OrderBy(value => value.TrackOrder))
            {
                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(value => IsRepresentableMidiSegment(value) && value.ProjectStartTick < interval.EndTick
                        && checked(value.ProjectStartTick + value.LengthTicks) > interval.StartTick)
                    .OrderBy(value => value.ProjectStartTick)
                    .ThenBy(value => value.Id))
                {
                    AppendLong(trackPlan.Track.Id.Value);
                    AppendLong(trackPlan.TrackOrder);
                    AppendLong(segment.Id.Value);
                    AppendLong(segment.ProjectStartTick);
                    AppendLong(segment.LengthTicks);
                    AppendLong(segment.ContentOffsetTick);
                    AppendText(segment.PagedContentFingerprint ?? string.Empty);
                    AppendDirectNoteContent(segment.Notes);
                    AppendDirectChannelEventContent(segment.ChannelEvents);
                    AppendOpaqueMidiContent(segment.OpaqueEvents);
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());

            void AppendDirectNoteContent(DirectMidiNoteCollection notes)
            {
                AppendLong(notes.HasPagedSource ? 1 : 0);
                AppendLong(notes.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = notes.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                DirectMidiNoteValue[] edited = notes.EditedValues
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (DirectMidiNoteValue value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.StartTick);
                    AppendLong(value.LengthTicks);
                    AppendLong(value.Key);
                    AppendLong(value.NoteOnVelocity);
                    AppendLong(value.NoteOffVelocity);
                    AppendLong(value.NoteOnOrder);
                    AppendLong(value.NoteOffOrder);
                }
            }

            void AppendDirectChannelEventContent(DirectMidiChannelEventCollection events)
            {
                AppendLong(events.HasPagedSource ? 1 : 0);
                AppendLong(events.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = events.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                DirectMidiChannelEventValue[] edited = events.EditedValues
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (DirectMidiChannelEventValue value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.Tick);
                    AppendLong((int)value.Kind);
                    AppendLong(value.Data1);
                    AppendLong(value.Data2);
                    AppendLong(value.Order);
                }
            }

            void AppendOpaqueMidiContent(OpaqueMidiEventCollection events)
            {
                AppendLong(events.HasPagedSource ? 1 : 0);
                AppendLong(events.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = events.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                OpaqueMidiEventValue[] edited = events.EditedValues
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (OpaqueMidiEventValue value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.Tick);
                    AppendLong((int)value.Kind);
                    AppendLong(value.MetaType);
                    AppendLong(value.Order);
                    AppendLong(value.Payload.Length);
                    hash.AppendData(value.Payload.Span);
                }
            }

            void AppendInitialState(MidiInitialState value)
            {
                AppendLong(value.BankMsb ?? -1);
                AppendLong(value.BankLsb ?? -1);
                AppendLong(value.Program ?? -1);
                AppendLong(value.PitchBend ?? -1);
                AppendLong(value.PitchBendRangeSemitones ?? -1);
                AppendLong(value.PitchBendRangeCents ?? -1);
                foreach ((int key, int item) in value.Controllers.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
                foreach ((int key, int item) in value.RegisteredParameters.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
                foreach ((int key, int item) in value.NonRegisteredParameters.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
            }

            void AppendLong(long value)
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendText(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                AppendLong(bytes.Length);
                hash.AppendData(bytes);
            }
        }

        public IEnumerable<CanonicalMidiEventPage> QueryPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            CancellationToken cancellationToken = default)
        {
            if (startTick < _compiledStartTick || endTick > _compiledEndTick || endTick <= startTick)
                throw new ArgumentOutOfRangeException(nameof(startTick));

            using BoundedCanonicalMidiRenderEventSorter sorter = new();
            CanonicalMidiRenderSortSink events = new(sorter);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    foreach (MidiSegment segment in trackPlan.Track.Segments.Where(IsRepresentableMidiSegment))
                    {
                        AppendSegmentRange(
                            rootPlan.Root,
                            trackPlan,
                            segment,
                            port,
                            channel,
                            startTick,
                            endTick,
                            includeStateAtStart,
                            events,
                            cancellationToken);
                    }
                }
                AppendRootRangeState(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    includeStateAtStart,
                    demandedMonitoringSourceIds: null,
                    events,
                    cancellationToken);
                AppendLifecycleRange(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    endTick,
                    includeStateAtStart,
                    events,
                    cancellationToken);
            }

            foreach (CanonicalMidiEventPage page in sorter.ReadCanonicalPages(cancellationToken))
                yield return page;
        }

        public IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            CancellationToken cancellationToken = default) =>
            QueryRenderPagesCore(
                startTick,
                endTick,
                includeStateAtStart,
                demandedMonitoringSourceIds: null,
                cancellationToken);

        public IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            IReadOnlySet<MidoraId> demandedMonitoringSourceIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(demandedMonitoringSourceIds);
            return QueryRenderPagesCore(
                startTick,
                endTick,
                includeStateAtStart,
                demandedMonitoringSourceIds,
                cancellationToken);
        }

        private IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPagesCore(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            IReadOnlySet<MidoraId>? demandedMonitoringSourceIds,
            CancellationToken cancellationToken)
        {
            if (startTick < _compiledStartTick || endTick > _compiledEndTick || endTick <= startTick)
                throw new ArgumentOutOfRangeException(nameof(startTick));

            using BoundedCanonicalMidiRenderEventSorter sorter = new();
            CanonicalMidiRenderSortSink sink = new(sorter);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                bool lifecycleDemanded = demandedMonitoringSourceIds is null
                    || demandedMonitoringSourceIds.Contains(rootPlan.Root.Id);
                if (!lifecycleDemanded
                    && demandedMonitoringSourceIds is not null
                    && !rootPlan.Tracks.Any(value =>
                        demandedMonitoringSourceIds.Contains(value.Track.Id)))
                {
                    continue;
                }
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    if (demandedMonitoringSourceIds is not null
                        && !demandedMonitoringSourceIds.Contains(trackPlan.Track.Id))
                    {
                        continue;
                    }
                    foreach (MidiSegment segment in trackPlan.Track.Segments.Where(IsRepresentableMidiSegment))
                    {
                        AppendSegmentRange(
                            rootPlan.Root,
                            trackPlan,
                            segment,
                            port,
                            channel,
                            startTick,
                            endTick,
                            includeStateAtStart,
                            sink,
                            cancellationToken);
                    }
                }
                AppendRootRangeState(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    includeStateAtStart,
                    demandedMonitoringSourceIds,
                    sink,
                    cancellationToken);
                if (lifecycleDemanded)
                {
                    AppendLifecycleRange(
                        rootPlan,
                        port,
                        channel,
                        startTick,
                        endTick,
                        includeStateAtStart,
                        sink,
                        cancellationToken);
                }
            }
            foreach (CanonicalMidiRenderEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        public IEnumerable<CanonicalSmfTrackChannelEventPage> QueryTrackChannelEventPages(
            MidoraId exportTrackId,
            long startTick,
            long endTick,
            CancellationToken cancellationToken = default)
        {
            if (exportTrackId == default
                || startTick < _compiledStartTick
                || endTick > _compiledEndTick
                || endTick <= startTick)
            {
                throw new ArgumentOutOfRangeException(nameof(exportTrackId));
            }

            using BoundedCanonicalSmfEventSorter sorter = new();
            CanonicalSmfSortSink sink = new(sorter);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PureMidiTrackPlan? trackPlan = rootPlan.Tracks
                    .FirstOrDefault(value => value.Track.Id == exportTrackId);
                if (trackPlan is null || !_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit))
                    continue;
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (MidiSegment segment in trackPlan.Track.Segments.Where(IsRepresentableMidiSegment))
                {
                    AppendSegmentRange(
                        rootPlan.Root,
                        trackPlan,
                        segment,
                        port,
                        channel,
                        startTick,
                        endTick,
                        includeStateAtStart: false,
                        sink,
                        cancellationToken);
                }
                FilteringSink lifecycle = new(sink, value => value.ExportTrackId == exportTrackId);
                AppendRootRangeState(rootPlan, port, channel, startTick,
                    startTick == _compiledStartTick, null, lifecycle, cancellationToken);
                AppendLifecycleRange(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    endTick,
                    includeStateAtStart: false,
                    lifecycle,
                    cancellationToken,
                    requiredExportTrackId: exportTrackId);
                break;
            }
            foreach (CanonicalSmfTrackChannelEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        public IEnumerable<CanonicalOpaqueMidiEventPage> QueryTrackOpaqueEventPages(
            MidoraId exportTrackId,
            long startTick,
            long endTick,
            CancellationToken cancellationToken = default)
        {
            if (exportTrackId == default
                || startTick < _compiledStartTick
                || endTick > _compiledEndTick
                || endTick <= startTick)
            {
                throw new ArgumentOutOfRangeException(nameof(exportTrackId));
            }

            using BoundedCanonicalOpaqueEventSorter sorter = new();
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                PureMidiTrackPlan? trackPlan = rootPlan.Tracks
                    .FirstOrDefault(value => value.Track.Id == exportTrackId);
                if (trackPlan is null) continue;
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long contentStart = segment.ContentOffsetTick;
                    long contentEnd = checked(contentStart + segment.LengthTicks);
                    foreach (OpaqueMidiEventValue value in segment.OpaqueEvents.QueryValues(
                        contentStart,
                        contentEnd))
                    {
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - contentStart);
                        if (absoluteTick < startTick || absoluteTick >= endTick) continue;
                        SourceReference source = DirectSource(
                            rootPlan.Root.Id,
                            trackPlan.Track.Id,
                            segment.Id,
                            value.Id,
                            absoluteTick,
                            SourceOrigin.OpaqueMidiEvent);
                        sorter.Add(new(
                            trackPlan.Track.Id,
                            absoluteTick,
                            value.Kind,
                            value.MetaType,
                            value.Payload,
                            value.Order,
                            source,
                            trackPlan.TrackOrder));
                    }
                }
                break;
            }
            foreach (CanonicalOpaqueMidiEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        private void AppendSegmentRange(
            MidiChannelRoot root,
            PureMidiTrackPlan trackPlan,
            MidiSegment segment,
            byte port,
            byte channel,
            long startTick,
            long endTick,
            bool includeStateAtStart,
            ICollection<CanonicalMidiEvent> output,
            CancellationToken cancellationToken,
            bool includePairedNotes = true)
        {
            long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
            if (segment.ProjectStartTick >= endTick || segmentEnd < startTick) return;
            long contentStart = segment.ContentOffsetTick;
            long contentEnd = checked(contentStart + segment.LengthTicks);
            long queryContentStart = Math.Max(
                contentStart,
                checked(startTick - segment.ProjectStartTick + contentStart));
            long queryContentEnd = Math.Min(
                contentEnd,
                checked(endTick - segment.ProjectStartTick + contentStart));

            long endpointQueryEnd = Math.Max(queryContentEnd, queryContentStart + 1);
            if (includePairedNotes)
            {
                foreach (DirectMidiNoteValue note in segment.Notes.QueryStartValues(
                    queryContentStart,
                    endpointQueryEnd))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                    long absoluteStart = checked(
                        segment.ProjectStartTick + note.StartTick - contentStart);
                    if (absoluteStart < startTick || absoluteStart >= endTick) continue;
                    SourceReference source = DirectSource(
                        root.Id,
                        trackPlan.Track.Id,
                        segment.Id,
                        note.Id,
                        absoluteStart,
                        SourceOrigin.DirectMidiNote);
                    AddDirect(
                        absoluteStart,
                        note.NoteOnOrder,
                        endpointOrder: 1,
                        note.Id,
                        MidiMessage.NoteOn(
                            channel,
                            checked((byte)note.Key),
                            checked((byte)note.NoteOnVelocity)),
                        source,
                        trackPlan,
                        output,
                        port,
                        channel);
                }

                long noteEndQueryEnd = endpointQueryEnd;
                if (endTick == _compiledEndTick && noteEndQueryEnd < long.MaxValue)
                {
                    noteEndQueryEnd++;
                }
                if (segmentEnd < endTick && noteEndQueryEnd <= contentEnd && contentEnd < long.MaxValue)
                    noteEndQueryEnd = contentEnd + 1;
                foreach (DirectMidiNoteValue note in segment.Notes.QueryEndValues(
                    queryContentStart,
                    noteEndQueryEnd))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                    // Gates crossing the content boundary are emitted only by the
                    // clipped-end query below, including a window starting at that end.
                    if (SaturatingAdd(note.StartTick, note.LengthTicks) > contentEnd) continue;
                    long absoluteStart = checked(
                        segment.ProjectStartTick + note.StartTick - contentStart);
                    long absoluteEnd = Math.Min(segmentEnd, SaturatingAdd(absoluteStart, note.LengthTicks));
                    if (absoluteEnd < startTick
                        || absoluteEnd > endTick
                        || absoluteEnd == endTick && endTick != _compiledEndTick)
                    {
                        continue;
                    }
                    SourceReference source = DirectSource(
                        root.Id,
                        trackPlan.Track.Id,
                        segment.Id,
                        note.Id,
                        absoluteEnd,
                        SourceOrigin.DirectMidiNote);
                    AddDirect(
                        absoluteEnd,
                        note.NoteOffOrder,
                        endpointOrder: 0,
                        note.Id,
                        MidiMessage.NoteOff(
                            channel,
                            checked((byte)note.Key),
                            checked((byte)note.NoteOffVelocity)),
                        source,
                        trackPlan,
                        output,
                        port,
                        channel);
                }

                bool includeSegmentEnd = segmentEnd >= startTick
                    && (segmentEnd < endTick
                        || endTick == _compiledEndTick && segmentEnd == endTick);
                if (includeSegmentEnd)
                {
                    foreach (DirectMidiNoteValue note in segment.Notes.QueryActiveValues(contentEnd))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                        SourceReference source = DirectSource(
                            root.Id,
                            trackPlan.Track.Id,
                            segment.Id,
                            note.Id,
                            segmentEnd,
                            SourceOrigin.DirectMidiNote);
                        AddDirect(
                            segmentEnd,
                            note.NoteOffOrder,
                            endpointOrder: 0,
                            note.Id,
                            MidiMessage.NoteOff(
                                channel,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOffVelocity)),
                            source,
                            trackPlan,
                            output,
                            port,
                            channel,
                            CanonicalEventRole.DirectMidi);
                    }
                }

            }

            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(
                queryContentStart,
                endTick == _compiledEndTick && queryContentEnd < contentEnd
                    ? queryContentEnd + 1 : Math.Max(queryContentEnd, queryContentStart + 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Tick < contentStart || value.Tick >= contentEnd) continue;
                long absoluteTick = checked(segment.ProjectStartTick + value.Tick - contentStart);
                MidiMessage message = ToMidiMessage(value, channel);
                long target = SemanticTargetForMessage(message);
                if (absoluteTick < startTick) continue;
                if (absoluteTick >= endTick && !(absoluteTick == endTick
                    && endTick == _compiledEndTick && IsNoteOff(message))) continue;
                SourceReference source = DirectSource(
                    root.Id,
                    trackPlan.Track.Id,
                    segment.Id,
                    value.Id,
                    absoluteTick,
                    SourceOrigin.DirectMidiChannelEvent);
                AddDirect(
                    absoluteTick,
                    value.Order,
                    endpointOrder: 1,
                    value.Id,
                    message,
                    source,
                    trackPlan,
                    output,
                    port,
                    channel,
                    CanonicalEventRole.DirectMidi,
                    target);
            }
            if ((segmentEnd >= startTick
                    && (segmentEnd < endTick
                        || endTick == _compiledEndTick && segmentEnd == endTick))
                && FindAnalysis(root.Id, segment.Id) is RootIntervalAnalysis analysis
                && analysis.RawBoundaryNotes.TryGetValue(segment.Id, out long[]? rawKeys))
            {
                AppendRemainingNoteOffs(root, [(trackPlan, segment)], rawKeys, segmentEnd,
                    port, channel, output, cancellationToken, rawOnly: true);
            }
        }


        private static int CompareRootStateCandidate(
            RootStateCandidate left,
            RootStateCandidate right)
        {
            int value = left.AbsoluteTick.CompareTo(right.AbsoluteTick);
            if (value != 0) return value;
            value = left.TrackPlan.TrackOrder.CompareTo(right.TrackPlan.TrackOrder);
            if (value != 0) return value;
            value = left.Value.Order.CompareTo(right.Value.Order);
            return value != 0 ? value : left.Value.Id.CompareTo(right.Value.Id);
        }


        private void BuildAnalysis(CancellationToken cancellationToken)
        {
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                foreach ((PureMidiTrackPlan _, MidiSegment segment) in
                    EnumerateAnalysisSegments(rootPlan))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _channelAnalysis.TryAdd(
                        segment.Id,
                        BuildSegmentChannelAnalysis(segment, cancellationToken));
                }
            }

            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                foreach (PureMidiRootInterval interval in rootPlan.Intervals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HashSet<long> targets = [];
                    Dictionary<MidoraId, long[]> rawBoundaryNotes = [];
                    foreach ((PureMidiTrackPlan _, MidiSegment segment) in
                        EnumerateAnalysisSegments(rootPlan))
                    {
                        long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                        if (segment.ProjectStartTick >= interval.EndTick
                            || segmentEnd <= interval.StartTick)
                        {
                            continue;
                        }
                        SegmentChannelAnalysis segmentAnalysis = _channelAnalysis[segment.Id];
                        targets.UnionWith(segmentAnalysis.UsedTargets);
                        long[] activeKeys = segmentAnalysis.RawBoundaryNoteKeys;
                        if (activeKeys.Any(value => value != 0)) rawBoundaryNotes[segment.Id] = activeKeys;
                    }
                    _analysis.Add(
                        (rootPlan.Root.Id, interval.GroupId),
                        new(targets.Order().ToArray(), rawBoundaryNotes));
                }
            }
        }

        private IEnumerable<(PureMidiTrackPlan TrackPlan, MidiSegment Segment)>
            EnumerateAnalysisSegments(PureMidiRootPlan rootPlan)
        {
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(IsRepresentableMidiSegment)
                    .Where(value => value.ProjectStartTick < _compiledEndTick)
                    .OrderBy(value => value.ProjectStartTick)
                    .ThenBy(value => value.Id))
                {
                    long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                    if (rootPlan.Intervals.Any(interval =>
                        segment.ProjectStartTick < Math.Min(interval.EndTick, _compiledEndTick)
                        && segmentEnd > interval.StartTick))
                    {
                        yield return (trackPlan, segment);
                    }
                }
            }
        }

        private static SegmentChannelAnalysis BuildSegmentChannelAnalysis(
            MidiSegment segment,
            CancellationToken cancellationToken)
        {
            const int CheckpointInterval = 16_384;
            long contentStart = segment.ContentOffsetTick;
            long contentEnd = checked(contentStart + segment.LengthTicks);
            HashSet<long> targets = [];
            HashSet<byte> banks = [0];
            HashSet<byte> programs = [0];
            long[] rawActiveNoteCounts = new long[128];
            bool hasRawNotes = false;
            long firstRawNoteTick = long.MaxValue;
            Dictionary<long, DirectMidiChannelEventValue> state = [];
            List<ChannelStateCheckpoint> checkpoints = [];
            long rawNoteOnCount = 0;
            int eventCount = 0;
            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(
                contentStart,
                contentEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MidiMessage message = ToMidiMessage(value, channel: 0);
                long target = SemanticTargetForMessage(message);
                if (target != long.MinValue)
                {
                    targets.Add(target);
                    state[target] = value;
                }
                if (value.Kind == DirectMidiChannelEventKind.ControlChange && value.Data1 == 0)
                {
                    banks.Add(checked((byte)value.Data2));
                }
                else if (value.Kind == DirectMidiChannelEventKind.ProgramChange)
                {
                    programs.Add(checked((byte)value.Data1));
                }
                if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                {
                    hasRawNotes = true;
                    firstRawNoteTick = Math.Min(firstRawNoteTick, value.Tick);
                    rawActiveNoteCounts[message.Byte1]++;
                    rawNoteOnCount++;
                }
                else if (message.MessageType == MidiMessageType.NoteOff
                    || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
                {
                    hasRawNotes = true;
                    firstRawNoteTick = Math.Min(firstRawNoteTick, value.Tick);
                    if (rawActiveNoteCounts[message.Byte1] != 0)
                    {
                        rawActiveNoteCounts[message.Byte1]--;
                    }
                }
                eventCount++;
                if (eventCount % CheckpointInterval == 0 && state.Count != 0)
                {
                    checkpoints.Add(new(
                        value.Tick,
                        state.ToDictionary(entry => entry.Key, entry => entry.Value)));
                }
            }
            return new(
                contentStart,
                targets.Order().ToArray(),
                rawActiveNoteCounts,
                rawNoteOnCount,
                hasRawNotes,
                firstRawNoteTick,
                banks.Order().ToArray(),
                programs.Order().ToArray(),
                checkpoints.ToArray());
        }



        private string CreateContentFingerprint(CancellationToken cancellationToken)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendText("MIDORA_PURE_MIDI_CANONICAL_RANGE_V2");
            AppendLong(_compiledStartTick);
            AppendLong(_compiledEndTick);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLong(rootPlan.Root.Id.Value);
                AppendLong(_unitByRoot.GetValueOrDefault(rootPlan.Root.Id, -1));
            }
            // Reuse the full lifecycle identities already computed above. They
            // include prior sibling state, defaults and COW content, not merely
            // Segments intersecting the requested playback range. Re-hashing
            // only that subset could reuse stale range state after an edit.
            foreach (CanonicalPureMidiAudioFragmentDescriptor fragment in PureMidiAudioFragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendText(fragment.SemanticFingerprint);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());

            void AppendLong(long value)
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendText(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                AppendLong(bytes.Length);
                hash.AppendData(bytes);
            }
        }

        private RootIntervalAnalysis? FindAnalysis(MidoraId rootId, MidoraId segmentId) =>
            _analysis
                .Where(value => value.Key.RootId == rootId
                    && value.Value.RawBoundaryNotes.ContainsKey(segmentId))
                .Select(value => value.Value)
                .FirstOrDefault();

        private static void AddDirect(
            long tick,
            long explicitOrder,
            int endpointOrder,
            MidoraId stableId,
            MidiMessage message,
            SourceReference source,
            PureMidiTrackPlan trackPlan,
            ICollection<CanonicalMidiEvent> output,
            byte port,
            byte channel,
            CanonicalEventRole role = CanonicalEventRole.DirectMidi,
            long semanticTarget = long.MinValue)
        {
            if (endpointOrder is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(endpointOrder));
            }
            long stableOrder = EncodeDirectMidiStableOrder(message, stableId);
            output.Add(new(
                tick,
                port,
                channel,
                message,
                role,
                stableOrder,
                semanticTarget,
                stableOrder,
                source,
                trackPlan.Track.Id,
                trackPlan.TrackOrder,
                explicitOrder));
        }

        private static long SaturatingAdd(long left, long right) =>
            right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

        private sealed record RootIntervalAnalysis(
            long[] UsedTargets,
            IReadOnlyDictionary<MidoraId, long[]> RawBoundaryNotes);

        private readonly record struct RootStateCandidate(
            PureMidiTrackPlan TrackPlan,
            MidiSegment Segment,
            DirectMidiChannelEventValue Value,
            long AbsoluteTick);

        private sealed record SegmentChannelAnalysis(
            long ContentStartTick,
            long[] UsedTargets,
            long[] RawBoundaryNoteKeys,
            long RawNoteOnCount,
            bool HasRawNotes,
            long FirstRawNoteTick,
            byte[] ReferencedBanks,
            byte[] ReferencedPrograms,
            ChannelStateCheckpoint[] StateCheckpoints)
        {
            public Dictionary<long, DirectMidiChannelEventValue> GetStateAt(
                DirectMidiChannelEventCollection events,
                long tick)
            {
                Dictionary<long, DirectMidiChannelEventValue> result = [];
                long scanStart = ContentStartTick;
                int low = 0;
                int high = StateCheckpoints.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    if (StateCheckpoints[middle].Tick < tick) low = middle + 1;
                    else high = middle;
                }
                int checkpointIndex = low - 1;
                if (checkpointIndex >= 0)
                {
                    ChannelStateCheckpoint checkpoint = StateCheckpoints[checkpointIndex];
                    foreach ((long target, DirectMidiChannelEventValue value) in checkpoint.State)
                    {
                        result.Add(target, value);
                    }
                    // Re-reading the checkpoint tick is intentional. A checkpoint
                    // may split one same-tick suffix; deterministic assignment
                    // makes replay idempotent and completes that suffix.
                    scanStart = checkpoint.Tick;
                }
                foreach (DirectMidiChannelEventValue value in events.QueryOrderedValues(
                    scanStart,
                    tick))
                {
                    MidiMessage message = ToMidiMessage(value, channel: 0);
                    long target = SemanticTargetForMessage(message);
                    if (target != long.MinValue)
                    {
                        result[target] = value;
                    }
                }
                return result;
            }
        }

        private sealed record ChannelStateCheckpoint(
            long Tick,
            IReadOnlyDictionary<long, DirectMidiChannelEventValue> State);

        private sealed class CanonicalSmfSortSink(BoundedCanonicalSmfEventSorter sorter) :
            ICollection<CanonicalMidiEvent>
        {
            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;

            public void Add(CanonicalMidiEvent item) => sorter.Add(new(
                item.ExportTrackId,
                item.Tick,
                item.ZeroBasedPort,
                item.ZeroBasedChannel,
                item.Message,
                item.Role,
                item.SmfEventOrder,
                item.Source.DirectMidiObjectId));

            public void Clear() => throw new NotSupportedException();
            public bool Contains(CanonicalMidiEvent item) => throw new NotSupportedException();
            public void CopyTo(CanonicalMidiEvent[] array, int arrayIndex) =>
                throw new NotSupportedException();
            public bool Remove(CanonicalMidiEvent item) => throw new NotSupportedException();
            public IEnumerator<CanonicalMidiEvent> GetEnumerator() =>
                throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();
        }

        private sealed class CanonicalMidiRenderSortSink(
            BoundedCanonicalMidiRenderEventSorter sorter) : ICollection<CanonicalMidiEvent>
        {
            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;
            public void Add(CanonicalMidiEvent item) => sorter.Add(item);
            public void Clear() => throw new NotSupportedException();
            public bool Contains(CanonicalMidiEvent item) => throw new NotSupportedException();
            public void CopyTo(CanonicalMidiEvent[] array, int arrayIndex) =>
                throw new NotSupportedException();
            public bool Remove(CanonicalMidiEvent item) => throw new NotSupportedException();
            public IEnumerator<CanonicalMidiEvent> GetEnumerator() =>
                throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();
        }
    }
}
