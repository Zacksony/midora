using System.Numerics;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport;

public static class CanonicalMidiFileExporter
{
    private const byte ReverbSendController = 91;
    private const byte ChorusSendController = 93;
    private static ReadOnlySpan<byte> RolandGsChannel10NormalPart =>
        [0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1b, 0xf7];
    private static ReadOnlySpan<byte> YamahaXgChannel10NormalPart =>
        [0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7];

    public static MidiExportEncodingResult EncodeWholeProject(WholeProjectMidiEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTracks);

        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            request.LogicalTracks,
            includedTrackId: null,
            includedPort: null,
            static port => port,
            requireIncludedEvent: false,
            includePureMidiTracks: true, cancellationToken);
    }

    public static MidiExportEncodingResult EncodeLogicalTrack(LogicalTrackMidiEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTrack);

        MidoraId trackId = request.LogicalTrack.TrackId;
        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            [request.LogicalTrack],
            trackId,
            includedPort: null,
            static port => port,
            requireIncludedEvent: false,
            includePureMidiTracks: false, cancellationToken);
    }

    public static MidiExportEncodingResult EncodePort(PortMidiEncodingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTracks);
        if (request.ZeroBasedOriginalPort > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ZeroBasedOriginalPort));
        }

        byte selectedPort = request.ZeroBasedOriginalPort;
        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            request.LogicalTracks,
            includedTrackId: null,
            includedPort: selectedPort,
            static _ => 0,
            requireIncludedEvent: true,
            includePureMidiTracks: true, cancellationToken);
    }

    private static MidiExportEncodingResult EncodeCore(
        CanonicalCompiledResult compiledResult,
        string conductorTrackName,
        IReadOnlyList<MidiExportLogicalTrackLayout> logicalTracks,
        MidoraId? includedTrackId,
        byte? includedPort,
        Func<byte, byte> mapOutputPort,
        bool requireIncludedEvent,
        bool includePureMidiTracks,
        CancellationToken cancellationToken)
    {
        List<MidiExportDiagnostic> diagnostics = [];
        try
        {
            ValidateCompiledResult(compiledResult, diagnostics);
            StandardMidiFile.ValidateEncodingHeader(compiledResult.TicksPerQuarterNote, 1);
            Dictionary<MidoraId, MidiExportLogicalTrackLayout> layouts =
                BuildLayoutIndex(logicalTracks, diagnostics);
            Dictionary<MidiExportChannelUnit, string> unitTrackNames =
                BuildUnitTrackNameIndex(logicalTracks, diagnostics);
            if (diagnostics.Count != 0) return Failure(diagnostics);

            long duration = checked(compiledResult.EndTick - compiledResult.StartTick);
            StandardMidiFileTrack conductor = BuildConductorTrack(
                compiledResult,
                conductorTrackName,
                duration);
            List<StandardMidiFileTrackSource> tracks =
            [
                new(conductor.EndTick, conductor.Events, conductorTrackName)
            ];
            List<SourceReference> sources = [default];

            CanonicalSmfTrackDescriptor[] pureDescriptors = compiledResult.SmfTracks.ToArray()
                .Where(value => value.Kind == CanonicalSmfTrackKind.PureMidiTrack
                    && includePureMidiTracks
                    && (!includedPort.HasValue || value.ZeroBasedPort == includedPort.Value))
                .OrderBy(value => value.SourceTrackOrder)
                .ToArray();
            HashSet<MidoraId> pureTrackIds = pureDescriptors
                .Select(value => value.ExportTrackId)
                .ToHashSet();

            HashSet<MidiExportChannelUnit> usedUnits = [];
            Dictionary<MidiExportChannelUnit, SourceReference> unitSources = [];
            Dictionary<MidiExportChannelUnit, (long Tick, int Phase)> bankState = [];
            foreach (CanonicalMidiEvent value in compiledResult.EnumerateResidentAndLogicalEvents(cancellationToken))
            {
                bool isPure = value.Source.PureMidiTrackId != default
                    || value.Source.MidiChannelRootId != default
                    || value.ExportTrackId != default && pureTrackIds.Contains(value.ExportTrackId);
                ValidateCanonicalEvent(compiledResult, value, diagnostics, isPure);
                if (isPure) continue;
                MidoraId ownerTrackId = ResolveTrackId(compiledResult, value, diagnostics);
                if (ownerTrackId == default
                    || includedTrackId.HasValue && ownerTrackId != includedTrackId.Value
                    || includedPort.HasValue && value.ZeroBasedPort != includedPort.Value)
                {
                    continue;
                }
                if (!layouts.TryGetValue(ownerTrackId, out MidiExportLogicalTrackLayout? ownerLayout))
                {
                    diagnostics.Add(new(
                        "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                        MidiExportDiagnosticCategory.CanonicalConsistency,
                        $"Canonical event references Track {ownerTrackId}, but the export layout does not contain it.",
                        value.Source));
                    continue;
                }
                MidiExportChannelUnit unit = new(value.ZeroBasedPort, value.ZeroBasedChannel);
                if (!ownerLayout.EventTrackNamesByUnit.ContainsKey(unit)
                    || !unitTrackNames.ContainsKey(unit))
                {
                    diagnostics.Add(new(
                        "MIDORA-MIDI-EXPORT-UNIT-LAYOUT",
                        MidiExportDiagnosticCategory.Encoding,
                        $"The frozen layout does not contain Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1}.",
                        value.Source));
                    continue;
                }
                usedUnits.Add(unit);
                unitSources.TryAdd(unit, new SourceReference(TrackId: ownerTrackId));
                ValidateBankProgramEvent(value, unit, bankState, diagnostics);
            }
            if (diagnostics.Count != 0) return Failure(diagnostics);
            if (requireIncludedEvent && usedUnits.Count == 0 && pureDescriptors.Length == 0)
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-UNUSED-PORT",
                    MidiExportDiagnosticCategory.Encoding,
                    "Per-Port export does not create a file for a Port with no canonical events."));
                return Failure(diagnostics);
            }

            StandardMidiFile.ValidateEncodingHeader(compiledResult.TicksPerQuarterNote,
                checked(1 + pureDescriptors.Length + usedUnits.Count));
            foreach (CanonicalSmfTrackDescriptor descriptor in pureDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracks.Add(BuildPureMidiTrackSource(
                    compiledResult,
                    descriptor,
                    mapOutputPort(descriptor.ZeroBasedPort), cancellationToken));
                sources.Add(new(PureMidiTrackId: descriptor.SourceTrackId,
                    MidiChannelRootId: descriptor.MidiChannelRootId, ExportTrackId: descriptor.ExportTrackId));
            }
            foreach (MidiExportChannelUnit unit in usedUnits
                .OrderBy(value => value.ZeroBasedPort)
                .ThenBy(value => value.ZeroBasedChannel))
            {
                tracks.Add(new StandardMidiFileTrackSource(
                    duration,
                    EnumerateLogicalTrackEvents(compiledResult, unit, mapOutputPort(unit.ZeroBasedPort),
                        unitTrackNames[unit], includedTrackId, cancellationToken), unitTrackNames[unit]));
                sources.Add(unitSources[unit]);
            }
            if (diagnostics.Count != 0) return Failure(diagnostics);

            return new MidiExportEncodingResult(
                (output, token, progress) => StandardMidiFile.WriteType1(
                    output,
                    compiledResult.TicksPerQuarterNote,
                    tracks, token, progress),
                [], sources, compiledResult.StartTick);
        }
        catch (Exception exception) when (exception is MidoraMidiException
            or ArgumentException
            or OverflowException
            or System.Text.EncoderFallbackException)
        {
            diagnostics.Add(MidiExportEncodingErrors.FromException(exception));
            return Failure(diagnostics);
        }
    }

    private static StandardMidiFileTrackSource BuildPureMidiTrackSource(
        CanonicalCompiledResult compiled,
        CanonicalSmfTrackDescriptor descriptor,
        byte outputPort, CancellationToken cancellationToken = default) => new(
            descriptor.EndTick,
            EnumeratePureMidiTrackEvents(compiled, descriptor, outputPort, cancellationToken), descriptor.Name);

    private static IEnumerable<StandardMidiFileEvent> EnumeratePureMidiTrackEvents(
        CanonicalCompiledResult compiled,
        CanonicalSmfTrackDescriptor descriptor,
        byte outputPort, CancellationToken cancellationToken = default)
    {
        yield return StandardMidiFileEvent.Text(
            0,
            StandardMidiFile.TrackNameMetaType,
            descriptor.Name);
        yield return StandardMidiFileEvent.Meta(
            0,
            StandardMidiFile.MidiPortMetaType,
            [outputPort]);
        yield return StandardMidiFileEvent.Meta(
            0,
            0x7f,
            BuildMidoraTrackMetadata(descriptor, outputPort));
        if (descriptor.ZeroBasedChannel == 9
            && descriptor.ChannelMode == MidiChannelMode.Melodic)
        {
            yield return StandardMidiFileEvent.SystemExclusive(0, RolandGsChannel10NormalPart);
            yield return StandardMidiFileEvent.SystemExclusive(0, YamahaXgChannel10NormalPart);
        }
        yield return EffectsOffEvent(descriptor.ZeroBasedChannel, ReverbSendController);
        yield return EffectsOffEvent(descriptor.ZeroBasedChannel, ChorusSendController);

        IEnumerable<CanonicalSmfTrackChannelEvent> channelSource = compiled
            .QuerySmfTrackChannelEventPages(descriptor.ExportTrackId, cancellationToken)
            .SelectMany(value => value.Items);
        IEnumerable<CanonicalOpaqueMidiEvent> opaqueSource = compiled
            .QuerySmfTrackOpaqueEventPages(descriptor.ExportTrackId, cancellationToken)
            .SelectMany(value => value.Items);
        using IEnumerator<CanonicalSmfTrackChannelEvent> channel = channelSource.GetEnumerator();
        using IEnumerator<CanonicalOpaqueMidiEvent> opaque = opaqueSource.GetEnumerator();
        bool hasChannel = channel.MoveNext();
        bool hasOpaque = opaque.MoveNext();
        while (hasChannel || hasOpaque)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool takeChannel = !hasOpaque || hasChannel && CompareSmf(channel.Current, opaque.Current) <= 0;
            if (takeChannel)
            {
                CanonicalSmfTrackChannelEvent value = channel.Current;
                ValidatePagedPureMidiEvent(compiled, descriptor, value);
                long tick = checked(value.Tick - compiled.StartTick);
                yield return StandardMidiFileEvent.ChannelVoice(tick, value.Message);
                hasChannel = channel.MoveNext();
            }
            else
            {
                CanonicalOpaqueMidiEvent value = opaque.Current;
                long tick = checked(value.Tick - compiled.StartTick);
                if (tick < 0 || tick > descriptor.EndTick)
                {
                    throw new MidoraMidiException(
                        $"Pure MIDI Track {descriptor.ExportTrackId} contains an opaque event outside its frozen Track range.");
                }
                yield return value.Kind switch
                {
                    OpaqueMidiEventKind.Meta => StandardMidiFileEvent.Meta(
                        tick, value.MetaType, value.Payload.Span),
                    OpaqueMidiEventKind.SystemExclusive => StandardMidiFileEvent.SystemExclusive(
                        tick, value.Payload.Span),
                    OpaqueMidiEventKind.SystemExclusiveContinuation => StandardMidiFileEvent.SystemExclusive(
                        tick, value.Payload.Span, continuation: true),
                    _ => throw new MidoraMidiException(
                        $"Unknown opaque MIDI event kind {value.Kind}.")
                };
                hasOpaque = opaque.MoveNext();
            }
        }
    }

    private static int CompareSmf(
        CanonicalSmfTrackChannelEvent channel,
        CanonicalOpaqueMidiEvent opaque)
    {
        int value = channel.Tick.CompareTo(opaque.Tick);
        if (value != 0) return value;
        value = channel.EventOrder.CompareTo(opaque.StableOrder);
        if (value != 0) return value;
        return SmfKindOrder(channel.Role).CompareTo(1);
    }

    private static int SmfKindOrder(CanonicalEventRole role) => role switch
    {
        CanonicalEventRole.Reset => 0,
        CanonicalEventRole.RootBoundaryCleanup => 2,
        _ => 1
    };

    private static void ValidatePagedPureMidiEvent(
        CanonicalCompiledResult compiled,
        CanonicalSmfTrackDescriptor descriptor,
        CanonicalSmfTrackChannelEvent value)
    {
        long tick = checked(value.Tick - compiled.StartTick);
        if (value.ExportTrackId != descriptor.ExportTrackId
            || value.ZeroBasedPort != descriptor.ZeroBasedPort
            || value.ZeroBasedChannel != descriptor.ZeroBasedChannel
            || tick < 0
            || tick > descriptor.EndTick
            || !value.Message.IsChannelVoiceMessage
            || value.Message.ChannelNumber != descriptor.ZeroBasedChannel)
        {
            throw new MidoraMidiException(
                $"Pure MIDI Track {descriptor.ExportTrackId} contains an inconsistent canonical channel event.");
        }
    }

    internal static int ConvertTempoToMicrosecondsPerQuarterNote(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            throw new MidoraMidiException("Tempo BPM must be greater than zero.");
        }
        decimal exact = 60_000_000m / beatsPerMinute;
        return RoundTempoMicrosecondsPerQuarterNote(exact);
    }

    internal static int RoundTempoMicrosecondsPerQuarterNote(decimal exact)
    {
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        if (rounded is < 1m or > 16_777_215m)
        {
            throw new MidoraMidiException(
                $"Set Tempo value {exact} cannot be represented by the 24-bit SMF field.");
        }
        return decimal.ToInt32(rounded);
    }

    private static void ValidateCompiledResult(
        CanonicalCompiledResult compiled,
        List<MidiExportDiagnostic> diagnostics)
    {
        if (compiled.Purpose != CompilationPurpose.MidiExport)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-CONTEXT",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "MIDI export only accepts a dedicated MidiExport CompileContext result."));
        }
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-NOT-CONSUMABLE",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "MIDI export cannot consume a failed, partial, or otherwise non-consumable canonical result."));
        }
        if (compiled.StartTick < 0 || compiled.EndTick < compiled.StartTick)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-RANGE",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "The canonical export range is invalid."));
        }
    }

    private static Dictionary<MidoraId, MidiExportLogicalTrackLayout> BuildLayoutIndex(
        IReadOnlyList<MidiExportLogicalTrackLayout> source,
        List<MidiExportDiagnostic> diagnostics)
    {
        Dictionary<MidoraId, MidiExportLogicalTrackLayout> result = [];
        foreach (MidiExportLogicalTrackLayout? value in source)
        {
            if (value is null)
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                    MidiExportDiagnosticCategory.Encoding,
                    "The MIDI export Track layout cannot contain null."));
                continue;
            }
            if (!result.TryAdd(value.TrackId, value))
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                    MidiExportDiagnosticCategory.Encoding,
                    $"The MIDI export Track layout contains duplicate Track ID {value.TrackId}."));
            }
        }
        return result;
    }

    private static Dictionary<MidiExportChannelUnit, string> BuildUnitTrackNameIndex(
        IReadOnlyList<MidiExportLogicalTrackLayout> source,
        List<MidiExportDiagnostic> diagnostics)
    {
        Dictionary<MidiExportChannelUnit, string> result = [];
        foreach (MidiExportLogicalTrackLayout? layout in source)
        {
            if (layout is null)
            {
                continue;
            }
            foreach ((MidiExportChannelUnit unit, string trackName) in layout.EventTrackNamesByUnit)
            {
                if (result.TryGetValue(unit, out string? existingTrackName))
                {
                    if (!string.Equals(existingTrackName, trackName, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new(
                            "MIDORA-MIDI-EXPORT-UNIT-LAYOUT",
                            MidiExportDiagnosticCategory.Encoding,
                            $"Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1} has conflicting MIDI Track Names in the frozen layout."));
                    }
                    continue;
                }
                result.Add(unit, trackName);
            }
        }
        return result;
    }

    private static StandardMidiFileTrack BuildConductorTrack(
        CanonicalCompiledResult compiled,
        string trackName,
        long duration)
    {
        List<(long Tick, int KindOrder, MidoraId StableId, StandardMidiFileEvent Event)> timed = [];
        foreach (CanonicalTempo value in compiled.Conductor.Tempos)
        {
            int microseconds = ConvertTempoToMicrosecondsPerQuarterNote(value.BeatsPerMinute);
            timed.Add((
                RelativeTick(compiled, value.Tick),
                0,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.SetTempoMetaType,
                    [
                        (byte)((microseconds >> 16) & 0xff),
                        (byte)((microseconds >> 8) & 0xff),
                        (byte)(microseconds & 0xff)
                    ])));
        }
        foreach (CanonicalTimeSignature value in compiled.Conductor.TimeSignatures)
        {
            if (value.Numerator is < 1 or > 99
                || value.Denominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
            {
                throw new MidoraMidiException(
                    $"Time Signature {value.Numerator}/{value.Denominator} cannot be encoded in SMF.");
            }
            int exponent = BitOperations.TrailingZeroCount(checked((uint)value.Denominator));
            timed.Add((
                RelativeTick(compiled, value.Tick),
                1,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.TimeSignatureMetaType,
                    [checked((byte)value.Numerator), checked((byte)exponent), 24, 8])));
        }
        foreach (CanonicalKeySignature value in compiled.Conductor.KeySignatures)
        {
            if (value.SharpsFlats is < -7 or > 7)
            {
                throw new MidoraMidiException(
                    $"Key Signature sf value {value.SharpsFlats} is outside the SMF range -7..7.");
            }
            timed.Add((
                RelativeTick(compiled, value.Tick),
                2,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.KeySignatureMetaType,
                    [unchecked((byte)(sbyte)value.SharpsFlats), value.IsMinor ? (byte)1 : (byte)0])));
        }
        foreach (CanonicalMarker value in compiled.Conductor.Markers)
        {
            timed.Add((
                RelativeTick(compiled, value.Tick),
                3,
                value.Id,
                StandardMidiFileEvent.Text(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.MarkerMetaType,
                    value.Name)));
        }

        List<StandardMidiFileEvent> events =
            [StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, trackName)];
        events.AddRange(timed
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.KindOrder)
            .ThenBy(value => value.StableId)
            .Select(value => value.Event));
        return new(duration, events);
    }

    private static IEnumerable<StandardMidiFileEvent> EnumerateLogicalTrackEvents(
        CanonicalCompiledResult compiled, MidiExportChannelUnit unit, byte port, string trackName,
        MidoraId? includedTrackId, CancellationToken cancellationToken)
    {
        yield return StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, trackName);
        yield return StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [port]);
        if (unit.ZeroBasedChannel == 9)
        {
            yield return StandardMidiFileEvent.SystemExclusive(0, RolandGsChannel10NormalPart);
            yield return StandardMidiFileEvent.SystemExclusive(0, YamahaXgChannel10NormalPart);
        }
        yield return EffectsOffEvent(unit.ZeroBasedChannel, ReverbSendController);
        yield return EffectsOffEvent(unit.ZeroBasedChannel, ChorusSendController);
        foreach (CanonicalMidiEvent value in compiled.EnumerateResidentAndLogicalUnitEvents(
            unit.ZeroBasedPort, unit.ZeroBasedChannel, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Source.PureMidiTrackId != default || value.Source.MidiChannelRootId != default) continue;
            if (includedTrackId.HasValue && value.Source.TrackId != includedTrackId.Value)
            {
                if (value.Source.TrackId != default) continue;
                List<MidiExportDiagnostic> diagnostics = [];
                if (ResolveTrackId(compiled, value, diagnostics) != includedTrackId.Value) continue;
            }
            yield return StandardMidiFileEvent.ChannelVoice(checked(value.Tick - compiled.StartTick), value.Message);
        }
    }

    private static void ValidateBankProgramEvent(CanonicalMidiEvent value, MidiExportChannelUnit unit,
        Dictionary<MidiExportChannelUnit, (long Tick, int Phase)> state, List<MidiExportDiagnostic> diagnostics)
    {
        int phase = state.TryGetValue(unit, out var previous) && previous.Tick == value.Tick ? previous.Phase : 0;
        MidiMessage message = value.Message;
        bool invalid = false;
        if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 0)
            invalid = phase != 0;
        else if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 32)
        { invalid = phase > 1; phase = 1; }
        else if (message.MessageType == MidiMessageType.ProgramChange) phase = 2;
        state[unit] = (value.Tick, phase);
        if (invalid) diagnostics.Add(new("MIDORA-MIDI-EXPORT-BANK-ORDER",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            "Bank/Program order must be CC0, then CC32, then Program Change at the same tick.", value.Source));
    }

    private static StandardMidiFileEvent EffectsOffEvent(
        byte zeroBasedChannel,
        byte controller) =>
        StandardMidiFileEvent.ChannelVoice(
            0,
            MidiMessage.ControlChange(zeroBasedChannel, controller, 0));

    private static byte[] BuildMidoraTrackMetadata(
        CanonicalSmfTrackDescriptor descriptor,
        byte outputPort)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(descriptor.MidiChannelRootName);
        if (name.Length > ushort.MaxValue)
        {
            throw new MidoraMidiException("MIDI Channel Root name is too long for Midora private metadata.");
        }
        using MemoryStream output = new();
        using BinaryWriter writer = new(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("MIDORA"u8);
        writer.Write((byte)2);
        writer.Write(descriptor.MidiChannelRootId.Value);
        writer.Write(descriptor.SourceTrackId.Value);
        writer.Write(descriptor.MidiChannelRootOrder);
        writer.Write(descriptor.SourceTrackOrder);
        writer.Write((byte)descriptor.RoutingMode);
        writer.Write((byte)descriptor.ChannelMode);
        writer.Write(outputPort);
        writer.Write(descriptor.ZeroBasedChannel);
        writer.Write(checked((ushort)name.Length));
        writer.Write(name);
        return output.ToArray();
    }

    private static void ValidateCanonicalEvent(
        CanonicalCompiledResult compiled,
        CanonicalMidiEvent value,
        List<MidiExportDiagnostic> diagnostics,
        bool isPureMidi)
    {
        MidiMessage message = value.Message;
        if (value.Tick < compiled.StartTick || value.Tick > compiled.EndTick)
        {
            Add("Canonical MIDI event lies outside the export range.");
        }
        if (value.ZeroBasedPort > 15 || value.ZeroBasedChannel > 15
            || !message.IsChannelVoiceMessage
            || message.ChannelNumber != value.ZeroBasedChannel)
        {
            Add("Canonical MIDI routing or status/channel data is inconsistent.");
            return;
        }
        if (!isPureMidi && message.MessageType == MidiMessageType.NoteOff && message.Byte2 != 0)
        {
            Add("Canonical Note Off must use velocity 0 for MIDI export.");
        }
        if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
        {
            Add("Canonical Note On velocity 0 is not an exportable Midora Note On.");
        }
        if (!isPureMidi
            && message.MessageType == MidiMessageType.ControlChange
            && message.Byte1 is 91 or 93)
        {
            Add($"Unsupported CC{message.Byte1} reached the MIDI exporter.");
        }
        if (!isPureMidi && message.MessageType is not MidiMessageType.NoteOff
            and not MidiMessageType.NoteOn
            and not MidiMessageType.ControlChange
            and not MidiMessageType.ProgramChange
            and not MidiMessageType.PitchWheelChange)
        {
            Add($"Unknown or unsupported canonical MIDI event type {message.MessageType}.");
        }

        void Add(string messageText) => diagnostics.Add(new(
            "MIDORA-MIDI-EXPORT-CANONICAL",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            messageText,
            value.Source));
    }

    private static MidoraId ResolveTrackId(
        CanonicalCompiledResult compiled,
        CanonicalMidiEvent value,
        List<MidiExportDiagnostic> diagnostics)
    {
        if (value.Source.TrackId != default)
        {
            return value.Source.TrackId;
        }

        // No captured CanonicalMidiEvent on the common source-owned path above.
        // Only uniqueness matters for synthetic cleanup ownership, not the
        // order of allocations or their number within the same Track.
        MidoraId candidate = default;
        bool found = false, ambiguous = false;
        foreach (ChannelUnitAllocation allocation in compiled.Allocations)
        {
            if (allocation.ZeroBasedPort != value.ZeroBasedPort
                || allocation.ZeroBasedChannel != value.ZeroBasedChannel
                || !(value.Tick == compiled.EndTick
                    ? allocation.StartTick < value.Tick && allocation.EndTick >= value.Tick
                    : allocation.StartTick <= value.Tick && allocation.EndTick > value.Tick))
                continue;
            if (found && candidate != allocation.TrackId) { ambiguous = true; break; }
            candidate = allocation.TrackId;
            found = true;
        }
        if (found && !ambiguous) return candidate;

        diagnostics.Add(new(
            "MIDORA-MIDI-EXPORT-OWNER",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            !found
                ? "A generated canonical event has no resolvable Logical Track owner."
                : "A generated canonical event has more than one possible Logical Track owner.",
            value.Source));
        return default;
    }

    private static long RelativeTick(CanonicalCompiledResult compiled, long tick)
    {
        if (tick < compiled.StartTick || tick > compiled.EndTick)
        {
            throw new MidoraMidiException(
                $"Conductor event tick {tick} lies outside [{compiled.StartTick}, {compiled.EndTick}].");
        }
        return checked(tick - compiled.StartTick);
    }

    private static MidiExportEncodingResult Failure(List<MidiExportDiagnostic> diagnostics) =>
        new([], diagnostics.ToArray());

}
