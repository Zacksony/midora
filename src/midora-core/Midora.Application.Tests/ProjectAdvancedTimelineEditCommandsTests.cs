using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectAdvancedTimelineEditCommandsTests
{
    [Fact]
    public void HumanizeLogicalNotesAppliesAllModesAndUndoRestoresExactly()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 10, 20, 60, 100);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.HumanizeLogicalNotes(
            segment.Id,
            [note.Id],
            new(
                TimelineHumanizeField.Add(5, 5),
                TimelineHumanizeField.Multiply(0.5, 0.5),
                TimelineHumanizeField.Override(120, 120),
                Seed: 123));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        LogicalNote applied = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(note.Id, applied.Id);
        Assert.Equal((15L, 10L, 120), (applied.StartTick, applied.LengthTicks, applied.Velocity));
        Assert.Equal([note.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal((10L, 20L, 100), (note.StartTick, note.LengthTicks, note.Velocity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HumanizeIntegerModesPreserveValuesBeyondBinary64IntegerPrecision(bool additive)
    {
        const long exactRangeValue = 9_007_199_254_740_993;
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 20, 60, 100);
        TimelineHumanizeField gate = additive
            ? TimelineHumanizeField.Add(exactRangeValue, exactRangeValue)
            : TimelineHumanizeField.Override(exactRangeValue, exactRangeValue);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.HumanizeLogicalNotes(
            segment.Id,
            [note.Id],
            new(
                TimelineHumanizeField.Disabled,
                gate,
                TimelineHumanizeField.Disabled,
                Seed: 123));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(
            additive ? exactRangeValue + 20 : exactRangeValue,
            Assert.Single(Active(project, segment).Notes).LengthTicks);
    }

    [Fact]
    public void HumanizeUsesStableSeedPreservesDirectPayloadAndDropsLaterCollision()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote incumbent = AddDirectNote(project, segment, 10, 10, 60, 90, 17, 1, 2);
        DirectMidiNote newcomer = AddDirectNote(project, segment, 20, 10, 60, 80, 29, 3, 4);
        TimelineHumanizeOptions options = new(
            TimelineHumanizeField.Override(10, 10),
            TimelineHumanizeField.Disabled,
            TimelineHumanizeField.Add(-3, 3),
            Seed: 0x1234);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.HumanizeDirectMidiNotes(
            segment.Id,
            [incumbent.Id, newcomer.Id],
            options);

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        DirectMidiNote applied = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(incumbent.Id, applied.Id);
        Assert.Equal((17, 1L, 2L), (
            applied.NoteOffVelocity,
            applied.NoteOnOrder,
            applied.NoteOffOrder));
        Assert.Equal([incumbent.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([incumbent, newcomer], Active(project, segment).Notes);
        Assert.Equal((29, 3L, 4L), (
            newcomer.NoteOffVelocity,
            newcomer.NoteOnOrder,
            newcomer.NoteOffOrder));
    }

    [Fact]
    public void HumanizeTemplateNotesDeletesHardBoundaryResultAndUndoRestores()
    {
        (MidoraProject project, EventInstrument instrument, SubVoice voice) = CreateTemplateVoice(100);
        TemplateEvent note = TemplateEvent.Note(project, 90, 10, 64, 100);
        Active(project, voice).Events.Add(note);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.HumanizeTemplateNotes(
            instrument.Id,
            voice.Id,
            [note.Id],
            new(
                TimelineHumanizeField.Override(100, 100),
                TimelineHumanizeField.Disabled,
                TimelineHumanizeField.Disabled,
                Seed: 1));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Empty(Active(project, voice).Events);
        Assert.Empty(command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Same(note, Assert.Single(Active(project, voice).Events));
    }

    [Fact]
    public void HumanizeCancellationStopsBeforeMutation()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 10, 20, 60, 100);
        var command = Assert.IsAssignableFrom<ICancellableProjectEditCommand>(
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                segment.Id,
                [note.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    Seed: 1)));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => command.Prepare(project, cancellation.Token));
        Assert.Equal((10L, 20L, 100), (note.StartTick, note.LengthTicks, note.Velocity));
    }

    [Fact]
    public void FixedSplitUsesOwnerWideKnivesKeepsFirstIdsAndSelectsAllFragments()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote first = AddLogicalNote(project, segment, 5, 20, 60, 100);
        LogicalNote second = AddLogicalNote(project, segment, 15, 20, 64, 80);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(4, Active(project, segment).Notes.Count);
        LogicalNote appliedFirst = Active(project, segment).Notes.Single(value => value.Id == first.Id);
        LogicalNote appliedSecond = Active(project, segment).Notes.Single(value => value.Id == second.Id);
        Assert.Equal((5L, 10L), (appliedFirst.StartTick, appliedFirst.LengthTicks));
        Assert.Equal((15L, 10L), (appliedSecond.StartTick, appliedSecond.LengthTicks));
        Assert.Equal([5L, 15L, 15L, 25L],
            Active(project, segment).Notes.OrderBy(static value => value.StartTick)
                .ThenBy(static value => value.Note)
                .Select(static value => value.StartTick).ToArray());
        Assert.Equal(4, command.ResultSelectionIds.Count);
        Assert.Contains(first.Id, command.ResultSelectionIds);
        Assert.Contains(second.Id, command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, segment).Notes);
        Assert.Equal((20L, 20L), (first.LengthTicks, second.LengthTicks));
    }

    [Fact]
    public void MaximumPieceSplitPreservesDirectPayloadAndCreatesOrderedCutEndpoints()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote note = AddDirectNote(project, segment, 0, 10, 60, 90, 37, 11, 19);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitDirectMidiNotes(
            segment.Id,
            [note.Id],
            new() { Mode = NoteSplitMode.MaximumPieceCount, MaximumPieceCount = 3 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        DirectMidiNote[] result = Active(project, segment).Notes.OrderBy(static value => value.StartTick).ToArray();
        Assert.Equal([3L, 3L, 4L], result.Select(static value => value.LengthTicks).ToArray());
        Assert.Equal(
            [(37, 11L, 0L), (37, 1L, 0L), (37, 1L, 19L)],
            result.Select(static value => (
                value.NoteOffVelocity,
                value.NoteOnOrder,
                value.NoteOffOrder)));
        Assert.Equal(note.Id, result[0].Id);
        MidoraId[] frozenIds = result.Select(static value => value.Id).ToArray();
        prepared.Undo(project);
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
        Assert.Equal((10L, 11L, 19L), (
            note.LengthTicks,
            note.NoteOnOrder,
            note.NoteOffOrder));

        prepared.Apply(project);
        DirectMidiNote[] redone = Active(project, segment).Notes.OrderBy(static value => value.StartTick).ToArray();
        Assert.Equal(frozenIds, redone.Select(static value => value.Id));
        Assert.Equal(
            [(11L, 0L), (1L, 0L), (1L, 19L)],
            redone.Select(static value => (value.NoteOnOrder, value.NoteOffOrder)));
    }

    [Fact]
    public void MaximumPieceSplitIgnoresExpressionMaximumCutsAndKeepsItsRequestedBlocks()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 100, 60, 90);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
            segment.Id,
            [note.Id],
            new()
            {
                Mode = NoteSplitMode.MaximumPieceCount,
                MaximumPieceCount = 10,
                MaximumCuts = 1
            });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(Enumerable.Repeat(10L, 10), Active(project, segment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => value.LengthTicks));
        Assert.Equal(10, command.ResultSelectionIds.Count);
        prepared.Undo(project);
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
        Assert.Equal(100, note.LengthTicks);
    }

    [Theory]
    [InlineData(NoteSplitMode.FixedPieceLength)]
    [InlineData(NoteSplitMode.MaximumPieceCount)]
    public void AutomaticSplitModesProduceMoreThanTheExpressionDefaultCutLimit(
        NoteSplitMode mode)
    {
        const int expectedPieces = 65_537;
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, expectedPieces, 60, 90);
        NoteSplitOptions options = mode switch
        {
            NoteSplitMode.FixedPieceLength => new()
            {
                Mode = mode,
                FixedPieceLengthTicks = 1,
                MaximumCuts = 1
            },
            NoteSplitMode.MaximumPieceCount => new()
            {
                Mode = mode,
                MaximumPieceCount = expectedPieces,
                MaximumCuts = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [note.Id], options);

        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));

        prepared.Apply(project);
        IReadOnlyList<LogicalNote> result = Active(project, segment).Notes;
        Assert.Equal(expectedPieces, command.ResultSelectionIds.Count);
        Assert.Equal(expectedPieces, result.Count);
        Assert.All(result, static value => Assert.Equal(1, value.LengthTicks));
    }

    [Fact]
    public void DirectSplitRejectsAStaleStableIdReservationWithoutPublishingTheRoot()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote first = AddDirectNote(project, segment, 0, 10, 60, 90, 17, 11, 12);
        DirectMidiNote second = AddDirectNote(project, segment, 0, 10, 61, 80, 29, 13, 14);
        _ = AddDirectNote(project, segment, 5, 2, 70, 70, 0, 30, 31);
        _ = AddDirectNote(project, segment, 0, 5, 71, 70, 0, 32, 40);
        _ = AddDirectEvent(project, segment, 5, 1, 64, 50);
        segment.OpaqueEvents.Add(new(project)
        {
            Tick = 5,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x01,
            Payload = [0x41],
            Order = 60
        });
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitDirectMidiNotes(
            segment.Id,
            [first.Id, second.Id],
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 });

        MidiSegment originalRoot = Active(project, segment);
        long reservedAt = project.NextStableId;
        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Same(originalRoot, Active(project, segment));
        Assert.Equal(reservedAt, project.NextStableId);
        for (int index = 0; index < 100; index++) _ = new DirectMidiNote(project);

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(originalRoot, Active(project, segment));
        Assert.Equal(4, originalRoot.Notes.Count);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void DirectSplitFreezesIdsAndAdvancesTheHighWaterOnlyOnFirstApply()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote first = AddDirectNote(project, segment, 0, 10, 60, 90, 17, 11, 12);
        DirectMidiNote second = AddDirectNote(project, segment, 0, 10, 61, 80, 29, 13, 14);
        _ = AddDirectEvent(project, segment, 5, 1, 64, 50);
        long beforePrepare = project.NextStableId;
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitDirectMidiNotes(
            segment.Id,
            [first.Id, second.Id],
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Equal(beforePrepare, project.NextStableId);
        prepared.Apply(project);

        DirectMidiNote[] firstPieces = Active(project, segment).Notes.Where(value => value.Key == 60)
            .OrderBy(static value => value.StartTick).ToArray();
        DirectMidiNote[] secondPieces = Active(project, segment).Notes.Where(value => value.Key == 61)
            .OrderBy(static value => value.StartTick).ToArray();
        Assert.Equal((11L, 51L), (firstPieces[0].NoteOnOrder, firstPieces[0].NoteOffOrder));
        Assert.Equal((52L, 12L), (firstPieces[1].NoteOnOrder, firstPieces[1].NoteOffOrder));
        Assert.Equal((13L, 53L), (secondPieces[0].NoteOnOrder, secondPieces[0].NoteOffOrder));
        Assert.Equal((54L, 14L), (secondPieces[1].NoteOnOrder, secondPieces[1].NoteOffOrder));
        Assert.Equal(beforePrepare + 2, project.NextStableId);
        MidoraId[] frozenIds = Active(project, segment).Notes.Select(static value => value.Id).ToArray();
        long afterApply = project.NextStableId;

        prepared.Undo(project);
        Assert.Equal(afterApply, project.NextStableId);
        Assert.Same(segment, Active(project, segment));
        prepared.Apply(project);
        Assert.Equal(afterApply, project.NextStableId);
        Assert.Equal(frozenIds, Active(project, segment).Notes.Select(static value => value.Id));
    }

    [Fact]
    public void DirectSplitOrderOverflowFailsBeforePublishingAnyChange()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote source = AddDirectNote(project, segment, 0, 10, 60, 90, 17, 11, 12);
        _ = AddDirectEvent(project, segment, 5, 1, 64, long.MaxValue);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitDirectMidiNotes(
            segment.Id,
            [source.Id],
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 });

        Assert.Throws<OverflowException>(() => command.Prepare(project));
        Assert.Same(source, Assert.Single(Active(project, segment).Notes));
        Assert.Equal((0L, 10L, 11L, 12L), (
            source.StartTick,
            source.LengthTicks,
            source.NoteOnOrder,
            source.NoteOffOrder));
    }

    [Fact]
    public void SplitCollisionReducerUsesSourceThenFragmentOrderAcrossAllNoteKinds()
    {
        NoteSplitOptions options = new()
        {
            Mode = NoteSplitMode.FixedPieceLength,
            FixedPieceLengthTicks = 10
        };

        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalEarly = AddLogicalNote(
            logicalProject, logicalSegment, 0, 20, 60, 100);
        LogicalNote logicalLater = AddLogicalNote(
            logicalProject, logicalSegment, 10, 10, 60, 70);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.SplitLogicalNotes(
                logicalSegment.Id,
                [logicalEarly.Id, logicalLater.Id],
                options);
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);
        LogicalNote logicalTail = Assert.Single(
            Active(logicalProject, logicalSegment).Notes,
            value => value.Id != logicalEarly.Id);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(logicalProject, logicalSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.Equal([logicalEarly.Id, logicalTail.Id], logicalCommand.ResultSelectionIds);
        Assert.DoesNotContain(logicalLater, Active(logicalProject, logicalSegment).Notes);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal([logicalEarly, logicalLater], Active(logicalProject, logicalSegment).Notes);
        Assert.Equal([logicalEarly.Id, logicalLater.Id], logicalCommand.ResultSelectionIds);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        DirectMidiNote directEarly = AddDirectNote(
            directProject, directSegment, 0, 20, 60, 100, 31, 11, 19);
        DirectMidiNote directLater = AddDirectNote(
            directProject, directSegment, 10, 10, 60, 70, 47, 23, 29);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                directSegment.Id,
                [directEarly.Id, directLater.Id],
                options);
        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);
        DirectMidiNote directTail = Assert.Single(
            Active(directProject, directSegment).Notes,
            value => value.Id != directEarly.Id);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(directProject, directSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.Equal((31, 25L, 19L), (
            directTail.NoteOffVelocity,
            directTail.NoteOnOrder,
            directTail.NoteOffOrder));
        Assert.Equal([directEarly.Id, directTail.Id], directCommand.ResultSelectionIds);
        Assert.DoesNotContain(directLater, Active(directProject, directSegment).Notes);
        directPrepared.Undo(directProject);
        Assert.Equal([directEarly, directLater], Active(directProject, directSegment).Notes);
        Assert.Equal([directEarly.Id, directLater.Id], directCommand.ResultSelectionIds);

        (MidoraProject templateProject, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent templateEarly = TemplateEvent.Note(
            templateProject, 0, 20, 60, 100);
        TemplateEvent templateLater = TemplateEvent.Note(
            templateProject, 10, 10, 60, 70);
        Active(templateProject, voice).Events.AddRange([templateEarly, templateLater]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.SplitTemplateNotes(
                instrument.Id,
                voice.Id,
                [templateEarly.Id, templateLater.Id],
                options);
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        TemplateEvent templateTail = Assert.Single(
            Active(templateProject, voice).Events,
            value => value.Id != templateEarly.Id);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(templateProject, voice).Events
            .OrderBy(static value => value.Tick)
            .Select(static value => (value.Tick, value.LengthTicks)));
        Assert.Equal([templateEarly.Id, templateTail.Id], templateCommand.ResultSelectionIds);
        Assert.DoesNotContain(templateLater, Active(templateProject, voice).Events);
        templatePrepared.Undo(templateProject);
        Assert.Equal([templateEarly, templateLater], Active(templateProject, voice).Events);
        Assert.Equal([templateEarly.Id, templateLater.Id], templateCommand.ResultSelectionIds);
    }

    [Fact]
    public void SplitReducesSelectedImportedDuplicatesAtTheOriginalStartAcrossAllNoteKinds()
    {
        NoteSplitOptions options = new()
        {
            Mode = NoteSplitMode.FixedPieceLength,
            FixedPieceLengthTicks = 10
        };

        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalFirst = AddLogicalNote(
            logicalProject, logicalSegment, 0, 20, 60, 100);
        LogicalNote logicalDuplicate = AddLogicalNote(
            logicalProject, logicalSegment, 0, 20, 60, 70);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.SplitLogicalNotes(
                logicalSegment.Id,
                [logicalFirst.Id, logicalDuplicate.Id],
                options);
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(logicalProject, logicalSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.Equal(logicalFirst.Id, logicalCommand.ResultSelectionIds[0]);
        Assert.DoesNotContain(logicalDuplicate.Id, logicalCommand.ResultSelectionIds);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        DirectMidiNote directFirst = AddDirectNote(
            directProject, directSegment, 0, 20, 60, 100, 31, 11, 19);
        DirectMidiNote directDuplicate = AddDirectNote(
            directProject, directSegment, 0, 20, 60, 70, 47, 23, 29);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                directSegment.Id,
                [directFirst.Id, directDuplicate.Id],
                options);
        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(directProject, directSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.Equal(directFirst.Id, directCommand.ResultSelectionIds[0]);
        Assert.DoesNotContain(directDuplicate.Id, directCommand.ResultSelectionIds);

        (MidoraProject templateProject, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent templateFirst = TemplateEvent.Note(
            templateProject, 0, 20, 60, 100);
        TemplateEvent templateDuplicate = TemplateEvent.Note(
            templateProject, 0, 20, 60, 70);
        Active(templateProject, voice).Events.AddRange([templateFirst, templateDuplicate]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.SplitTemplateNotes(
                instrument.Id,
                voice.Id,
                [templateFirst.Id, templateDuplicate.Id],
                options);
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(templateProject, voice).Events
            .OrderBy(static value => value.Tick)
            .Select(static value => (value.Tick, value.LengthTicks)));
        Assert.Equal(templateFirst.Id, templateCommand.ResultSelectionIds[0]);
        Assert.DoesNotContain(templateDuplicate.Id, templateCommand.ResultSelectionIds);
    }

    [Fact]
    public void SplitFragmentDisplacesLaterStationaryIncumbentAcrossAllNoteKinds()
    {
        NoteSplitOptions options = new()
        {
            Mode = NoteSplitMode.FixedPieceLength,
            FixedPieceLengthTicks = 10
        };

        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalSource = AddLogicalNote(
            logicalProject, logicalSegment, 0, 20, 60, 100);
        LogicalNote logicalIncumbent = AddLogicalNote(
            logicalProject, logicalSegment, 10, 5, 60, 70);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.SplitLogicalNotes(
                logicalSegment.Id, [logicalSource.Id], options);
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(logicalProject, logicalSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.DoesNotContain(logicalIncumbent, Active(logicalProject, logicalSegment).Notes);
        Assert.Equal(2, logicalCommand.ResultSelectionIds.Count);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal([logicalSource, logicalIncumbent], Active(logicalProject, logicalSegment).Notes);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        DirectMidiNote directSource = AddDirectNote(
            directProject, directSegment, 0, 20, 60, 100, 31, 11, 19);
        DirectMidiNote directIncumbent = AddDirectNote(
            directProject, directSegment, 10, 5, 60, 70, 47, 23, 29);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                directSegment.Id, [directSource.Id], options);
        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(directProject, directSegment).Notes
            .OrderBy(static value => value.StartTick)
            .Select(static value => (value.StartTick, value.LengthTicks)));
        Assert.DoesNotContain(directIncumbent, Active(directProject, directSegment).Notes);
        Assert.Equal(2, directCommand.ResultSelectionIds.Count);
        directPrepared.Undo(directProject);
        Assert.Equal([directSource, directIncumbent], Active(directProject, directSegment).Notes);

        (MidoraProject templateProject, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent templateSource = TemplateEvent.Note(
            templateProject, 0, 20, 60, 100);
        TemplateEvent templateIncumbent = TemplateEvent.Note(
            templateProject, 10, 5, 60, 70);
        Active(templateProject, voice).Events.AddRange([templateSource, templateIncumbent]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.SplitTemplateNotes(
                instrument.Id, voice.Id, [templateSource.Id], options);
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        Assert.Equal([(0L, 10L), (10L, 10L)], Active(templateProject, voice).Events
            .OrderBy(static value => value.Tick)
            .Select(static value => (value.Tick, value.LengthTicks)));
        Assert.DoesNotContain(templateIncumbent, Active(templateProject, voice).Events);
        Assert.Equal(2, templateCommand.ResultSelectionIds.Count);
        templatePrepared.Undo(templateProject);
        Assert.Equal([templateSource, templateIncumbent], Active(templateProject, voice).Events);
    }

    [Fact]
    public void ExpressionSplitUsesZeroBasedKnifeIndexAndGlobalRelativeTick()
    {
        (MidoraProject project, EventInstrument instrument, SubVoice voice) = CreateTemplateVoice(100);
        TemplateEvent note = TemplateEvent.Note(project, 10, 10, 60, 100);
        Active(project, voice).Events.Add(note);
        using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile(
            "=i == 0 && tr == 0 ? 2.5 : i == 1 && tr == 3 ? 2.5 : 100");
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitTemplateNotes(
            instrument.Id,
            voice.Id,
            [note.Id],
            new() { Mode = NoteSplitMode.Expression, ExpressionProgram = program });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal([3L, 3L, 4L], Active(project, voice).Events
            .OrderBy(static value => value.Tick)
            .Select(static value => value.LengthTicks).ToArray());
        Assert.All(Active(project, voice).Events, static value => Assert.True(value.LengthTicks > 0));
        Assert.Equal(note.Id, Active(project, voice).Events.OrderBy(static value => value.Tick).First().Id);
        prepared.Undo(project);
        Assert.Same(note, Assert.Single(Active(project, voice).Events));
    }

    [Fact]
    public void SegmentHumanizePreservesHiddenLogicalAndDirectNotesOutsideTheActiveCrop()
    {
        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        logicalSegment.ProjectStartTick = 0;
        logicalSegment.ContentOffsetTick = 100;
        LogicalNote logical = AddLogicalNote(logicalProject, logicalSegment, 50, 20, 60, 80);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                logicalSegment.Id,
                [logical.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(99, 99),
                    Seed: 1));

        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);

        LogicalNote appliedLogical = Assert.Single(Active(logicalProject, logicalSegment).Notes);
        Assert.Equal((50L, 20L, 99), (
            appliedLogical.StartTick,
            appliedLogical.LengthTicks,
            appliedLogical.Velocity));
        Assert.Equal([logical.Id], logicalCommand.ResultSelectionIds);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal((50L, 20L, 80), (
            logical.StartTick,
            logical.LengthTicks,
            logical.Velocity));

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        directSegment.ProjectStartTick = 0;
        directSegment.ContentOffsetTick = 100;
        DirectMidiNote direct = AddDirectNote(
            directProject,
            directSegment,
            50,
            20,
            60,
            80,
            17,
            1,
            2);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                directSegment.Id,
                [direct.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(30, 30),
                    TimelineHumanizeField.Disabled,
                    Seed: 1));

        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);

        DirectMidiNote appliedDirect = Assert.Single(Active(directProject, directSegment).Notes);
        Assert.Equal((50L, 30L, 80, 17), (
            appliedDirect.StartTick,
            appliedDirect.LengthTicks,
            appliedDirect.NoteOnVelocity,
            appliedDirect.NoteOffVelocity));
        Assert.Equal([direct.Id], directCommand.ResultSelectionIds);
        directPrepared.Undo(directProject);
        Assert.Equal((50L, 20L, 80, 17), (
            direct.StartTick,
            direct.LengthTicks,
            direct.NoteOnVelocity,
            direct.NoteOffVelocity));
    }

    [Fact]
    public void ExpressionSplitCountsEmptyGapKnivesAndUsesTheirRelativeTickForTheNextKnife()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote first = AddLogicalNote(project, segment, 0, 2, 60, 100);
        LogicalNote second = AddLogicalNote(project, segment, 9, 3, 64, 90);
        using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile(
            "=i == 0 && tr == 0 ? 5 : i == 1 && tr == 5 ? 5 : 100");
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            new() { Mode = NoteSplitMode.Expression, ExpressionProgram = program });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal([(0L, 2L, 60), (9L, 1L, 64), (10L, 2L, 64)],
            Active(project, segment).Notes
                .OrderBy(static value => value.StartTick)
                .Select(static value => (value.StartTick, value.LengthTicks, value.Note))
                .ToArray());
        Assert.Equal(3, command.ResultSelectionIds.Count);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, segment).Notes);
    }

    [Fact]
    public void ExpressionSplitKnifeAtTheSelectionRightDoesNotCreateAZeroLengthFragment()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 10, 60, 100);
        using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile("=10");
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
            segment.Id,
            [note.Id],
            new() { Mode = NoteSplitMode.Expression, ExpressionProgram = program });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        LogicalNote result = Assert.Single(Active(project, segment).Notes);
        Assert.Equal((note.Id, 0L, 10L), (result.Id, result.StartTick, result.LengthTicks));
        Assert.Equal([note.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
    }

    [Fact]
    public void ExpressionSplitArithmeticFailuresPublishNothing()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 10, 60, 100);

        using (NoteSplitExpressionProgram nonFinite = NoteSplitExpressionProgram.Compile(
                   "=1.0 / (i - i)"))
        {
            ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new() { Mode = NoteSplitMode.Expression, ExpressionProgram = nonFinite });
            Assert.Throws<InvalidOperationException>(() => command.Prepare(project));
            Assert.Same(note, Assert.Single(Active(project, segment).Notes));
            Assert.Equal((0L, 10L), (note.StartTick, note.LengthTicks));
        }

        using (NoteSplitExpressionProgram overflow = NoteSplitExpressionProgram.Compile(
                   "=Abs(-9223372036854775807L - 1L)"))
        {
            ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new() { Mode = NoteSplitMode.Expression, ExpressionProgram = overflow });
            Assert.Throws<OverflowException>(() => command.Prepare(project));
            Assert.Same(note, Assert.Single(Active(project, segment).Notes));
            Assert.Equal((0L, 10L), (note.StartTick, note.LengthTicks));
        }
    }

    [Fact]
    public void ExpressionSplitRejectsMaximumCutsAboveTheFormalHardLimitWithoutMutation()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 10, 60, 100);
        using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile("=1");
        var command = Assert.IsAssignableFrom<ICancellableProjectEditCommand>(
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new()
                {
                    Mode = NoteSplitMode.Expression,
                    ExpressionProgram = program,
                    MaximumCuts = NoteSplitOptions.MaximumSupportedCuts + 1
                }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            command.Prepare(project, CancellationToken.None));
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
        Assert.Equal((0L, 10L), (note.StartTick, note.LengthTicks));
    }

    [Theory]
    [InlineData(NoteSplitMode.FixedPieceLength)]
    [InlineData(NoteSplitMode.MaximumPieceCount)]
    public void AutomaticSplitModesRejectTheoreticalCutsAboveTheHardLimitBeforeAllocation(
        NoteSplitMode mode)
    {
        long pieceCount = (long)NoteSplitOptions.MaximumSupportedCuts + 2;
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, pieceCount, 60, 100);
        NoteSplitOptions options = mode switch
        {
            NoteSplitMode.FixedPieceLength => new()
            {
                Mode = mode,
                FixedPieceLengthTicks = 1,
                MaximumCuts = 1
            },
            NoteSplitMode.MaximumPieceCount => new()
            {
                Mode = mode,
                MaximumPieceCount = checked((int)pieceCount),
                MaximumCuts = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [note.Id], options);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => command.Prepare(project));

        Assert.Contains("formal hard limit", error.Message, StringComparison.Ordinal);
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
        Assert.Equal((0L, pieceCount), (note.StartTick, note.LengthTicks));
        Assert.Empty(command.ResultSelectionIds);
    }

    [Fact]
    public void OneTickSplitStillRejectsInvalidModeSpecificOptions()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote note = AddLogicalNote(project, segment, 0, 1, 60, 100);

        ITimelineSelectionResultEditCommand invalidFixed =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 0
                });
        ITimelineSelectionResultEditCommand invalidMaximumPieces =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new()
                {
                    Mode = NoteSplitMode.MaximumPieceCount,
                    MaximumPieceCount = 0
                });
        ITimelineSelectionResultEditCommand invalidExpression =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [note.Id],
                new()
                {
                    Mode = NoteSplitMode.Expression,
                    ExpressionProgram = null
                });

        Assert.Throws<ArgumentOutOfRangeException>(() => invalidFixed.Prepare(project));
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidMaximumPieces.Prepare(project));
        Assert.Throws<ArgumentException>(() => invalidExpression.Prepare(project));
        Assert.Same(note, Assert.Single(Active(project, segment).Notes));
        Assert.Equal((0L, 1L), (note.StartTick, note.LengthTicks));
    }

    [Fact]
    public void SplitChecksTotalResultLimitBeforePublishingAnyFragments()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote first = AddLogicalNote(project, segment, 0, 10, 60, 100);
        LogicalNote second = AddLogicalNote(project, segment, 0, 10, 64, 100);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [first.Id, second.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 5,
                    MaximumResultObjects = 3
                });

        Assert.Throws<InvalidOperationException>(() => command.Prepare(project));
        Assert.Equal([first, second], Active(project, segment).Notes);
        Assert.Equal((10L, 10L), (first.LengthTicks, second.LengthTicks));
    }

    [Fact]
    public void JoinLogicalNotesUsesGapAndKeepsFirstIdentity()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote first = AddLogicalNote(project, segment, 0, 5, 60, 100);
        LogicalNote second = AddLogicalNote(project, segment, 7, 3, 60, 70);
        LogicalNote otherKey = AddLogicalNote(project, segment, 2, 3, 64, 80);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.JoinLogicalNotes(
            segment.Id,
            [first.Id, second.Id, otherKey.Id],
            new(MaximumGapTicks: 2));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(2, Active(project, segment).Notes.Count);
        LogicalNote joined = Active(project, segment).Notes.Single(value => value.Id == first.Id);
        Assert.Equal(10, joined.LengthTicks);
        Assert.Equal(100, joined.Velocity);
        Assert.Equal([first.Id, otherKey.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second, otherKey], Active(project, segment).Notes);
    }

    [Fact]
    public void JoinUsesTheCheckedGapDifferenceNearInt64Maximum()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        LogicalNote first = AddLogicalNote(
            project,
            segment,
            long.MaxValue - 2,
            1,
            60,
            100);
        LogicalNote second = AddLogicalNote(
            project,
            segment,
            long.MaxValue - 1,
            1,
            60,
            80);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.JoinLogicalNotes(
                segment.Id,
                [first.Id, second.Id],
                new(MaximumGapTicks: 2));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        LogicalNote joined = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(first.Id, joined.Id);
        Assert.Equal(2, joined.LengthTicks);
        Assert.Equal([first.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, segment).Notes);
        Assert.Equal((1L, 1L), (first.LengthTicks, second.LengthTicks));
    }

    [Fact]
    public void JoinDirectNotesUsesFirstOnAndLastOffPayload()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote first = AddDirectNote(project, segment, 0, 5, 60, 100, 12, 1, 2);
        DirectMidiNote last = AddDirectNote(project, segment, 5, 5, 60, 80, 34, 3, 4);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.JoinDirectMidiNotes(
            segment.Id,
            [first.Id, last.Id],
            new());

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        DirectMidiNote joined = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(first.Id, joined.Id);
        Assert.Equal((10L, 100, 34, 1L, 4L), (
            joined.LengthTicks,
            joined.NoteOnVelocity,
            joined.NoteOffVelocity,
            joined.NoteOnOrder,
            joined.NoteOffOrder));
        prepared.Undo(project);
        Assert.Equal([first, last], Active(project, segment).Notes);
    }

    [Fact]
    public void JoinTemplateNotesKeepsFirstPayload()
    {
        (MidoraProject project, EventInstrument instrument, SubVoice voice) = CreateTemplateVoice(100);
        TemplateEvent first = TemplateEvent.Note(project, 0, 5, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 5, 5, 60, 70);
        Active(project, voice).Events.AddRange([first, second]);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.JoinTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            new());

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        TemplateEvent joined = Assert.Single(Active(project, voice).Events);
        Assert.Equal(first.Id, joined.Id);
        Assert.Equal((10L, 100), (joined.LengthTicks, joined.Value));
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, voice).Events);
    }

    [Fact]
    public void LogicalNoteQuantizeUsesAbsoluteProjectGridTieEarlierAndLaterNoteLoses()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        segment.ProjectStartTick = 100;
        segment.ContentOffsetTick = 50;
        LogicalNote first = AddLogicalNote(project, segment, 100, 100, 60, 100);
        LogicalNote second = AddLogicalNote(project, segment, 99, 20, 60, 80);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.QuantizeLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            new(TimelineQuantizeGrid.FromCustomTicks(100), NoteQuantizeMode.StartAndEnd));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        LogicalNote survivor = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(first.Id, survivor.Id);
        Assert.Equal((50L, 100L), (survivor.StartTick, survivor.LengthTicks));
        Assert.Equal([first.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, segment).Notes);
        Assert.Equal((100L, 99L), (first.StartTick, second.StartTick));
    }

    [Fact]
    public void DirectAndTemplateNoteQuantizePreservePayloadAndUseTheirOrigins()
    {
        (MidoraProject midiProject, _, MidiSegment midiSegment) = CreateMidiSegment();
        midiSegment.ProjectStartTick = 100;
        midiSegment.ContentOffsetTick = 50;
        DirectMidiNote direct = AddDirectNote(midiProject, midiSegment, 74, 20, 60, 90, 22, 7, 9);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                midiSegment.Id,
                [direct.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(50)));
        IPreparedProjectEdit directPrepared = directCommand.Prepare(midiProject);
        directPrepared.Apply(midiProject);
        DirectMidiNote appliedDirect = Assert.Single(Active(midiProject, midiSegment).Notes);
        Assert.Equal(direct.Id, appliedDirect.Id);
        Assert.Equal(50, appliedDirect.StartTick);
        Assert.Equal((22, 7L, 9L), (
            appliedDirect.NoteOffVelocity,
            appliedDirect.NoteOnOrder,
            appliedDirect.NoteOffOrder));
        directPrepared.Undo(midiProject);

        (MidoraProject templateProject, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent template = TemplateEvent.Note(templateProject, 26, 30, 64, 77);
        Active(templateProject, voice).Events.Add(template);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                instrument.Id,
                voice.Id,
                [template.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(20)));
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        TemplateEvent appliedTemplate = Assert.Single(Active(templateProject, voice).Events);
        Assert.Equal(template.Id, appliedTemplate.Id);
        Assert.Equal((20L, 30L, 77),
            (appliedTemplate.Tick, appliedTemplate.LengthTicks, appliedTemplate.Value));
        templatePrepared.Undo(templateProject);
        Assert.Equal(26, template.Tick);
    }

    [Fact]
    public void SegmentNoteQuantizePreservesHiddenContentThatMapsBeforeProjectZero()
    {
        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        logicalSegment.ProjectStartTick = 0;
        logicalSegment.ContentOffsetTick = 100;
        LogicalNote logical = AddLogicalNote(logicalProject, logicalSegment, 55, 20, 60, 90);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                logicalSegment.Id,
                [logical.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);

        LogicalNote appliedLogical = Assert.Single(Active(logicalProject, logicalSegment).Notes);
        Assert.Equal((50L, 20L), (appliedLogical.StartTick, appliedLogical.LengthTicks));
        Assert.Equal([logical.Id], logicalCommand.ResultSelectionIds);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal(55, logical.StartTick);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        directSegment.ProjectStartTick = 0;
        directSegment.ContentOffsetTick = 100;
        DirectMidiNote direct = AddDirectNote(
            directProject,
            directSegment,
            55,
            20,
            60,
            90,
            22,
            7,
            9);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                directSegment.Id,
                [direct.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);

        DirectMidiNote appliedDirect = Assert.Single(Active(directProject, directSegment).Notes);
        Assert.Equal((50L, 20L, 22, 7L, 9L), (
            appliedDirect.StartTick,
            appliedDirect.LengthTicks,
            appliedDirect.NoteOffVelocity,
            appliedDirect.NoteOnOrder,
            appliedDirect.NoteOffOrder));
        Assert.Equal([direct.Id], directCommand.ResultSelectionIds);
        directPrepared.Undo(directProject);
        Assert.Equal(55, direct.StartTick);
    }

    [Fact]
    public void TemplateStartOnlyQuantizeKeepsGateOrDiscardsAtTheHardBoundary()
    {
        (MidoraProject project, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent survivor = TemplateEvent.Note(project, 74, 19, 60, 90);
        TemplateEvent crossing = TemplateEvent.Note(project, 76, 24, 62, 80);
        TemplateEvent startOutside = TemplateEvent.Note(project, 96, 1, 64, 70);
        Active(project, voice).Events.AddRange([survivor, crossing, startOutside]);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                instrument.Id,
                voice.Id,
                [survivor.Id, crossing.Id, startOutside.Id],
                new(
                    TimelineQuantizeGrid.FromCustomTicks(20),
                    NoteQuantizeMode.StartOnly));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        TemplateEvent applied = Assert.Single(Active(project, voice).Events);
        Assert.Equal(survivor.Id, applied.Id);
        Assert.Equal((80L, 19L), (applied.Tick, applied.LengthTicks));
        Assert.Equal([survivor.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([survivor, crossing, startOutside], Active(project, voice).Events);
        Assert.Equal((74L, 19L), (survivor.Tick, survivor.LengthTicks));
        Assert.Equal((76L, 24L), (crossing.Tick, crossing.LengthTicks));
        Assert.Equal((96L, 1L), (startOutside.Tick, startOutside.LengthTicks));
        Assert.Equal(
            [survivor.Id, crossing.Id, startOutside.Id],
            command.ResultSelectionIds);
    }

    [Fact]
    public void LogicalPointQuantizeKeepsLaterSelectedAndRestoresBothOnUndo()
    {
        (MidoraProject project, LogicalTrack track, Segment segment) = CreateLogicalSegment();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalParameterDefinition definition = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(definition);
        LogicalParameterLane lane = new(project) { ParameterId = definition.Id };
        CurvePoint first = new(project, 11, 0.25, CurveInterpolation.Step);
        CurvePoint second = new(project, 14, 0.75, CurveInterpolation.Step);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                segment.Id,
                lane.Id,
                [first.Id, second.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        CurvePoint survivor = Assert.Single(Active(project, lane).Points);
        Assert.Equal(second.Id, survivor.Id);
        Assert.Equal((10L, 0.75), (survivor.Tick, survivor.Value));
        Assert.Equal([second.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, lane).Points);
        _ = track;
    }

    [Fact]
    public void DirectEventQuantizeKeepsLaterUnknownControllerAndRestoresIncumbent()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiChannelEvent first = AddDirectEvent(project, segment, 11, 119, 20, 1);
        DirectMidiChannelEvent second = AddDirectEvent(project, segment, 14, 119, 80, 2);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                segment.Id,
                [first.Id, second.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        DirectMidiChannelEvent applied = Assert.Single(Active(project, segment).ChannelEvents);
        Assert.Equal(second.Id, applied.Id);
        Assert.Equal((10L, 119, 80), (applied.Tick, applied.Data1, applied.Data2));
        Assert.Equal([second.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, segment).ChannelEvents);
    }

    [Fact]
    public void SegmentEventQuantizePreservesHiddenLogicalAndDirectPointsBeforeProjectZero()
    {
        (MidoraProject logicalProject, LogicalParameterLane lane, Segment logicalSegment) =
            CreateLogicalParameterLane();
        logicalSegment.ProjectStartTick = 0;
        logicalSegment.ContentOffsetTick = 100;
        CurvePoint point = new(logicalProject, 55, 0.75, CurveInterpolation.Step);
        lane.Points.Add(point);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                logicalSegment.Id,
                lane.Id,
                [point.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);

        CurvePoint appliedPoint = Assert.Single(Active(logicalProject, lane).Points);
        Assert.Equal((50L, 0.75), (appliedPoint.Tick, appliedPoint.Value));
        Assert.Equal([point.Id], logicalCommand.ResultSelectionIds);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal(55, point.Tick);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        directSegment.ProjectStartTick = 0;
        directSegment.ContentOffsetTick = 100;
        DirectMidiChannelEvent direct = AddDirectEvent(
            directProject,
            directSegment,
            55,
            11,
            96,
            7);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                directSegment.Id,
                [direct.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);

        DirectMidiChannelEvent appliedDirect =
            Assert.Single(Active(directProject, directSegment).ChannelEvents);
        Assert.Equal((50L, 11, 96, 7L), (
            appliedDirect.Tick,
            appliedDirect.Data1,
            appliedDirect.Data2,
            appliedDirect.Order));
        Assert.Equal([direct.Id], directCommand.ResultSelectionIds);
        directPrepared.Undo(directProject);
        Assert.Equal(55, direct.Tick);
    }

    [Fact]
    public void DirectEventQuantizeReducesLargeCollisionSetsInFormalOrderAndUndoRestores()
    {
        const int targetCount = 2_048;
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        List<DirectMidiChannelEvent> values = new(targetCount * 2);
        for (int target = 0; target < targetCount; target++)
        {
            long baseTick = target * 10L;
            values.Add(AddDirectEvent(project, segment, baseTick + 1, 74, 20, target * 2L));
            values.Add(AddDirectEvent(project, segment, baseTick + 4, 74, 80, target * 2L + 1));
        }
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                segment.Id,
                values.Select(static value => value.Id).ToArray(),
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(targetCount, Active(project, segment).ChannelEvents.Count);
        Assert.All(Active(project, segment).ChannelEvents, static value => Assert.Equal(80, value.Data2));
        Assert.Equal(targetCount, command.ResultSelectionIds.Count);
        prepared.Undo(project);
        Assert.Equal(targetCount * 2, Active(project, segment).ChannelEvents.Count);
        Assert.Equal(values, Active(project, segment).ChannelEvents);
    }

    [Fact]
    public void TemplateEventQuantizeUsesLaterWinsAndUndoRestoresCompositeEvents()
    {
        (MidoraProject project, EventInstrument instrument, SubVoice voice) = CreateTemplateVoice(100);
        TemplateEvent first = TemplateEvent.Bank(project, 11, msb: 1, lsb: 2);
        TemplateEvent second = TemplateEvent.Bank(project, 14, msb: 3, lsb: 4);
        Active(project, voice).Events.AddRange([first, second]);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeTemplateEvents(
                instrument.Id,
                voice.Id,
                [first.Id, second.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        TemplateEvent applied = Assert.Single(Active(project, voice).Events);
        Assert.Equal(second.Id, applied.Id);
        Assert.Equal((10L, 3, 4), (applied.Tick, applied.Value, applied.SecondaryValue));
        Assert.Equal([second.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal([first, second], Active(project, voice).Events);
    }

    [Fact]
    public void LogicalNoteQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, _, Segment earlierSegment) = CreateLogicalSegment();
        LogicalNote earlierMover = AddLogicalNote(earlierProject, earlierSegment, 11, 5, 60, 90);
        LogicalNote laterIncumbent = AddLogicalNote(earlierProject, earlierSegment, 10, 5, 60, 70);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                earlierSegment.Id,
                [earlierMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        LogicalNote earlierApplied = Assert.Single(Active(earlierProject, earlierSegment).Notes);
        Assert.Equal(earlierMover.Id, earlierApplied.Id);
        Assert.Equal(10, earlierApplied.StartTick);
        Assert.Equal([earlierMover.Id], earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierSegment).Notes);

        (MidoraProject laterProject, _, Segment laterSegment) = CreateLogicalSegment();
        LogicalNote earlierIncumbent = AddLogicalNote(laterProject, laterSegment, 10, 5, 60, 70);
        LogicalNote laterMover = AddLogicalNote(laterProject, laterSegment, 11, 5, 60, 90);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                laterSegment.Id,
                [laterMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        Assert.Equal(earlierIncumbent.Id,
            Assert.Single(Active(laterProject, laterSegment).Notes).Id);
        Assert.Empty(laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterSegment).Notes);
    }

    [Fact]
    public void DirectNoteQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, _, MidiSegment earlierSegment) = CreateMidiSegment();
        DirectMidiNote earlierMover = AddDirectNote(
            earlierProject, earlierSegment, 11, 5, 60, 90, 10, 1, 2);
        DirectMidiNote laterIncumbent = AddDirectNote(
            earlierProject, earlierSegment, 10, 5, 60, 70, 20, 3, 4);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                earlierSegment.Id,
                [earlierMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        Assert.Equal(earlierMover.Id,
            Assert.Single(Active(earlierProject, earlierSegment).Notes).Id);
        Assert.Equal([earlierMover.Id], earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierSegment).Notes);

        (MidoraProject laterProject, _, MidiSegment laterSegment) = CreateMidiSegment();
        DirectMidiNote earlierIncumbent = AddDirectNote(
            laterProject, laterSegment, 10, 5, 60, 70, 20, 1, 2);
        DirectMidiNote laterMover = AddDirectNote(
            laterProject, laterSegment, 11, 5, 60, 90, 10, 3, 4);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                laterSegment.Id,
                [laterMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        Assert.Equal(earlierIncumbent.Id,
            Assert.Single(Active(laterProject, laterSegment).Notes).Id);
        Assert.Empty(laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterSegment).Notes);
    }

    [Fact]
    public void TemplateNoteQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, EventInstrument earlierInstrument, SubVoice earlierVoice) =
            CreateTemplateVoice(100);
        TemplateEvent earlierMover = TemplateEvent.Note(earlierProject, 11, 5, 60, 90);
        TemplateEvent laterIncumbent = TemplateEvent.Note(earlierProject, 10, 5, 60, 70);
        Active(earlierProject, earlierVoice).Events.AddRange([earlierMover, laterIncumbent]);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                earlierInstrument.Id,
                earlierVoice.Id,
                [earlierMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        Assert.Equal(earlierMover.Id,
            Assert.Single(Active(earlierProject, earlierVoice).Events).Id);
        Assert.Equal([earlierMover.Id], earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierVoice).Events);

        (MidoraProject laterProject, EventInstrument laterInstrument, SubVoice laterVoice) =
            CreateTemplateVoice(100);
        TemplateEvent earlierIncumbent = TemplateEvent.Note(laterProject, 10, 5, 60, 70);
        TemplateEvent laterMover = TemplateEvent.Note(laterProject, 11, 5, 60, 90);
        Active(laterProject, laterVoice).Events.AddRange([earlierIncumbent, laterMover]);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                laterInstrument.Id,
                laterVoice.Id,
                [laterMover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        Assert.Equal(earlierIncumbent.Id,
            Assert.Single(Active(laterProject, laterVoice).Events).Id);
        Assert.Empty(laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterVoice).Events);
    }

    [Fact]
    public void LogicalPointQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, LogicalParameterLane earlierLane, Segment earlierSegment) =
            CreateLogicalParameterLane();
        CurvePoint earlierMover = new(earlierProject, 11, 0.25, CurveInterpolation.Step);
        CurvePoint laterIncumbent = new(earlierProject, 10, 0.75, CurveInterpolation.Step);
        Active(earlierProject, earlierLane).Points.AddRange([earlierMover, laterIncumbent]);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                earlierSegment.Id,
                earlierLane.Id,
                [earlierMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        Assert.Equal(laterIncumbent.Id,
            Assert.Single(Active(earlierProject, earlierLane).Points).Id);
        Assert.Empty(earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierLane).Points);

        (MidoraProject laterProject, LogicalParameterLane laterLane, Segment laterSegment) =
            CreateLogicalParameterLane();
        CurvePoint earlierIncumbent = new(laterProject, 10, 0.75, CurveInterpolation.Step);
        CurvePoint laterMover = new(laterProject, 11, 0.25, CurveInterpolation.Step);
        Active(laterProject, laterLane).Points.AddRange([earlierIncumbent, laterMover]);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                laterSegment.Id,
                laterLane.Id,
                [laterMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        CurvePoint laterSurvivor = Assert.Single(Active(laterProject, laterLane).Points);
        Assert.Equal((laterMover.Id, 10L, 0.25),
            (laterSurvivor.Id, laterSurvivor.Tick, laterSurvivor.Value));
        Assert.Equal([laterMover.Id], laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterLane).Points);
    }

    [Fact]
    public void DirectEventQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, _, MidiSegment earlierSegment) = CreateMidiSegment();
        DirectMidiChannelEvent earlierMover = AddDirectEvent(
            earlierProject, earlierSegment, 11, 11, 25, 1);
        DirectMidiChannelEvent laterIncumbent = AddDirectEvent(
            earlierProject, earlierSegment, 10, 11, 75, 2);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                earlierSegment.Id,
                [earlierMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        Assert.Equal(laterIncumbent.Id,
            Assert.Single(Active(earlierProject, earlierSegment).ChannelEvents).Id);
        Assert.Empty(earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierSegment).ChannelEvents);

        (MidoraProject laterProject, _, MidiSegment laterSegment) = CreateMidiSegment();
        DirectMidiChannelEvent earlierIncumbent = AddDirectEvent(
            laterProject, laterSegment, 10, 11, 75, 1);
        DirectMidiChannelEvent laterMover = AddDirectEvent(
            laterProject, laterSegment, 11, 11, 25, 2);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                laterSegment.Id,
                [laterMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        Assert.Equal(laterMover.Id,
            Assert.Single(Active(laterProject, laterSegment).ChannelEvents).Id);
        Assert.Equal([laterMover.Id], laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterSegment).ChannelEvents);
    }

    [Fact]
    public void TemplateEventQuantizeComparesMoverAndStationaryIncumbentInBothFormalDirections()
    {
        (MidoraProject earlierProject, EventInstrument earlierInstrument, SubVoice earlierVoice) =
            CreateTemplateVoice(100);
        TemplateEvent earlierMover = TemplateEvent.ControlChange(earlierProject, 11, 11, 25);
        TemplateEvent laterIncumbent = TemplateEvent.ControlChange(earlierProject, 10, 11, 75);
        Active(earlierProject, earlierVoice).Events.AddRange([earlierMover, laterIncumbent]);
        ITimelineSelectionResultEditCommand earlierCommand =
            ProjectDomainEditCommands.QuantizeTemplateEvents(
                earlierInstrument.Id,
                earlierVoice.Id,
                [earlierMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit earlierPrepared = earlierCommand.Prepare(earlierProject);
        earlierPrepared.Apply(earlierProject);

        Assert.Equal(laterIncumbent.Id,
            Assert.Single(Active(earlierProject, earlierVoice).Events).Id);
        Assert.Empty(earlierCommand.ResultSelectionIds);
        earlierPrepared.Undo(earlierProject);
        Assert.Equal([earlierMover, laterIncumbent], Active(earlierProject, earlierVoice).Events);

        (MidoraProject laterProject, EventInstrument laterInstrument, SubVoice laterVoice) =
            CreateTemplateVoice(100);
        TemplateEvent earlierIncumbent = TemplateEvent.ControlChange(laterProject, 10, 11, 75);
        TemplateEvent laterMover = TemplateEvent.ControlChange(laterProject, 11, 11, 25);
        Active(laterProject, laterVoice).Events.AddRange([earlierIncumbent, laterMover]);
        ITimelineSelectionResultEditCommand laterCommand =
            ProjectDomainEditCommands.QuantizeTemplateEvents(
                laterInstrument.Id,
                laterVoice.Id,
                [laterMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit laterPrepared = laterCommand.Prepare(laterProject);
        laterPrepared.Apply(laterProject);

        Assert.Equal(laterMover.Id,
            Assert.Single(Active(laterProject, laterVoice).Events).Id);
        Assert.Equal([laterMover.Id], laterCommand.ResultSelectionIds);
        laterPrepared.Undo(laterProject);
        Assert.Equal([earlierIncumbent, laterMover], Active(laterProject, laterVoice).Events);
    }

    [Fact]
    public void QuantizeReducesAlreadyAlignedSelectedDuplicatesAcrossAllSixSources()
    {
        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalFirst = AddLogicalNote(logicalProject, logicalSegment, 10, 5, 60, 70);
        LogicalNote logicalSecond = AddLogicalNote(logicalProject, logicalSegment, 10, 5, 60, 90);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                logicalSegment.Id,
                [logicalFirst.Id, logicalSecond.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);
        Assert.Equal(logicalFirst.Id,
            Assert.Single(Active(logicalProject, logicalSegment).Notes).Id);
        Assert.Equal([logicalFirst.Id], logicalCommand.ResultSelectionIds);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal([logicalFirst, logicalSecond], Active(logicalProject, logicalSegment).Notes);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        DirectMidiNote directFirst = AddDirectNote(
            directProject, directSegment, 10, 5, 60, 70, 10, 1, 2);
        DirectMidiNote directSecond = AddDirectNote(
            directProject, directSegment, 10, 5, 60, 90, 20, 3, 4);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                directSegment.Id,
                [directFirst.Id, directSecond.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);
        Assert.Equal(directFirst.Id,
            Assert.Single(Active(directProject, directSegment).Notes).Id);
        Assert.Equal([directFirst.Id], directCommand.ResultSelectionIds);
        directPrepared.Undo(directProject);
        Assert.Equal([directFirst, directSecond], Active(directProject, directSegment).Notes);

        (MidoraProject templateProject, EventInstrument templateInstrument, SubVoice templateVoice) =
            CreateTemplateVoice(100);
        TemplateEvent templateFirst = TemplateEvent.Note(templateProject, 10, 5, 60, 70);
        TemplateEvent templateSecond = TemplateEvent.Note(templateProject, 10, 5, 60, 90);
        Active(templateProject, templateVoice).Events.AddRange([templateFirst, templateSecond]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                templateInstrument.Id,
                templateVoice.Id,
                [templateFirst.Id, templateSecond.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        Assert.Equal(templateFirst.Id,
            Assert.Single(Active(templateProject, templateVoice).Events).Id);
        Assert.Equal([templateFirst.Id], templateCommand.ResultSelectionIds);
        templatePrepared.Undo(templateProject);
        Assert.Equal([templateFirst, templateSecond], Active(templateProject, templateVoice).Events);

        (MidoraProject curveProject, LogicalParameterLane curveLane, Segment curveSegment) =
            CreateLogicalParameterLane();
        CurvePoint curveFirst = new(curveProject, 10, 0.25, CurveInterpolation.Step);
        CurvePoint curveSecond = new(curveProject, 10, 0.75, CurveInterpolation.Step);
        Active(curveProject, curveLane).Points.AddRange([curveFirst, curveSecond]);
        ITimelineSelectionResultEditCommand curveCommand =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                curveSegment.Id,
                curveLane.Id,
                [curveFirst.Id, curveSecond.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        IPreparedProjectEdit curvePrepared = curveCommand.Prepare(curveProject);
        curvePrepared.Apply(curveProject);
        Assert.Equal(curveSecond.Id, Assert.Single(Active(curveProject, curveLane).Points).Id);
        Assert.Equal([curveSecond.Id], curveCommand.ResultSelectionIds);
        curvePrepared.Undo(curveProject);
        Assert.Equal([curveFirst, curveSecond], Active(curveProject, curveLane).Points);

        (MidoraProject channelProject, _, MidiSegment channelSegment) = CreateMidiSegment();
        DirectMidiChannelEvent channelFirst = AddDirectEvent(
            channelProject, channelSegment, 10, 11, 25, 1);
        DirectMidiChannelEvent channelSecond = AddDirectEvent(
            channelProject, channelSegment, 10, 11, 75, 2);
        ITimelineSelectionResultEditCommand channelCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                channelSegment.Id,
                [channelFirst.Id, channelSecond.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        IPreparedProjectEdit channelPrepared = channelCommand.Prepare(channelProject);
        channelPrepared.Apply(channelProject);
        Assert.Equal(channelSecond.Id,
            Assert.Single(Active(channelProject, channelSegment).ChannelEvents).Id);
        Assert.Equal([channelSecond.Id], channelCommand.ResultSelectionIds);
        channelPrepared.Undo(channelProject);
        Assert.Equal([channelFirst, channelSecond], Active(channelProject, channelSegment).ChannelEvents);

        (MidoraProject eventProject, EventInstrument eventInstrument, SubVoice eventVoice) =
            CreateTemplateVoice(100);
        TemplateEvent eventFirst = TemplateEvent.ControlChange(eventProject, 10, 11, 25);
        TemplateEvent eventSecond = TemplateEvent.ControlChange(eventProject, 10, 11, 75);
        Active(eventProject, eventVoice).Events.AddRange([eventFirst, eventSecond]);
        ITimelineSelectionResultEditCommand eventCommand =
            ProjectDomainEditCommands.QuantizeTemplateEvents(
                eventInstrument.Id,
                eventVoice.Id,
                [eventFirst.Id, eventSecond.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        IPreparedProjectEdit eventPrepared = eventCommand.Prepare(eventProject);
        eventPrepared.Apply(eventProject);
        Assert.Equal(eventSecond.Id,
            Assert.Single(Active(eventProject, eventVoice).Events).Id);
        Assert.Equal([eventSecond.Id], eventCommand.ResultSelectionIds);
        eventPrepared.Undo(eventProject);
        Assert.Equal([eventFirst, eventSecond], Active(eventProject, eventVoice).Events);
    }

    [Fact]
    public void AlreadyAlignedSelectedNoteDisplacesLaterStationaryIncumbentAcrossAllThreeSources()
    {
        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalSelected = AddLogicalNote(logicalProject, logicalSegment, 10, 5, 60, 70);
        LogicalNote logicalIncumbent = AddLogicalNote(logicalProject, logicalSegment, 10, 5, 60, 90);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                logicalSegment.Id,
                [logicalSelected.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);
        logicalPrepared.Apply(logicalProject);
        Assert.Equal(logicalSelected.Id,
            Assert.Single(Active(logicalProject, logicalSegment).Notes).Id);
        Assert.Equal([logicalSelected.Id], logicalCommand.ResultSelectionIds);
        logicalPrepared.Undo(logicalProject);
        Assert.Equal([logicalSelected, logicalIncumbent], Active(logicalProject, logicalSegment).Notes);

        (MidoraProject directProject, _, MidiSegment directSegment) = CreateMidiSegment();
        DirectMidiNote directSelected = AddDirectNote(
            directProject, directSegment, 10, 5, 60, 70, 10, 1, 2);
        DirectMidiNote directIncumbent = AddDirectNote(
            directProject, directSegment, 10, 5, 60, 90, 20, 3, 4);
        ITimelineSelectionResultEditCommand directCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                directSegment.Id,
                [directSelected.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit directPrepared = directCommand.Prepare(directProject);
        directPrepared.Apply(directProject);
        Assert.Equal(directSelected.Id,
            Assert.Single(Active(directProject, directSegment).Notes).Id);
        Assert.Equal([directSelected.Id], directCommand.ResultSelectionIds);
        directPrepared.Undo(directProject);
        Assert.Equal([directSelected, directIncumbent], Active(directProject, directSegment).Notes);

        (MidoraProject templateProject, EventInstrument templateInstrument, SubVoice templateVoice) =
            CreateTemplateVoice(100);
        TemplateEvent templateSelected = TemplateEvent.Note(templateProject, 10, 5, 60, 70);
        TemplateEvent templateIncumbent = TemplateEvent.Note(templateProject, 10, 5, 60, 90);
        Active(templateProject, templateVoice).Events.AddRange([templateSelected, templateIncumbent]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                templateInstrument.Id,
                templateVoice.Id,
                [templateSelected.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);
        templatePrepared.Apply(templateProject);
        Assert.Equal(templateSelected.Id,
            Assert.Single(Active(templateProject, templateVoice).Events).Id);
        Assert.Equal([templateSelected.Id], templateCommand.ResultSelectionIds);
        templatePrepared.Undo(templateProject);
        Assert.Equal([templateSelected, templateIncumbent], Active(templateProject, templateVoice).Events);
    }

    [Fact]
    public void DirectNoteQuantizeReducesOnlyTheTouchedImportedDuplicateKey()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote mover = AddDirectNote(project, segment, 11, 5, 60, 90, 10, 1, 2);
        DirectMidiNote touchedDuplicate1 = AddDirectNote(
            project, segment, 10, 5, 60, 70, 20, 3, 4);
        DirectMidiNote touchedDuplicate2 = AddDirectNote(
            project, segment, 10, 5, 60, 60, 30, 5, 6);
        DirectMidiNote untouchedDuplicate1 = AddDirectNote(
            project, segment, 30, 5, 61, 50, 40, 7, 8);
        DirectMidiNote untouchedDuplicate2 = AddDirectNote(
            project, segment, 30, 5, 61, 40, 50, 9, 10);
        DirectMidiNote[] original =
            [mover, touchedDuplicate1, touchedDuplicate2, untouchedDuplicate1, untouchedDuplicate2];
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                segment.Id,
                [mover.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(
            [mover.Id, untouchedDuplicate1.Id, untouchedDuplicate2.Id],
            Active(project, segment).Notes.Select(static value => value.Id));
        Assert.Equal([mover.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal(original, Active(project, segment).Notes);
    }

    [Fact]
    public void DirectEventQuantizeReducesOnlyTheTouchedImportedDuplicateKey()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiChannelEvent touchedDuplicate1 = AddDirectEvent(project, segment, 10, 11, 20, 1);
        DirectMidiChannelEvent touchedDuplicate2 = AddDirectEvent(project, segment, 10, 11, 40, 2);
        DirectMidiChannelEvent untouchedDuplicate1 = AddDirectEvent(project, segment, 30, 11, 60, 3);
        DirectMidiChannelEvent untouchedDuplicate2 = AddDirectEvent(project, segment, 30, 11, 80, 4);
        DirectMidiChannelEvent mover = AddDirectEvent(project, segment, 11, 11, 100, 5);
        DirectMidiChannelEvent[] original =
            [touchedDuplicate1, touchedDuplicate2, untouchedDuplicate1, untouchedDuplicate2, mover];
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                segment.Id,
                [mover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(
            [untouchedDuplicate1.Id, untouchedDuplicate2.Id, mover.Id],
            Active(project, segment).ChannelEvents.Select(static value => value.Id));
        Assert.Equal([mover.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal(original, Active(project, segment).ChannelEvents);
    }

    [Fact]
    public void MusicalFractionGridUsesCeilingAndMidpointGoesEarlier()
    {
        MidoraProject project = new(191);
        long step = InvokeQuantizeStepThroughLogicalNote(project);
        Assert.Equal(48, step);
    }

    [Fact]
    public void SplitPublishesLogicalAndTemplateFragmentsInSourceThenFragmentOrder()
    {
        NoteSplitOptions options = new()
        {
            Mode = NoteSplitMode.FixedPieceLength,
            FixedPieceLengthTicks = 5
        };

        (MidoraProject logicalProject, _, Segment logicalSegment) = CreateLogicalSegment();
        LogicalNote logicalA = AddLogicalNote(logicalProject, logicalSegment, 0, 10, 60, 100);
        LogicalNote logicalUnselected = AddLogicalNote(logicalProject, logicalSegment, 20, 5, 61, 90);
        LogicalNote logicalB = AddLogicalNote(logicalProject, logicalSegment, 30, 10, 62, 80);
        ITimelineSelectionResultEditCommand logicalCommand =
            ProjectDomainEditCommands.SplitLogicalNotes(
                logicalSegment.Id,
                [logicalB.Id, logicalA.Id],
                options);
        IPreparedProjectEdit logicalPrepared = logicalCommand.Prepare(logicalProject);

        logicalPrepared.Apply(logicalProject);

        MidoraId[] logicalResult = logicalCommand.ResultSelectionIds.ToArray();
        Assert.Equal(4, logicalResult.Length);
        Assert.Equal(
            [logicalResult[0], logicalResult[1], logicalUnselected.Id, logicalResult[2], logicalResult[3]],
            Active(logicalProject, logicalSegment).Notes.Select(static value => value.Id));
        logicalPrepared.Undo(logicalProject);
        Assert.Equal(
            [logicalA.Id, logicalUnselected.Id, logicalB.Id],
            Active(logicalProject, logicalSegment).Notes.Select(static value => value.Id));
        logicalPrepared.Apply(logicalProject);
        Assert.Equal(logicalResult, logicalCommand.ResultSelectionIds);
        Assert.Equal(
            [logicalResult[0], logicalResult[1], logicalUnselected.Id, logicalResult[2], logicalResult[3]],
            Active(logicalProject, logicalSegment).Notes.Select(static value => value.Id));

        (MidoraProject templateProject, EventInstrument instrument, SubVoice voice) =
            CreateTemplateVoice(100);
        TemplateEvent templateA = TemplateEvent.Note(templateProject, 0, 10, 60, 100);
        TemplateEvent templateUnselected = TemplateEvent.Note(templateProject, 20, 5, 61, 90);
        TemplateEvent templateB = TemplateEvent.Note(templateProject, 30, 10, 62, 80);
        Active(templateProject, voice).Events.AddRange(
            [templateA, templateUnselected, templateB]);
        ITimelineSelectionResultEditCommand templateCommand =
            ProjectDomainEditCommands.SplitTemplateNotes(
                instrument.Id,
                voice.Id,
                [templateB.Id, templateA.Id],
                options);
        IPreparedProjectEdit templatePrepared = templateCommand.Prepare(templateProject);

        templatePrepared.Apply(templateProject);

        MidoraId[] templateResult = templateCommand.ResultSelectionIds.ToArray();
        Assert.Equal(4, templateResult.Length);
        Assert.Equal(
            [templateResult[0], templateResult[1], templateUnselected.Id, templateResult[2], templateResult[3]],
            Active(templateProject, voice).Events.Select(static value => value.Id));
        templatePrepared.Undo(templateProject);
        Assert.Equal(
            [templateA.Id, templateUnselected.Id, templateB.Id],
            Active(templateProject, voice).Events.Select(static value => value.Id));
        templatePrepared.Apply(templateProject);
        Assert.Equal(templateResult, templateCommand.ResultSelectionIds);
        Assert.Equal(
            [templateResult[0], templateResult[1], templateUnselected.Id, templateResult[2], templateResult[3]],
            Active(templateProject, voice).Events.Select(static value => value.Id));
    }

    [Fact]
    public void DirectNoteQuantizeUsesExplicitOnsetOrderInsteadOfCollectionIndex()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote collectionFirst = AddDirectNote(
            project, segment, 11, 5, 60, 100, 10, 100, 101);
        DirectMidiNote formallyEarlier = AddDirectNote(
            project, segment, 12, 5, 60, 90, 20, 1, 2);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                segment.Id,
                [collectionFirst.Id, formallyEarlier.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10)));
        IPreparedProjectEdit prepared = command.Prepare(project);

        prepared.Apply(project);

        DirectMidiNote winner = Assert.Single(Active(project, segment).Notes);
        Assert.Equal(formallyEarlier.Id, winner.Id);
        Assert.Equal((1L, 2L), (winner.NoteOnOrder, winner.NoteOffOrder));
        Assert.Equal([formallyEarlier.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal(
            [collectionFirst.Id, formallyEarlier.Id],
            Active(project, segment).Notes.Select(static value => value.Id));
    }

    [Fact]
    public void DirectHumanizeAndJoinUseExplicitOnsetOrderInsteadOfCollectionIndex()
    {
        (MidoraProject humanizeProject, _, MidiSegment humanizeSegment) = CreateMidiSegment();
        DirectMidiNote collectionFirst = AddDirectNote(
            humanizeProject, humanizeSegment, 11, 5, 60, 100, 10, 100, 101);
        DirectMidiNote formallyEarlier = AddDirectNote(
            humanizeProject, humanizeSegment, 12, 5, 60, 90, 20, 1, 2);
        ITimelineSelectionResultEditCommand humanizeCommand =
            ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                humanizeSegment.Id,
                [collectionFirst.Id, formallyEarlier.Id],
                new(
                    TimelineHumanizeField.Override(10, 10),
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    Seed: 1));
        IPreparedProjectEdit humanizePrepared = humanizeCommand.Prepare(humanizeProject);

        humanizePrepared.Apply(humanizeProject);

        Assert.Equal(
            formallyEarlier.Id,
            Assert.Single(Active(humanizeProject, humanizeSegment).Notes).Id);
        humanizePrepared.Undo(humanizeProject);
        Assert.Equal(
            [collectionFirst.Id, formallyEarlier.Id],
            Active(humanizeProject, humanizeSegment).Notes.Select(static value => value.Id));

        (MidoraProject joinProject, _, MidiSegment joinSegment) = CreateMidiSegment();
        DirectMidiNote joinCollectionFirst = AddDirectNote(
            joinProject, joinSegment, 10, 10, 61, 110, 30, 100, 101);
        DirectMidiNote joinFormallyEarlier = AddDirectNote(
            joinProject, joinSegment, 10, 10, 61, 70, 40, 1, 2);
        ITimelineSelectionResultEditCommand joinCommand =
            ProjectDomainEditCommands.JoinDirectMidiNotes(
                joinSegment.Id,
                [joinCollectionFirst.Id, joinFormallyEarlier.Id],
                new());
        IPreparedProjectEdit joinPrepared = joinCommand.Prepare(joinProject);

        joinPrepared.Apply(joinProject);

        DirectMidiNote joined = Assert.Single(Active(joinProject, joinSegment).Notes);
        Assert.Equal(joinFormallyEarlier.Id, joined.Id);
        Assert.Equal(70, joined.NoteOnVelocity);
    }

    [Fact]
    public void DirectEventQuantizeUsesExplicitOrderAndStableIdTieBreak()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiChannelEvent formallyLater = AddDirectEvent(project, segment, 11, 11, 100, 100);
        DirectMidiChannelEvent collectionLater = AddDirectEvent(project, segment, 12, 11, 20, 1);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                segment.Id,
                [collectionLater.Id, formallyLater.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        IPreparedProjectEdit prepared = command.Prepare(project);

        prepared.Apply(project);

        DirectMidiChannelEvent winner = Assert.Single(Active(project, segment).ChannelEvents);
        Assert.Equal(formallyLater.Id, winner.Id);
        Assert.Equal(100, winner.Order);
        prepared.Undo(project);
        Assert.Equal(
            [formallyLater.Id, collectionLater.Id],
            Active(project, segment).ChannelEvents.Select(static value => value.Id));

        DirectMidiChannelEvent lowerId = new(project)
        {
            Tick = 21,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 12,
            Data2 = 30,
            Order = 7
        };
        DirectMidiChannelEvent higherId = new(project)
        {
            Tick = 22,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 12,
            Data2 = 40,
            Order = 7
        };
        Active(project, segment).ChannelEvents.Add(higherId);
        Active(project, segment).ChannelEvents.Add(lowerId);
        ITimelineSelectionResultEditCommand tieCommand =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                segment.Id,
                [lowerId.Id, higherId.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        IPreparedProjectEdit tiePrepared = tieCommand.Prepare(project);

        tiePrepared.Apply(project);

        DirectMidiChannelEvent tieWinner = Assert.Single(
            Active(project, segment).ChannelEvents,
            value => value.Data1 == 12);
        Assert.Equal(higherId.Id, tieWinner.Id);
    }

    [Fact]
    public void DirectSplitCollisionUsesExplicitOnsetOrderInsteadOfCollectionIndex()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote source = AddDirectNote(project, segment, 0, 20, 60, 100, 10, 100, 101);
        DirectMidiNote formallyEarlierIncumbent = AddDirectNote(
            project, segment, 10, 5, 60, 90, 20, 1, 2);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                segment.Id,
                [source.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 10
                });
        IPreparedProjectEdit prepared = command.Prepare(project);

        prepared.Apply(project);

        Assert.Equal(
            [source.Id, formallyEarlierIncumbent.Id],
            Active(project, segment).Notes.Select(static value => value.Id));
        Assert.Equal([source.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void DirectSplitOrdersSourcesAndGeneratedCutEndpointsByExplicitOnsetOrder()
    {
        (MidoraProject project, _, MidiSegment segment) = CreateMidiSegment();
        DirectMidiNote collectionFirst = AddDirectNote(
            project, segment, 0, 10, 60, 100, 10, 100, 101);
        DirectMidiNote formallyEarlier = AddDirectNote(
            project, segment, 0, 10, 61, 90, 20, 1, 2);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                segment.Id,
                [collectionFirst.Id, formallyEarlier.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 5
                });
        IPreparedProjectEdit prepared = command.Prepare(project);

        prepared.Apply(project);

        MidoraId[] result = command.ResultSelectionIds.ToArray();
        Assert.Equal(4, result.Length);
        Assert.Equal(formallyEarlier.Id, result[0]);
        Assert.Equal(collectionFirst.Id, result[2]);
        DirectMidiNote earlierLeft = Active(project, segment).Notes.Single(value => value.Id == result[0]);
        DirectMidiNote earlierRight = Active(project, segment).Notes.Single(value => value.Id == result[1]);
        DirectMidiNote laterLeft = Active(project, segment).Notes.Single(value => value.Id == result[2]);
        DirectMidiNote laterRight = Active(project, segment).Notes.Single(value => value.Id == result[3]);
        Assert.Equal((0L, 1L), (earlierLeft.NoteOffOrder, earlierRight.NoteOnOrder));
        Assert.Equal((2L, 3L), (laterLeft.NoteOffOrder, laterRight.NoteOnOrder));
        prepared.Undo(project);
        Assert.Equal(
            [collectionFirst.Id, formallyEarlier.Id],
            Active(project, segment).Notes.Select(static value => value.Id));
    }

    private static long InvokeQuantizeStepThroughLogicalNote(MidoraProject project)
    {
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 1000 };
        LogicalNote note = AddLogicalNote(project, segment, 25, 1, 60, 100);
        track.Segments.Add(segment);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.QuantizeLogicalNotes(
            segment.Id,
            [note.Id],
            new(TimelineQuantizeGrid.MusicalFraction(1, 16)));
        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);
        long result = Active(project, segment).Notes.Single(value => value.Id == note.Id).StartTick;
        prepared.Undo(project);
        return result;
    }

    private static (MidoraProject Project, LogicalTrack Track, Segment Segment)
        CreateLogicalSegment()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 10_000 };
        track.Segments.Add(segment);
        return (project, track, segment);
    }

    private static (MidoraProject Project, PureMidiTrack Track, MidiSegment Segment)
        CreateMidiSegment()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 10_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return (project, track, segment);
    }

    private static (MidoraProject Project, LogicalParameterLane Lane, Segment Segment)
        CreateLogicalParameterLane()
    {
        (MidoraProject project, _, Segment segment) = CreateLogicalSegment();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalParameterDefinition definition = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(definition);
        LogicalParameterLane lane = new(project) { ParameterId = definition.Id };
        segment.ParameterLanes.Add(lane);
        return (project, lane, segment);
    }

    private static (MidoraProject Project, EventInstrument Instrument, SubVoice Voice)
        CreateTemplateVoice(long templateLength)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.TemplateLengthTicks = templateLength;
        return (project, instrument, instrument.SubVoices[0]);
    }

    private static LogicalNote AddLogicalNote(
        MidoraProject project,
        Segment segment,
        long tick,
        long gate,
        int key,
        int velocity)
    {
        LogicalNote result = new(project)
        {
            StartTick = tick,
            LengthTicks = gate,
            Note = key,
            Velocity = velocity
        };
        segment.Notes.Add(result);
        return result;
    }

    private static DirectMidiNote AddDirectNote(
        MidoraProject project,
        MidiSegment segment,
        long tick,
        long gate,
        int key,
        int velocity,
        int offVelocity,
        long onOrder,
        long offOrder)
    {
        DirectMidiNote result = new(project)
        {
            StartTick = tick,
            LengthTicks = gate,
            Key = key,
            NoteOnVelocity = velocity,
            NoteOffVelocity = offVelocity,
            NoteOnOrder = onOrder,
            NoteOffOrder = offOrder
        };
        segment.Notes.Add(result);
        return result;
    }

    private static DirectMidiChannelEvent AddDirectEvent(
        MidoraProject project,
        MidiSegment segment,
        long tick,
        int controller,
        int value,
        long order)
    {
        DirectMidiChannelEvent result = new(project)
        {
            Tick = tick,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = controller,
            Data2 = value,
            Order = order
        };
        segment.ChannelEvents.Add(result);
        return result;
    }

    private static Segment Active(MidoraProject project, Segment identity) =>
        project.Tracks.SelectMany(static value => value.Segments)
            .Single(value => value.Id == identity.Id);

    private static MidiSegment Active(MidoraProject project, MidiSegment identity) =>
        project.PureMidiTracks.SelectMany(static value => value.Segments)
            .Single(value => value.Id == identity.Id);

    private static SubVoice Active(MidoraProject project, SubVoice identity) =>
        project.EventInstruments.SelectMany(static value => value.SubVoices)
            .Single(value => value.Id == identity.Id);

    private static LogicalParameterLane Active(
        MidoraProject project,
        LogicalParameterLane identity) => project.Tracks
            .SelectMany(static value => value.Segments)
            .SelectMany(static value => value.ParameterLanes)
            .Single(value => value.Id == identity.Id);
}
