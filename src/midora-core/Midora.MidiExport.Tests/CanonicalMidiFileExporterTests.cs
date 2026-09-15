using System.Buffers.Binary;
using System.Text;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport.Tests;

public sealed class CanonicalMidiFileExporterTests
{
    [Theory]
    [InlineData(576)]
    [InlineData(768)]
    public void ExportsLoopRepeatsBeforeGateExceedsTemplateLength(long gate)
    {
        var fixture = CreateProject();
        using var project = fixture.Project;
        fixture.Instrument.TemplateLengthTicks = 768;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 192;
        fixture.Instrument.LoopEndTick = 384;
        fixture.Track.Segments[0].LengthTicks = 1920;
        fixture.Track.Segments[0].Notes[0].LengthTicks = gate;
        fixture.Voice.Events[0].LengthTicks = 768;
        fixture.Voice.Events.Add(new(project) { Tick = 192, Kind = TemplateEventKind.PitchBend, Value = -7701 });
        fixture.Voice.Events.Add(new(project) { Tick = 288, Kind = TemplateEventKind.PitchBend, Value = 8191 });
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.MidiExport
        });
        Assert.True(compiled.IsConsumable);
        MidiExportEncodingResult exported = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Loop regression",
            LogicalTracks = [Layout(fixture.Track.Id, 0, "Loop")]
        });
        Assert.True(exported.Succeeded);
        StandardMidiFile.ValidateType1(exported.FileBytes);
        ParsedTrack track = ParseTracks(exported.FileBytes)[1];
        var expected = Enumerable.Range(0, checked((int)((gate - 192) / 96)))
            .Select(i => (192L + i * 96, i % 2 == 0 ? -7701 : 8191));
        Assert.Equal(expected, track.ChannelEvents
            .Where(e => (e.Data[0] & 0xf0) == 0xe0 && e.Tick > 0 && e.Tick < gate)
            .Select(e => (e.Tick, (e.Data[2] << 7 | e.Data[1]) - 8192)));
    }

    [Fact]
    public void ExportsCanonicalType1WithCompatibilityProfileAndEffectsOffInitialization()
    {
        (MidoraProject project, LogicalTrack track, _, SubVoice voice) = CreateProject();
        voice.InitialState.BankMsb = 1;
        voice.InitialState.BankLsb = 2;
        voice.InitialState.Program = 5;
        project.Conductor.KeySignatures.Add(new(project, 0, -2, true));
        project.Conductor.Markers.Add(new(project, 48, "标记"));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.MidiExport,
            StartTick = 0,
            EndTick = 192
        });

        MidiExportEncodingResult first = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Midora Conductor",
            LogicalTracks = [Layout(track.Id, 0, "Port 1 / Channel 1")]
        });
        MidiExportEncodingResult second = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Midora Conductor",
            LogicalTracks = [Layout(track.Id, 0, "Port 1 / Channel 1")]
        });

        Assert.True(compiled.IsConsumable);
        Assert.True(first.Succeeded);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(first.FileBytes, second.FileBytes);
        StandardMidiFile.ValidateType1(first.FileBytes);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(first.FileBytes.AsSpan(8, 2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(first.FileBytes.AsSpan(10, 2)));
        Assert.True(Contains(first.FileBytes, [0xff, 0x51, 0x03, 0x07, 0xa1, 0x20]));
        Assert.True(Contains(first.FileBytes, [0xff, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08]));
        Assert.True(Contains(first.FileBytes, [0xff, 0x59, 0x02, 0xfe, 0x01]));
        Assert.True(Contains(first.FileBytes, Encoding.UTF8.GetBytes("标记")));
        Assert.True(Contains(first.FileBytes, Encoding.UTF8.GetBytes("Port 1 / Channel 1")));
        Assert.True(Contains(first.FileBytes, [0xff, 0x21, 0x01, 0x00]));

        List<byte[]> exportedMessages = ParseTracks(first.FileBytes)[1].ChannelMessages;
        byte[][] canonicalMessages = compiled.Events.ToArray().Select(value => value.Message.ToArray()).ToArray();
        Assert.Equal(canonicalMessages.Length + 2, exportedMessages.Count);
        Assert.Equal(new byte[] { 0xb0, 91, 0 }, exportedMessages[0]);
        Assert.Equal(new byte[] { 0xb0, 93, 0 }, exportedMessages[1]);
        for (int index = 0; index < canonicalMessages.Length; index++)
        {
            Assert.Equal(canonicalMessages[index], exportedMessages[index + 2]);
        }

        int bankMsb = Find(exportedMessages, 0xb0, 0, 1);
        int bankLsb = Find(exportedMessages, 0xb0, 32, 2);
        int program = Find(exportedMessages, 0xc0, 5);
        Assert.True(bankMsb < bankLsb && bankLsb < program);
        Assert.Contains(exportedMessages, value => value.SequenceEqual(new byte[] { 0x80, 60, 0 }));
        Assert.Contains(exportedMessages, value => value.SequenceEqual(new byte[] { 0xb0, 120, 0 }));

        ParsedTrack[] parsed = ParseTracks(first.FileBytes);
        Assert.All(parsed, value => Assert.Equal(192, value.EndTick));
        AssertEffectsDisabledAtTrackStart(parsed[1], zeroBasedChannel: 0);
    }

    [Fact]
    public void RoundsTempoOnceAwayFromZeroAndRejectsOutOfRangeResult()
    {
        Assert.Equal(468_750, CanonicalMidiFileExporter.ConvertTempoToMicrosecondsPerQuarterNote(128m));
        Assert.Equal(500_001, CanonicalMidiFileExporter.RoundTempoMicrosecondsPerQuarterNote(500_000.5m));
        Assert.Equal(500_000, CanonicalMidiFileExporter.RoundTempoMicrosecondsPerQuarterNote(500_000.49m));
        Assert.Throws<MidoraMidiException>(() =>
            CanonicalMidiFileExporter.RoundTempoMicrosecondsPerQuarterNote(0.49m));
        Assert.Throws<MidoraMidiException>(() =>
            CanonicalMidiFileExporter.RoundTempoMicrosecondsPerQuarterNote(16_777_215.5m));
    }

    [Fact]
    public void RebasesNonZeroRangeAndWritesUnifiedEndOfTrack()
    {
        (MidoraProject project, LogicalTrack track, _, _) = CreateProject(segmentStart: 100);
        project.Conductor.Tempos.Add(new(project, 120, 100m));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.MidiExport,
            StartTick = 100,
            EndTick = 292
        });

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = [Layout(track.Id, 0, "Port 1 / Channel 1")]
        });

        Assert.True(encoded.Succeeded);
        ParsedTrack[] parsed = ParseTracks(encoded.FileBytes);
        Assert.All(parsed, value => Assert.Equal(192, value.EndTick));
        Assert.Contains(parsed[0].MetaEvents, value => value.Tick == 20 && value.Type == 0x51);
        Assert.Contains(parsed[1].ChannelEvents, value => value.Tick == 0 && value.Data[0] == 0x90);
    }

    [Fact]
    public void RejectsNonExportContextAndReturnsNoPartialBytes()
    {
        (MidoraProject project, LogicalTrack track, _, _) = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = [Layout(track.Id, 0, "Port 1 / Channel 1")]
        });

        Assert.False(encoded.Succeeded);
        Assert.Empty(encoded.FileBytes);
        Assert.Contains(encoded.Diagnostics, value => value.Code == "MIDORA-MIDI-EXPORT-CONTEXT");
    }

    [Fact]
    public void RejectsCc91IfItReachesCanonicalResult()
    {
        MidoraId trackId = MidoraId.FromSequence(1);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
            trackId,
            MidiMessage.ControlChange(0, 91, 1));

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = [Layout(trackId, 0, "Port 1 / Channel 1")]
        });

        Assert.False(encoded.Succeeded);
        Assert.Empty(encoded.FileBytes);
        Assert.Contains(encoded.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-EXPORT-CANONICAL" && value.Message.Contains("CC91", StringComparison.Ordinal));
    }

    [Fact]
    public void WritesGsThenXgNormalPartBeforeChannel10Events()
    {
        MidoraId trackId = MidoraId.FromSequence(1);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
            trackId,
            MidiMessage.NoteOn(9, 60, 100),
            zeroBasedChannel: 9);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = [Layout(trackId, 0, "Port 1 / Channel 10", zeroBasedChannel: 9)]
        });

        Assert.True(encoded.Succeeded);
        Assert.Empty(encoded.Diagnostics);
        ParsedTrack eventTrack = ParseTracks(encoded.FileBytes)[1];
        Assert.Equal(2, eventTrack.SystemExclusiveEvents.Count);
        Assert.Equal(
            new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1b, 0xf7 },
            eventTrack.SystemExclusiveEvents[0].Data);
        Assert.Equal(
            new byte[] { 0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7 },
            eventTrack.SystemExclusiveEvents[1].Data);
        Assert.All(eventTrack.SystemExclusiveEvents, value => Assert.Equal(0, value.Tick));

        int port = encoded.FileBytes.AsSpan().IndexOf(new byte[] { 0x00, 0xff, 0x21, 0x01, 0x00 });
        int gs = encoded.FileBytes.AsSpan().IndexOf(
            new byte[] { 0x00, 0xf0, 0x0a, 0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1b, 0xf7 });
        int xg = encoded.FileBytes.AsSpan().IndexOf(
            new byte[] { 0x00, 0xf0, 0x08, 0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7 });
        int reverbOff = encoded.FileBytes.AsSpan().IndexOf(new byte[] { 0x00, 0xb9, 91, 0 });
        int chorusOff = encoded.FileBytes.AsSpan().IndexOf(new byte[] { 0x00, 0xb9, 93, 0 });
        int noteOn = encoded.FileBytes.AsSpan().IndexOf(new byte[] { 0x00, 0x99, 0x3c, 0x64 });
        Assert.True(port < gs && gs < xg && xg < reverbOff && reverbOff < chorusOff && chorusOff < noteOn);
    }

    [Fact]
    public void WritesChannel10InitializationOncePerRelevantEventTrackAndNeverElsewhere()
    {
        MidoraId trackId = MidoraId.FromSequence(1);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
        [
            SyntheticEvent(trackId, 0, MidiMessage.NoteOn(0, 60, 100), 0),
            SyntheticEvent(trackId, 1, MidiMessage.NoteOn(9, 61, 100), 1, zeroBasedChannel: 9)
        ]);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = [new(trackId, new Dictionary<MidiExportChannelUnit, string>
            {
                [new(0, 0)] = "Port 1 / Channel 1",
                [new(1, 9)] = "Port 2 / Channel 10"
            })]
        });

        Assert.True(encoded.Succeeded);
        ParsedTrack[] tracks = ParseTracks(encoded.FileBytes);
        Assert.Empty(tracks[0].SystemExclusiveEvents);
        Assert.Empty(tracks[1].SystemExclusiveEvents);
        Assert.Equal(2, tracks[2].SystemExclusiveEvents.Count);
    }

    [Fact]
    public void EmitsAValidConductorOnlyFileForZeroLengthExport()
    {
        MidoraProject project = new(192);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.MidiExport,
            StartTick = 0,
            EndTick = 0
        });

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks = []
        });

        Assert.True(encoded.Succeeded);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(encoded.FileBytes.AsSpan(10, 2)));
        ParsedTrack track = Assert.Single(ParseTracks(encoded.FileBytes));
        Assert.Equal(0, track.EndTick);
        Assert.Empty(track.ChannelMessages);
    }

    [Fact]
    public void AcceptsMaximumSmfDeltaAndPadsTheNextTickWithoutChangingCanonical()
    {
        CanonicalCompiledResult maximum = CreateSyntheticCompiled(
            [],
            StandardMidiFile.MaximumVariableLengthValue);
        CanonicalCompiledResult overflow = CreateSyntheticCompiled(
            [],
            (long)StandardMidiFile.MaximumVariableLengthValue + 1);

        MidiExportEncodingResult accepted = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = maximum,
            ConductorTrackName = "Conductor",
            LogicalTracks = []
        });
        MidiExportEncodingResult padded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = overflow,
            ConductorTrackName = "Conductor",
            LogicalTracks = []
        });

        Assert.True(accepted.Succeeded);
        ParsedTrack acceptedTrack = Assert.Single(ParseTracks(accepted.FileBytes));
        Assert.Equal(StandardMidiFile.MaximumVariableLengthValue, acceptedTrack.EndTick);
        Assert.Single(acceptedTrack.MetaEvents, value => value.Type == StandardMidiFile.EndOfTrackMetaType);

        Assert.True(padded.Succeeded);
        ParsedTrack paddedTrack = Assert.Single(ParseTracks(padded.FileBytes));
        Assert.Equal((long)StandardMidiFile.MaximumVariableLengthValue + 1, paddedTrack.EndTick);
        Assert.Single(paddedTrack.MetaEvents, value => value.Type == StandardMidiFile.TextMetaType);
        Assert.Equal(new StandardMidiFileWriteSummary(1, 1), padded.WriteSummary);
        Assert.Empty(overflow.Events.ToArray());
    }

    [Fact]
    public void OrdersEventTracksByUnitAndMergesAUnitReusedAcrossLogicalTracks()
    {
        MidoraId trackA = MidoraId.FromSequence(1);
        MidoraId trackB = MidoraId.FromSequence(2);
        CanonicalMidiEvent[] events =
        [
            SyntheticEvent(trackB, 0, MidiMessage.ControlChange(0, 3, 3), 0),
            SyntheticEvent(trackA, 0, MidiMessage.ControlChange(0, 1, 1), 1),
            SyntheticEvent(trackA, 0, MidiMessage.ControlChange(1, 4, 4), 2, zeroBasedChannel: 1),
            SyntheticEvent(trackB, 1, MidiMessage.ControlChange(0, 2, 2), 3)
        ];
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(events);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks =
            [
                new(trackB, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(0, 0)] = "Port 1 / Channel 1",
                    [new(1, 0)] = "Port 2 / Channel 1"
                }),
                new(trackA, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(0, 0)] = "Port 1 / Channel 1",
                    [new(0, 1)] = "Port 1 / Channel 2"
                })
            ]
        });

        Assert.True(encoded.Succeeded);
        ParsedTrack[] parsed = ParseTracks(encoded.FileBytes);
        Assert.Equal(
            ["Conductor", "Port 1 / Channel 1", "Port 1 / Channel 2", "Port 2 / Channel 1"],
            parsed.Select(GetTrackName));
        Assert.Equal(4, parsed[1].ChannelEvents.Count);
        Assert.All(parsed.Skip(1), AssertUsesSingleChannel);
        Assert.All(parsed.Skip(1), track => AssertEffectsDisabledAtTrackStart(
            track,
            (byte)(track.ChannelEvents[0].Data[0] & 0x0f)));
    }

    [Fact]
    public void RejectsAUnitMissingFromItsOwnerTrackLayoutEvenWhenAnotherTrackDeclaresIt()
    {
        MidoraId trackA = MidoraId.FromSequence(1);
        MidoraId trackB = MidoraId.FromSequence(2);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
        [
            SyntheticEvent(trackA, 0, MidiMessage.NoteOn(0, 60, 100), 0)
        ]);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTracks =
            [
                new(trackA, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(0, 1)] = "Port 1 / Channel 2"
                }),
                new(trackB, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(0, 0)] = "Port 1 / Channel 1"
                })
            ]
        });

        Assert.False(encoded.Succeeded);
        Assert.Empty(encoded.FileBytes);
        Assert.Contains(encoded.Diagnostics, value => value.Code == "MIDORA-MIDI-EXPORT-UNIT-LAYOUT");
    }

    [Fact]
    public void PerLogicalTrackKeepsAllUsedPortsAndCanEncodeAnEmptyMusicFile()
    {
        MidoraId trackId = MidoraId.FromSequence(1);
        MidoraId excludedTrackId = MidoraId.FromSequence(2);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
        [
            SyntheticEvent(trackId, 0, MidiMessage.NoteOn(0, 60, 100), 0),
            SyntheticEvent(trackId, 2, MidiMessage.NoteOn(1, 64, 100), 1, zeroBasedChannel: 1),
            SyntheticEvent(excludedTrackId, 0, MidiMessage.NoteOn(0, 72, 100), 2)
        ]);
        MidiExportLogicalTrackLayout layout = new(
            trackId,
            new Dictionary<MidiExportChannelUnit, string>
            {
                [new(0, 0)] = "Port 1 / Channel 1",
                [new(2, 1)] = "Port 3 / Channel 2"
            });

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeLogicalTrack(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            LogicalTrack = layout
        });
        MidiExportEncodingResult empty = CanonicalMidiFileExporter.EncodeLogicalTrack(new()
        {
            CompiledResult = CreateSyntheticCompiled([]),
            ConductorTrackName = "Conductor",
            LogicalTrack = new(trackId, new Dictionary<MidiExportChannelUnit, string>())
        });

        Assert.True(encoded.Succeeded);
        ParsedTrack[] tracks = ParseTracks(encoded.FileBytes);
        Assert.Equal(["Conductor", "Port 1 / Channel 1", "Port 3 / Channel 2"], tracks.Select(GetTrackName));
        Assert.Contains(tracks[1].MetaEvents, value =>
            value.Type == StandardMidiFile.MidiPortMetaType && value.Data.SequenceEqual(new byte[] { 0 }));
        Assert.Contains(tracks[2].MetaEvents, value =>
            value.Type == StandardMidiFile.MidiPortMetaType && value.Data.SequenceEqual(new byte[] { 2 }));
        Assert.All(tracks.Skip(1), track => AssertEffectsDisabledAtTrackStart(
            track,
            (byte)(track.ChannelEvents[0].Data[0] & 0x0f)));
        Assert.DoesNotContain(
            tracks.SelectMany(track => track.ChannelEvents),
            value => value.Data.Length > 1 && value.Data[1] == 72);
        Assert.True(empty.Succeeded);
        Assert.Single(ParseTracks(empty.FileBytes));
    }

    [Fact]
    public void PerPortOrdersUnitsByChannelAndNormalizesEveryEventTrackToPortOne()
    {
        MidoraId trackA = MidoraId.FromSequence(1);
        MidoraId trackB = MidoraId.FromSequence(2);
        CanonicalCompiledResult compiled = CreateSyntheticCompiled(
        [
            SyntheticEvent(trackA, 2, MidiMessage.NoteOn(0, 60, 100), 0),
            SyntheticEvent(trackB, 2, MidiMessage.NoteOn(1, 61, 100), 1, zeroBasedChannel: 1),
            SyntheticEvent(trackA, 0, MidiMessage.NoteOn(0, 62, 100), 2)
        ]);

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodePort(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            ZeroBasedOriginalPort = 2,
            LogicalTracks =
            [
                new(trackB, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(2, 1)] = "Port 3 / Channel 2"
                }),
                new(trackA, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(2, 0)] = "Port 3 / Channel 1"
                })
            ]
        });
        MidiExportEncodingResult unused = CanonicalMidiFileExporter.EncodePort(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Conductor",
            ZeroBasedOriginalPort = 15,
            LogicalTracks =
            [
                new(trackB, new Dictionary<MidiExportChannelUnit, string>()),
                new(trackA, new Dictionary<MidiExportChannelUnit, string>())
            ]
        });

        Assert.True(encoded.Succeeded);
        ParsedTrack[] tracks = ParseTracks(encoded.FileBytes);
        Assert.Equal(["Conductor", "Port 3 / Channel 1", "Port 3 / Channel 2"], tracks.Select(GetTrackName));
        Assert.All(tracks.Skip(1), track => Assert.Contains(track.MetaEvents, value =>
            value.Type == StandardMidiFile.MidiPortMetaType && value.Data.SequenceEqual(new byte[] { 0 })));
        Assert.All(tracks.Skip(1), AssertUsesSingleChannel);
        Assert.All(tracks.Skip(1), track => AssertEffectsDisabledAtTrackStart(
            track,
            (byte)(track.ChannelEvents[0].Data[0] & 0x0f)));
        Assert.DoesNotContain(tracks.SelectMany(track => track.ChannelEvents), value => value.Data[1] == 62);
        Assert.False(unused.Succeeded);
        Assert.Contains(unused.Diagnostics, value => value.Code == "MIDORA-MIDI-EXPORT-UNUSED-PORT");
    }

    private static (MidoraProject Project, LogicalTrack Track, EventInstrument Instrument, SubVoice Voice) CreateProject(
        long segmentStart = 0)
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RootNote = 60,
            TemplateLengthTicks = 192,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track"};
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { ProjectStartTick = segmentStart, LengthTicks = 192 };
        segment.Notes.Add(new(project)
        {
            StartTick = 0,
            LengthTicks = 192,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return (project, track, instrument, voice);
    }

    private static MidiExportLogicalTrackLayout Layout(
        MidoraId trackId,
        byte port,
        string name,
        byte zeroBasedChannel = 0) =>
        new(trackId, new Dictionary<MidiExportChannelUnit, string>
        {
            [new(port, zeroBasedChannel)] = name
        });

    private static CanonicalCompiledResult CreateSyntheticCompiled(
        MidoraId trackId,
        MidiMessage message,
        byte zeroBasedChannel = 0)
    {
        CanonicalMidiEvent value = SyntheticEvent(trackId, 0, message, 0, zeroBasedChannel);
        return CreateSyntheticCompiled([value]);
    }

    private static CanonicalMidiEvent SyntheticEvent(
        MidoraId trackId,
        byte port,
        MidiMessage message,
        long stableOrder,
        byte zeroBasedChannel = 0)
    {
        SourceReference source = new(TrackId: trackId, Tick: 0);
        return new(
            0,
            port,
            zeroBasedChannel,
            message,
            message.MessageType == MidiMessageType.NoteOn ? CanonicalEventRole.NoteOn : CanonicalEventRole.ControlChange,
            stableOrder,
            long.MinValue,
            long.MinValue,
            source);
    }

    private static CanonicalCompiledResult CreateSyntheticCompiled(
        CanonicalMidiEvent[] events,
        long endTick = 1)
    {
        CanonicalConductor conductor = new(
            [new CanonicalTempo(0, 120m)],
            [],
            [],
            [],
            [],
            null);
        return new(
            192,
            new CompilationContextSummary(
                new CompilationRequest
                {
                    Purpose = CompilationPurpose.MidiExport,
                    EndTick = endTick
                },
                endTick,
                CompilationEndTickSource.ExplicitRequest),
            events,
            conductor,
            [],
            [],
            false,
            true,
            null,
            endTick,
            new(events.Select(value => value.Source.TrackId).Distinct().Count(), 1, events.Length, 1));
    }

    private static int Find(IReadOnlyList<byte[]> messages, params byte[] expected)
    {
        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index].SequenceEqual(expected)) return index;
        }
        return -1;
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> expected) =>
        source.IndexOf(expected) >= 0;

    private static string GetTrackName(ParsedTrack track)
    {
        TimedMetaEvent name = Assert.Single(track.MetaEvents, value => value.Type == StandardMidiFile.TrackNameMetaType);
        return Encoding.UTF8.GetString(name.Data);
    }

    private static void AssertUsesSingleChannel(ParsedTrack track)
    {
        byte[] channels = track.ChannelEvents
            .Select(value => (byte)(value.Data[0] & 0x0f))
            .Distinct()
            .ToArray();
        Assert.Single(channels);
    }

    private static void AssertEffectsDisabledAtTrackStart(
        ParsedTrack track,
        byte zeroBasedChannel)
    {
        TimedChannelEvent[] initialization = track.ChannelEvents.Take(2).ToArray();
        Assert.Equal(2, initialization.Length);
        Assert.Equal(0, initialization[0].Tick);
        Assert.Equal(new byte[] { (byte)(0xb0 | zeroBasedChannel), 91, 0 }, initialization[0].Data);
        Assert.Equal(0, initialization[1].Tick);
        Assert.Equal(new byte[] { (byte)(0xb0 | zeroBasedChannel), 93, 0 }, initialization[1].Data);
    }

    private static ParsedTrack[] ParseTracks(byte[] file)
    {
        int trackCount = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(10, 2));
        int position = 14;
        ParsedTrack[] result = new ParsedTrack[trackCount];
        for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            Assert.Equal("MTrk", Encoding.ASCII.GetString(file, position, 4));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position + 4, 4)));
            position += 8;
            int end = position + length;
            long tick = 0;
            List<TimedChannelEvent> channelEvents = [];
            List<byte[]> channelMessages = [];
            List<TimedMetaEvent> metaEvents = [];
            List<TimedSystemExclusiveEvent> systemExclusiveEvents = [];
            while (position < end)
            {
                tick += ReadVariableLength(file, ref position);
                byte status = file[position++];
                if (status == 0xff)
                {
                    byte type = file[position++];
                    int dataLength = ReadVariableLength(file, ref position);
                    byte[] data = file.AsSpan(position, dataLength).ToArray();
                    position += dataLength;
                    metaEvents.Add(new(tick, type, data));
                    continue;
                }
                if (status is 0xf0 or 0xf7)
                {
                    int dataLength = ReadVariableLength(file, ref position);
                    byte[] data = file.AsSpan(position, dataLength).ToArray();
                    position += dataLength;
                    systemExclusiveEvents.Add(new(tick, status, data));
                    continue;
                }
                int dataCount = (status & 0xf0) is 0xc0 or 0xd0 ? 1 : 2;
                byte[] message = new byte[dataCount + 1];
                message[0] = status;
                file.AsSpan(position, dataCount).CopyTo(message.AsSpan(1));
                position += dataCount;
                channelMessages.Add(message);
                channelEvents.Add(new(tick, message));
            }
            result[trackIndex] = new(
                tick,
                channelMessages,
                channelEvents,
                metaEvents,
                systemExclusiveEvents);
        }
        return result;
    }

    private static int ReadVariableLength(byte[] source, ref int position)
    {
        int result = 0;
        byte value;
        do
        {
            value = source[position++];
            result = (result << 7) | (value & 0x7f);
        }
        while ((value & 0x80) != 0);
        return result;
    }

    private sealed record ParsedTrack(
        long EndTick,
        List<byte[]> ChannelMessages,
        List<TimedChannelEvent> ChannelEvents,
        List<TimedMetaEvent> MetaEvents,
        List<TimedSystemExclusiveEvent> SystemExclusiveEvents);
    private sealed record TimedChannelEvent(long Tick, byte[] Data);
    private sealed record TimedMetaEvent(long Tick, byte Type, byte[] Data);
    private sealed record TimedSystemExclusiveEvent(long Tick, byte Status, byte[] Data);
}
