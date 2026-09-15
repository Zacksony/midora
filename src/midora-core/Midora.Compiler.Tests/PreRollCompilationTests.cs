using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class PreRollCompilationTests
{
    [Fact]
    public void ZeroPreRollPreservesTheExistingLogicalNoteProjection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        Assert.Equal(0, fixture.Instrument.PreRollTicks);
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 40,
            lengthTicks: 80,
            note: 60,
            velocity: 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 160);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        CanonicalMidiEvent noteOn = Assert.Single(
            result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(240, noteOn.Tick);
    }

    [Fact]
    public void PreRollIsAppliedInSegmentContentCoordinatesBeforeProjectProjection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Segment.ProjectStartTick = 1_000;
        fixture.Segment.ContentOffsetTick = 200;
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 40,
            lengthTicks: 80,
            note: 60,
            velocity: 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 300,
            length: 160);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        CanonicalMidiEvent noteOn = Assert.Single(
            result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(1_040, noteOn.Tick);
    }

    [Fact]
    public void TemplateEventsUseAdvancedOriginWhileGateEndAndMappingGateLengthStayLogical()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        TemplateEvent control = TemplateEvent.ControlChange(fixture.Project, 10, 11, 1);
        control.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.GateLength,
            Operation = MappingOperation.Override
        });
        fixture.Voice.Events.Add(control);
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 0,
            lengthTicks: 480,
            note: 60,
            velocity: 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 100 && value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 110
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 120);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 320 && value.Role == CanonicalEventRole.NoteOff);
    }

    [Fact]
    public void LifecycleClassificationStillUsesLogicalGateLength()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.TemplateLengthTicks = 200;
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Instrument.LongLifecycle = LongNoteLifecycle.EndAtTemplate;
        InstrumentEnvelope envelope = new(fixture.Project)
        {
            ReleaseTicks = 100,
            EndValue = 0
        };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "release probe",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 0,
            lengthTicks: 200,
            note: 60,
            velocity: 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 100 && value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 370
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 63);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 419
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 0);
    }

    [Fact]
    public void InitialStateAndLogicalParametersUseTheAdvancedActualTick()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.InitialState.Program = 12;
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11),
            Steps =
            {
                new ValueMappingStep(fixture.Project)
                {
                    Operation = MappingOperation.Remap,
                    Source = MappingSource.LogicalParameter,
                    LogicalParameterId = parameter.Id,
                    SourceMinimum = 0,
                    SourceMaximum = 1,
                    TargetMinimum = 0,
                    TargetMaximum = 127
                }
            }
        };
        mapping.TargetSettings.Rounding = MappingRounding.Floor;
        fixture.Instrument.ParameterMappings.Add(mapping);
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = parameter.Id };
        lane.Points.Add(new(fixture.Project, 0, 0.25, CurveInterpolation.Step));
        lane.Points.Add(new(fixture.Project, 150, 0.75, CurveInterpolation.Step));
        fixture.Segment.ParameterLanes.Add(lane);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 100, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 100
            && value.Message.MessageType == MidiMessageType.ProgramChange
            && value.Message.Byte1 == 12
            && value.Role == CanonicalEventRole.InitialState);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 100
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 31
            && value.Role == CanonicalEventRole.LogicalParameter);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 150
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 95
            && value.Role == CanonicalEventRole.LogicalParameter);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(481)]
    public void PreRollMustBeWithinTheEventInstrumentTemplate(long preRollTicks)
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.PreRollTicks = preRollTicks;

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1217");
        Assert.Empty(result.Events.ToArray());
        Assert.Empty(result.Allocations.ToArray());
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(200, 250)]
    public void PreRollOriginMustNotCrossTheActiveSegmentLeftBoundary(
        long contentOffsetTick,
        long noteStartTick)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Segment.ProjectStartTick = 1_000;
        fixture.Segment.ContentOffsetTick = contentOffsetTick;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 100, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: noteStartTick,
            length: 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA1321");
        Assert.Equal(fixture.Track.Id, diagnostic.Source.TrackId);
        Assert.Equal(fixture.Segment.Id, diagnostic.Source.SegmentId);
        Assert.Empty(result.Events.ToArray());
        Assert.Empty(result.Allocations.ToArray());
    }

    [Fact]
    public void PreRollMayLandExactlyAtProjectAndSegmentTickZero()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = fixture.Instrument.TemplateLengthTicks;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 100, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 480,
            length: 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 0 && value.Role == CanonicalEventRole.NoteOn);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick < 0);
    }

    [Fact]
    public void CutPreviousUsesTheAdvancedOriginAsItsConflictBoundary()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutPrevious;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 50);
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 300,
            length: 100);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Equal(
            [100L, 200L],
            result.Events.ToArray()
                .Where(value => value.Role == CanonicalEventRole.NoteOn)
                .Select(value => value.Tick)
                .ToArray());
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 200 && value.Role == CanonicalEventRole.NoteOff);
    }

    [Fact]
    public void SharedUsageAcrossTracksUsesAdvancedOriginsInOneOverlapDomain()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.Warn;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 50);
        LogicalTrack secondTrack = new(fixture.Project)
        {
            Name = "Second",
            EventInstrumentUsageId = fixture.Track.EventInstrumentUsageId
        };
        Segment secondSegment = new(fixture.Project)
        {
            ProjectStartTick = 0,
            LengthTicks = 600
        };
        secondTrack.Segments.Add(secondSegment);
        fixture.Project.Tracks.Add(secondTrack);
        fixture.Project.ArrangementTracks.Add(new(
            ArrangementTrackKind.LogicalTrack,
            secondTrack.Id));
        CompilerTestProject.RegisterSegment(fixture.Project, secondSegment);
        CompilerTestProject.AddNote(
            secondSegment,
            fixture.Instrument,
            start: 300,
            length: 100);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        CompilerDiagnostic overlap = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA2201");
        Assert.Equal(secondTrack.Id, overlap.Source.TrackId);
        Assert.Equal(1, result.Statistics.PeakChannelUnitCount);
        Assert.Single(result.Allocations.ToArray().Select(value => value.InstanceGroupId).Distinct());
    }

    [Fact]
    public void CutPreviousTruncatesAnEarlierInstanceAcrossSharedUsageTracks()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutPrevious;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        LogicalNote first = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 300);
        (LogicalTrack secondTrack, Segment secondSegment) = AddSharedUsageTrack(
            fixture.Project,
            fixture.Track,
            segmentLength: 600);
        CompilerTestProject.AddNote(
            secondSegment,
            fixture.Instrument,
            start: 300,
            length: 200);

        MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project,
            new ProjectChangeSet());
        CanonicalCompiledResult fresh = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Equal(
            [100L, 200L],
            result.Events.ToArray()
                .Where(value => value.Role == CanonicalEventRole.NoteOn)
                .Select(value => value.Tick)
                .ToArray());
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 200
            && value.Role == CanonicalEventRole.NoteOff
            && value.Source.LogicalNoteId == first.Id);
        Assert.Contains(result.Allocations.ToArray(), value => value.TrackId == secondTrack.Id);
        Assert.Equal(fresh.Fingerprint, incremental.Fingerprint);
        Assert.Equal(fresh.Events.ToArray(), incremental.Events.ToArray());
        Assert.Equal(fresh.Allocations.ToArray(), incremental.Allocations.ToArray());
        Assert.Equal(fresh.Diagnostics.ToArray(), incremental.Diagnostics.ToArray());
    }

    [Fact]
    public void CutPreviousInsidePrefixUsesZeroMappingGateLengthForTheTruncatedInstance()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutPrevious;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "gate length",
            Body = "context.GateLength"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.GateLength));
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent controller = TemplateEvent.ControlChange(
            fixture.Project,
            tick: 0,
            controller: 4,
            value: 1);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        controller.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(controller);
        LogicalNote first = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 300);
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 250,
            length: 100);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        CanonicalMidiEvent truncated = Assert.Single(
            result.Events.ToArray(),
            value => value.Source.LogicalNoteId == first.Id
                && value.Role == CanonicalEventRole.ControlChange
                && value.Message.Byte1 == 4);
        Assert.Equal(100, truncated.Tick);
        Assert.Equal(0, truncated.Message.Byte2);
    }

    [Fact]
    public void CutNewRejectsTheLaterInstanceAcrossSharedUsageTracksAndRemainsIncrementalEquivalent()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutNewRejectNew;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        LogicalNote first = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 300);
        (LogicalTrack secondTrack, Segment secondSegment) = AddSharedUsageTrack(
            fixture.Project,
            fixture.Track,
            segmentLength: 600);
        LogicalNote rejected = CompilerTestProject.AddNote(
            secondSegment,
            fixture.Instrument,
            start: 300,
            length: 200);
        MidoraCompiler compiler = new();

        CanonicalCompiledResult full = compiler.CompileFull(fixture.Project);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project,
            new ProjectChangeSet());
        CanonicalCompiledResult fresh = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(full.IsConsumable, FormatDiagnostics(full));
        CanonicalMidiEvent noteOn = Assert.Single(
            full.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(first.Id, noteOn.Source.LogicalNoteId);
        CompilerDiagnostic diagnostic = Assert.Single(
            full.Diagnostics,
            value => value.Code == "MIDORA2203");
        Assert.Equal(secondTrack.Id, diagnostic.Source.TrackId);
        Assert.Equal(rejected.Id, diagnostic.Source.LogicalNoteId);
        Assert.Equal(fresh.Fingerprint, incremental.Fingerprint);
        Assert.Equal(fresh.Events.ToArray(), incremental.Events.ToArray());
        Assert.Equal(fresh.Allocations.ToArray(), incremental.Allocations.ToArray());
        Assert.Equal(fresh.Diagnostics.ToArray(), incremental.Diagnostics.ToArray());
    }

    [Fact]
    public void GateLengthEqualToTemplateLoopsButStillEndsAtTemplateWithoutLongLifecycle()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.TemplateLengthTicks = 200;
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LongLifecycle = LongNoteLifecycle.HoldLastState;
        fixture.Instrument.LoopStartTick = 50;
        fixture.Instrument.LoopEndTick = 100;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 60, 1, 77));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 200, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Equal(new long[] { 160, 210, 260 }, result.Events.ToArray().Where(value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 77).Select(value => value.Tick));
        CanonicalMidiEvent noteOff = Assert.Single(
            result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOff);
        Assert.Equal(300, noteOff.Tick);
    }

    [Fact]
    public void ShortPreRollHorizonLoopsCurvesAndMappingTemplateTickButNotEnvelopeTime()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.TemplateLengthTicks = 200;
        fixture.Instrument.PreRollTicks = 100;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Instrument.LoopStartTick = 50;
        fixture.Instrument.LoopEndTick = 100;
        ValueCurve curve = new(fixture.Project)
        {
            Target = MidiValueTarget.ControlChange(1)
        };
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 0));
        curve.Points.Add(new CurvePoint(fixture.Project, 100, 127));
        fixture.Voice.Curves.Add(curve);
        InstrumentEnvelope envelope = new(fixture.Project)
        {
            StartValue = 0,
            PeakValue = 1,
            AttackTicks = 200,
            SustainValue = 1
        };
        fixture.Instrument.Envelopes.Add(envelope);
        TemplateEvent expression = TemplateEvent.ControlChange(
            fixture.Project,
            tick: 0,
            controller: 3,
            value: 127);
        expression.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Multiply
        });
        expression.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(expression);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "template tick probe",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "template tick",
            Body = "context.TemplateTick % 128"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.TemplateTick));
        fixture.Instrument.MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(2)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 200, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 150);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 200
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 64);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 200
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 2
            && value.Message.Byte2 == 50);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 200
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 3
            && value.Message.Byte2 == 64);
    }

    [Fact]
    public void DefinitionPreRollChangeInvalidatesIncrementalFragmentsAndMatchesFullCompile()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 100, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 120);
        MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project);
        fixture.Instrument.PreRollTicks = 100;
        ProjectChangeSet changes = new();
        changes.EventInstrumentIds.Add(fixture.Instrument.Id);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project, changes);
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.NotEqual(before.Fingerprint, incremental.Fingerprint);
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(full.Events.ToArray(), incremental.Events.ToArray());
        Assert.Equal(full.Allocations.ToArray(), incremental.Allocations.ToArray());
        Assert.Equal(full.Diagnostics.ToArray(), incremental.Diagnostics.ToArray());
        Assert.Equal(1, compiler.LastTelemetry.RecompiledTrackCount);
        Assert.Contains(incremental.Events.ToArray(), value =>
            value.Tick == 100 && value.Role == CanonicalEventRole.NoteOn);
    }

    [Fact]
    public void StandaloneEventInstrumentPreviewIgnoresDefinitionPreRoll()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 20, 100, 60, 100));

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(
                fixture.Instrument.Id,
                GateLengthTicks: 240));

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        CanonicalMidiEvent noteOn = Assert.Single(
            result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(20, noteOn.Tick);
    }

    [Fact]
    public void SegmentPreviewRetainsTheSourceDefinitionPreRoll()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Segment.ProjectStartTick = 480;
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 100, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 100,
            length: 120);

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 480 && value.Role == CanonicalEventRole.NoteOn);
    }

    [Fact]
    public void HeldSegmentPitchAuditionIgnoresDefinitionPreRollAtTheActiveLeftEdge()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        SegmentNotePreviewRequest request = new(
            fixture.Track.Id,
            fixture.Segment.Id,
            StartTick: 0,
            Pitch: 67,
            Velocity: 111);
        PreviewCompiler preview = new();

        CanonicalCompiledResult open = preview.CompileHeldSegmentNoteGateOpen(
            fixture.Project,
            request,
            windowLengthTicks: 480);
        CanonicalCompiledResult ended = preview.CompileHeldSegmentNoteGateEnd(
            fixture.Project,
            request,
            finalGateLengthTicks: 120,
            effectiveGateEndTick: 120);

        Assert.True(open.IsConsumable, FormatDiagnostics(open));
        Assert.True(ended.IsConsumable, FormatDiagnostics(ended));
        Assert.Contains(open.Events.ToArray(), value =>
            value.Tick == 0
            && value.Role == CanonicalEventRole.NoteOn
            && value.Message.Byte1 == 67);
        Assert.Contains(ended.Events.ToArray(), value =>
            value.Tick == 0
            && value.Role == CanonicalEventRole.NoteOn
            && value.Message.Byte1 == 67);
        Assert.Contains(ended.Events.ToArray(), value =>
            value.Tick == 120 && value.Role == CanonicalEventRole.NoteOff);
    }

    [Fact]
    public void RangeColdStartDoesNotRetriggerANoteOnFromThePreRollPrefix()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.PreRollTicks = 100;
        fixture.Voice.InitialState.Program = 12;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 200,
            length: 300);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                StartTick = 150,
                EndTick = 350
            });

        Assert.True(result.IsConsumable, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 150
            && value.Message.MessageType == MidiMessageType.ProgramChange
            && value.Message.Byte1 == 12
            && value.Source.Origin == SourceOrigin.RangeRestore);
    }

    private static string FormatDiagnostics(CanonicalCompiledResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(value => $"{value.Code}: {value.Message}"));

    private static (LogicalTrack Track, Segment Segment) AddSharedUsageTrack(
        MidoraProject project,
        LogicalTrack sourceTrack,
        long segmentLength)
    {
        LogicalTrack track = new(project)
        {
            Name = "Shared",
            EventInstrumentUsageId = sourceTrack.EventInstrumentUsageId
        };
        Segment segment = new(project)
        {
            ProjectStartTick = 0,
            LengthTicks = segmentLength
        };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        CompilerTestProject.RegisterSegment(project, segment);
        return (track, segment);
    }
}
