using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectLogicalParameterEditCommandsTests
{
    [Fact]
    public void SafeDefinitionPropertiesPreserveIdentityAndUndoExactly()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        AssertController(compilation.LastAttempt, controller: 1, value: 21, tick: 0);

        document.Execute(ProjectDomainEditCommands.RenameLogicalParameter(
            fixture.Instrument.Id,
            fixture.FirstParameter.Id,
            "  Main Amount  "));
        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterDefaultValue(
            fixture.Instrument.Id,
            fixture.SecondParameter.Id,
            defaultValue: 4));
        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterDisplayRange(
            fixture.Instrument.Id,
            fixture.FirstParameter.Id,
            displayMinimum: -20,
            displayMaximum: 20));
        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
            fixture.Instrument.Id,
            fixture.FirstParameter.Id,
            minimum: 0,
            maximum: 8));

        Assert.Same(fixture.FirstParameter, fixture.Instrument.LogicalParameters[0]);
        Assert.Equal("Main Amount", fixture.FirstParameter.Name);
        Assert.Equal(4, fixture.SecondParameter.DefaultValue);
        Assert.Equal(-20, fixture.FirstParameter.DisplayMinimum);
        Assert.Equal(20, fixture.FirstParameter.DisplayMaximum);
        Assert.Equal(8, fixture.FirstParameter.Maximum);
        Assert.Equal(fixture.FirstParameter.Id, fixture.Lane.ParameterId);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertController(compilation.LastAttempt, controller: 1, value: 28, tick: 0);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        document.Undo();
        document.Undo();
        Assert.Equal("Amount", fixture.FirstParameter.Name);
        Assert.Equal(3, fixture.SecondParameter.DefaultValue);
        Assert.Equal(0, fixture.FirstParameter.DisplayMinimum);
        Assert.Equal(10, fixture.FirstParameter.DisplayMaximum);
        Assert.Equal(10, fixture.FirstParameter.Maximum);
        AssertController(compilation.LastAttempt, controller: 1, value: 21, tick: 0);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DisplayRangeIsPersistedHistoryButDoesNotInvalidateCompiledTracks()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        CanonicalCompiledResult before = compilation.LastAttempt;
        CompilerRunTelemetry telemetryBefore = compilation.LastCompilationTelemetry;
        long sourceRevisionBefore = compilation.SourceRevision;
        long compiledRevisionBefore = compilation.CompiledRevision;
        int compilationEvents = 0;
        compilation.CompilationChanged += (_, _) => compilationEvents++;

        ProjectEditExecution edit = document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterDisplayRange(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                displayMinimum: -20,
                displayMaximum: 20));

        Assert.True(edit.Changed);
        Assert.Equal(before.Fingerprint, edit.CompilationResult.Fingerprint);
        Assert.Equal(telemetryBefore, compilation.LastCompilationTelemetry);
        Assert.Equal(sourceRevisionBefore, compilation.SourceRevision);
        Assert.Equal(compiledRevisionBefore, compilation.CompiledRevision);
        Assert.Equal(1, compilationEvents);
        Assert.True(document.IsModified);

        CanonicalCompiledResult undone = document.Undo();

        Assert.Equal(before.Fingerprint, undone.Fingerprint);
        Assert.Equal(telemetryBefore, compilation.LastCompilationTelemetry);
        Assert.Equal(sourceRevisionBefore, compilation.SourceRevision);
        Assert.Equal(compiledRevisionBefore, compilation.CompiledRevision);
        Assert.Equal(2, compilationEvents);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DefinitionValidationRejectsDuplicateInvalidAndMigrationRequiringChanges()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.RenameLogicalParameter(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                " factor ")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterDefaultValue(
                fixture.Instrument.Id,
                fixture.SecondParameter.Id,
                defaultValue: 3.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterDisplayRange(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                displayMinimum: 2,
                displayMaximum: 1)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                minimum: 0.5,
                maximum: 10)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                minimum: 0,
                maximum: 7)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
                fixture.Instrument.Id,
                fixture.EnumParameter.Id,
                minimum: 0,
                maximum: 0)));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EnumItemRenamePreservesItemIdentityValueAndLaneSemantics()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterEnumItem item = fixture.EnumParameter.EnumItems[1];
        MidoraId id = item.Id;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.RenameLogicalParameterEnumItem(
            fixture.Instrument.Id,
            fixture.EnumParameter.Id,
            item.Id,
            "  Enabled  "));

        Assert.Same(item, fixture.EnumParameter.EnumItems[1]);
        Assert.Equal(id, item.Id);
        Assert.Equal("Enabled", item.Name);
        Assert.Equal(0, item.Value);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal("On", item.Name);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ReferencedParameterDeletePreservesAllBrokenReferencesAndUndoRestoresObject()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteLogicalParameter(
                fixture.Instrument.Id,
                fixture.FirstParameter.Id,
                referencedDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteLogicalParameter(
            fixture.Instrument.Id,
            fixture.FirstParameter.Id,
            referencedDeletionConfirmed: true));

        Assert.DoesNotContain(fixture.FirstParameter, fixture.Instrument.LogicalParameters);
        Assert.Equal(fixture.FirstParameter.Id, fixture.Lane.ParameterId);
        Assert.Equal(fixture.FirstParameter.Id, fixture.FirstMapping.ParameterId);
        Assert.Equal(fixture.FirstParameter.Id, fixture.FirstMapping.Steps[0].LogicalParameterId);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1314");
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1230");
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstParameter, fixture.Instrument.LogicalParameters[0]);
        Assert.Equal(fixture.FirstParameter.Id, fixture.Lane.ParameterId);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingSourceAndTargetUpdatesPreserveChainAndAdoptSharedTargetSettings()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterMapping targetPeer = new(fixture.Project)
        {
            ParameterId = fixture.SecondParameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(2)
        };
        targetPeer.TargetSettings.Rounding = MappingRounding.Ceiling;
        targetPeer.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(targetPeer);
        MappingChain chain = fixture.FirstMapping.Steps;
        ValueMappingStep step = chain[0];
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterMappingSource(
            fixture.Instrument.Id,
            fixture.FirstMapping.Id,
            fixture.SecondParameter.Id));
        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterMappingTarget(
            fixture.Instrument.Id,
            fixture.FirstMapping.Id,
            fixture.Voice.Id,
            MidiValueTarget.ControlChange(2)));

        Assert.Equal(fixture.SecondParameter.Id, fixture.FirstMapping.ParameterId);
        Assert.Equal(MidiValueTarget.ControlChange(2), fixture.FirstMapping.Target);
        Assert.Equal(MappingRounding.Ceiling, fixture.FirstMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.FirstMapping.TargetSettings.Overflow);
        Assert.Same(chain, fixture.FirstMapping.Steps);
        Assert.Same(step, fixture.FirstMapping.Steps[0]);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        Assert.Equal(fixture.FirstParameter.Id, fixture.FirstMapping.ParameterId);
        Assert.Equal(MidiValueTarget.ControlChange(1), fixture.FirstMapping.Target);
        Assert.Equal(MappingRounding.Round, fixture.FirstMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.FirstMapping.TargetSettings.Overflow);
        Assert.Same(step, fixture.FirstMapping.Steps[0]);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingRouteUpdateIsOneAtomicUndoableEdit()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterMapping targetPeer = new(fixture.Project)
        {
            ParameterId = fixture.FirstParameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(2)
        };
        targetPeer.TargetSettings.Rounding = MappingRounding.Ceiling;
        targetPeer.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(targetPeer);
        MappingChain chain = fixture.FirstMapping.Steps;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterMappingRoute(
            fixture.Instrument.Id,
            fixture.FirstMapping.Id,
            fixture.SecondParameter.Id,
            fixture.Voice.Id,
            MidiValueTarget.ControlChange(2)));

        Assert.Equal(fixture.SecondParameter.Id, fixture.FirstMapping.ParameterId);
        Assert.Equal(MidiValueTarget.ControlChange(2), fixture.FirstMapping.Target);
        Assert.Equal(MappingRounding.Ceiling, fixture.FirstMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.FirstMapping.TargetSettings.Overflow);
        Assert.Same(chain, fixture.FirstMapping.Steps);
        Assert.Single(document.History);

        document.Undo();
        Assert.Equal(fixture.FirstParameter.Id, fixture.FirstMapping.ParameterId);
        Assert.Equal(MidiValueTarget.ControlChange(1), fixture.FirstMapping.Target);
        Assert.Equal(MappingRounding.Round, fixture.FirstMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.FirstMapping.TargetSettings.Overflow);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingTargetSettingsPropagateAcrossSharedTargetAndUndoDistinctValues()
    {
        Fixture fixture = CreateFixture();
        fixture.SecondMapping.TargetSettings.Rounding = MappingRounding.Ceiling;
        fixture.SecondMapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
            fixture.Instrument.Id,
            fixture.FirstMapping.Id,
            MappingRounding.Floor,
            MappingOverflow.Clamp));

        Assert.All(
            new[] { fixture.FirstMapping, fixture.SecondMapping },
            mapping =>
            {
                Assert.Equal(MappingRounding.Floor, mapping.TargetSettings.Rounding);
                Assert.Equal(MappingOverflow.Clamp, mapping.TargetSettings.Overflow);
            });
        Assert.True(compilation.LastAttempt.IsConsumable);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(MappingRounding.Round, fixture.FirstMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.FirstMapping.TargetSettings.Overflow);
        Assert.Equal(MappingRounding.Ceiling, fixture.SecondMapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.SecondMapping.TargetSettings.Overflow);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1275");
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingReorderChangesFormalExecutionOrderAndUndoRestoresIt()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        AssertController(compilation.LastAttempt, controller: 1, value: 21, tick: 0);

        document.Execute(ProjectDomainEditCommands.ReorderLogicalParameterMapping(
            fixture.Instrument.Id,
            fixture.SecondMapping.Id,
            newIndex: 0));

        Assert.Same(fixture.SecondMapping, fixture.Instrument.ParameterMappings[0]);
        Assert.Same(fixture.FirstMapping, fixture.Instrument.ParameterMappings[1]);
        AssertController(compilation.LastAttempt, controller: 1, value: 17, tick: 0);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstMapping, fixture.Instrument.ParameterMappings[0]);
        Assert.Same(fixture.SecondMapping, fixture.Instrument.ParameterMappings[1]);
        AssertController(compilation.LastAttempt, controller: 1, value: 21, tick: 0);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingDeleteRequiresConfirmationAndUndoRestoresSameObjectAndIndex()
    {
        Fixture fixture = CreateFixture();
        MappingChain chain = fixture.FirstMapping.Steps;
        ValueMappingStep step = chain[0];
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteLogicalParameterMapping(
                fixture.Instrument.Id,
                fixture.FirstMapping.Id,
                deletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteLogicalParameterMapping(
            fixture.Instrument.Id,
            fixture.FirstMapping.Id,
            deletionConfirmed: true));

        Assert.DoesNotContain(fixture.FirstMapping, fixture.Instrument.ParameterMappings);
        Assert.Same(chain, fixture.FirstMapping.Steps);
        Assert.Same(step, fixture.FirstMapping.Steps[0]);
        AssertController(compilation.LastAttempt, controller: 1, value: 15, tick: 0);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstMapping, fixture.Instrument.ParameterMappings[0]);
        Assert.Same(step, fixture.FirstMapping.Steps[0]);
        AssertController(compilation.LastAttempt, controller: 1, value: 21, tick: 0);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MappingReferenceAndTargetValidationRejectsUnknownOrForbiddenChoices()
    {
        Fixture fixture = CreateFixture();
        MidoraId unknown = fixture.Project.AllocateStableId();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterMappingSource(
                fixture.Instrument.Id,
                fixture.FirstMapping.Id,
                unknown)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterMappingTarget(
                fixture.Instrument.Id,
                fixture.FirstMapping.Id,
                unknown,
                MidiValueTarget.ControlChange(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterMappingTarget(
                fixture.Instrument.Id,
                fixture.FirstMapping.Id,
                fixture.Voice.Id,
                MidiValueTarget.ControlChange(91))));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
                fixture.Instrument.Id,
                fixture.FirstMapping.Id,
                (MappingRounding)999,
                MappingOverflow.Fail)));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static void AssertController(
        CanonicalCompiledResult result,
        byte controller,
        byte value,
        long tick)
    {
        Assert.True(result.IsConsumable);
        CanonicalMidiEvent match = Assert.Single(result.Events.ToArray(), item =>
            item.Tick == tick
            && item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == controller);
        Assert.Equal(value, match.Message.Byte2);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.Warn
        };
        LogicalParameterDefinition firstParameter = IntegerParameter(project, "Amount", 2);
        LogicalParameterDefinition secondParameter = IntegerParameter(project, "Factor", 3);
        LogicalParameterDefinition enumParameter = new(project)
        {
            Name = "Mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0
        };
        enumParameter.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "Off" });
        enumParameter.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "On" });
        instrument.LogicalParameters.Add(firstParameter);
        instrument.LogicalParameters.Add(secondParameter);
        instrument.LogicalParameters.Add(enumParameter);
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.ControlChange(project, 0, 1, 5));
        voice.Events.Add(TemplateEvent.Note(project, 0, 240, 60, 100));
        instrument.SubVoices.Add(voice);
        LogicalParameterMapping firstMapping = new(project)
        {
            ParameterId = firstParameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        firstMapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = firstParameter.Id,
            Operation = MappingOperation.Add
        });
        LogicalParameterMapping secondMapping = new(project)
        {
            ParameterId = secondParameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        secondMapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = secondParameter.Id,
            Operation = MappingOperation.Multiply
        });
        instrument.ParameterMappings.Add(firstMapping);
        instrument.ParameterMappings.Add(secondMapping);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        LogicalParameterLane lane = new(project) { ParameterId = firstParameter.Id };
        lane.Points.Add(new CurvePoint(project, 0, 2, CurveInterpolation.Step));
        lane.Points.Add(new CurvePoint(project, 120, 8, CurveInterpolation.Step));
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        return new(
            project,
            instrument,
            voice,
            firstParameter,
            secondParameter,
            enumParameter,
            firstMapping,
            secondMapping,
            lane);
    }

    private static LogicalParameterDefinition IntegerParameter(
        MidoraProject project,
        string name,
        int defaultValue) =>
        new(project)
        {
            Name = name,
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 10,
            DisplayMinimum = 0,
            DisplayMaximum = 10,
            DefaultValue = defaultValue
        };

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        SubVoice Voice,
        LogicalParameterDefinition FirstParameter,
        LogicalParameterDefinition SecondParameter,
        LogicalParameterDefinition EnumParameter,
        LogicalParameterMapping FirstMapping,
        LogicalParameterMapping SecondMapping,
        LogicalParameterLane Lane);
}
