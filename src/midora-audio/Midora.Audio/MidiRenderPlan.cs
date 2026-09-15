using Midora.Midi;

namespace Midora.Audio;

public sealed partial class MidiRenderPlan
{
    private readonly MidiPortRenderPlan[] _ports;
    private readonly MidiUnitRenderPlan[] _units;
    private readonly MidiUnitFragmentRenderPlan[] _unitFragments;
    private readonly MidiSegmentRenderPlan[] _segments;
    private readonly long[] _sourceIds;
    private readonly int[] _initiallyDisabledSourceIndices;
    private readonly MidiRenderUnitDescriptor[] _unitDescriptors;
    private readonly MidiRenderCacheSourceBinding[] _cacheSourceBindings;
    private readonly int[] _referencedPresetKeys;

    public MidiRenderPlan(
        int sampleRate,
        long totalFrameCount,
        ReadOnlySpan<MidiPortRenderPlan> ports,
        ReadOnlySpan<long> sourceIds = default,
        ReadOnlySpan<int> initiallyDisabledSourceIndices = default,
        ReadOnlySpan<MidiUnitFragmentRenderPlan> unitFragments = default,
        ReadOnlySpan<MidiSegmentRenderPlan> segments = default,
        ReadOnlySpan<MidiRenderUnitDescriptor> unitDescriptors = default,
        IMidiRenderEventPageProvider? eventPageProvider = null,
        MidiRenderEventStreamDescriptor? eventStreamDescriptor = null,
        ReadOnlySpan<MidiRenderCacheSourceBinding> cacheSourceBindings = default,
        ReadOnlySpan<int> referencedPresetKeys = default)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (totalFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrameCount));
        }

        if (ports.Length > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(ports));
        }

        SampleRate = sampleRate;
        TotalFrameCount = totalFrameCount;
        _ports = ports.ToArray();
        _unitDescriptors = unitDescriptors.ToArray();
        ValidateUnitDescriptors(_unitDescriptors);
        _units = CreateUnitStreamSlots(_ports, _unitDescriptors);
        _unitFragments = unitFragments.ToArray();
        _segments = segments.ToArray();
        _sourceIds = sourceIds.ToArray();
        _initiallyDisabledSourceIndices = initiallyDisabledSourceIndices.ToArray();
        _cacheSourceBindings = cacheSourceBindings.ToArray();
        _referencedPresetKeys = referencedPresetKeys.IsEmpty
            ? CollectReferencedPresetKeys(_ports)
            : referencedPresetKeys.ToArray();
        EventPageProvider = eventPageProvider;
        EventStreamDescriptor = eventStreamDescriptor;
        ValidateSources(_sourceIds, _initiallyDisabledSourceIndices);
        ValidateCacheSourceBindings(_cacheSourceBindings, _sourceIds.Length);
        ValidateReferencedPresetKeys(_referencedPresetKeys);
        ValidatePorts(_ports, totalFrameCount, _sourceIds.Length);
        ValidateUnitFragments(_unitFragments, totalFrameCount, _sourceIds.Length);
        ValidateSegments(_segments, _unitFragments, totalFrameCount, _sourceIds.Length);
    }

    private MidiRenderPlan(MidiRenderPlan source, int[] initiallyDisabledSourceIndices)
    {
        SampleRate = source.SampleRate;
        TotalFrameCount = source.TotalFrameCount;
        _ports = source._ports;
        _units = source._units;
        _unitFragments = source._unitFragments;
        _segments = source._segments;
        _sourceIds = source._sourceIds;
        _initiallyDisabledSourceIndices = initiallyDisabledSourceIndices;
        _unitDescriptors = source._unitDescriptors;
        _cacheSourceBindings = source._cacheSourceBindings;
        _referencedPresetKeys = source._referencedPresetKeys;
        EventPageProvider = source.EventPageProvider;
        EventStreamDescriptor = source.EventStreamDescriptor;
        ValidateSources(_sourceIds, _initiallyDisabledSourceIndices);
    }

    public int SampleRate { get; }

    public long TotalFrameCount { get; }

    public ReadOnlySpan<MidiPortRenderPlan> Ports => _ports;

    public ReadOnlySpan<MidiUnitRenderPlan> Units => _units;

    public ReadOnlySpan<MidiUnitFragmentRenderPlan> UnitFragments => _unitFragments;

    public ReadOnlySpan<MidiSegmentRenderPlan> Segments => _segments;

    public ReadOnlySpan<long> SourceIds => _sourceIds;

    public ReadOnlySpan<int> InitiallyDisabledSourceIndices => _initiallyDisabledSourceIndices;

    public ReadOnlySpan<MidiRenderUnitDescriptor> UnitDescriptors => _unitDescriptors;

    public ReadOnlySpan<MidiRenderCacheSourceBinding> CacheSourceBindings => _cacheSourceBindings;

    public ReadOnlySpan<int> ReferencedPresetKeys => _referencedPresetKeys;

    public IMidiRenderEventPageProvider? EventPageProvider { get; }

    public MidiRenderEventStreamDescriptor? EventStreamDescriptor { get; }

    public int FindSourceIndex(long sourceId) => Array.IndexOf(_sourceIds, sourceId);

    /// <summary>
    /// Creates a cheap monitoring-state view over the same immutable render
    /// schedule. Initial Mute/Solo state must not force the canonical event plan
    /// to be rebuilt on every playback.
    /// </summary>
    public MidiRenderPlan WithInitiallyDisabledSourceIndices(
        ReadOnlySpan<int> initiallyDisabledSourceIndices)
    {
        int[] disabled = initiallyDisabledSourceIndices.ToArray();
        return disabled.AsSpan().SequenceEqual(_initiallyDisabledSourceIndices)
            ? this
            : new MidiRenderPlan(this, disabled);
    }

    public MidiRenderPlan WithEventStreamDescriptor(MidiRenderEventStreamDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new MidiRenderPlan(
            SampleRate,
            TotalFrameCount,
            _ports,
            _sourceIds,
            _initiallyDisabledSourceIndices,
            _unitFragments,
            _segments,
            _unitDescriptors,
            EventPageProvider,
            descriptor,
            _cacheSourceBindings,
            _referencedPresetKeys);
    }

    private static int[] CollectReferencedPresetKeys(ReadOnlySpan<MidiPortRenderPlan> ports)
    {
        bool[] referenced = new bool[128 * 128];
        referenced[0] = true;
        Span<byte> banks = stackalloc byte[16];
        Span<byte> programs = stackalloc byte[16];
        foreach (MidiPortRenderPlan port in ports)
        {
            banks.Clear();
            programs.Clear();
            foreach (ScheduledMidiMessage scheduled in port.Events)
            {
                // The ordinary MIDI message is only the channel-routing carrier for
                // privileged channel-mode SysEx. It must not mutate preset state.
                if (scheduled.IsChannelModeSystemExclusive)
                    continue;

                MidiMessage message = scheduled.Message;
                int channel = message.ChannelNumber;
                if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 0)
                    banks[channel] = message.Byte2;
                else if (message.MessageType == MidiMessageType.ProgramChange)
                    programs[channel] = message.Byte1;
                else if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                    referenced[(banks[channel] << 7) | programs[channel]] = true;
            }
        }
        return Enumerable.Range(0, referenced.Length).Where(index => referenced[index]).ToArray();
    }

    private static void ValidateCacheSourceBindings(
        ReadOnlySpan<MidiRenderCacheSourceBinding> bindings,
        int sourceCount)
    {
        HashSet<(int Source, int Owner)> seen = [];
        foreach (MidiRenderCacheSourceBinding value in bindings)
        {
            if ((uint)value.SourceIndex >= (uint)sourceCount
                || (uint)value.CacheOwnerSourceIndex >= (uint)sourceCount
                || !seen.Add((value.SourceIndex, value.CacheOwnerSourceIndex)))
            {
                throw new ArgumentException(
                    "Cache source bindings must be unique and reference the source table.",
                    nameof(bindings));
            }
        }
    }

    private static void ValidateReferencedPresetKeys(ReadOnlySpan<int> keys)
    {
        int previous = -1;
        foreach (int value in keys)
        {
            if (value is < 0 or >= 128 * 128 || value <= previous)
                throw new ArgumentException(
                    "Referenced MIDI preset keys must be unique, ordered, and in range.",
                    nameof(keys));
            previous = value;
        }
    }

    private static void ValidateSources(ReadOnlySpan<long> sourceIds, ReadOnlySpan<int> disabledIndices)
    {
        HashSet<long> seen = [];
        foreach (long sourceId in sourceIds)
        {
            if (sourceId <= 0 || !seen.Add(sourceId))
            {
                throw new ArgumentException("Render source IDs must be non-empty and unique.", nameof(sourceIds));
            }
        }

        HashSet<int> disabled = [];
        foreach (int sourceIndex in disabledIndices)
        {
            if ((uint)sourceIndex >= (uint)sourceIds.Length || !disabled.Add(sourceIndex))
            {
                throw new ArgumentException(
                    "Initially disabled source indices must be unique and reference the source table.",
                    nameof(disabledIndices));
            }
        }
    }

    private static void ValidatePorts(
        ReadOnlySpan<MidiPortRenderPlan> ports,
        long totalFrameCount,
        int sourceCount)
    {
        int previousPort = -1;
        for (int i = 0; i < ports.Length; i++)
        {
            MidiPortRenderPlan port = ports[i] ?? throw new ArgumentException("A render port cannot be null.", nameof(ports));
            if (port.ZeroBasedPortNumber <= previousPort)
            {
                throw new ArgumentException("Render ports must be unique and sorted by zero-based Port number.", nameof(ports));
            }

            ReadOnlySpan<ScheduledMidiMessage> events = port.Events;
            if (!events.IsEmpty && events[^1].SampleFrame > totalFrameCount)
            {
                throw new ArgumentException("A MIDI event is beyond the render range.", nameof(ports));
            }

            foreach (ScheduledMidiMessage scheduled in events)
            {
                if (scheduled.SourceIndex < -1 || scheduled.SourceIndex >= sourceCount)
                {
                    throw new ArgumentException("A MIDI event references an invalid render source.", nameof(ports));
                }
            }

            previousPort = port.ZeroBasedPortNumber;
        }
    }

    private static MidiUnitRenderPlan[] CreateUnitStreamSlots(
        ReadOnlySpan<MidiPortRenderPlan> ports,
        ReadOnlySpan<MidiRenderUnitDescriptor> descriptors)
    {
        Dictionary<int, MidiUnitRenderPlan> result = [];
        foreach (MidiPortRenderPlan port in ports)
        {
            List<ScheduledMidiMessage>?[] eventsByChannel = new List<ScheduledMidiMessage>?[16];
            foreach (ScheduledMidiMessage scheduled in port.Events)
            {
                byte channel = scheduled.Message.ChannelNumber;
                List<ScheduledMidiMessage> events = eventsByChannel[channel] ??= [];
                uint packed = scheduled.Message.PackedValue & ~MidiMessage.ChannelNumberMask;
                events.Add(scheduled with
                {
                    Message = MidiMessage.FromPackedValue(packed),
                    ChannelModeSystemExclusive = scheduled.ChannelModeSystemExclusive is { } systemExclusive
                        ? systemExclusive with { TargetChannel = 0 }
                        : null
                });
            }
            for (byte channel = 0; channel < eventsByChannel.Length; channel++)
            {
                if (eventsByChannel[channel] is { Count: > 0 } events)
                {
                    MidiUnitRenderPlan plan = new(port.ZeroBasedPortNumber, channel, events.ToArray());
                    result.Add(plan.CanonicalUnitNumber, plan);
                }
            }
        }
        foreach (MidiRenderUnitDescriptor descriptor in descriptors)
        {
            result.TryAdd(
                descriptor.CanonicalUnitNumber,
                new MidiUnitRenderPlan(
                    descriptor.ZeroBasedPortNumber,
                    descriptor.ZeroBasedChannelNumber,
                    []));
        }
        return result.Values.OrderBy(value => value.CanonicalUnitNumber).ToArray();
    }

    private static void ValidateUnitDescriptors(ReadOnlySpan<MidiRenderUnitDescriptor> descriptors)
    {
        Span<bool> seen = stackalloc bool[256];
        foreach (MidiRenderUnitDescriptor value in descriptors)
        {
            if (seen[value.CanonicalUnitNumber])
                throw new ArgumentException("MIDI render Unit descriptors must be unique.", nameof(descriptors));
            seen[value.CanonicalUnitNumber] = true;
        }
    }

    private static void ValidateUnitFragments(
        ReadOnlySpan<MidiUnitFragmentRenderPlan> fragments,
        long totalFrameCount,
        int sourceCount)
    {
        Dictionary<int, long> endByUnit = [];
        HashSet<(long InstanceGroupId, long SubVoiceId)> identities = [];
        foreach (MidiUnitFragmentRenderPlan fragment in fragments)
        {
            if (fragment.EndFrame > totalFrameCount
                || fragment.SourceIndex >= sourceCount
                || !identities.Add((fragment.InstanceGroupId, fragment.SubVoiceId)))
            {
                throw new ArgumentException(
                    "The Unit fragment set contains an invalid boundary, source, or duplicate identity.",
                    nameof(fragments));
            }
            if (endByUnit.TryGetValue(fragment.CanonicalUnitNumber, out long previousEnd)
                && fragment.StartFrame < previousEnd)
            {
                throw new ArgumentException(
                    "Unit fragments assigned to one canonical stream slot must not overlap.",
                    nameof(fragments));
            }
            endByUnit[fragment.CanonicalUnitNumber] = fragment.EndFrame;
        }
    }

    private static void ValidateSegments(
        ReadOnlySpan<MidiSegmentRenderPlan> segments,
        ReadOnlySpan<MidiUnitFragmentRenderPlan> fragments,
        long totalFrameCount,
        int sourceCount)
    {
        HashSet<(long TrackId, long SegmentId)> identities = [];
        Dictionary<int, long> endBySource = [];
        foreach (MidiSegmentRenderPlan segment in segments)
        {
            if (segment.EndFrame > totalFrameCount
                || segment.SourceIndex >= sourceCount
                || !identities.Add((segment.TrackId, segment.SegmentId)))
            {
                throw new ArgumentException(
                    "The Segment cache plan contains an invalid boundary, source, or duplicate identity.",
                    nameof(segments));
            }
            if (endBySource.TryGetValue(segment.SourceIndex, out long previousEnd)
                && segment.StartFrame < previousEnd)
            {
                throw new ArgumentException(
                    "Segments belonging to one render source must not overlap.",
                    nameof(segments));
            }
            endBySource[segment.SourceIndex] = segment.EndFrame;
        }

        if (segments.IsEmpty)
        {
            return;
        }

        foreach (MidiUnitFragmentRenderPlan fragment in fragments)
        {
            MidiSegmentRenderPlan? owner = null;
            foreach (MidiSegmentRenderPlan segment in segments)
            {
                if (segment.TrackId == fragment.TrackId
                    && segment.SegmentId == fragment.SegmentId)
                {
                    owner = segment;
                    break;
                }
            }
            if (owner is null
                || owner.SourceIndex != fragment.SourceIndex
                || fragment.StartFrame < owner.StartFrame
                || fragment.EndFrame > owner.EndFrame)
            {
                throw new ArgumentException(
                    "Every Unit fragment must be contained by its owning Segment cache plan.",
                    nameof(segments));
            }
        }
    }
}
