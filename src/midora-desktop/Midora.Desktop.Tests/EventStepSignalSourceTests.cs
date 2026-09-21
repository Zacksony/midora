using System.Diagnostics;
using System.Windows.Media;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class EventStepSignalSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void SourcesUseFormalOrderOwnTargetAndFrozenRevision()
    {
        using var project = new MidoraProject(480);
        var voice = EventInstrumentLibrary.Create(project, "Instrument").SubVoices[0];
        // IDs deliberately disagree with formal order.
        var last = TemplateEvent.ControlChange(project, 100, 10, 96);
        var first = TemplateEvent.ControlChange(project, 100, 10, 32);
        voice.Events.Add(first); voice.Events.Add(last);
        voice.Events.Add(TemplateEvent.ControlChange(project, 200, 11, 127));
        var old = EventStepSignalSources.Template(voice.Id, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(10), 768)!;
        Assert.Equal(768, old.EndTick);
        var summary = old.Summarize(100, 101);
        Assert.Equal(32 / 127d, summary.First.Value); Assert.Equal(96 / 127d, summary.Last.Value);
        Assert.True(old.TryGetBefore(300, out var previous)); Assert.Equal(96 / 127d, previous.Value);
        Assert.False(old.TryGetBefore(100, out _));
        last.Value = 64;
        var next = EventStepSignalSources.Template(voice.Id, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(10), 768)!;
        Assert.Equal(96 / 127d, old.Summarize(100, 101).Last.Value);
        Assert.Equal(64 / 127d, next.Summarize(100, 101).Last.Value);
        Assert.NotEqual(old.GetFingerprint(256, 512), next.GetFingerprint(256, 512));
        Assert.NotEqual(next.Identity, EventStepSignalSources.Template(voice.Id, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(11), 768)!.Identity);

        var midi = EventDisplayProjectionTests.CreateMidi(project);
        midi.ChannelEvents.Add(new(project) { Tick = 100, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = 96, Order = 9 });
        midi.ChannelEvents.Add(new(project) { Tick = 100, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = 32, Order = 2 });
        var direct = EventStepSignalSources.Direct(midi.Id, midi.ChannelEvents.CreateQuerySnapshot(), new(DirectMidiChannelEventKind.ControlChange, 10))!;
        Assert.Equal(96 / 127d, direct.Summarize(100, 101).Last.Value);
        var emptyOther = EventDisplayProjectionTests.CreateMidi(project);
        Assert.False(EventStepSignalSources.Direct(emptyOther.Id, emptyOther.ChannelEvents.CreateQuerySnapshot(), new(DirectMidiChannelEventKind.ControlChange, 10))!.TryGetBefore(1000, out _));
    }

    [Fact]
    public void CommandAndBankTargetsHaveGuidesAndLogicalUsesItsOwnRange()
    {
        foreach (int cc in Enumerable.Range(0, 128))
            Assert.True(EventStepSignalSources.Eligible(MidiValueTarget.ControlChange(cc)));
        Assert.True(EventStepSignalSources.Eligible(MidiValueTarget.Program));
        Assert.True(EventStepSignalSources.Eligible(MidiValueTarget.BankMsb));
        Assert.True(EventStepSignalSources.Eligible(MidiValueTarget.Rpn(10)));
        Assert.True(EventStepSignalSources.Eligible(MidiValueTarget.PitchBend));
        using var project = new MidoraProject(480);
        var lane = new LogicalParameterLane(project);
        lane.Points.Add(new(project, 10, -8, CurveInterpolation.Step));
        lane.Points.Add(new(project, 20, 8, CurveInterpolation.Step));
        var source = EventStepSignalSources.Logical(lane.Id, lane.Points.CreateQuerySnapshot(), -16, 16);
        Assert.Equal(.25, source.Summarize(10, 11).Last.Value);
        Assert.Equal(.75, source.Summarize(20, 21).Last.Value);
        Assert.False(source.TryGetBefore(10, out _));
    }

    [Fact]
    public void FormerlyExcludedLanesMatchPointCoordinatesWithoutInterpretingCommandsOrPayloads()
    {
        using var project = new MidoraProject(480);
        var midi = EventDisplayProjectionTests.CreateMidi(project);
        foreach (int cc in Enumerable.Range(0, 128))
            midi.ChannelEvents.Add(new(project) { Tick = 10, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = cc, Data2 = 80, Order = cc });
        midi.ChannelEvents.Add(new(project) { Tick = 20, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 100, Order = 200 });
        var snapshot = midi.ChannelEvents.CreateQuerySnapshot();
        foreach (int cc in Enumerable.Range(0, 128))
        {
            var source = EventStepSignalSources.Direct(midi.Id, snapshot, new(DirectMidiChannelEventKind.ControlChange, cc))!;
            Assert.Equal(1, source.Summarize(0, 20).Count);
            Assert.Equal(80 / 127d, source.Summarize(0, 20).Last.Value);
        }
        Assert.Equal(100 / 127d, EventStepSignalSources.Direct(midi.Id, snapshot, new(DirectMidiChannelEventKind.ProgramChange, 0))!.Summarize(0, 30).Last.Value);
        Assert.Null(EventStepSignalSources.Direct(midi.Id, snapshot, new(DirectMidiChannelEventKind.NoteOn, 60)));
        midi.OpaqueEvents.Add(new(project) { Tick = 42, Kind = OpaqueMidiEventKind.SystemExclusive, Payload = [0x7e, 0x7f, 9, 1, 0xf7] });
        var opaque = EventStepSignalSources.Opaque(midi.Id, midi.OpaqueEvents.CreateQuerySnapshot());
        Assert.False(opaque.TryGetBefore(42, out _));
        Assert.True(opaque.TryGetBefore(100, out var previous));
        Assert.Equal(1, previous.Value); Assert.Equal(42, previous.Tick);
        var vm = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, midi.Id), "MIDI", TimelineWorkspaceMode.Segment);
        try
        {
            vm.Rebuild(project, 1);
            vm.ActiveParameterLaneIndex = vm.ParameterLaneOptions.ToList().FindIndex(p => p.IsOpaqueMidiLane);
            vm.Rebuild(project, 1);
            Assert.Equal(opaque.Identity, vm.ParameterSnapshot!.StepSignalSource!.Identity);
        }
        finally { vm.CancelBackgroundPresentationWork(); }
        var voice = EventInstrumentLibrary.Create(project, "I").SubVoices[0];
        voice.Events.Add(TemplateEvent.Bank(project, 10, 80, 64));
        voice.Events.Add(TemplateEvent.Program(project, 20, 100));
        var template = voice.Events.CreateQuerySnapshot();
        foreach (var (target, value) in new[] { (MidiValueTarget.BankMsb, 80), (MidiValueTarget.BankLsb, 64), (MidiValueTarget.Program, 100) })
            Assert.Equal(value / 127d, EventStepSignalSources.Template(voice.Id, template, target, 768)!.Summarize(0, 30).Last.Value);
    }

    [Fact]
    public void OptInImportedSampleKeepsEventsAndMillionNoteSelectionIndependent()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_A4A_SAMPLE_MIDI");
        if (string.IsNullOrWhiteSpace(path)) return;
        var clock = Stopwatch.StartNew();
        using var project = MidiProjectImportService.ImportFile(path, "A4a sample probe").Project;
        output.WriteLine($"Import: {clock.Elapsed.TotalSeconds:F2} s");
        var track = Assert.Single(project.PureMidiTracks, t => t.Name == "MIDI Out #23");
        var segment = Assert.Single(track.Segments);
        var point = new DirectMidiChannelEvent(project)
            { Tick = 168960, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = 96, Order = long.MaxValue - 1 };
        segment.ChannelEvents.Add(point);
        var workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id), "Sample", TimelineWorkspaceMode.Segment);
        try
        {
            workspace.AddDirectMidiLaneTarget(new(DirectMidiChannelEventKind.ControlChange, 10));
            workspace.Rebuild(project, 1);
            Assert.Equal(-64, workspace.ActiveValueAxisMinimum); Assert.Equal(63, workspace.ActiveValueAxisMaximum);
            clock.Restart();
            var selection = workspace.Snapshot!.MaterializeRangeSelection(168816, 193536, 0, 128, 0, 1, false,
                new TimelineSelectionSnapshot(0, [], null), Midora.Desktop.Presentation.Interaction.WorkspaceSelectionRangeMode.Replace);
            Assert.Equal(1_382_908, selection.Ids.Count);
            output.WriteLine($"Marquee: {clock.Elapsed.TotalMilliseconds:F2} ms; selected {selection.Ids.Count}");
            using var program = Midora.Compiler.BatchEditExpressionProgram.Compile(new Dictionary<Midora.Compiler.BatchEditField, string?>
                { [Midora.Compiler.BatchEditField.PointValue] = "=p0*0.5", [Midora.Compiler.BatchEditField.Tick] = null });
            var edit = ProjectDomainEditCommands.BatchEditDirectMidiEventPoints(segment.Id, [point.Id], program).Prepare(project);
            for (int revision = 2; revision <= 3; revision++)
            {
                if (revision == 2) edit.Apply(project); else edit.Undo(project);
                workspace.Rebuild(project, revision);
                var source = workspace.ParameterSnapshot!.StepSignalSource!;
                Assert.Equal((revision == 2 ? 80 : 96) / 127d, source.Summarize(168960, 168961).Last.Value);
                clock.Restart();
                for (int i = 0; i < 16; i++)
                {
                    double scale = i % 2 == 0 ? .02 : .04;
                    long tile = (long)((168816 + i * 128) * scale / 256);
                    _ = TimelineStepSignalRasterizer.Fingerprint(source, scale, tile, 1);
                    _ = TimelineStepSignalRasterizer.Rasterize(source, scale, 200, tile, 0, 1, 1, Colors.Blue);
                    Assert.True(Process.GetCurrentProcess().WorkingSet64 < 8L * 1024 * 1024 * 1024);
                }
                output.WriteLine($"Revision {revision}: 16 signal requests {clock.Elapsed.TotalMilliseconds:F2} ms; WS {Process.GetCurrentProcess().WorkingSet64 / 1048576d:F1} MiB");
            }
        }
        finally { workspace.CancelBackgroundPresentationWork(); }
    }

    [Theory]
    [InlineData("direct")] [InlineData("template")] [InlineData("logical")] [InlineData("opaque")]
    public void MillionPointSignalColdAndWarmWorkStayBounded(string kind)
    {
        const int count = 1_000_000;
        using var project = new MidoraProject(480);
        TimelineStepSignalSource source;
        if (kind == "direct")
        {
            var midi = EventDisplayProjectionTests.CreateMidi(project);
            midi.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(project)
                { Tick = i, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = i % 128, Order = i }));
            source = EventStepSignalSources.Direct(midi.Id, midi.ChannelEvents.CreateQuerySnapshot(), new(DirectMidiChannelEventKind.ControlChange, 10))!;
        }
        else if (kind == "opaque")
        {
            var midi = EventDisplayProjectionTests.CreateMidi(project);
            midi.OpaqueEvents.AddRange(Enumerable.Range(0, count).Select(i => new OpaqueMidiEvent(project)
                { Tick = i, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [65], Order = i }));
            source = EventStepSignalSources.Opaque(midi.Id, midi.OpaqueEvents.CreateQuerySnapshot());
        }
        else if (kind == "template")
        {
            var voice = EventInstrumentLibrary.Create(project, "Instrument").SubVoices[0];
            voice.Events.AddRange(Enumerable.Range(0, count).Select(i => TemplateEvent.ControlChange(project, i, 10, i % 128)));
            source = EventStepSignalSources.Template(voice.Id, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(10), count)!;
        }
        else
        {
            var lane = new LogicalParameterLane(project);
            lane.Points.AddRange(Enumerable.Range(0, count).Select(i => new CurvePoint(project, i, i % 128, CurveInterpolation.Step)));
            source = EventStepSignalSources.Logical(lane.Id, lane.Points.CreateQuerySnapshot(), 0, 127);
        }
        TimelineStepSignalSource.ClearSharedCache();
        long startBytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        _ = TimelineStepSignalRasterizer.Fingerprint(source, 256d / count, 0, 1);
        _ = TimelineStepSignalRasterizer.Rasterize(source, 256d / count, 200, 0, 0, 1, 1, Colors.Blue);
        double cold = clock.Elapsed.TotalMilliseconds; long coldBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        var warmTimes = new List<double>();
        startBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            clock.Restart();
            _ = TimelineStepSignalRasterizer.Fingerprint(source, 256d / count, 0, 1);
            _ = TimelineStepSignalRasterizer.Rasterize(source, 256d / count, 200, 0, 0, 1, 1, Colors.Blue);
            warmTimes.Add(clock.Elapsed.TotalMilliseconds);
        }
        long warmBytes = (GC.GetAllocatedBytesForCurrentThread() - startBytes) / 10;
        output.WriteLine($"{kind}: cold {cold:F2} ms / {coldBytes / 1048576d:F2} MiB; warm p50 {warmTimes.Order().ElementAt(5):F2} ms, max {warmTimes.Max():F2} ms / {warmBytes / 1048576d:F2} MiB; WS {Process.GetCurrentProcess().WorkingSet64 / 1048576d:F1} MiB");
        Assert.InRange(coldBytes, 0, 32L * 1024 * 1024);
        Assert.InRange(warmBytes, 0, 2L * 1024 * 1024);
        Assert.True(cold < 30_000, "A single cold tile unexpectedly exceeded 30 seconds.");
        Assert.True(Process.GetCurrentProcess().WorkingSet64 < 8L * 1024 * 1024 * 1024, "Stop before the 9 GiB guard.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DenseSameTickUsesSharedFormalOrderDirectory(bool logical)
    {
        const int count = 100_000;
        using var project = new MidoraProject(480);
        TimelineStepSignalSource source;
        if (logical)
        {
            var lane = new LogicalParameterLane(project);
            lane.Points.AddRange(Enumerable.Range(0, count).Select(i => new CurvePoint(project, 100, i % 128, CurveInterpolation.Step)));
            source = EventStepSignalSources.Logical(lane.Id, lane.Points.CreateQuerySnapshot(), 0, 127);
        }
        else
        {
            var voice = EventInstrumentLibrary.Create(project, "Instrument").SubVoices[0];
            // Reversed collection order is intentionally different from ID order.
            var values = Enumerable.Range(0, count).Select(i => TemplateEvent.ControlChange(project, 100, 10, i % 128)).ToArray();
            voice.Events.AddRange(values.Reverse());
            source = EventStepSignalSources.Template(voice.Id, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(10), 1000)!;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew(); long before = GC.GetAllocatedBytesForCurrentThread();
        var summary = source.Summarize(100, 101, cancellation.Token);
        Assert.Equal(count, summary.Count);
        Assert.Equal(logical ? (count - 1) % 128 / 127d : 0, summary.Last.Value);
        output.WriteLine($"Same-tick {(logical ? "logical" : "template")}: {clock.Elapsed.TotalMilliseconds:F2} ms; {(GC.GetAllocatedBytesForCurrentThread() - before) / 1048576d:F2} MiB allocated");
    }
}
