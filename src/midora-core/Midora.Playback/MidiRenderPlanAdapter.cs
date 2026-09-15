using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Text;

namespace Midora.Playback;

public static class MidiRenderPlanAdapter
{
    public static MidiRenderPlan Create(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds = null,
        CancellationToken cancellationToken = default)
        => CreateCore(compiled, sampleRate, audibleTrackIds,
            preserveFilteredTrackEvents: false, restrictToFileSampleRateRange: true, cancellationToken);

    public static MidiRenderPlan CreateRealtime(
        CanonicalCompiledResult compiled,
        int sampleRate)
        => CreateCore(compiled, sampleRate, audibleTrackIds: null,
            preserveFilteredTrackEvents: false, restrictToFileSampleRateRange: false);

    public static MidiRenderPlan CreateRealtime(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId> audibleTrackIds)
    {
        ArgumentNullException.ThrowIfNull(audibleTrackIds);
        return CreateCore(compiled, sampleRate, audibleTrackIds,
            preserveFilteredTrackEvents: true, restrictToFileSampleRateRange: false);
    }

    private static MidiRenderPlan CreateCore(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds,
        bool preserveFilteredTrackEvents,
        bool restrictToFileSampleRateRange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        cancellationToken.ThrowIfCancellationRequested();
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException("Only a consumable canonical result can produce an audio plan.", nameof(compiled));
        }
        if (sampleRate <= 0
            || restrictToFileSampleRateRange && sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        TempoSampleMap map = new(compiled.TicksPerQuarterNote, compiled.Tempos);
        long totalFrames = map.TickToSampleFrame(compiled.EndTick, compiled.StartTick, sampleRate);
        ReadOnlySpan<CanonicalMidiEvent> events = compiled.Events;
        CanonicalAudioUnitProjection audioProjection = CanonicalAudioUnitProjection.Create(compiled,
            cancellationToken: cancellationToken);
        ChannelUnitAllocation[] allocations = compiled.Allocations.ToArray();
        HashSet<MidoraId> sourceIdSet = [];
        foreach (MidoraId source in audioProjection.SourceIds) sourceIdSet.Add(source);
        foreach (CanonicalMidiEvent value in events)
        {
            if (value.Source.TrackId != default)
            {
                sourceIdSet.Add(value.Source.TrackId);
            }
        }
        foreach (ChannelUnitAllocation value in allocations)
        {
            if (value.TrackId != default)
            {
                sourceIdSet.Add(value.TrackId);
            }
            if (value.MidiChannelRootId != default)
            {
                sourceIdSet.Add(value.MidiChannelRootId);
            }
        }
        foreach (CanonicalSmfTrackDescriptor value in compiled.SmfTracks)
        {
            if (value.SourceTrackId != default) sourceIdSet.Add(value.SourceTrackId);
            if (value.MidiChannelRootId != default) sourceIdSet.Add(value.MidiChannelRootId);
        }
        MidoraId[] sourceIds = sourceIdSet.OrderBy(value => value).ToArray();
        Dictionary<MidoraId, int> sourceIndices = new(sourceIds.Length);
        for (int i = 0; i < sourceIds.Length; i++)
        {
            sourceIndices.Add(sourceIds[i], i);
        }
        List<int> initiallyDisabledBuilder = [];
        HashSet<MidoraId> rootSourceIds = allocations
            .Where(value => value.MidiChannelRootId != default)
            .Select(value => value.MidiChannelRootId)
            .ToHashSet();
        if (preserveFilteredTrackEvents && audibleTrackIds is not null)
        {
            for (int sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
            {
                if (!rootSourceIds.Contains(sourceIds[sourceIndex])
                    && !audibleTrackIds.Contains(sourceIds[sourceIndex]))
                {
                    initiallyDisabledBuilder.Add(sourceIndex);
                }
            }
        }
        int[] initiallyDisabled = initiallyDisabledBuilder.ToArray();

        Dictionary<(MidoraId InstanceGroupId, MidoraId SubVoiceId), ChannelUnitAllocation>
            allocationByFragment = [];
        foreach (ChannelUnitAllocation allocation in allocations)
        {
            allocationByFragment.TryAdd(
                (allocation.InstanceGroupId, allocation.SubVoiceId),
                allocation);
        }
        List<MidiUnitFragmentRenderPlan> unitFragmentBuilder = [];
        Dictionary<(MidoraId RootId, MidoraId GroupId), CanonicalPureMidiAudioFragmentDescriptor>
            pureAudioFragments = compiled.PureMidiAudioFragments.ToDictionary(
                value => (value.MidiChannelRootId, value.GroupId));
        foreach (CanonicalAudioUnitFragment fragment in
            audioProjection.Fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preserveFilteredTrackEvents
                && audibleTrackIds is not null
                && !audibleTrackIds.Contains(fragment.TrackId))
            {
                continue;
            }
            if (!allocationByFragment.TryGetValue(
                    (fragment.InstanceGroupId, fragment.SubVoiceId),
                    out ChannelUnitAllocation allocation))
            {
                throw new InvalidDataException(
                    "A canonical audio Unit fragment has no physical allocation.");
            }
            int sourceIndex = fragment.MidiChannelRootId != default
                ? sourceIndices[fragment.MidiChannelRootId]
                : sourceIndices[fragment.TrackId];
            ReadOnlySpan<CanonicalAudioUnitEvent> fragmentEvents = fragment.Events;
            List<(CanonicalMidiRenderEvent Order, ScheduledMidiMessage Scheduled)>
                orderedFragmentEvents = new(fragmentEvents.Length);
            for (int eventIndex = 0; eventIndex < fragmentEvents.Length; eventIndex++)
            {
                CanonicalAudioUnitEvent value = fragmentEvents[eventIndex];
                if (!IsSupportedByInitialReleaseAudioProjection(value.Message))
                {
                    continue;
                }

                long tick = fragment.GroupStartTick + value.RelativeTick;
                orderedFragmentEvents.Add((
                    new(
                        tick,
                        allocation.ZeroBasedPort,
                        value.Message,
                        value.Source.TrackId,
                        value.Source.Origin == SourceOrigin.MidiChannelRootLifecycle
                            && value.Source.MidiChannelRootId != default
                                ? value.Source.MidiChannelRootId
                                : value.Source.TrackId,
                        value.Role,
                        value.StableOrder,
                        value.SmfTrackOrder,
                        value.SmfEventOrder,
                        value.StableObjectId),
                    new(
                        map.TickToSampleFrame(tick, compiled.StartTick, sampleRate),
                        value.Message,
                        ResolveMonitoringSourceIndex(value.Source, sourceIndices, sourceIndex))));
            }
            if (fragment.MidiChannelRootId != default && !compiled.HasPagedEvents)
            {
                foreach (CanonicalMidiChannelModeSystemExclusiveEvent value in
                    compiled.ChannelModeSystemExclusiveEvents)
                {
                    if (value.Source.MidiChannelRootId != fragment.MidiChannelRootId
                        || value.Tick < fragment.EffectiveStartTick
                        || value.Tick >= fragment.EffectiveEndTick)
                    {
                        continue;
                    }
                    if (!preserveFilteredTrackEvents
                        && audibleTrackIds is not null
                        && !audibleTrackIds.Contains(value.Source.TrackId))
                    {
                        continue;
                    }
                    CanonicalMidiRenderEvent order = ToRenderEvent(value) with
                    {
                        Message = MidiMessage.ProgramChange(0, 0),
                        ChannelModeSystemExclusive = value.Value with { TargetChannel = 0 }
                    };
                    orderedFragmentEvents.Add((
                        order,
                        ScheduledMidiMessage.CreateChannelModeSystemExclusive(
                            map.TickToSampleFrame(value.Tick, compiled.StartTick, sampleRate),
                            channel: 0,
                            value.Value,
                            ResolveMonitoringSourceIndex(
                                value.Source,
                                sourceIndices,
                                sourceIndex))));
                }
            }
            ScheduledMidiMessage[] scheduledFragmentEvents = orderedFragmentEvents
                .OrderBy(value => value.Order, CanonicalMidiRenderEventPageProvider.RenderEventComparer.Instance)
                .Select(value => value.Scheduled)
                .ToArray();
            unitFragmentBuilder.Add(new MidiUnitFragmentRenderPlan(
                allocation.ZeroBasedPort,
                allocation.ZeroBasedChannel,
                fragment.TrackId.Value,
                fragment.SegmentId.Value,
                fragment.EventInstrumentId.Value,
                fragment.InstanceGroupId.Value,
                fragment.SubVoiceId.Value,
                sourceIndex,
                map.TickToSampleFrame(fragment.EffectiveStartTick, compiled.StartTick, sampleRate),
                map.TickToSampleFrame(fragment.EffectiveEndTick, compiled.StartTick, sampleRate),
                fragment.MidiChannelRootId != default
                    && pureAudioFragments.TryGetValue(
                        (fragment.MidiChannelRootId, fragment.InstanceGroupId),
                        out CanonicalPureMidiAudioFragmentDescriptor? pureDescriptor)
                            ? CreatePureMidiAudioFingerprint(compiled, pureDescriptor)
                            : compiled.HasPagedLogicalEvents
                                ? CreatePagedLogicalAudioFingerprint(compiled, fragment)
                                : fragment.SemanticFingerprint,
                scheduledFragmentEvents,
                midiChannelRootId: fragment.MidiChannelRootId.Value,
                isPercussion: fragment.ChannelMode == MidiChannelMode.Percussion));
        }
        MidiUnitFragmentRenderPlan[] unitFragments = unitFragmentBuilder
            .OrderBy(value => value.CanonicalUnitNumber)
            .ThenBy(value => value.StartFrame)
            .ThenBy(value => value.InstanceGroupId)
            .ThenBy(value => value.SubVoiceId)
            .ToArray();
        MidiSegmentRenderPlan[] segments = unitFragments
            .GroupBy(value => (value.TrackId, value.SegmentId, value.SourceIndex))
            .Select(group => CreateSegmentPlan(group.Key, group.ToArray()))
            .OrderBy(value => value.SourceIndex)
            .ThenBy(value => value.StartFrame)
            .ThenBy(value => value.SegmentId)
            .ToArray();
        List<CanonicalMidiRenderEvent> orderedRenderEvents = new(events.Length);
        foreach (CanonicalMidiEvent value in events) orderedRenderEvents.Add(ToRenderEvent(value));
        if (!compiled.HasPagedEvents)
        {
            orderedRenderEvents.AddRange(compiled.ChannelModeSystemExclusiveEvents
                .ToArray()
                .Select(ToRenderEvent));
        }
        orderedRenderEvents.Sort(CanonicalMidiRenderEventPageProvider.RenderEventComparer.Instance);
        List<ScheduledMidiMessage>?[] eventsByPort = new List<ScheduledMidiMessage>?[16];
        foreach (CanonicalMidiRenderEvent value in orderedRenderEvents)
        {
            if (value.ChannelModeSystemExclusive is null
                && !IsSupportedByInitialReleaseAudioProjection(value.Message))
            {
                continue;
            }

            bool hasTrack = value.TrackId != default;
            if (!preserveFilteredTrackEvents && audibleTrackIds is not null
                && hasTrack && !audibleTrackIds.Contains(value.TrackId))
            {
                continue;
            }
            List<ScheduledMidiMessage> scheduled = eventsByPort[value.ZeroBasedPort]
                ??= [];
            long frame = map.TickToSampleFrame(value.Tick, compiled.StartTick, sampleRate);
            int sourceIndex = sourceIndices.GetValueOrDefault(value.MonitoringSourceId, -1);
            scheduled.Add(value.ChannelModeSystemExclusive is { } systemExclusive
                ? ScheduledMidiMessage.CreateChannelModeSystemExclusive(
                    frame,
                    value.Message.ChannelNumber,
                    systemExclusive,
                    sourceIndex)
                : new(frame, value.Message, sourceIndex));
        }
        List<MidiPortRenderPlan> ports = [];
        for (byte port = 0; port < eventsByPort.Length; port++)
        {
            if (eventsByPort[port] is { Count: > 0 } scheduled)
            {
                ports.Add(new MidiPortRenderPlan(port, scheduled.ToArray()));
            }
        }
        IMidiRenderEventPageProvider? eventPageProvider = compiled.HasPagedEvents
            ? new CanonicalMidiRenderEventPageProvider(
                compiled,
                map,
                sampleRate,
                sourceIndices,
                preserveFilteredTrackEvents ? null : audibleTrackIds)
            : null;
        MidiRenderUnitDescriptor[] unitDescriptors = allocations
            .Select(value => new MidiRenderUnitDescriptor(
                value.ZeroBasedPort,
                value.ZeroBasedChannel))
            .Distinct()
            .OrderBy(value => value.CanonicalUnitNumber)
            .ToArray();
        // SMF Track descriptors exist for both in-memory and paged Pure MIDI
        // content. PureMidiAudioFragments is deliberately paged-content
        // metadata, so deriving monitoring/cache ownership from it omitted small
        // freshly-created MIDI Tracks and left merged Root caches unprotected.
        MidiRenderCacheSourceBinding[] cacheSourceBindings = compiled.SmfTracks.ToArray()
            .Where(value => value.Kind == CanonicalSmfTrackKind.PureMidiTrack
                && value.SourceTrackId != default
                && value.MidiChannelRootId != default)
            .Select(value => new
            {
                Track = sourceIndices[value.SourceTrackId],
                Root = sourceIndices[value.MidiChannelRootId]
            })
            .Distinct()
            .OrderBy(value => value.Track)
            .ThenBy(value => value.Root)
            .Select(value => new MidiRenderCacheSourceBinding(value.Track, value.Root))
            .ToArray();
        int[] referencedPresetKeys = CollectReferencedPresetKeys(
            events,
            compiled.PureMidiPresetReferences,
            audioProjection.ReferencedPresetKeys);
        return new MidiRenderPlan(
            sampleRate,
            totalFrames,
            ports.ToArray(),
            sourceIds.Select(value => value.Value).ToArray(),
            initiallyDisabled,
            unitFragments,
            segments,
            unitDescriptors,
            eventPageProvider: eventPageProvider,
            cacheSourceBindings: cacheSourceBindings,
            referencedPresetKeys: referencedPresetKeys);
    }

