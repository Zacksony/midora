using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Security.Cryptography;
using System.Text;
using Midora.Common;

namespace Midora.Playback;

public readonly record struct CanonicalAudioUnitEvent(
    long RelativeTick,
    MidiMessage Message,
    CanonicalEventRole Role,
    long StableOrder,
    long SemanticTargetKey,
    long SemanticGroup,
    SourceReference Source,
    int SmfTrackOrder,
    long SmfEventOrder,
    MidoraId StableObjectId);

public sealed class CanonicalAudioUnitFragment
{
    private readonly CanonicalAudioUnitEvent[] _events;

    internal CanonicalAudioUnitFragment(
        MidoraId trackId,
        MidoraId segmentId,
        MidoraId eventInstrumentId,
        MidoraId instanceGroupId,
        MidoraId subVoiceId,
        MidoraId midiChannelRootId,
        MidiChannelMode channelMode,
        long groupStartTick,
        long groupEndTick,
        long effectiveStartTick,
        long effectiveEndTick,
        CanonicalAudioUnitEvent[] events,
        string? semanticFingerprint = null)
    {
        TrackId = trackId;
        SegmentId = segmentId;
        EventInstrumentId = eventInstrumentId;
        InstanceGroupId = instanceGroupId;
        SubVoiceId = subVoiceId;
        MidiChannelRootId = midiChannelRootId;
        ChannelMode = channelMode;
        GroupStartTick = groupStartTick;
        GroupEndTick = groupEndTick;
        EffectiveStartTick = effectiveStartTick;
        EffectiveEndTick = effectiveEndTick;
        _events = events;
        SemanticFingerprint = semanticFingerprint ?? ComputeSemanticFingerprint(this);
    }

    public MidoraId TrackId { get; }
    public MidoraId SegmentId { get; }
    public MidoraId EventInstrumentId { get; }
    public MidoraId InstanceGroupId { get; }
    public MidoraId SubVoiceId { get; }
    public MidoraId MidiChannelRootId { get; }
    public MidiChannelMode ChannelMode { get; }
    public long GroupStartTick { get; }
    public long GroupEndTick { get; }
    public long EffectiveStartTick { get; }
    public long EffectiveEndTick { get; }
    public string SemanticFingerprint { get; }
    public ReadOnlySpan<CanonicalAudioUnitEvent> Events => _events;

    internal void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 160)) return;
        collector.Array(_events);
        collector.Text(SemanticFingerprint);
    }

    private static string ComputeSemanticFingerprint(CanonicalAudioUnitFragment fragment)
    {
        using HashingWriteStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_CANONICAL_AUDIO_UNIT_V1");
            writer.Write(fragment.TrackId.Value);
            writer.Write(fragment.SegmentId.Value);
            writer.Write(fragment.EventInstrumentId.Value);
            writer.Write(fragment.InstanceGroupId.Value);
            writer.Write(fragment.SubVoiceId.Value);
            writer.Write(fragment.MidiChannelRootId.Value);
            writer.Write((int)fragment.ChannelMode);
            writer.Write(fragment.GroupEndTick - fragment.GroupStartTick);
            writer.Write(fragment.EffectiveStartTick - fragment.GroupStartTick);
            writer.Write(fragment.EffectiveEndTick - fragment.GroupStartTick);
            writer.Write(fragment._events.Length);
            foreach (CanonicalAudioUnitEvent value in fragment._events)
            {
                writer.Write(value.RelativeTick);
                writer.Write(value.Message.PackedValue);
                writer.Write((byte)value.Role);
                writer.Write(value.SemanticTargetKey);
                WriteSource(writer, value.Source, fragment.GroupStartTick);
            }
        }
        return payload.GetHash();
    }

    internal static void WriteSource(BinaryWriter writer, SourceReference source, long originTick)
    {
        writer.Write(source.TrackId.Value);
        writer.Write(source.SegmentId.Value);
        writer.Write(source.LogicalNoteId.Value);
        writer.Write(source.EventInstrumentId.Value);
        writer.Write(source.SubVoiceId.Value);
        writer.Write(source.SourceEventId.Value);
        writer.Write(source.Tick < 0 ? source.Tick : source.Tick - originTick);
        writer.Write(source.LogicalParameterId.Value);
        writer.Write(source.LogicalParameterMappingId.Value);
        writer.Write(source.MappingStepId.Value);
        writer.Write(source.MappingFunctionId.Value);
        writer.Write(source.ValueCurveId.Value);
        writer.Write(source.EnvelopeId.Value);
        writer.Write(source.MidiChannelRootId.Value);
        writer.Write(source.PureMidiTrackId.Value);
        writer.Write(source.MidiSegmentId.Value);
        writer.Write(source.DirectMidiObjectId.Value);
        writer.Write((byte)source.Origin);
    }
}

