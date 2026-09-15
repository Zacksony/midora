using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTemplateEventEditCommandsTests
{
    [Fact]
    public void TemplateEventMappingsAreSharedByExactSubVoiceEventTarget()
    {
        MidoraProject project = new(480);
        SubVoice voice = new(project);
        TemplateEvent first = TemplateEvent.ControlChange(project, 0, 1, 64);
        voice.Events.Add(first);
        long afterFirst = project.NextStableId;

        for (int index = 1; index < 1_000; index++)
        {
            voice.Events.Add(TemplateEvent.ControlChange(project, index, 1, index % 128));
        }

        Assert.Equal(afterFirst + 999, project.NextStableId);
        Assert.Single(voice.EventMappings);
        Assert.All(voice.Events, value => Assert.Same(first.ValueMappings, value.ValueMappings));

        TemplateEvent otherController = TemplateEvent.ControlChange(project, 1_100, 11, 100);
        voice.Events.Add(otherController);
        Assert.Equal(2, voice.EventMappings.Count);
        Assert.NotSame(first.ValueMappings, otherController.ValueMappings);

        TemplateEvent firstNote = TemplateEvent.Note(project, 1_200, 120, 60, 100);
        TemplateEvent secondNote = TemplateEvent.Note(project, 1_440, 120, 64, 90);
        voice.Events.AddRange([firstNote, secondNote]);
        Assert.Equal(4, voice.EventMappings.Count);
        Assert.Same(firstNote.NumberMappings, secondNote.NumberMappings);
        Assert.Same(firstNote.ValueMappings, secondNote.ValueMappings);
    }

    [Fact]
    public void TemplateNoteUpdateAutoExtendsLengthPreservesIdentityAndUndoIsExact()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent note = voice.Events[0];
        ValueMappingStep mapping = new(project)
        {
            Source = MappingSource.Constant,
            Constant = 1
        };
        note.ValueMappings.Add(mapping);
        MappingChain valueMappings = note.ValueMappings;
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateNote(
            instrument.Id,
            voice.Id,
            note.Id,
            tick: 600,
            lengthTicks: 240,
            note: 72,
            velocity: 90,
            followPitchDelta: false));

        Assert.Same(note, voice.Events.Single());
        Assert.Same(valueMappings, note.ValueMappings);
        Assert.Same(mapping, note.ValueMappings.Single());
        Assert.Equal((600, 240, 72, 90, false),
            (note.Tick, note.LengthTicks, note.Number, note.Value, note.FollowPitchDelta));
        Assert.Equal(840, instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(note, voice.Events.Single());
        Assert.Equal((0, 480, 60, 100, true),
            (note.Tick, note.LengthTicks, note.Number, note.Value, note.FollowPitchDelta));
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TemplateEventEditsRejectInvalidRawMidiValuesWithoutHistoryChanges()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent note = voice.Events[0];
        TemplateEvent controller = TemplateEvent.ControlChange(project, 10, 1, 64);
        TemplateEvent bank = TemplateEvent.Bank(project, 20, 0, 0);
        TemplateEvent program = TemplateEvent.Program(project, 30, 0);
        TemplateEvent pitchBend = Event(project, TemplateEventKind.PitchBend, 40, value: 0);
        TemplateEvent rpn = Event(project, TemplateEventKind.RegisteredParameter, 50, 1, 2);
        TemplateEvent nrpn = Event(project, TemplateEventKind.NonRegisteredParameter, 60, 1, 2);
        TemplateEvent range = Event(project, TemplateEventKind.PitchBendRange, 70, value: 2);
        voice.Events.AddRange([controller, bank, program, pitchBend, rpn, nrpn, range]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 0, 60, 100, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 480, 128, 100, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 480, 60, 0, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrument.Id, voice.Id, controller.Id, 10, 91, 64)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateBank(
                instrument.Id, voice.Id, bank.Id, 20, null, null)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateProgram(
                instrument.Id, voice.Id, program.Id, 30, 128)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrument.Id, voice.Id, pitchBend.Id, 40, 8_192)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrument.Id, voice.Id, rpn.Id, 50, 16_384, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrument.Id, voice.Id, nrpn.Id, 60, 0, 16_384)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrument.Id, voice.Id, range.Id, 70, 12, 100)));

        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SameTickStateConflictsAreReplacedAndUndoRestoresExactOrder()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent controllerConflict = TemplateEvent.ControlChange(project, 120, 1, 10);
        TemplateEvent controllerTarget = TemplateEvent.ControlChange(project, 240, 1, 20);
        TemplateEvent rpnZero = Event(
            project,
            TemplateEventKind.RegisteredParameter,
            300,
            number: 0,
            value: 256);
        TemplateEvent pitchBendRange = Event(
            project,
            TemplateEventKind.PitchBendRange,
            360,
            value: 2,
            secondaryValue: 0);
        voice.Events.AddRange([controllerConflict, rpnZero, controllerTarget, pitchBendRange]);
        TemplateEvent[] originalOrder = voice.Events.ToArray();
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateControlChange(
            instrument.Id,
            voice.Id,
            controllerTarget.Id,
            tick: 120,
            controller: 1,
            value: 99));

        Assert.DoesNotContain(controllerConflict, voice.Events);
        Assert.Contains(controllerTarget, voice.Events);
        Assert.Equal((120L, 99), (controllerTarget.Tick, controllerTarget.Value));
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.Same(controllerConflict, voice.Events[1]);

        document.Execute(ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
            instrument.Id,
            voice.Id,
            pitchBendRange.Id,
            tick: 300,
            semitones: 12,
            cents: 50));

        Assert.DoesNotContain(rpnZero, voice.Events);
        Assert.Contains(pitchBendRange, voice.Events);
        Assert.Equal((300L, 12, 50), (
            pitchBendRange.Tick,
            pitchBendRange.Value,
            pitchBendRange.SecondaryValue));
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void BankCanDropAComponentWithoutDeletingItsSharedMapping()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent bank = TemplateEvent.Bank(project, 120, 1, 2);
        ValueMappingStep mappingStep = new(project);
        bank.ValueMappings.Add(mappingStep);
        voice.Events.Add(bank);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateBank(
            instrument.Id,
            voice.Id,
            bank.Id,
            tick: 600,
            bankMsb: null,
            bankLsb: 127));

        Assert.False(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Equal(127, bank.SecondaryValue);
        Assert.Same(mappingStep, Assert.Single(bank.ValueMappings));
        Assert.Equal(601, instrument.TemplateLengthTicks);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Equal((1, 2), (bank.Value, bank.SecondaryValue));
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ChangingAnEventToAGenuinelyNewTargetCreatesOnlyThatOptionalMapping()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent controller = TemplateEvent.ControlChange(project, 120, 1, 64);
        voice.Events.Add(controller);
        SubVoiceEventMapping originalMapping = voice.FindEventMapping(
            TemplateEventMappingTarget.Create(
                TemplateEventKind.ControlChange,
                1,
                TemplateEventMappingParameter.Value))!;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateControlChange(
            instrument.Id,
            voice.Id,
            controller.Id,
            tick: 120,
            controller: 11,
            value: 96));

        SubVoiceEventMapping createdMapping = Assert.Single(
            voice.EventMappings,
            value => value.Target == TemplateEventMappingTarget.Create(
                TemplateEventKind.ControlChange,
                11,
                TemplateEventMappingParameter.Value));
        Assert.Contains(originalMapping, voice.EventMappings);
        Assert.Equal(4, voice.EventMappings.Count);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.DoesNotContain(createdMapping, voice.EventMappings);
        Assert.Same(
            originalMapping,
            Assert.Single(voice.EventMappings, value =>
                value.Target.EventKind == TemplateEventKind.ControlChange));
        Assert.Equal(3, voice.EventMappings.Count);
        Assert.Equal(1, controller.Number);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TemplateEventDeleteRestoresTheExactObjectAndListPosition()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent controller = TemplateEvent.ControlChange(project, 120, 1, 64);
        TemplateEvent program = TemplateEvent.Program(project, 240, 10);
        voice.Events.AddRange([controller, program]);
        TemplateEvent[] originalOrder = voice.Events.ToArray();
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteTemplateEvent(
            instrument.Id,
            voice.Id,
            controller.Id));

        Assert.DoesNotContain(controller, voice.Events);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.Same(controller, voice.Events[1]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DrawnEventPointsBatchUpsertsExactTargetAsOneUndoUnit()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent existing = TemplateEvent.ControlChange(project, 120, 11, 30);
        voice.Events.Add(existing);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpsertTemplateEventPoints(
            instrument.Id,
            voice.Id,
            MidiValueTarget.ControlChange(11),
            [new(120, 64), new(240, 100)]));

        Assert.Equal(64, existing.Value);
        TemplateEvent created = Assert.Single(voice.Events, value =>
            value.Kind == TemplateEventKind.ControlChange && value.Tick == 240);
        Assert.Equal((11, 100), (created.Number, created.Value));
        Assert.Single(document.History);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(30, existing.Value);
        Assert.DoesNotContain(created, voice.Events);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Redo();
        Assert.Equal(64, existing.Value);
        Assert.Same(created, voice.Events.Single(value => value.Tick == 240));
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DrawnBankFieldMergesWithExistingBankEventAndUndoRestoresMissingField()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent bank = TemplateEvent.Bank(project, 120, null, 9);
        voice.Events.Add(bank);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpsertTemplateEventPoints(
            instrument.Id,
            voice.Id,
            MidiValueTarget.BankMsb,
            [new(120, 7)]));

        Assert.True(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Equal((7, 9), (bank.Value, bank.SecondaryValue));
        Assert.Single(voice.Events, value => value.Kind == TemplateEventKind.Bank);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.False(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Equal(9, bank.SecondaryValue);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EventPointBatchMoveAndCopyAreAtomicAndPreserveTheSharedLaneMapping()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent first = TemplateEvent.ControlChange(project, 120, 11, 30);
        TemplateEvent second = TemplateEvent.ControlChange(project, 240, 11, 60);
        voice.Events.AddRange([first, second]);
        SubVoiceEventMapping mapping = voice.EventMappings.Single(value =>
            value.Target == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(11)));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11),
            tickDelta: 60,
            valueDelta: 5,
            duplicate: false));

        Assert.Equal([(180L, 35), (300L, 65)],
            new[] { first, second }.Select(value => (value.Tick, value.Value)).ToArray());
        Assert.Same(mapping, voice.FindEventMapping(mapping.Target));
        Assert.Single(document.History);
        AssertCurrentCompilationMatchesFull(compilation);

        long firstCopyId = project.NextStableId;
        document.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11),
            tickDelta: 480,
            valueDelta: 0,
            duplicate: true));

        TemplateEvent[] copies = voice.Events
            .Where(value => value.Id.Value >= firstCopyId)
            .OrderBy(value => value.Tick)
            .ToArray();
        Assert.Equal([(660L, 35), (780L, 65)],
            copies.Select(value => (value.Tick, value.Value)).ToArray());
        Assert.All(copies, value => Assert.Same(mapping.Steps, value.ValueMappings));
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.DoesNotContain(voice.Events, value => value.Id.Value >= firstCopyId);
        document.Undo();
        Assert.Equal([(120L, 30), (240L, 60)],
            new[] { first, second }.Select(value => (value.Tick, value.Value)).ToArray());
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void CreatingEventLaneAddsOnlyTheSharedMappingAndNoTickZeroEvent()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent[] originalEvents = voice.Events.ToArray();
        TemplateEventMappingTarget target = TemplateEventMidiTargets.ToMappingTarget(
            MidiValueTarget.ControlChange(74));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateSubVoiceEventLane(
            instrument.Id,
            voice.Id,
            MidiValueTarget.ControlChange(74)));

        Assert.Equal(originalEvents, voice.Events);
        Assert.NotNull(voice.FindEventMapping(target));
        Assert.Single(document.History);
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.CreateSubVoiceEventLane(
                instrument.Id,
                voice.Id,
                MidiValueTarget.ControlChange(74))));
        Assert.Equal(originalEvents, voice.Events);
        Assert.Single(voice.EventMappings, value => value.Target == target);
        Assert.Single(document.History);

        document.Undo();
        Assert.Equal(originalEvents, voice.Events);
        Assert.Null(voice.FindEventMapping(target));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ExplicitEventLaneDeletionRemovesBankComponentAndUndoRestoresExactLane()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent bank = TemplateEvent.Bank(project, 120, 7, 9);
        voice.Events.Add(bank);
        TemplateEventMappingTarget msbTarget = TemplateEventMidiTargets.ToMappingTarget(
            MidiValueTarget.BankMsb);
        SubVoiceEventMapping msbMapping = voice.FindEventMapping(msbTarget)!;
        int mappingIndex = voice.EventMappings.IndexOf(msbMapping);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteSubVoiceEventLane(
            instrument.Id,
            voice.Id,
            MidiValueTarget.BankMsb,
            nonEmptyDeletionConfirmed: true));

        Assert.False(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Null(voice.FindEventMapping(msbTarget));
        Assert.NotNull(voice.FindEventMapping(TemplateEventMidiTargets.ToMappingTarget(
            MidiValueTarget.BankLsb)));
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(bank.HasBankMsb);
        Assert.True(bank.HasBankLsb);
        Assert.Same(msbMapping, voice.EventMappings[mappingIndex]);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EventLaneDeletionTargetsOnlyRequestedEventsAndRefreshesItsIndexAfterUndo()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent target = Event(
            project,
            TemplateEventKind.ControlChange,
            tick: 120,
            number: 74,
            value: 64);
        voice.Events.Add(target);
        for (int index = 0; index < 2_000; index++)
        {
            voice.Events.Add(Event(
                project,
                TemplateEventKind.ControlChange,
                tick: 240 + index,
                number: 71,
                value: index % 128));
        }
        TemplateEventMappingTarget targetMapping = TemplateEventMidiTargets.ToMappingTarget(
            MidiValueTarget.ControlChange(74));
        Assert.NotNull(voice.FindEventMapping(targetMapping));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteSubVoiceEventLane(
            instrument.Id,
            voice.Id,
            MidiValueTarget.ControlChange(74),
            nonEmptyDeletionConfirmed: true));

        Assert.DoesNotContain(target, voice.Events);
        Assert.Equal(2_001, voice.Events.Count);
        Assert.All(
            voice.Events.Where(static value => value.Kind != TemplateEventKind.Note),
            static value => Assert.Equal(71, value.Number));

        document.Undo();
        Assert.Contains(target, voice.Events);
        Assert.NotNull(voice.FindEventMapping(targetMapping));

        document.Redo();
        Assert.DoesNotContain(target, voice.Events);
        Assert.Equal(2_001, voice.Events.Count);
    }

    private static TemplateEvent Event(
        MidoraProject project,
        TemplateEventKind kind,
        long tick,
        int number = 0,
        int value = 0,
        int secondaryValue = 0) =>
        new(project)
        {
            Kind = kind,
            Tick = tick,
            Number = number,
            Value = value,
            SecondaryValue = secondaryValue
        };

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static MidoraProject CreateProject()
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
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return project;
    }

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
}