    private static int[] CollectReferencedPresetKeys(
        ReadOnlySpan<CanonicalMidiEvent> inMemoryEvents,
        IReadOnlyList<CanonicalMidiPresetReference> pagedReferences,
        IReadOnlyList<int> logicalReferences)
    {
        bool[] referenced = new bool[128 * 128];
        referenced[0] = true;
        Span<byte> banks = stackalloc byte[256];
        Span<byte> programs = stackalloc byte[256];
        foreach (CanonicalMidiEvent value in inMemoryEvents)
        {
            MidiMessage message = value.Message;
            int unit = value.ZeroBasedPort * 16 + value.ZeroBasedChannel;
            if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 0)
                banks[unit] = message.Byte2;
            else if (message.MessageType == MidiMessageType.ProgramChange)
                programs[unit] = message.Byte1;
            else if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                referenced[(banks[unit] << 7) | programs[unit]] = true;
        }
        foreach (CanonicalMidiPresetReference value in pagedReferences)
            referenced[(value.Bank << 7) | value.Program] = true;
        foreach (int key in logicalReferences) referenced[key] = true;
        return Enumerable.Range(0, referenced.Length).Where(index => referenced[index]).ToArray();
    }

    private static CanonicalMidiRenderEvent ToRenderEvent(CanonicalMidiEvent value) => new(
        value.Tick,
        value.ZeroBasedPort,
        value.Message,
        value.Source.TrackId,
        value.Source.Origin == SourceOrigin.MidiChannelRootLifecycle
            && value.Source.MidiChannelRootId != default
                ? value.Source.MidiChannelRootId
                : value.Source.TrackId,
        value.Role,
        value.StableOrder,
        value.SmfTrackOrder,
        value.SmfEventOrder,
        value.Source.DirectMidiObjectId);

    private static CanonicalMidiRenderEvent ToRenderEvent(
        CanonicalMidiChannelModeSystemExclusiveEvent value) => new(
        value.Tick,
        value.ZeroBasedPort,
        MidiMessage.ProgramChange(value.ZeroBasedChannel, 0),
        value.Source.TrackId,
        value.Source.TrackId,
        value.Role,
        value.StableOrder,
        value.SmfTrackOrder,
        value.SmfEventOrder,
        value.Source.DirectMidiObjectId,
        value.Value with { TargetChannel = value.ZeroBasedChannel });

    private static string CreatePureMidiAudioFingerprint(
        CanonicalCompiledResult compiled,
        CanonicalPureMidiAudioFragmentDescriptor descriptor)
    {
        using HashingWriteStream payload = new();
        using BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true);
        writer.Write("MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V1");
        writer.Write(descriptor.SemanticFingerprint);
        writer.Write(descriptor.StartTick);
        writer.Write(descriptor.EndTick);
        writer.Write(compiled.StartTick);
        writer.Write(compiled.EndTick);
        foreach (CanonicalTempo tempo in compiled.Tempos)
        {
            writer.Write(tempo.SourceId.Value);
            writer.Write(tempo.Tick);
            foreach (int part in decimal.GetBits(tempo.BeatsPerMinute)) writer.Write(part);
            writer.Write(tempo.IsRangeRestore);
        }
        writer.Flush();
        return payload.GetHash();
    }

    private static string CreatePagedLogicalAudioFingerprint(
        CanonicalCompiledResult compiled, CanonicalAudioUnitFragment fragment)
    {
        // Paged fragments have no resident ScheduledEvents. Their sample-domain cache
        // identity must still distinguish event timing under different Tempo maps,
        // including maps with the same total duration but different internal timing.
        using HashingWriteStream payload = new();
        using BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true);
        writer.Write("MIDORA_PAGED_LOGICAL_SAMPLE_TIMING_V1");
        writer.Write(fragment.SemanticFingerprint);
        writer.Write(compiled.TicksPerQuarterNote);
        writer.Write(compiled.StartTick);
        writer.Write(fragment.GroupStartTick);
        writer.Write(fragment.EffectiveStartTick);
        writer.Write(fragment.EffectiveEndTick);
        foreach (CanonicalTempo tempo in compiled.Tempos)
        {
            writer.Write(tempo.Tick);
            foreach (int part in decimal.GetBits(tempo.BeatsPerMinute)) writer.Write(part);
        }
        writer.Flush();
        return payload.GetHash();
    }

    private static MidiSegmentRenderPlan CreateSegmentPlan(
        (long TrackId, long SegmentId, int SourceIndex) identity,
        MidiUnitFragmentRenderPlan[] fragments)
    {
        long startFrame = fragments.Min(value => value.StartFrame);
        long endFrame = fragments.Max(value => value.EndFrame);
        using HashingWriteStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_SEGMENT_PROJECTION_V1");
            writer.Write(identity.TrackId);
            writer.Write(identity.SegmentId);
            writer.Write(endFrame - startFrame);
            writer.Write(fragments.Length);
            foreach (MidiUnitFragmentRenderPlan fragment in fragments
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.EndFrame)
                .ThenBy(value => value.EventInstrumentId)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId))
            {
                // Physical route is deliberately excluded: the Segment stem is the
                // deterministic sum of abstract 1-channel Unit projections.
                writer.Write(fragment.EventInstrumentId);
                writer.Write(fragment.InstanceGroupId);
                writer.Write(fragment.SubVoiceId);
                writer.Write(fragment.StartFrame - startFrame);
                writer.Write(fragment.EndFrame - startFrame);
                writer.Write(fragment.SemanticFingerprint);
                writer.Write(fragment.Events.Length);
                foreach (ScheduledMidiMessage value in fragment.Events)
                {
                    writer.Write(value.SampleFrame - startFrame);
                    writer.Write(value.Message.PackedValue);
                    if (value.ChannelModeSystemExclusive is { } systemExclusive)
                    {
                        writer.Write((byte)systemExclusive.Kind);
                        writer.Write(systemExclusive.DeviceId);
                        writer.Write(systemExclusive.ModeValue);
                    }
                    else
                    {
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                    }
                }
            }
        }
        string fingerprint = payload.GetHash();
        return new(
            identity.TrackId,
            identity.SegmentId,
            identity.SourceIndex,
            startFrame,
            endFrame,
            fingerprint);
    }

    internal static bool IsSupportedByInitialReleaseAudioProjection(MidiMessage message)
        => message.MessageType != MidiMessageType.ControlChange
            || message.Byte1 is not (91 or 93);

    internal static int ResolveMonitoringSourceIndex(
        SourceReference source,
        IReadOnlyDictionary<MidoraId, int> sourceIndices,
        int fallback)
    {
        if (source.Origin == SourceOrigin.MidiChannelRootLifecycle
            && source.MidiChannelRootId != default)
        {
            return sourceIndices[source.MidiChannelRootId];
        }
        return source.TrackId != default
            ? sourceIndices[source.TrackId]
            : fallback;
    }

}