public sealed class CanonicalAudioUnitProjection : IRetainedStorageSource
{
    private readonly CanonicalAudioUnitFragment[] _fragments;

    private CanonicalAudioUnitProjection(CanonicalAudioUnitFragment[] fragments,
        MidoraId[]? sourceIds = null, int[]? referencedPresetKeys = null)
    {
        _fragments = fragments;
        SourceIds = sourceIds ?? [];
        ReferencedPresetKeys = referencedPresetKeys ?? [];
    }

    public ReadOnlySpan<CanonicalAudioUnitFragment> Fragments => _fragments;
    internal IReadOnlyList<MidoraId> SourceIds { get; }
    internal IReadOnlyList<int> ReferencedPresetKeys { get; }

    public static CanonicalAudioUnitProjection Create(
        CanonicalCompiledResult compiled,
        IEnumerable<CanonicalMidiEvent>? eventSubset = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        cancellationToken.ThrowIfCancellationRequested();
        return compiled.HasPagedLogicalEvents && eventSubset is null
            ? compiled.GetOrCreateConsumerMetadata(() => CreateCore(compiled, null, cancellationToken))
            : CreateCore(compiled, eventSubset, cancellationToken);
    }

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 48)) return;
        if (collector.Array(_fragments))
            foreach (CanonicalAudioUnitFragment fragment in _fragments)
                fragment.CollectRetainedStorage(collector);
        collector.Add(SourceIds, 24 + SourceIds.Count * 8L);
        collector.Add(ReferencedPresetKeys, 24 + ReferencedPresetKeys.Count * 4L);
    }

    private static CanonicalAudioUnitProjection CreateCore(
        CanonicalCompiledResult compiled,
        IEnumerable<CanonicalMidiEvent>? eventSubset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException(
                "Only a consumable canonical result can produce an audio Unit projection.",
                nameof(compiled));
        }

        Dictionary<(MidoraId GroupId, MidoraId SubVoiceId), FragmentBuilder> builders = [];
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), FragmentBuilder> byInstance = [];
        foreach (ChannelUnitAllocation allocation in compiled.Allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (MidoraId, MidoraId) key = (allocation.InstanceGroupId, allocation.SubVoiceId);
            if (!builders.TryGetValue(key, out FragmentBuilder? builder))
            {
                builder = new FragmentBuilder(allocation, compiled.StartTick, compiled.EndTick);
                builders.Add(key, builder);
            }
            else
            {
                builder.ValidateCompatible(allocation);
            }
            byInstance.Add((allocation.InstanceId, allocation.SubVoiceId), builder);
        }

        bool streaming = compiled.HasPagedLogicalEvents && eventSubset is null;
        HashSet<MidoraId> sourceIds = [];
        bool[] referencedPresets = new bool[128 * 128];
        byte[] banks = new byte[256], programs = new byte[256];
        try
        {
            if (streaming)
            {
                // The original hash begins with each fragment's event count. Count first,
                // then hash the same immutable cursor without retaining a second event graph.
                foreach (CanonicalMidiEvent value in compiled.EnumerateResidentAndLogicalEvents(cancellationToken))
                {
                    FragmentBuilder builder = ResolveRequiredBuilder(value);
                    builder.EventCount = checked(builder.EventCount + 1);
                    if (value.Source.TrackId != default) sourceIds.Add(value.Source.TrackId);
                    int unit = value.ZeroBasedPort * 16 + value.ZeroBasedChannel;
                    if (value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 0)
                        banks[unit] = value.Message.Byte2;
                    else if (value.Message.MessageType == MidiMessageType.ProgramChange)
                        programs[unit] = value.Message.Byte1;
                    else if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0)
                        referencedPresets[(banks[unit] << 7) | programs[unit]] = true;
                }
                int hashBufferSize = Math.Min(32768, 8 * 1024 * 1024 / Math.Max(1, builders.Count));
                if (hashBufferSize < 256) hashBufferSize = 0;
                foreach (FragmentBuilder builder in builders.Values) builder.BeginHash(hashBufferSize);
            }
            foreach (CanonicalMidiEvent value in eventSubset ?? compiled.EnumerateResidentAndLogicalEvents(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendEvent(value);
            }

            CanonicalAudioUnitFragment[] fragments = builders.Values
                .OrderBy(value => value.EffectiveStartTick)
                .ThenBy(value => value.TrackId)
                .ThenBy(value => value.SegmentId)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId)
                .Select(value => value.Build())
                .ToArray();
            return new CanonicalAudioUnitProjection(fragments, sourceIds.Order().ToArray(),
                Enumerable.Range(0, referencedPresets.Length).Where(index => referencedPresets[index]).ToArray());
        }
        finally
        {
            foreach (FragmentBuilder builder in builders.Values) builder.DisposeHash();
        }

        FragmentBuilder ResolveRequiredBuilder(CanonicalMidiEvent value) =>
            ResolveBuilder(value, byInstance, builders.Values)
            ?? throw new InvalidDataException(
                "A canonical MIDI event could not be assigned to a Segment/Unit audio fragment.");

        void AppendEvent(CanonicalMidiEvent value)
        {
            FragmentBuilder builder = ResolveRequiredBuilder(value);
            uint unitPackedMessage = value.Message.PackedValue & ~MidiMessage.ChannelNumberMask;
            CanonicalAudioUnitEvent converted = new(
                value.Tick - builder.GroupStartTick,
                MidiMessage.FromPackedValue(unitPackedMessage),
                value.Role,
                value.StableOrder,
                value.SemanticTargetKey,
                value.SemanticGroup,
                value.Source,
                value.SmfTrackOrder,
                value.SmfEventOrder,
                value.Source.DirectMidiObjectId);
            if (streaming)
            {
                builder.AppendHash(converted);
            }
            else builder.Events.Add(converted);
        }
    }

    private static FragmentBuilder? ResolveBuilder(
        CanonicalMidiEvent value,
        IReadOnlyDictionary<(MidoraId InstanceId, MidoraId SubVoiceId), FragmentBuilder> byInstance,
        IEnumerable<FragmentBuilder> builders)
    {
        if (value.Source.LogicalNoteId != default
            && value.Source.SubVoiceId != default
            && byInstance.TryGetValue(
                (value.Source.LogicalNoteId, value.Source.SubVoiceId),
                out FragmentBuilder? exact))
        {
            return exact;
        }

        // Keep the per-event dictionary fast path free of a display class. The
        // exceptional path needs neither a captured predicate nor a candidate array.
        bool root = value.Source.MidiChannelRootId != default;
        FragmentBuilder? single = null, boundary = null;
        int count = 0, boundaryCount = 0;
        foreach (FragmentBuilder candidate in builders)
        {
            if (candidate.ZeroBasedPort != value.ZeroBasedPort
                || candidate.ZeroBasedChannel != value.ZeroBasedChannel
                || value.Tick < candidate.EffectiveStartTick || value.Tick > candidate.EffectiveEndTick)
                continue;
            if (root ? candidate.MidiChannelRootId != value.Source.MidiChannelRootId
                : (value.Source.SegmentId != default && value.Source.SegmentId != candidate.SegmentId)
                    || (value.Source.SubVoiceId != default && value.Source.SubVoiceId != candidate.SubVoiceId))
                continue;
            single = candidate;
            count++;
            if (root || (value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup
                    ? candidate.EffectiveEndTick : candidate.EffectiveStartTick) == value.Tick)
            {
                boundary = candidate;
                boundaryCount++;
            }
        }
        if (count == 1) return single;
        if (boundaryCount > 1) throw new InvalidOperationException("Sequence contains more than one matching element");
        return boundary;
    }

    private sealed class FragmentBuilder
    {
        public FragmentBuilder(
            ChannelUnitAllocation allocation,
            long compilationStartTick,
            long compilationEndTick)
        {
            TrackId = allocation.TrackId;
            SegmentId = allocation.SegmentId;
            EventInstrumentId = allocation.EventInstrumentId;
            InstanceGroupId = allocation.InstanceGroupId;
            SubVoiceId = allocation.SubVoiceId;
            MidiChannelRootId = allocation.MidiChannelRootId;
            EventInstrumentUsageId = allocation.EventInstrumentUsageId;
            ChannelMode = allocation.ChannelMode;
            GroupStartTick = allocation.StartTick;
            GroupEndTick = allocation.EndTick;
            EffectiveStartTick = Math.Max(allocation.StartTick, compilationStartTick);
            EffectiveEndTick = Math.Min(allocation.EndTick, compilationEndTick);
            ZeroBasedPort = allocation.ZeroBasedPort;
            ZeroBasedChannel = allocation.ZeroBasedChannel;
        }

        public MidoraId TrackId { get; }
        public MidoraId SegmentId { get; }
        public MidoraId EventInstrumentId { get; }
        public MidoraId InstanceGroupId { get; }
        public MidoraId SubVoiceId { get; }
        public MidoraId MidiChannelRootId { get; }
        public MidoraId EventInstrumentUsageId { get; }
        public MidiChannelMode ChannelMode { get; }
        public long GroupStartTick { get; }
        public long GroupEndTick { get; }
        public long EffectiveStartTick { get; }
        public long EffectiveEndTick { get; }
        public byte ZeroBasedPort { get; }
        public byte ZeroBasedChannel { get; }
        public List<CanonicalAudioUnitEvent> Events { get; } = [];
        public long EventCount { get; set; }
        private HashingWriteStream? _hash;
        private BinaryWriter? _writer;

        public void BeginHash(int bufferSize)
        {
            _hash = new(bufferSize);
            _writer = new(_hash, Encoding.UTF8, leaveOpen: true);
            // Preserve the existing cache key bytes for every representable V1 fragment.
            // A larger count uses an explicit new domain separator and an Int64 count.
            _writer.Write(EventCount <= int.MaxValue ? "MIDORA_CANONICAL_AUDIO_UNIT_V1" : "MIDORA_CANONICAL_AUDIO_UNIT_V2");
            _writer.Write(TrackId.Value); _writer.Write(SegmentId.Value);
            _writer.Write(EventInstrumentId.Value); _writer.Write(InstanceGroupId.Value);
            _writer.Write(SubVoiceId.Value); _writer.Write(MidiChannelRootId.Value);
            _writer.Write((int)ChannelMode);
            _writer.Write(GroupEndTick - GroupStartTick);
            _writer.Write(EffectiveStartTick - GroupStartTick);
            _writer.Write(EffectiveEndTick - GroupStartTick);
            if (EventCount <= int.MaxValue) _writer.Write((int)EventCount); else _writer.Write(EventCount);
        }

        public void AppendHash(CanonicalAudioUnitEvent value)
        {
            BinaryWriter writer = _writer!;
            writer.Write(value.RelativeTick); writer.Write(value.Message.PackedValue);
            writer.Write((byte)value.Role); writer.Write(value.SemanticTargetKey);
            CanonicalAudioUnitFragment.WriteSource(writer, value.Source, GroupStartTick);
        }

        public void DisposeHash() { _writer?.Dispose(); _hash?.Dispose(); }

        public void ValidateCompatible(ChannelUnitAllocation value)
        {
            bool sharedLogicalUsage = EventInstrumentUsageId != default;
            if ((!sharedLogicalUsage
                    && (TrackId != value.TrackId || SegmentId != value.SegmentId))
                || EventInstrumentId != value.EventInstrumentId
                || InstanceGroupId != value.InstanceGroupId
                || SubVoiceId != value.SubVoiceId
                || MidiChannelRootId != value.MidiChannelRootId
                || EventInstrumentUsageId != value.EventInstrumentUsageId
                || ChannelMode != value.ChannelMode
                || GroupStartTick != value.StartTick
                || GroupEndTick != value.EndTick
                || ZeroBasedPort != value.ZeroBasedPort
                || ZeroBasedChannel != value.ZeroBasedChannel)
            {
                throw new InvalidDataException(
                    "Canonical allocation records disagree about one abstract Segment/Unit fragment.");
            }
        }

        public CanonicalAudioUnitFragment Build() => new(
            TrackId,
            SegmentId,
            EventInstrumentId,
            InstanceGroupId,
            SubVoiceId,
            MidiChannelRootId,
            ChannelMode,
            GroupStartTick,
            GroupEndTick,
            EffectiveStartTick,
            EffectiveEndTick,
            Events.ToArray(),
            _hash?.GetHash());
    }
}

/// <summary>Writes hash input incrementally; never retains the serialized event payload.</summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer;
    private int _count;
    public HashingWriteStream(int bufferSize = 32768) => _buffer = bufferSize == 0 ? [] : new byte[bufferSize];
    public string GetHash() { Flush(); return Convert.ToHexStringLower(_hash.GetHashAndReset()); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { if (_count != 0) { _hash.AppendData(_buffer.AsSpan(0, _count)); _count = 0; } }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length >= _buffer.Length) { Flush(); _hash.AppendData(buffer); return; }
        if (buffer.Length > _buffer.Length - _count) Flush();
        buffer.CopyTo(_buffer.AsSpan(_count));
        _count += buffer.Length;
    }
    public override void WriteByte(byte value)
    {
        if (_buffer.Length != 0)
        {
            if (_count == _buffer.Length) Flush();
            _buffer[_count++] = value;
        }
        else { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; _hash.AppendData(bytes); }
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
}
