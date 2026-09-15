using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

#if !LOGICAL_MEMORY_PROBE
public sealed class LogicalCompiledMemoryOracleTests
{
    [Fact]
    public void PagedComplexMappingLoopAndContinuousCurvePreserveFrozenWholeAndRangeOutputs()
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = LogicalCompiledMemoryOracle.Create("complex", 64);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
        Assert.True(result.IsConsumable);
        Assert.True(result.HasPagedLogicalEvents);
        Assert.Equal(5766, result.TotalEventCount);
        Assert.Equal("1ba54f96581dafba211f87f3763a960ff3e196cb3d1e6d8f847a679678bd03b0", LogicalCompiledMemoryOracle.Digest(result));
        CanonicalMidiEvent[] all = result.QueryEventPages(result.StartTick, result.EndTick).SelectMany(page => page.Items).ToArray();
        Assert.Equal(5766, all.Length);
        Assert.Contains(all, item => item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == 11 && item.Message.Byte2 == 42);
        Assert.Contains(all, item => item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == 1 && item.Message.Byte2 is > 0 and < 12);
        foreach ((long start, long end) in new[] { (200L, 1500L), (20_000L, 26_000L), (result.EndTick - 512, result.EndTick) })
        {
            CanonicalMidiEvent[] expected = all.Where(item => item.Tick >= start
                && (item.Tick < end || end == result.EndTick && item.Tick == end)).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, result.QueryEventPages(start, end).SelectMany(page => page.Items));
        }
        CanonicalCompiledResult range = compiler.CompileFull(fixture.Project, LogicalCompiledMemoryOracle.Request(true));
        Assert.True(range.IsConsumable);
        Assert.Equal(262, range.TotalEventCount);
        Assert.Equal("3f4c6936ec6d254fc93d9ef145849c41cc565bc294a357ba206215b7ed433fbd", LogicalCompiledMemoryOracle.Digest(range));
    }

    [Theory]
    [InlineData("shared", 64, false)]
    [InlineData("isolated", 32, false)]
    [InlineData("complex", 8, false)]
    [InlineData("complex", 8, true)]
    [InlineData("invalid", 8, false)]
    [InlineData("mixed", 32, false)]
    [InlineData("mixed", 32, true)]
    public void FrozenPreOptimizationFormalOutputIsPreserved(string scenario, int count, bool range)
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = LogicalCompiledMemoryOracle.Create(scenario, count);
        using MidoraCompiler compiler = new();
        CompilationRequest request = LogicalCompiledMemoryOracle.Request(range);
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project, request);
        Assert.Equal(LogicalCompiledMemoryOracle.Expected(scenario, range), LogicalCompiledMemoryOracle.Digest(result));
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("isolated")]
    [InlineData("complex")]
    public void IncrementalAndOldReadersPreserveCompleteOutput(string scenario)
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = LogicalCompiledMemoryOracle.Create(scenario, 16);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult old = compiler.CompileFull(fixture.Project);
        Assert.True(old.IsConsumable);
        string oldDigest = LogicalCompiledMemoryOracle.Digest(old);
        fixture.Segment.Notes[0].StartTick++;
        CanonicalCompiledResult edited = compiler.CompileIncremental(fixture.Project,
            new ProjectChangeSet { TrackIds = { fixture.Track.Id } });
        using MidoraCompiler independent = new();
        CanonicalCompiledResult full = independent.CompileFull(fixture.Project);
        Assert.Equal(LogicalCompiledMemoryOracle.Digest(full), LogicalCompiledMemoryOracle.Digest(edited));
        Assert.NotEqual(oldDigest, LogicalCompiledMemoryOracle.Digest(edited));
        compiler.ClearCache();
        Assert.Equal(oldDigest, LogicalCompiledMemoryOracle.Digest(old));
    }

    [Fact]
    public void BoundedWindowReadsEqualTheFrozenWholeEventSequenceAndHonorCancellation()
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = LogicalCompiledMemoryOracle.Create("isolated", 128);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
        CanonicalMidiEvent[] all = result.QueryEventPages(result.StartTick, result.EndTick).SelectMany(page => page.Items).ToArray();
        foreach ((long start, long end) in new[] { (0L, 512L), (1024L, 2048L), (result.EndTick - 512, result.EndTick) })
        {
            var actual = result.QueryEventPages(start, end).SelectMany(page => page.Items);
            Assert.Equal(all.Where(item => item.Tick >= start && (item.Tick < end || end == result.EndTick && item.Tick == end)), actual);
        }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => result.QueryEventPages(result.StartTick, result.EndTick,
            cancellationToken: cancellation.Token).SelectMany(page => page.Items).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagedLogicalAndInlinePureConsumersPreserveFrozenOutput(bool range)
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = LogicalCompiledMemoryOracle.Create("mixed", 32);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project, LogicalCompiledMemoryOracle.Request(range));
        Assert.True(result.IsConsumable);
        if (!range) Assert.True(result.HasPagedLogicalEvents);
        Assert.False(result.HasPagedPureMidiEvents);
        Assert.Contains(result.Events.ToArray(), value => value.Source.PureMidiTrackId != default);
        Assert.Equal(LogicalCompiledMemoryOracle.ExpectedConsumer(range), LogicalCompiledMemoryOracle.ConsumerDigest(result));
    }
}
#endif

