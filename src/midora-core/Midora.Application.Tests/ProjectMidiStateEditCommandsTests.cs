using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMidiStateEditCommandsTests
{
    public static TheoryData<MidiValueTarget, int> ValidTargets => new()
    {
        { MidiValueTarget.ControlChange(11), 127 },
        { MidiValueTarget.BankMsb, 127 },
        { MidiValueTarget.BankLsb, 0 },
        { MidiValueTarget.Program, 64 },
        { MidiValueTarget.PitchBend, -8192 },
        { MidiValueTarget.Rpn(16_383), 16_383 },
        { MidiValueTarget.Nrpn(0), 0 },
        { MidiValueTarget.PitchBendRangeSemitones, 127 },
        { MidiValueTarget.PitchBendRangeCents, 99 }
    };

    public static TheoryData<MidiValueTarget, int?> InvalidTargets => new()
    {
        { MidiValueTarget.ControlChange(91), 64 },
        { MidiValueTarget.ControlChange(120), 64 },
        { new MidiValueTarget(MidiValueKind.Program, 1), 10 },
        { MidiValueTarget.PitchBend, 8_192 },
        { MidiValueTarget.Rpn(16_384), 0 },
        { MidiValueTarget.Nrpn(0), 16_384 },
        { MidiValueTarget.PitchBendRangeCents, 100 },
        { new MidiValueTarget((MidiValueKind)999), 0 }
    };

    [Theory]
    [InlineData(MidiValueKind.ControlChange, 11, -20, 0)]
    [InlineData(MidiValueKind.ControlChange, 11, 300, 127)]
    [InlineData(MidiValueKind.PitchBend, 0, -9_000, -8_192)]
    [InlineData(MidiValueKind.PitchBend, 0, 9_000, 8_191)]
    [InlineData(MidiValueKind.PitchBendRangeCents, 0, 120, 99)]
    public void InitialStateUiAndCommandsShareOneAuthoritativeClampRange(
        MidiValueKind kind,
        int number,
        int entered,
        int expected)
    {
        MidiValueTarget target = new(kind, number);

        Assert.Equal(expected, MidiStateValueRules.Clamp(target, entered));
        MidiStateValueRules.Validate(target, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MidiStateValueRules.Validate(target, entered));
    }

    [Theory]
    [InlineData(91)]
    [InlineData(93)]
    [InlineData(120)]
    public void InitialStateClampDoesNotTurnUnsupportedControllersIntoEditableValues(
        int controller) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MidiStateValueRules.Clamp(MidiValueTarget.ControlChange(controller), 64));

    [Fact]
    public void InitialStateScopesUseProjectInstrumentVoicePrecedenceAndUndoExactly()
    {
        Fixture fixture = CreateFixture();
        MidiInitialState projectState = fixture.Project.GlobalInitialState;
        MidiInitialState instrumentState = fixture.Instrument.InitialState;
        MidiInitialState voiceState = fixture.Voice.InitialState;
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectInitialStateValue(
            MidiValueTarget.ControlChange(11),
            10));
        AssertInitialController(compilation.LastAttempt, 11, 10);
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
            fixture.Instrument.Id,
            MidiValueTarget.ControlChange(11),
            20));
        AssertInitialController(compilation.LastAttempt, 11, 20);
        document.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            MidiValueTarget.ControlChange(11),
            30));
        AssertInitialController(compilation.LastAttempt, 11, 30);

        Assert.Same(projectState, fixture.Project.GlobalInitialState);
        Assert.Same(instrumentState, fixture.Instrument.InitialState);
        Assert.Same(voiceState, fixture.Voice.InitialState);
        Assert.Equal(480, fixture.Instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        AssertInitialController(compilation.LastAttempt, 11, 20);
        document.Undo();
        AssertInitialController(compilation.LastAttempt, 11, 10);
        document.Undo();
        Assert.DoesNotContain(compilation.LastAttempt.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.InitialState
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Theory]
    [MemberData(nameof(ValidTargets))]
    public void ProjectInitialStateSupportsEverySpecifiedTarget(
        MidiValueTarget target,
        int value)
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectInitialStateValue(target, value));

        Assert.Equal(value, ReadValue(fixture.Project.GlobalInitialState, target));
        AssertCurrentCompilationMatchesFull(compilation);
        document.Undo();
        Assert.Null(ReadValue(fixture.Project.GlobalInitialState, target));
        Assert.False(document.IsModified);
    }

    [Theory]
    [MemberData(nameof(InvalidTargets))]
    public void MidiStateEditRejectsInvalidTargetsAndValues(
        MidiValueTarget target,
        int? value)
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateProjectInitialStateValue(target, value)));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void RemovingOverrideAndAbsentNullHaveDistinctHistoryBehavior()
    {
        Fixture fixture = CreateFixture();
        fixture.Voice.InitialState.Program = 10;
        MidiInitialState state = fixture.Voice.InitialState;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectInitialStateValue(
            MidiValueTarget.ControlChange(11),
            null));
        Assert.False(document.CanUndo);

        document.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            MidiValueTarget.Program,
            null));

        Assert.Same(state, fixture.Voice.InitialState);
        Assert.Null(state.Program);
        Assert.True(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(10, state.Program);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ProjectResetDefaultControlsCanonicalResetAndUndoRemovesOverride()
    {
        Fixture fixture = CreateFixture();
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(
            fixture.Project,
            tick: 0,
            controller: 11,
            value: 100));
        MidiInitialState resetDefaults = fixture.Project.GlobalResetDefaults;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectResetDefaultValue(
            MidiValueTarget.ControlChange(11),
            77));

        Assert.Same(resetDefaults, fixture.Project.GlobalResetDefaults);
        CanonicalMidiEvent reset = Assert.Single(
            compilation.LastAttempt.Events.ToArray(),
            value => value.Role == CanonicalEventRole.Reset
                && value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 11);
        Assert.Equal((byte)77, reset.Message.Byte2);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.False(resetDefaults.Controllers.ContainsKey(11));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static int? ReadValue(MidiInitialState state, MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange => ReadDictionary(state.Controllers, target.Number),
            MidiValueKind.BankMsb => state.BankMsb,
            MidiValueKind.BankLsb => state.BankLsb,
            MidiValueKind.Program => state.Program,
            MidiValueKind.PitchBend => state.PitchBend,
            MidiValueKind.RegisteredParameter =>
                ReadDictionary(state.RegisteredParameters, target.Number),
            MidiValueKind.NonRegisteredParameter =>
                ReadDictionary(state.NonRegisteredParameters, target.Number),
            MidiValueKind.PitchBendRangeSemitones => state.PitchBendRangeSemitones,
            MidiValueKind.PitchBendRangeCents => state.PitchBendRangeCents,
            _ => throw new InvalidOperationException()
        };

    private static int? ReadDictionary(Dictionary<int, int> values, int key) =>
        values.TryGetValue(key, out int value) ? value : null;

    private static void AssertInitialController(
        CanonicalCompiledResult result,
        int controller,
        int expectedValue)
    {
        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(), item =>
            item.Role == CanonicalEventRole.InitialState
            && item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == controller);
        Assert.Equal((byte)expectedValue, value.Message.Byte2);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return new(project, instrument, voice);
    }

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
        SubVoice Voice);
}
