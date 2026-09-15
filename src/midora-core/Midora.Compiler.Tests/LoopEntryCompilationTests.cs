using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LoopEntryCompilationTests
{
    [Theory]
    [InlineData(191)]
    [InlineData(192)]
    [InlineData(193)]
    [InlineData(383)]
    [InlineData(384)]
    [InlineData(385)]
    [InlineData(575)]
    [InlineData(576)]
    [InlineData(767)]
    [InlineData(768)]
    [InlineData(769)]
    [InlineData(960)]
    public void LoopRepeatsWhenGatePassesLoopEndNotTemplateEnd(long gate)
    {
        var fixture = Create(gate);
        using var project = fixture.Project;
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        var expected = Enumerable.Range(0, (int)((gate + 95) / 96))
            .Select(i => 192L + i * 96)
            .Where(tick => tick < gate)
            .Select(tick => (tick, (tick - 192) % 192 == 0 ? -7701 : 8191))
            .ToArray();
        Assert.Equal(expected, PitchBends(result));
        Assert.Equal(0, Assert.Single(result.Events.ToArray(), e => e.Role == CanonicalEventRole.NoteOn).Tick);
        Assert.Equal(gate <= 768 ? gate : gate + 384,
            Assert.Single(result.Events.ToArray(), e => e.Role == CanonicalEventRole.NoteOff).Tick);
        Assert.DoesNotContain(result.Events.ToArray(), e => e.Tick < fixture.Segment.ProjectStartTick + fixture.Segment.LengthTicks
            && e.Message.MessageType == MidiMessageType.ControlChange && e.Message.Byte1 == 120);
    }

    [Fact]
    public void ChangingGateAcrossLoopAndTemplateBoundariesMatchesFreshFullCompile()
    {
        var fixture = Create(384);
        using var project = fixture.Project;
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult previous = compiler.CompileFull(project);
        foreach (long gate in new long[] { 576, 768, 769, 576, 384, 385 })
        {
            fixture.Note.LengthTicks = gate;
            ProjectChangeSet changes = new();
            changes.TrackIds.Add(fixture.Track.Id);
            CanonicalCompiledResult incremental = compiler.CompileIncremental(project, changes);
            using MidoraCompiler fresh = new();
            CanonicalCompiledResult full = fresh.CompileFull(project);
            Assert.True(incremental.IsConsumable, Diagnostics(incremental));
            Assert.NotEqual(previous.Fingerprint, incremental.Fingerprint);
            AssertEquivalent(full, incremental);
            AssertEquivalent(full, fresh.CompileFull(project));
            Assert.Equal(gate > 768 ? 7 : gate > 384 ? (gate + 95) / 96 - 2 : 2,
                PitchBends(incremental).Length);
            previous = incremental;
        }
    }

    [Theory]
    [InlineData(ShortNoteLifecycle.CutAtNoteOff, 576)]
    [InlineData(ShortNoteLifecycle.Tail, 960)]
    public void ShortLoopSustainsCoveringNoteAndPlaysPrefixOnlyOnce(
        ShortNoteLifecycle lifecycle, long expectedOff)
    {
        var fixture = Create(576);
        using var project = fixture.Project;
        fixture.Instrument.ShortLifecycle = lifecycle;
        fixture.Voice.Events.Single(e => e.Kind == TemplateEventKind.Note).LengthTicks = 400;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 100, 1, 77));
        fixture.Voice.Events.Add(TemplateEvent.Note(project, 192, 48, 62, 100));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        Assert.Equal(new long[] { 0 }, NoteTicks(result, CanonicalEventRole.NoteOn, 60));
        Assert.Equal(new[] { expectedOff }, NoteTicks(result, CanonicalEventRole.NoteOff, 60));
        Assert.Equal(new long[] { 192, 384 }, NoteTicks(result, CanonicalEventRole.NoteOn, 62));
        Assert.Equal(new long[] { 240, 432 }, NoteTicks(result, CanonicalEventRole.NoteOff, 62));
        Assert.Equal(new long[] { 100 }, ControllerEvents(result, 1).Where(e => e.Message.Byte2 == 77)
            .Select(e => e.Tick));
    }

    [Theory]
    [InlineData(576, 4096, 960)]
    [InlineData(575, 4096, 959)]
    [InlineData(576, 800, 800)]
    public void ShortTailStartsAtGateEndAndKeepsTheEntireShiftedTail(
        long gate, long segmentLength, long expectedOff)
    {
        var fixture = Create(gate, segmentLength);
        using var project = fixture.Project;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.Tail;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 384, 1, 70));
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 767, 1, 80));
        fixture.Voice.Events.Add(TemplateEvent.Note(project, 700, 20, 62, 100));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        Assert.Equal(new[] { expectedOff }, NoteTicks(result, CanonicalEventRole.NoteOff, 60));
        Assert.Empty(NoteTicks(result, CanonicalEventRole.NoteOn, 62));
        Assert.Equal(new[] { gate }, ControllerEvents(result, 1).Where(e => e.Message.Byte2 == 70)
            .Select(e => e.Tick));
        Assert.Equal(segmentLength > gate + 383 ? new[] { gate + 383 } : [],
            ControllerEvents(result, 1).Where(e => e.Message.Byte2 == 80).Select(e => e.Tick));
        Assert.All(PitchBends(result), e => Assert.True(e.Tick < gate));
    }

    [Fact]
    public void ShortOneShotIgnoresGateAndDoesNotLoop()
    {
        var fixture = Create(576);
        using var project = fixture.Project;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.OneShot;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 700, 1, 77));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        Assert.Equal(new[] { (192L, -7701), (288L, 8191) }, PitchBends(result));
        Assert.Equal(new long[] { 768 }, NoteTicks(result, CanonicalEventRole.NoteOff, 60));
        Assert.Contains(ControllerEvents(result, 1), e => e.Tick == 700 && e.Message.Byte2 == 77);
    }

    [Theory]
    [InlineData(576, 576)]
    [InlineData(768, 768)]
    [InlineData(960, 768)]
    public void EndAtTemplateStillCapsLongNotesButDoesNotDisableShortLoop(long gate, long expectedOff)
    {
        var fixture = Create(gate);
        using var project = fixture.Project;
        fixture.Instrument.LongLifecycle = LongNoteLifecycle.EndAtTemplate;
        fixture.Voice.Events.Single(e => e.Kind == TemplateEventKind.Note).LengthTicks = 400;
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        Assert.Equal(new[] { expectedOff }, NoteTicks(result, CanonicalEventRole.NoteOff, 60));
        Assert.Contains(PitchBends(result), e => e.Tick == 384 && e.Value == -7701);
        Assert.All(PitchBends(result), e => Assert.True(e.Tick < expectedOff));
    }

    [Fact]
    public void LoopRepeatsCurvesAndTemplateContextWhileEnvelopeUsesElapsedInstanceTime()
    {
        var fixture = Create(576);
        using var project = fixture.Project;
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(project, 192, 10));
        curve.Points.Add(new CurvePoint(project, 288, 100));
        fixture.Voice.Curves.Add(curve);
        CSharpMappingFunction function = new(project)
        {
            Name = "template tick probe",
            Body = "context.TemplateTick % 128"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.TemplateTick));
        fixture.Instrument.MappingFunctions.Add(function);
        foreach (long tick in new long[] { 192, 288 })
        {
            TemplateEvent controller = TemplateEvent.ControlChange(project, tick, 2, 0);
            fixture.Voice.Events.Add(controller);
            if (tick == 192)
            {
                controller.ValueMappings.Add(new ValueMappingStep(project)
                {
                    Operation = MappingOperation.CustomCSharp,
                    MappingFunctionId = function.Id
                });
            }
        }
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "probe", Type = LogicalParameterType.Double, Minimum = 0, Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id, SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(3)
        };
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Operation = MappingOperation.CustomCSharp, MappingFunctionId = function.Id
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        InstrumentEnvelope envelope = new(project)
        {
            StartValue = 0, PeakValue = 1, AttackTicks = 512, SustainValue = 1
        };
        fixture.Instrument.Envelopes.Add(envelope);
        foreach (var point in new[] { (192L, 64), (288L, 127) })
        {
            TemplateEvent expression = TemplateEvent.ControlChange(project, point.Item1, 11, point.Item2);
            fixture.Voice.Events.Add(expression);
            if (point.Item1 == 192)
            {
                expression.ValueMappings.Add(new ValueMappingStep(project)
                {
                    Source = MappingSource.Envelope, EnvelopeId = envelope.Id, Operation = MappingOperation.Multiply
                });
            }
        }
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        foreach (long tick in new long[] { 192, 384 })
        {
            Assert.Contains(ControllerEvents(result, 1), e => e.Tick == tick && e.Message.Byte2 == 10);
            Assert.Contains(ControllerEvents(result, 2), e => e.Tick == tick && e.Message.Byte2 == 64);
            Assert.Contains(ControllerEvents(result, 3), e => e.Tick == tick && e.Message.Byte2 == 64);
        }
        Assert.Contains(ControllerEvents(result, 11), e => e.Tick == 192 && e.Message.Byte2 == 24);
        Assert.Contains(ControllerEvents(result, 11), e => e.Tick == 384 && e.Message.Byte2 == 48);
    }

    [Theory]
    [InlineData(4096, 672)]
    [InlineData(600, 600)]
    public void ShortCutReleasesAfterGateWithoutNewLoopEventsOrPrematureReset(long segmentLength, long expectedOff)
    {
        var fixture = Create(576, segmentLength);
        using var project = fixture.Project;
        InstrumentEnvelope envelope = new(project)
        {
            StartValue = 1, PeakValue = 1, SustainValue = 1, ReleaseTicks = 96, EndValue = 0
        };
        fixture.Instrument.Envelopes.Add(envelope);
        TemplateEvent expression = TemplateEvent.ControlChange(project, 0, 11, 127);
        expression.ValueMappings.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.Envelope, EnvelopeId = envelope.Id, Operation = MappingOperation.Multiply
        });
        fixture.Voice.Events.Add(expression);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 384, 1, 77));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project);

        Assert.True(result.IsConsumable, Diagnostics(result));
        Assert.Equal(new[] { expectedOff }, NoteTicks(result, CanonicalEventRole.NoteOff, 60));
        Assert.Equal(new[] { (192L, -7701), (288L, 8191), (384L, -7701), (480L, 8191) }, PitchBends(result));
        Assert.DoesNotContain(ControllerEvents(result, 1), e => e.Message.Byte2 == 77);
        Assert.Contains(ControllerEvents(result, 11), e => e.Tick > 576 && e.Tick < expectedOff
            && e.Message.Byte2 < 127);
        Assert.DoesNotContain(result.Events.ToArray(), e => e.Tick < segmentLength
            && e.Message.MessageType == MidiMessageType.ControlChange && e.Message.Byte1 == 120);
    }

    [Theory]
    [InlineData(576)]
    [InlineData(768)]
    [InlineData(769)]
    public void ProjectAndFixedOrHeldPreviewUseTheSameLoopClock(long gate)
    {
        var fixture = Create(gate);
        using var project = fixture.Project;
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult full = compiler.CompileFull(project);
        PreviewCompiler preview = new();
        CanonicalCompiledResult fixedGate = preview.CompileEventInstrument(project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, GateLengthTicks: gate));
        CanonicalCompiledResult heldEnd = preview.CompileHeldEventInstrumentGateEnd(project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id), gate, gate);
        CanonicalCompiledResult heldOpen = preview.CompileHeldEventInstrumentGateOpen(project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id), gate);

        Assert.True(fixedGate.IsConsumable, Diagnostics(fixedGate));
        Assert.True(heldEnd.IsConsumable, Diagnostics(heldEnd));
        Assert.True(heldOpen.IsConsumable, Diagnostics(heldOpen));
        Assert.Equal(PitchBends(full), PitchBends(fixedGate));
        Assert.Equal(PitchBends(full), PitchBends(heldEnd));
        Assert.Equal(PitchBends(full), PitchBends(heldOpen));
        Assert.Equal(NoteTicks(full, CanonicalEventRole.NoteOff, 60), NoteTicks(fixedGate, CanonicalEventRole.NoteOff, 60));
        Assert.Equal(NoteTicks(full, CanonicalEventRole.NoteOff, 60), NoteTicks(heldEnd, CanonicalEventRole.NoteOff, 60));
    }

    private static void AssertEquivalent(CanonicalCompiledResult expected, CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
    }

    private static IEnumerable<CanonicalMidiEvent> ControllerEvents(CanonicalCompiledResult result, int controller) =>
        result.Events.ToArray().Where(e => e.Message.MessageType == MidiMessageType.ControlChange
            && e.Message.Byte1 == controller && e.Role != CanonicalEventRole.Reset);

    private static long[] NoteTicks(CanonicalCompiledResult result, CanonicalEventRole role, int key) =>
        result.Events.ToArray().Where(e => e.Role == role && e.Message.Byte1 == key).Select(e => e.Tick).ToArray();

    private static (MidoraProject Project, LogicalTrack Track, Segment Segment,
        EventInstrument Instrument, SubVoice Voice, LogicalNote Note) Create(long gate, long segmentLength = 4096)
    {
        var fixture = CompilerTestProject.Create(segmentLength: segmentLength);
        fixture.Instrument.TemplateLengthTicks = 768;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.PreRollTicks = 0;
        fixture.Instrument.LoopStartTick = 192;
        fixture.Instrument.LoopEndTick = 384;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Instrument.LongLifecycle = LongNoteLifecycle.HoldLastState;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 768, 60, 100));
        fixture.Voice.Events.Add(new(fixture.Project) { Tick = 192, Kind = TemplateEventKind.PitchBend, Value = -7701 });
        fixture.Voice.Events.Add(new(fixture.Project) { Tick = 288, Kind = TemplateEventKind.PitchBend, Value = 8191 });
        LogicalNote note = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, gate);
        note.Velocity = 100;
        return (fixture.Project, fixture.Track, fixture.Segment, fixture.Instrument, fixture.Voice, note);
    }

    private static (long Tick, int Value)[] PitchBends(CanonicalCompiledResult result) => result.Events.ToArray()
        .Where(e => e.Role == CanonicalEventRole.PitchBend && e.Source.Origin == SourceOrigin.TemplateEvent)
        .Select(e => (e.Tick, ((e.Message.Byte2 << 7) | e.Message.Byte1) - 8192)).ToArray();

    private static string Diagnostics(CanonicalCompiledResult result) => string.Join(" | ",
        result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));
}