// Also compiled into the standalone probe against frozen pre-change DLLs. A SHA-256 over
// every formal field is an exact full-stream regression oracle, not a sampled comparison.
internal static class LogicalCompiledMemoryOracle
{
    public static string Expected(string scenario, bool range) => (scenario, range) switch
    {
        ("shared", false) => "c492a50a5a8392c7fb7a184ccb6123bbe9e350543e181a3ef47a2db3106a3014",
        ("isolated", false) => "0c029f88e905ecabb1e106177006742ccdb4d9fd2457220bbb563f09f125a28b",
        ("complex", false) => "f6fe7d72980859a9fda48443a0866231e5541a00ce4babde7d6fac11be401b58",
        ("complex", true) => "5523f957da33b6b1cd581f7b107a7a6e27f8360541b8e46e2d991920d65005e7",
        ("invalid", false) => "56de5a4705156aeb30e378ada00f59e530305bb007dd5da4a9951a0df5c2fade",
        ("mixed", false) => "fbf9c36018b204ed8ada3d1106604199c489fdc554dba80683599bafa0b04bec",
        ("mixed", true) => "0f072028ed4a6297480f87c4c4d8323af67fdb4e54f1b8c5a096a5f8df14f0a6",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    public static string ExpectedConsumer(bool range) => range
        ? "c6860476434999a7b8ac618f2e4b8c0397a9e9507a2cfe2fee3f1400a9a82720"
        : "24258c23bb5a4e8b0faa6dd6e76a490981eeb610079c9edaf32cbf754c3302ad";

    public sealed record Fixture(MidoraProject Project, LogicalTrack Track, Segment Segment) : IDisposable
    {
        public void Dispose() => Project.Dispose();
    }

    public static CompilationRequest Request(bool range) => range
        ? new() { Purpose = CompilationPurpose.Playback, StartTick = 200, EndTick = 1_500 }
        : new();

    public static Fixture Create(string scenario, int count, int templateNotes = 8, int loops = 4, int voices = 4)
    {
        if (count is < 1 or > 100_000 || templateNotes is < 1 or > 4096 || loops is < 1 or > 64 || voices is < 1 or > 8
            || (scenario is "isolated" or "expansion" or "mixed") && (long)count * templateNotes * loops * voices > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(count), "Probe safety bounds, not product limits.");
        if (scenario is not ("shared" or "isolated" or "complex" or "invalid" or "expansion" or "mixed"))
            throw new ArgumentOutOfRangeException(nameof(scenario));
        MidoraProject project = new(480, new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        bool expanded = scenario is "isolated" or "expansion" or "mixed";
        bool complex = scenario == "complex";
        EventInstrument instrument = EventInstrumentLibrary.Create(project);
        instrument.Name = "Logical memory oracle";
        instrument.RequiresChannelIsolation = scenario != "shared" && scenario != "invalid";
        instrument.OverlapPolicy = complex ? OverlapPolicy.Warn : OverlapPolicy.Reject;
        instrument.TemplateLengthTicks = expanded ? Math.Max(480, templateNotes * 4L) : 768;
        instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        instrument.LongLifecycle = LongNoteLifecycle.HoldLastState;
        if (expanded || complex)
        {
            instrument.LoopStartTick = complex ? 192 : 0;
            instrument.LoopEndTick = complex ? 384 : instrument.TemplateLengthTicks;
        }
        if (complex) instrument.PreRollTicks = 48;
        int voiceCount = expanded ? voices : complex ? 2 : 1;
        for (int i = 1; i < voiceCount; i++) instrument.SubVoices.Add(new SubVoice(project));
        foreach (SubVoice voice in instrument.SubVoices)
        {
            int notes = expanded ? templateNotes : 1;
            for (int i = 0; i < notes; i++)
                voice.Events.Add(TemplateEvent.Note(project, expanded ? i * 4L : 0, expanded ? 2 : 700, 60, 100));
            if (complex)
            {
                voice.Events.Add(TemplateEvent.Program(project, 0, 4));
                TemplateEvent cc = TemplateEvent.ControlChange(project, 192, 11, 40);
                cc.ValueMappings.Add(new ValueMappingStep(project)
                {
                    Source = MappingSource.Constant, Operation = MappingOperation.Add, Constant = 2
                });
                voice.Events.Add(cc);
                voice.Events.Add(TemplateEvent.ControlChange(project, 288, 11, 80));
                voice.Events.Add(new TemplateEvent(project) { Tick = 192, Kind = TemplateEventKind.PitchBend, Value = -7701 });
                voice.Events.Add(new TemplateEvent(project) { Tick = 288, Kind = TemplateEventKind.PitchBend, Value = 8191 });
                ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(1) };
                curve.Points.Add(new CurvePoint(project, 0, 0));
                curve.Points.Add(new CurvePoint(project, 64, 12));
                voice.Curves.Add(curve);
            }
        }
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        long gate = expanded ? instrument.TemplateLengthTicks * loops : complex ? 960 : 720;
        long spacing = scenario == "invalid" ? 4 : complex ? 1024 : Math.Max(1024, gate + 128);
        LogicalTrack track = new(project) { Name = "Logical A", EventInstrumentUsageId = usage.Id };
        Segment segment = new(project) { LengthTicks = checked(count * spacing + gate + 1) };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        for (int i = 0; i < count; i++)
            segment.Notes.Add(new LogicalNote(project)
            {
                StartTick = i * spacing + instrument.PreRollTicks,
                LengthTicks = gate, Note = scenario == "invalid" ? 60 : 48 + i % 24,
                Velocity = 64 + i % 63
            });
        if (scenario == "shared" || complex)
        {
            LogicalTrack sibling = new(project) { Name = "Logical B", EventInstrumentUsageId = usage.Id };
            Segment siblingSegment = new(project) { ProjectStartTick = 128, LengthTicks = segment.LengthTicks };
            siblingSegment.Notes.Add(new LogicalNote(project)
            {
                StartTick = instrument.PreRollTicks, LengthTicks = gate, Note = 72, Velocity = 79
            });
            sibling.Segments.Add(siblingSegment);
            project.Tracks.Add(sibling);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, sibling.Id));
        }
        if (scenario == "mixed")
        {
            MidiChannelRoot root = new(project) { Name = "Inline fixed root", RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 2, FixedZeroBasedChannel = 3, ChannelMode = MidiChannelMode.Melodic };
            project.MidiChannelRoots.Add(root);
            for (int i = 0; i < 2; i++)
            {
                PureMidiTrack pure = new(project) { Name = "Inline Pure " + i, MidiChannelRootId = root.Id };
                MidiSegment direct = new(project) { LengthTicks = i == 0 ? segment.LengthTicks : 600 };
                pure.Segments.Add(direct); project.PureMidiTracks.Add(pure);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, pure.Id));
                direct.Notes.Add(new(project) { StartTick = i * 400, LengthTicks = 2000, Key = 60,
                    NoteOnVelocity = 97 + i, NoteOffVelocity = 37 + i, NoteOnOrder = 3, NoteOffOrder = 9 });
                direct.ChannelEvents.Add(new(project) { Tick = 80 + i * 20, Kind = DirectMidiChannelEventKind.ControlChange,
                    Data1 = 11, Data2 = 40 + i, Order = 2 });
                if (i == 1)
                {
                    direct.ChannelEvents.Add(new(project) { Tick = 120, Kind = DirectMidiChannelEventKind.PitchBend,
                        Data1 = 65, Data2 = 63, Order = 5 });
                    direct.OpaqueEvents.Add(new(project) { Tick = 40, Kind = OpaqueMidiEventKind.SystemExclusive,
                        Payload = [0x43, 0x10, 0x4c, 0x08, 0x03, 0x07, 0x00, 0xf7], Order = 1 });
                    direct.OpaqueEvents.Add(new(project) { Tick = 80, Kind = OpaqueMidiEventKind.Meta,
                        MetaType = 1, Payload = Encoding.UTF8.GetBytes("retained text"), Order = 6 });
                }
            }
        }
        return new(project, track, segment);
    }

    public static string Digest(CanonicalCompiledResult result)
    {
        using HashStream stream = new();
        using BufferedStream buffer = new(stream, 65_536);
        using BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write("midora-logical-compiled-oracle-v1");
        writer.Write(result.IsConsumable); writer.Write(result.IsPartial);
        writer.Write((int?)result.FailureStage ?? -1); writer.Write(result.Fingerprint);
        writer.Write(result.TicksPerQuarterNote); writer.Write(result.TotalEventCount); writer.Write(result.TotalNoteOnEventCount);
        CompilationContextSummary context = result.Context;
        writer.Write((int)context.Purpose); writer.Write(context.StartTick);
        writer.Write(context.RequestedEndTick ?? -1); writer.Write(context.EndTick); writer.Write((int)context.EndTickSource);
        writer.Write(context.IncludesAllTracks); writer.Write(context.IncludesAllSubVoices);
        writer.Write(context.TreatWarningsAsErrors); writer.Write(context.CollectDebugDiagnostics);
        foreach (MidoraId id in context.IncludedTrackIds) writer.Write(id.Value);
        writer.Write(-1L);
        foreach (MidoraId id in context.IncludedSubVoiceIds) writer.Write(id.Value);
        writer.Write(-1L);
        CompilationStatistics statistics = result.Statistics;
        writer.Write(statistics.SourceTrackCount); writer.Write(statistics.ExpandedInstanceCount);
        writer.Write(statistics.EventCount); writer.Write(statistics.PeakChannelUnitCount);
        writer.Write(statistics.ExpandedSegmentCount); writer.Write(statistics.ParticipatingEventInstrumentCount);
        writer.Write(statistics.ParticipatingSubVoiceCount); writer.Write(statistics.UsedPortCount);
        writer.Write(statistics.NoteOnEventCount); writer.Write(statistics.ReservedMidiRootUnitCount);
        writer.Write(statistics.AllocatedMidiRootUnitCount); writer.Write(statistics.LogicalPeakChannelUnitCount);
        writer.Write(statistics.ResourceShortage is not null);
        if (statistics.ResourceShortage is { } shortage)
        {
            writer.Write(shortage.Range.StartTick); writer.Write(shortage.Range.EndTick);
            writer.Write(shortage.RequestedChannelUnitCount); writer.Write(shortage.AvailableChannelUnitCount);
            WriteIds(shortage.TrackIds); WriteIds(shortage.SegmentIds); WriteIds(shortage.LogicalNoteIds);
            WriteIds(shortage.EventInstrumentIds); WriteIds(shortage.SubVoiceIds);
        }
        long events = 0;
        if (result.EndTick > result.StartTick)
        {
            foreach (CanonicalMidiEventPage page in result.QueryEventPages(result.StartTick, result.EndTick))
            foreach (CanonicalMidiEvent item in page.Items) { WriteEvent(writer, item); events++; }
        }
        else foreach (CanonicalMidiEvent item in result.Events) { WriteEvent(writer, item); events++; }
        if (events != result.TotalEventCount) throw new InvalidDataException($"Incomplete formal event enumeration: {events}/{result.TotalEventCount}.");
        writer.Write(result.Allocations.Length);
        foreach (ChannelUnitAllocation allocation in result.Allocations) writer.Write(JsonSerializer.Serialize(allocation));
        writer.Write(result.Diagnostics.Count);
        foreach (CompilerDiagnostic diagnostic in result.Diagnostics)
        {
            writer.Write(diagnostic.Code); writer.Write((int)diagnostic.Severity);
            writer.Write(diagnostic.Message); WriteSource(writer, diagnostic.Source);
        }
        writer.Write(result.SmfTracks.Length);
        foreach (CanonicalSmfTrackDescriptor descriptor in result.SmfTracks) writer.Write(JsonSerializer.Serialize(descriptor));
        writer.Write(result.Conductor.Tempos.Length);
        foreach (CanonicalTempo tempo in result.Conductor.Tempos) writer.Write(JsonSerializer.Serialize(tempo));
        writer.Write(result.Conductor.TimeSignatures.Length);
        foreach (var signature in result.Conductor.TimeSignatures) writer.Write(JsonSerializer.Serialize(signature));
        writer.Write(result.Conductor.KeySignatures.Length);
        foreach (var signature in result.Conductor.KeySignatures) writer.Write(JsonSerializer.Serialize(signature));
        writer.Write(result.Conductor.Markers.Length);
        foreach (var marker in result.Conductor.Markers) writer.Write(JsonSerializer.Serialize(marker));
        writer.Write(JsonSerializer.Serialize(result.Conductor.EndMarker));
        writer.Flush(); buffer.Flush();
        return stream.Finish();

        void WriteIds(ReadOnlySpan<MidoraId> ids)
        {
            foreach (MidoraId id in ids) writer.Write(id.Value);
            writer.Write(-1L);
        }
    }

    public static (string Digest, long EventCount) QueryDigest(CanonicalCompiledResult result, long start, long end)
    {
        using HashStream stream = new();
        using BufferedStream buffer = new(stream, 65_536);
        using BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        long count = 0;
        foreach (CanonicalMidiEventPage page in result.QueryEventPages(start, end))
        foreach (CanonicalMidiEvent item in page.Items) { WriteEvent(writer, item); count++; }
        writer.Flush(); buffer.Flush();
        return (stream.Finish(), count);
    }

    public static string ConsumerDigest(CanonicalCompiledResult result, Action<string>? inspect = null)
    {
        // QueryMidiRenderEventPages is only the deferred backing stream, not a whole
        // result reader. Normalize both physical layouts before freezing this small oracle.
        if (result.TotalEventCount > 100_000) throw new ArgumentOutOfRangeException(nameof(result), "Small consumer oracle only.");
        using HashStream stream = new();
        using BufferedStream buffer = new(stream, 65_536);
        using BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        Write("midora-logical-mixed-consumers-v1");
        List<CanonicalMidiRenderEvent> render = result.Events.ToArray().Select(item => new CanonicalMidiRenderEvent(
            item.Tick, item.ZeroBasedPort, item.Message, item.Source.TrackId,
            item.Source.Origin == SourceOrigin.MidiChannelRootLifecycle ? item.Source.MidiChannelRootId : item.Source.TrackId,
            item.Role, item.StableOrder, item.SmfTrackOrder, item.SmfEventOrder, item.Source.DirectMidiObjectId)).ToList();
        foreach (CanonicalMidiRenderEventPage page in result.QueryMidiRenderEventPages(result.StartTick, result.EndTick, true)) render.AddRange(page.Items);
        render.Sort(CompareRender);
        foreach (CanonicalMidiRenderEvent item in render) Write(JsonSerializer.Serialize(item));
        Write("smf");
        foreach (CanonicalSmfTrackDescriptor descriptor in result.SmfTracks)
        {
            Write(JsonSerializer.Serialize(descriptor));
            foreach (var page in result.QuerySmfTrackChannelEventPages(descriptor.ExportTrackId))
            foreach (var item in page.Items) Write(JsonSerializer.Serialize(item));
            Write("opaque");
            foreach (var page in result.QuerySmfTrackOpaqueEventPages(descriptor.ExportTrackId))
            foreach (var item in page.Items) Write(JsonSerializer.Serialize(item));
        }
        Write("audio-metadata");
        foreach (var fragment in result.PureMidiAudioFragments) Write(JsonSerializer.Serialize(fragment));
        foreach (var preset in result.PureMidiPresetReferences) Write(JsonSerializer.Serialize(preset));
        foreach (var mode in result.ChannelModeSystemExclusiveEvents) Write(JsonSerializer.Serialize(mode));
        writer.Flush(); buffer.Flush();
        return stream.Finish();

        void Write(string item) { writer.Write(item); inspect?.Invoke(item); }
    }

    private static int CompareRender(CanonicalMidiRenderEvent x, CanonicalMidiRenderEvent y)
    {
        int c = x.Tick.CompareTo(y.Tick); if (c != 0) return c;
        c = x.Role.CompareTo(y.Role); if (c != 0) return c;
        c = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort); if (c != 0) return c;
        c = x.Message.ChannelNumber.CompareTo(y.Message.ChannelNumber); if (c != 0) return c;
        c = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder); if (c != 0) return c;
        if (x.Role == CanonicalEventRole.DirectMidi && y.Role == CanonicalEventRole.DirectMidi)
        { c = x.SmfEventOrder.CompareTo(y.SmfEventOrder); if (c != 0) return c; }
        c = x.StableOrder.CompareTo(y.StableOrder); if (c != 0) return c;
        c = x.TrackId.CompareTo(y.TrackId); if (c != 0) return c;
        c = x.StableObjectId.CompareTo(y.StableObjectId); if (c != 0) return c;
        return x.Message.PackedValue.CompareTo(y.Message.PackedValue);
    }

    private static void WriteEvent(BinaryWriter writer, CanonicalMidiEvent item)
    {
        writer.Write(item.Tick); writer.Write(item.ZeroBasedPort); writer.Write(item.ZeroBasedChannel);
        writer.Write(item.Message.Byte0); writer.Write(item.Message.Byte1); writer.Write(item.Message.Byte2);
        writer.Write(item.Message.Length);
        writer.Write((int)item.Role); writer.Write(item.StableOrder); writer.Write(item.SemanticTargetKey);
        writer.Write(item.SemanticGroup); WriteSource(writer, item.Source);
        writer.Write(item.ExportTrackId.Value); writer.Write(item.SmfTrackOrder); writer.Write(item.SmfEventOrder);
    }

    private static void WriteSource(BinaryWriter writer, SourceReference s)
    {
        writer.Write(s.TrackId.Value); writer.Write(s.SegmentId.Value); writer.Write(s.LogicalNoteId.Value);
        writer.Write(s.EventInstrumentId.Value); writer.Write(s.SubVoiceId.Value); writer.Write(s.SourceEventId.Value);
        writer.Write(s.Tick); writer.Write(s.LogicalParameterId.Value); writer.Write(s.LogicalParameterMappingId.Value);
        writer.Write(s.MappingStepId.Value); writer.Write(s.MappingFunctionId.Value); writer.Write(s.ValueCurveId.Value);
        writer.Write(s.EnvelopeId.Value); writer.Write((int)s.Origin); writer.Write(s.MidiChannelRootId.Value);
        writer.Write(s.PureMidiTrackId.Value); writer.Write(s.MidiSegmentId.Value); writer.Write(s.DirectMidiObjectId.Value);
        writer.Write(s.ExportTrackId.Value); writer.Write(s.EventInstrumentUsageId.Value);
    }

    private sealed class HashStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
