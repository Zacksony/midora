using System.Text;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.OutputPlanning;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportEncodingBoundaryTests
{
    private const long M = StandardMidiFile.MaximumVariableLengthValue;

    [Theory]
    [InlineData(MidiExportMode.WholeProject, false)]
    [InlineData(MidiExportMode.WholeProject, true)]
    [InlineData(MidiExportMode.PerLogicalTrack, false)]
    [InlineData(MidiExportMode.PerLogicalTrack, true)]
    [InlineData(MidiExportMode.PerPort, false)]
    [InlineData(MidiExportMode.PerPort, true)]
    public async Task AllModesKeepNonzeroOriginAndPublishOneSeparatePaddingSummary(MidiExportMode mode, bool readme)
    {
        using TestDirectory directory = new();
        using MidoraProject project = new(32767);
        EventInstrument instrument = new(project) { Name = "Voice", RootNote = 60, TemplateLengthTicks = 192 };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        const long origin = 31;
        Segment segment = new(project) { ProjectStartTick = origin, LengthTicks = M + 1000 };
        segment.Notes.Add(new(project) { StartTick = M + 1, LengthTicks = 192, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        using MidoraCompiler compiler = new();
        var compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project, Mode = mode, Routing = MidiExportRoutingStrategy.Preserve,
            StartTick = origin, EndTick = origin + segment.LengthTicks, TreatWarningsAsErrors = true
        });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        long fingerprint = compilation.CompiledResult.Fingerprint, count = compilation.CompiledResult.TotalEventCount;
        var plan = mode switch
        {
            MidiExportMode.WholeProject => MidiExportOutputPlanner.PlanWholeProject(directory.Output, "Song", null, readme),
            MidiExportMode.PerLogicalTrack => MidiExportOutputPlanner.PlanLogicalTracks(directory.Output,
                [new LogicalTrackOutputName(track.Id.ToString(), 1, track.Name)], 1, readme),
            _ => MidiExportOutputPlanner.PlanPorts(directory.Output, [1], readme)
        };
        var request = new MidiExportTaskRequest
        {
            Compilation = compilation, OutputPlan = plan,
            Readme = readme ? MidiExportReadmeFactory.Create(project, compilation, plan,
                MidiExportRangeSource.Manual, "test", "test", "test", DateTimeOffset.UnixEpoch) : null
        };
        List<MidiExportProgress> progress = [];
        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(request,
            progress: new InlineProgress(progress.Add));
        Assert.Equal(MidiExportTaskStatus.Succeeded, result.Status);
        Assert.True(result.PaddingSummary.HasPadding);
        Assert.Equal(1, result.PaddingSummary.AffectedFileCount);
        Assert.Empty(result.ArtifactDiagnostics);
        long actualPadding = 0, paddedTracks = 0;
        foreach (string file in Directory.GetFiles(directory.Output, "*.mid"))
        {
            var parsed = StandardMidiFile.ParseType0Or1(await File.ReadAllBytesAsync(file));
            foreach (var midiTrack in parsed.Tracks)
            {
                Assert.Equal(segment.LengthTicks, midiTrack.EndTick);
                int padding = midiTrack.Events.Count(IsEmptyText);
                actualPadding += padding; if (padding > 0) paddedTracks++;
            }
            Assert.Contains(parsed.Tracks.SelectMany(t => t.Events), e => e.Tick == M + 1
                && e.Kind == StandardMidiFileEventKind.ChannelVoice && e.Message.MessageType == MidiMessageType.NoteOn);
        }
        Assert.Equal(actualPadding, result.PaddingSummary.PaddingEventCount);
        Assert.Equal(paddedTracks, result.PaddingSummary.PaddedTrackCount);
        Assert.Equal(fingerprint, compilation.CompiledResult.Fingerprint);
        Assert.Equal(count, compilation.CompiledResult.TotalEventCount);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Code.Contains("PADDING", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.Encoding is { DataByteCount: > 0 });
        Assert.Contains(progress, p => p.Phase == "Validating");
        Assert.Equal("Finalizing", progress[^1].Phase);
        if (readme)
        {
            string contents = await File.ReadAllTextAsync(Path.Combine(directory.Output, "README.md"));
            Assert.Equal(1, contents.Split("## SMF Timing Compatibility").Length - 1);
            Assert.Contains(result.PaddingSummary.Message, contents);
            Assert.Contains($"- Notes: {compilation.CompiledResult.TotalNoteOnEventCount}", contents);
        }
        else Assert.False(File.Exists(Path.Combine(directory.Output, "README.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedFixedAndAutoRootsHaveIdenticalResidentAndPagedEncoding(bool fixedRoute)
    {
        using TestDirectory folder = new();
        using MidoraProject resident = CreatePure(fixedRoute), paged = CreatePure(fixedRoute);
        using PureMidiContentPackWriter writer = new(Path.Combine(folder.Root, "pages.mpk"));
        foreach (MidiSegment segment in paged.PureMidiTracks.SelectMany(t => t.Segments))
        {
            foreach (var note in segment.Notes.EnumerateValues()) writer.AddNote(segment.Id, note);
            foreach (var e in segment.OpaqueEvents.EnumerateValues()) writer.AddOpaqueEvent(segment.Id, e);
        }
        using PureMidiContentPack pack = writer.Complete();
        foreach (MidiSegment segment in paged.PureMidiTracks.SelectMany(t => t.Segments))
        {
            segment.Notes.Clear(); segment.OpaqueEvents.Clear();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
        }
        using MidoraCompiler first = new(), second = new();
        foreach (long origin in new[] { 0L, 17L })
        {
            CompilationRequest request = new() { Purpose = CompilationPurpose.MidiExport, StartTick = origin };
            var a = first.CompileFull(resident, request); var b = second.CompileFull(paged, request);
            Assert.True(a.IsConsumable); Assert.True(b.IsConsumable); Assert.True(b.HasPagedEvents);
            var x = Encode(a); var y = Encode(b);
            Assert.True(x.Succeeded); Assert.True(y.Succeeded);
            byte[] bytes = x.FileBytes;
            Assert.Equal(bytes, y.FileBytes);
            Assert.Equal(x.WriteSummary, y.WriteSummary);
            Assert.True(x.WriteSummary.PaddingEventCount > 0);
            using MemoryStream repeat = new(); y.WriteTo(repeat);
            Assert.Equal(bytes, repeat.ToArray());
            Assert.Equal(x.WriteSummary, y.WriteSummary); // Writing again must not double-count.
            var tracks = StandardMidiFile.ParseType0Or1(bytes).Tracks;
            Assert.Equal(new[] { "Song", "Long", "Short", "Structure" }, tracks.Select(t =>
                Encoding.UTF8.GetString(t.Events.Single(e => e.Kind == StandardMidiFileEventKind.Meta
                    && e.Type == StandardMidiFile.TrackNameMetaType).Data.Span)));
            Assert.Equal(new[] { 3 * M + 40 - origin, 3 * M + 40 - origin, M + 100 - origin, 2 * M + 10 - origin },
                tracks.Select(t => t.EndTick));
            Assert.Contains(tracks[1].Events, e => e.Kind == StandardMidiFileEventKind.ChannelVoice
                && e.Message.MessageType == MidiMessageType.NoteOff && e.Message.Byte2 == 37);
            Assert.Contains(tracks[3].Events, e => e.Tick == M + 1 - origin && IsEmptyText(e));
            Assert.Single(tracks[3].Events, e => e.Kind == StandardMidiFileEventKind.SystemExclusive
                && e.Type == 0xf0 && e.Data.Span.SequenceEqual(new byte[] { 0x41, 0x10 }));
            Assert.Equal(1, tracks[3].Events.Count(e => e.Kind == StandardMidiFileEventKind.SystemExclusive && e.Type == 0xf7));
        }
    }

    [Fact]
    public async Task ExtremeTrackSizeFailsWithStructuredEncodingErrorAndNoArtifacts()
    {
        using TestDirectory directory = new();
        var compiled = Synthetic(long.MaxValue);
        var plan = MidiExportOutputPlanner.PlanWholeProject(directory.Output, "Huge", null, false);
        var result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = Wrap(compiled), OutputPlan = plan
        });
        Assert.Equal(MidiExportTaskStatus.Failed, result.Status);
        Assert.Equal(MidiExportOutputStage.Encoding, result.OutputFailure?.Stage);
        var diagnostic = Assert.Single(result.ArtifactDiagnostics).Diagnostic;
        Assert.Equal("MIDORA-MIDI-EXPORT-MTRK-SIZE", diagnostic.Code);
        Assert.Equal(MidiExportDiagnosticCategory.Encoding, diagnostic.Category);
        Assert.Contains("Huge.mid", diagnostic.Message);
        Assert.Contains("MTrk 1 (Song)", diagnostic.Message);
        Assert.Contains("4294967295", diagnostic.Message);
        Assert.Equal(long.MaxValue, diagnostic.Source.Tick);
        Assert.False(Directory.Exists(directory.Output));
        Assert.Empty(Directory.GetDirectories(directory.Root));
        Assert.False(result.PaddingSummary.HasPadding);
        Assert.True(compiled.IsConsumable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleFilesAggregateOnlyActualPaddingAndKeepOneSummary(bool readme)
    {
        using TestDirectory directory = new();
        using MidoraProject project = CreatePure(fixedRoute: true);
        MidiChannelRoot root = new(project) { Name = "Second Port", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0, FixedZeroBasedChannel = 1 };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = new(project) { Name = "Other", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 3 * M + 40 };
        segment.Notes.Add(new(project) { StartTick = M + 1, LengthTicks = 10, Key = 60, NoteOnVelocity = 100 });
        track.Segments.Add(segment); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using MidoraCompiler compiler = new();
        var compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        { Project = project, Mode = MidiExportMode.PerPort, Routing = MidiExportRoutingStrategy.Preserve });
        Assert.True(compilation.Succeeded);
        var plan = MidiExportOutputPlanner.PlanPorts(directory.Output, [1, 2], readme);
        var result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = compilation, OutputPlan = plan,
            Readme = readme ? MidiExportReadmeFactory.Create(project, compilation, plan,
                MidiExportRangeSource.NaturalContentEnd, "test", "test", "test", DateTimeOffset.UnixEpoch) : null
        });
        Assert.Equal(MidiExportTaskStatus.Succeeded, result.Status);
        Assert.Equal(2, result.PaddingSummary.AffectedFileCount);
        long texts = 0;
        foreach (string path in Directory.GetFiles(directory.Output, "*.mid"))
            texts += StandardMidiFile.ParseType0Or1(await File.ReadAllBytesAsync(path)).Tracks.Sum(t => t.Events.Count(IsEmptyText));
        Assert.Equal(texts - 1, result.PaddingSummary.PaddingEventCount); // One original empty Text.
        if (readme)
        {
            string text = await File.ReadAllTextAsync(Path.Combine(directory.Output, "README.md"));
            Assert.Equal(1, text.Split(result.PaddingSummary.Message).Length - 1);
        }
    }

    [Fact]
    public async Task CancellationInsideRealStagedPaddingLeavesNoOutput()
    {
        using TestDirectory directory = new();
        using CancellationTokenSource cancel = new();
        var plan = MidiExportOutputPlanner.PlanWholeProject(directory.Output, "Cancel", null, false);
        long bytes = 0;
        MidiExportPreparedArtifact artifact = new("whole-project-midi", (stream, token) =>
        {
            using var intercept = new CancelPaddingStream(stream, count =>
            {
                bytes += count;
                if (count > 4096) cancel.Cancel();
            });
            StandardMidiFile.WriteType1(intercept, 192, [new(100_000 * M, [])], token);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new MidiExportOutputTransaction()
            .PublishAsync(plan, [artifact], false, cancel.Token));
        Assert.InRange(bytes, 4097, 64 * 1024);
        Assert.Empty(Directory.GetDirectories(directory.Root));
    }

    [Fact]
    public async Task DeferredPagedCanonicalFailureIsNotMisreportedAsAnIoFailure()
    {
        using TestDirectory directory = new();
        var descriptor = new CanonicalSmfTrackDescriptor(new(1), CanonicalSmfTrackKind.PureMidiTrack,
            "Bad Page", 0, 0, 100, new(2), new(1));
        var compiled = Synthetic(100, [descriptor], new FailingPageSource());
        Assert.True(compiled.HasPagedEvents);
        var result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = Wrap(compiled),
            OutputPlan = MidiExportOutputPlanner.PlanWholeProject(directory.Output, "Paged", null, false)
        });
        Assert.Equal(MidiExportTaskStatus.Failed, result.Status);
        Assert.Equal(MidiExportOutputStage.Encoding, result.OutputFailure?.Stage);
        Assert.Contains("injected deferred encoding failure", Assert.Single(result.ArtifactDiagnostics).Diagnostic.Message);
        Assert.False(Directory.Exists(directory.Output));
        Assert.Empty(Directory.GetDirectories(directory.Root));
    }

    [Fact]
    public void TrackCountFailsDuringPreparationWithoutEnumeratingPages()
    {
        var descriptors = Enumerable.Range(1, ushort.MaxValue).Select(i =>
            new CanonicalSmfTrackDescriptor(new(i), CanonicalSmfTrackKind.PureMidiTrack, "Track", 0, 0, 0,
                new(100_000), new(i))).ToArray();
        var result = Encode(Synthetic(0, descriptors, new FailingPageSource()));
        Assert.False(result.Succeeded);
        Assert.Equal("MIDORA-MIDI-EXPORT-TRACK-COUNT", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredIoOrUnexpectedFailureAlwaysCleansStaging(bool io)
    {
        using TestDirectory directory = new();
        var plan = MidiExportOutputPlanner.PlanWholeProject(directory.Output, "Failure", null, false);
        MidiExportPreparedArtifact artifact = new("whole-project-midi", (stream, token) =>
        {
            stream.WriteByte(1);
            if (io) throw new IOException("injected disk write failure");
            throw new InvalidOperationException("injected deferred source failure");
        });
        var failure = await Assert.ThrowsAsync<MidiExportOutputException>(() =>
            new MidiExportOutputTransaction().PublishAsync(plan, [artifact], false));
        Assert.Equal(MidiExportOutputStage.Staging, failure.Stage);
        Assert.Null(failure.EncodingDiagnostic);
        Assert.Contains(io ? "injected disk write failure" : "injected deferred source failure", failure.Message);
        Assert.Empty(Directory.GetDirectories(directory.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastFileEncodingFailureOrCancellationPreservesAllExistingTargets(bool cancel)
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(directory.Output);
        var names = new[] { "Port 01.mid", "Port 02.mid", "README.md" };
        foreach (string name in names) await File.WriteAllTextAsync(Path.Combine(directory.Output, name), "original " + name);
        var plan = MidiExportOutputPlanner.PlanPorts(directory.Output, [1, 2], true);
        using CancellationTokenSource cancellation = new();
        var first = new MidiExportPreparedArtifact("port:01", (stream, token) =>
            StandardMidiFile.WriteType1(stream, 192, [new(M + 1, [])], token));
        var last = new MidiExportPreparedArtifact("port:02", (stream, token) =>
        {
            if (cancel) cancellation.Cancel();
            StandardMidiFile.WriteType1(stream, 192, [new(long.MaxValue, [])], token);
        });
        var readme = new MidiExportPreparedArtifact("readme", "not published"u8);
        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(() => new MidiExportOutputTransaction()
                .PublishAsync(plan, [readme, first, last], true, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<MidiExportOutputException>(() => new MidiExportOutputTransaction()
                .PublishAsync(plan, [readme, first, last], true, cancellation.Token));
            Assert.Equal(MidiExportOutputStage.Encoding, error.Stage);
            Assert.Equal("port:02", error.EncodingDiagnostic?.SourceKey);
        }
        foreach (string name in names) Assert.Equal("original " + name, await File.ReadAllTextAsync(Path.Combine(directory.Output, name)));
        Assert.Equal([directory.Output], Directory.GetDirectories(directory.Root));
    }

    private static MidoraProject CreatePure(bool fixedRoute)
    {
        MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Root", RoutingMode = fixedRoute ? MidiChannelRootRoutingMode.Fixed : MidiChannelRootRoutingMode.Auto,
            FixedZeroBasedPort = fixedRoute ? (byte)1 : (byte)0, FixedZeroBasedChannel = fixedRoute ? (byte)9 : (byte)0 };
        p.MidiChannelRoots.Add(root);
        string[] names = ["Long", "Short", "Structure"];
        long[] lengths = [3 * M + 40, M + 100, 2 * M + 10];
        for (int i = 0; i < 3; i++)
        {
            PureMidiTrack track = new(p) { Name = names[i], MidiChannelRootId = root.Id };
            MidiSegment segment = new(p) { LengthTicks = lengths[i] };
            if (i < 2) segment.Notes.Add(new(p) { StartTick = 17, LengthTicks = M + 50, Key = 60,
                NoteOnVelocity = 100, NoteOffVelocity = 37, NoteOnOrder = 1, NoteOffOrder = 2 });
            else
            {
                segment.OpaqueEvents.Add(new(p) { Tick = M + 1, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [], Order = 1 });
                segment.OpaqueEvents.Add(new(p) { Tick = M + 2, Kind = OpaqueMidiEventKind.SystemExclusive, Payload = new byte[] { 0x41, 0x10 }, Order = 2 });
                segment.OpaqueEvents.Add(new(p) { Tick = 2 * M + 3, Kind = OpaqueMidiEventKind.SystemExclusiveContinuation,
                    Payload = new byte[] { 0x40, 0xf7 }, Order = 3 });
            }
            track.Segments.Add(segment); p.PureMidiTracks.Add(track);
            p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        }
        return p;
    }

    private static bool IsEmptyText(ParsedStandardMidiFileEvent value) => value.Kind == StandardMidiFileEventKind.Meta
        && value.Type == StandardMidiFile.TextMetaType && value.Data.IsEmpty;
    private static MidiExportEncodingResult Encode(CanonicalCompiledResult result) => CanonicalMidiFileExporter.EncodeWholeProject(new()
        { CompiledResult = result, ConductorTrackName = "Song", LogicalTracks = [] });
    private static MidiExportCompilationResult Wrap(CanonicalCompiledResult result) =>
        new(MidiExportMode.WholeProject, MidiExportRoutingStrategy.Preserve, "Song", result, [], [], []);
    private static CanonicalCompiledResult Synthetic(long endTick, CanonicalSmfTrackDescriptor[]? descriptors = null,
        ICanonicalMidiEventPageSource? pages = null) => new(192,
            new(new CompilationRequest { Purpose = CompilationPurpose.MidiExport, EndTick = endTick },
                endTick, CompilationEndTickSource.ExplicitRequest), [], new([new(0, 120m)], [], [], [], [], null),
            [], [], false, true, null, 123, new(0, 0, 0, 0), smfTracks: descriptors, pagedEventSource: pages);

    private sealed class FailingPageSource : ICanonicalMidiEventPageSource, ICanonicalSmfTrackPageSource
    {
        public long EventCount => 1;
        public long NoteOnEventCount => 0;
        public string ContentFingerprint => "fault-test";
        public IEnumerable<CanonicalMidiEventPage> QueryPages(long start, long end, bool state, CancellationToken token = default) => [];
        public IEnumerable<CanonicalSmfTrackChannelEventPage> QueryTrackChannelEventPages(MidoraId id, long start, long end,
            CancellationToken token = default) => throw new MidoraMidiException("injected deferred encoding failure");
        public IEnumerable<CanonicalOpaqueMidiEventPage> QueryTrackOpaqueEventPages(MidoraId id, long start, long end,
            CancellationToken token = default) => [];
    }
    private sealed class InlineProgress(Action<MidiExportProgress> callback) : IProgress<MidiExportProgress>
    { public void Report(MidiExportProgress value) => callback(value); }
    private sealed class CancelPaddingStream(Stream target, Action<int> afterWrite) : Stream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => target.Position = value; }
        public override void Write(ReadOnlySpan<byte> buffer) { target.Write(buffer); afterWrite(buffer.Length); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value) { target.WriteByte(value); afterWrite(1); }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => target.Seek(offset, origin);
        public override void SetLength(long value) => target.SetLength(value);
    }
    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory() => Directory.CreateDirectory(Root);
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, ".tmp", "smf-encoding-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "export");
        public void Dispose() => Directory.Delete(Root, true);
    }
}
