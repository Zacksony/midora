using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMultiOwnerAdvancedEditCommandsTests
{
    [Fact]
    public void HumanizeAcrossLogicalOwnersPublishesOneAtomicEditAndSelection()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack firstTrack = AddLogicalTrack(project, instrument, "First");
        LogicalTrack secondTrack = AddLogicalTrack(project, instrument, "Second");
        Segment firstRoot = AddLogicalSegment(project, firstTrack);
        Segment secondRoot = AddLogicalSegment(project, secondTrack);
        LogicalNote first = AddLogicalNote(project, firstRoot, 10, 60);
        LogicalNote second = AddLogicalNote(project, secondRoot, 20, 62);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new(
                    TimelineHumanizeField.Add(5, 5),
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    Seed: 7));

        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);

        prepared.Apply(project);

        Assert.Equal(15, Assert.Single(firstTrack.Segments[0].Notes).StartTick);
        Assert.Equal(25, Assert.Single(secondTrack.Segments[0].Notes).StartTick);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
        prepared.Apply(project);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void MultiOwnerCommandCreatesOneDocumentHistoryEntryAndOneUndoRestoresEveryOwner()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, firstTrack.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, secondTrack.Id));
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote first = AddDirectNote(project, firstRoot, 0, 10, 60);
        DirectMidiNote second = AddDirectNote(project, secondRoot, 0, 10, 62);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new(
                    TimelineHumanizeField.Add(5, 5),
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    Seed: 7));

        using StagedProjectEdit staged = document.PrepareEdit(command);
        PreparedTimelineSelection preparedSelection =
            Assert.IsType<PreparedTimelineSelection>(staged.PreparedSelection);
        Assert.Equal([first.Id, second.Id], preparedSelection.OriginalSelectionIds);
        Assert.Equal([first.Id, second.Id], preparedSelection.ResultSelectionIds);
        Assert.True(document.ExecutePrepared(staged).Changed);

        Assert.Single(document.History);
        Assert.Equal(5, firstTrack.Segments[0].Notes[0].StartTick);
        Assert.Equal(5, secondTrack.Segments[0].Notes[0].StartTick);
        _ = document.Undo();
        Assert.Single(document.History);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Same(preparedSelection, staged.PreparedSelection);
    }

    [Fact]
    public void JoinAcrossDirectOwnersNeverMergesAcrossOwnersAndIsOneUndo()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote firstA = AddDirectNote(project, firstRoot, 0, 10, 60);
        DirectMidiNote firstB = AddDirectNote(project, firstRoot, 10, 10, 60);
        DirectMidiNote secondA = AddDirectNote(project, secondRoot, 0, 10, 60);
        DirectMidiNote secondB = AddDirectNote(project, secondRoot, 10, 10, 60);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.JoinDirectMidiNotes(
                [
                    new(firstRoot.Id, [firstA.Id, firstB.Id]),
                    new(secondRoot.Id, [secondA.Id, secondB.Id])
                ],
                new());

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        Assert.Equal(20, Assert.Single(firstTrack.Segments[0].Notes).LengthTicks);
        Assert.Equal(20, Assert.Single(secondTrack.Segments[0].Notes).LengthTicks);
        Assert.Equal([firstA.Id, secondA.Id], command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Equal(2, firstTrack.Segments[0].Notes.Count);
        Assert.Equal(2, secondTrack.Segments[0].Notes.Count);
        Assert.Equal(
            [firstA.Id, firstB.Id, secondA.Id, secondB.Id],
            command.ResultSelectionIds);
    }

    [Fact]
    public void SplitAcrossDirectOwnersReservesOneDistinctStableIdRange()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote first = AddDirectNote(project, firstRoot, 0, 20, 60);
        DirectMidiNote second = AddDirectNote(project, secondRoot, 0, 20, 62);
        long expectedAllocator = project.NextStableId;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Equal(expectedAllocator, project.NextStableId);
        prepared.Apply(project);

        MidoraId[] allIds = firstTrack.Segments[0].Notes
            .Concat(secondTrack.Segments[0].Notes)
            .Select(static value => value.Id)
            .ToArray();
        Assert.Equal(4, allIds.Length);
        Assert.Equal(4, allIds.Distinct().Count());
        Assert.Equal(expectedAllocator + 2, project.NextStableId);
        Assert.Equal(allIds, command.ResultSelectionIds);
        prepared.Undo(project);
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
        Assert.Equal(expectedAllocator + 2, project.NextStableId);
        prepared.Apply(project);
        Assert.Equal(expectedAllocator + 2, project.NextStableId);
        Assert.Equal(allIds, command.ResultSelectionIds);
    }

    [Fact]
    public void SplitAcrossLogicalOwnersUsesTheSharedStableIdAllocator()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack firstTrack = AddLogicalTrack(project, instrument, "First");
        LogicalTrack secondTrack = AddLogicalTrack(project, instrument, "Second");
        Segment firstRoot = AddLogicalSegment(project, firstTrack);
        Segment secondRoot = AddLogicalSegment(project, secondTrack);
        LogicalNote first = AddLogicalNote(project, firstRoot, 0, 60);
        LogicalNote second = AddLogicalNote(project, secondRoot, 0, 62);
        first.LengthTicks = 20;
        second.LengthTicks = 20;
        long expectedAllocator = project.NextStableId;

        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        MidoraId[] allIds = firstTrack.Segments[0].Notes
            .Concat(secondTrack.Segments[0].Notes)
            .Select(static value => value.Id)
            .ToArray();
        Assert.Equal(4, allIds.Length);
        Assert.Equal(4, allIds.Distinct().Count());
        Assert.Equal(expectedAllocator + 2, project.NextStableId);
        Assert.Equal(allIds, command.ResultSelectionIds);
    }

    [Fact]
    public void SplitAcrossTemplateOwnersUsesTheSharedStableIdAllocator()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.TemplateLengthTicks = 100;
        SubVoice firstVoice = instrument.SubVoices[0];
        SubVoice secondVoice = new(project) { Name = "Second" };
        instrument.SubVoices.Add(secondVoice);
        TemplateEvent first = TemplateEvent.Note(project, 0, 20, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 0, 20, 62, 100);
        firstVoice.Events.Add(first);
        secondVoice.Events.Add(second);
        long expectedAllocator = project.NextStableId;

        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitTemplateNotes(
                [
                    new(instrument.Id, firstVoice.Id, [first.Id]),
                    new(instrument.Id, secondVoice.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        prepared.Apply(project);

        SubVoice activeFirst = project.EventInstruments
            .Single(value => value.Id == instrument.Id)
            .SubVoices.Single(value => value.Id == firstVoice.Id);
        SubVoice activeSecond = project.EventInstruments
            .Single(value => value.Id == instrument.Id)
            .SubVoices.Single(value => value.Id == secondVoice.Id);
        MidoraId[] allIds = activeFirst.Events
            .Concat(activeSecond.Events)
            .Select(static value => value.Id)
            .ToArray();
        Assert.Equal(4, allIds.Length);
        Assert.Equal(4, allIds.Distinct().Count());
        Assert.Equal(expectedAllocator + 2, project.NextStableId);
        Assert.Equal(allIds, command.ResultSelectionIds);
    }

    [Fact]
    public void SplitStableIdAssignmentDoesNotDependOnOwnerCollectionOrder()
    {
        (MidoraProject firstProject, PureMidiTrack firstA, PureMidiTrack firstB,
            MidiSegment firstSegmentA, MidiSegment firstSegmentB,
            DirectMidiNote firstNoteA, DirectMidiNote firstNoteB) = CreateDirectSplitFixture();
        using (firstProject)
        {
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.SplitDirectMidiNotes(
                    [
                        new(firstSegmentA.Id, [firstNoteA.Id]),
                        new(firstSegmentB.Id, [firstNoteB.Id])
                    ],
                    new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });
            IPreparedProjectEdit prepared = command.Prepare(firstProject);
            prepared.Apply(firstProject);

            MidoraId[] expectedFirst = firstA.Segments[0].Notes
                .Select(static value => value.Id)
                .ToArray();
            MidoraId[] expectedSecond = firstB.Segments[0].Notes
                .Select(static value => value.Id)
                .ToArray();
            MidoraId[] expectedSelection = command.ResultSelectionIds.ToArray();

            (MidoraProject secondProject, PureMidiTrack secondA, PureMidiTrack secondB,
                MidiSegment secondSegmentA, MidiSegment secondSegmentB,
                DirectMidiNote secondNoteA, DirectMidiNote secondNoteB) = CreateDirectSplitFixture();
            using (secondProject)
            {
                ITimelineSelectionResultEditCommand reversed =
                    ProjectDomainEditCommands.SplitDirectMidiNotes(
                        [
                            new(secondSegmentB.Id, [secondNoteB.Id]),
                            new(secondSegmentA.Id, [secondNoteA.Id])
                        ],
                        new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });
                IPreparedProjectEdit reversedPrepared = reversed.Prepare(secondProject);
                reversedPrepared.Apply(secondProject);

                Assert.Equal(
                    expectedFirst,
                    secondA.Segments[0].Notes.Select(static value => value.Id));
                Assert.Equal(
                    expectedSecond,
                    secondB.Segments[0].Notes.Select(static value => value.Id));
                Assert.Equal(expectedSelection, reversed.ResultSelectionIds);
            }
        }
    }

    [Fact]
    public void SplitAcrossOwnersRollsBackEarlierRootWhenLaterRevisionIsStale()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote first = AddDirectNote(project, firstRoot, 0, 20, 60);
        DirectMidiNote second = AddDirectNote(project, secondRoot, 0, 20, 62);
        long expectedAllocator = project.NextStableId;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        second.NoteOnVelocity = 99;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Single(firstRoot.Notes);
        Assert.Single(secondRoot.Notes);
        Assert.Equal(expectedAllocator, project.NextStableId);
        Assert.Equal([first.Id, second.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void HumanizeRejectsStaleNoOpOwnerBeforePublishingChangedOwner()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack changedTrack = AddLogicalTrack(project, instrument, "Changed");
        LogicalTrack noOpTrack = AddLogicalTrack(project, instrument, "No-op");
        Segment changedRoot = AddLogicalSegment(project, changedTrack);
        Segment noOpRoot = AddLogicalSegment(project, noOpTrack);
        LogicalNote changed = AddLogicalNote(project, changedRoot, 0, 60);
        changed.Velocity = 90;
        LogicalNote noOp = AddLogicalNote(project, noOpRoot, 0, 62);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                [
                    new(changedRoot.Id, [changed.Id]),
                    new(noOpRoot.Id, [noOp.Id])
                ],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(100, 100),
                    Seed: 7));

        IPreparedProjectEdit prepared = command.Prepare(project);
        noOp.Velocity = 99;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(changedRoot, changedTrack.Segments[0]);
        Assert.Same(noOpRoot, noOpTrack.Segments[0]);
        Assert.Equal(90, changed.Velocity);
        Assert.Equal(99, noOp.Velocity);
        Assert.Equal([changed.Id, noOp.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void SplitRejectsStaleNoOpOwnerBeforePublishingRootsOrStableIds()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack changedTrack = AddMidiTrack(project, route, "Changed");
        PureMidiTrack noOpTrack = AddMidiTrack(project, route, "No-op");
        MidiSegment changedRoot = AddMidiSegment(project, changedTrack);
        MidiSegment noOpRoot = AddMidiSegment(project, noOpTrack);
        DirectMidiNote changed = AddDirectNote(project, changedRoot, 0, 20, 60);
        DirectMidiNote noOp = AddDirectNote(project, noOpRoot, 0, 10, 62);
        long expectedAllocator = project.NextStableId;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                [
                    new(changedRoot.Id, [changed.Id]),
                    new(noOpRoot.Id, [noOp.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });

        IPreparedProjectEdit prepared = command.Prepare(project);
        noOp.NoteOnVelocity = 99;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(changedRoot, changedTrack.Segments[0]);
        Assert.Same(noOpRoot, noOpTrack.Segments[0]);
        Assert.Single(changedRoot.Notes);
        Assert.Single(noOpRoot.Notes);
        Assert.Equal(expectedAllocator, project.NextStableId);
        Assert.Equal([changed.Id, noOp.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void JoinRejectsStaleNoOpSubVoiceBeforePublishingChangedSubVoice()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.TemplateLengthTicks = 100;
        SubVoice changedVoice = instrument.SubVoices[0];
        SubVoice noOpVoice = new(project) { Name = "No-op" };
        instrument.SubVoices.Add(noOpVoice);
        TemplateEvent changedFirst = TemplateEvent.Note(project, 0, 10, 60, 100);
        TemplateEvent changedSecond = TemplateEvent.Note(project, 10, 10, 60, 100);
        TemplateEvent noOp = TemplateEvent.Note(project, 0, 10, 62, 100);
        changedVoice.Events.Add(changedFirst);
        changedVoice.Events.Add(changedSecond);
        noOpVoice.Events.Add(noOp);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.JoinTemplateNotes(
                [
                    new(instrument.Id, changedVoice.Id, [changedFirst.Id, changedSecond.Id]),
                    new(instrument.Id, noOpVoice.Id, [noOp.Id])
                ],
                new());

        IPreparedProjectEdit prepared = command.Prepare(project);
        noOp.Value = 99;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(changedVoice, instrument.SubVoices[0]);
        Assert.Same(noOpVoice, instrument.SubVoices[1]);
        Assert.Equal(2, changedVoice.Events.Count);
        Assert.Single(noOpVoice.Events);
        Assert.Equal(
            [changedFirst.Id, changedSecond.Id, noOp.Id],
            command.ResultSelectionIds);
    }

    [Fact]
    public void DocumentRollbackAfterRejectedMultiOwnerApplyIsANoOpInsteadOfASecondUndo()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, firstTrack.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, secondTrack.Id));
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote first = AddDirectNote(project, firstRoot, 0, 20, 60);
        DirectMidiNote second = AddDirectNote(project, secondRoot, 0, 20, 62);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });
        using StagedProjectEdit staged = document.PrepareEdit(command);
        second.NoteOnVelocity = 99;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.ExecutePrepared(staged));

        Assert.Contains("revision", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.False(document.CanUndo);
        Assert.Empty(document.History);
    }

    [Fact]
    public void CancelledMultiOwnerSplitPublishesNeitherRootsNorStableIds()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiNote first = AddDirectNote(project, firstRoot, 0, 20, 60);
        DirectMidiNote second = AddDirectNote(project, secondRoot, 0, 20, 62);
        long expectedAllocator = project.NextStableId;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 10 });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ((ICancellableProjectEditCommand)command).Prepare(project, cancellation.Token));
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Equal(expectedAllocator, project.NextStableId);
    }

    [Fact]
    public void QuantizeAcrossDirectEventOwnersUsesOneAtomicRevisionGate()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstRoot = AddMidiSegment(project, firstTrack);
        MidiSegment secondRoot = AddMidiSegment(project, secondTrack);
        DirectMidiChannelEvent first = AddDirectEvent(project, firstRoot, 14);
        DirectMidiChannelEvent second = AddDirectEvent(project, secondRoot, 26);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                [
                    new(firstRoot.Id, [first.Id]),
                    new(secondRoot.Id, [second.Id])
                ],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        second.Data2 = 12;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(firstRoot, firstTrack.Segments[0]);
        Assert.Same(secondRoot, secondTrack.Segments[0]);
        Assert.Equal(14, first.Tick);
        Assert.Equal(26, second.Tick);
    }

    [Fact]
    public void QuantizeRejectsStaleNoOpEventOwnerBeforePublishingChangedOwner()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack changedTrack = AddMidiTrack(project, route, "Changed");
        PureMidiTrack noOpTrack = AddMidiTrack(project, route, "No-op");
        MidiSegment changedRoot = AddMidiSegment(project, changedTrack);
        MidiSegment noOpRoot = AddMidiSegment(project, noOpTrack);
        DirectMidiChannelEvent changed = AddDirectEvent(project, changedRoot, 14);
        DirectMidiChannelEvent noOp = AddDirectEvent(project, noOpRoot, 20);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                [
                    new(changedRoot.Id, [changed.Id]),
                    new(noOpRoot.Id, [noOp.Id])
                ],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(project);
        noOp.Data2 = 99;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(changedRoot, changedTrack.Segments[0]);
        Assert.Same(noOpRoot, noOpTrack.Segments[0]);
        Assert.Equal(14, changed.Tick);
        Assert.Equal(20, noOp.Tick);
        Assert.Equal([changed.Id, noOp.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void MultiOwnerDescriptorsRejectDuplicateOwnersAndObjectIds()
    {
        MidoraId owner = MidoraId.FromSequence(1);
        MidoraId first = MidoraId.FromSequence(2);
        MidoraId second = MidoraId.FromSequence(3);
        Assert.Throws<ArgumentException>(() =>
            ProjectDomainEditCommands.JoinLogicalNotes(
                [new(owner, [first]), new(owner, [second])],
                new()));
        Assert.Throws<ArgumentException>(() =>
            ProjectDomainEditCommands.JoinLogicalNotes(
                [new(owner, [first]), new(MidoraId.FromSequence(4), [first])],
                new()));
    }

    private static LogicalTrack AddLogicalTrack(
        MidoraProject project,
        EventInstrument instrument,
        string name)
    {
        LogicalTrack track = new(project) { Name = name };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        return track;
    }

    private static Segment AddLogicalSegment(MidoraProject project, LogicalTrack track)
    {
        Segment segment = new(project) { LengthTicks = 1_000 };
        track.Segments.Add(segment);
        return segment;
    }

    private static LogicalNote AddLogicalNote(
        MidoraProject project,
        Segment segment,
        long tick,
        int key)
    {
        LogicalNote note = new(project)
        {
            StartTick = tick,
            LengthTicks = 10,
            Note = key,
            Velocity = 100
        };
        segment.Notes.Add(note);
        return note;
    }

    private static PureMidiTrack AddMidiTrack(
        MidoraProject project,
        MidiChannelRoot route,
        string name)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = route.Id
        };
        project.PureMidiTracks.Add(track);
        return track;
    }

    private static MidiSegment AddMidiSegment(MidoraProject project, PureMidiTrack track)
    {
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        track.Segments.Add(segment);
        return segment;
    }

    private static DirectMidiNote AddDirectNote(
        MidoraProject project,
        MidiSegment segment,
        long tick,
        long length,
        int key)
    {
        DirectMidiNote note = new(project)
        {
            StartTick = tick,
            LengthTicks = length,
            Key = key,
            NoteOnVelocity = 100,
            NoteOffVelocity = 64
        };
        segment.Notes.Add(note);
        return note;
    }

    private static DirectMidiChannelEvent AddDirectEvent(
        MidoraProject project,
        MidiSegment segment,
        long tick)
    {
        DirectMidiChannelEvent value = new(project)
        {
            Tick = tick,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 100
        };
        segment.ChannelEvents.Add(value);
        return value;
    }

    private static (MidoraProject Project, PureMidiTrack FirstTrack, PureMidiTrack SecondTrack,
        MidiSegment FirstSegment, MidiSegment SecondSegment,
        DirectMidiNote FirstNote, DirectMidiNote SecondNote) CreateDirectSplitFixture()
    {
        MidoraProject project = new(480);
        MidiChannelRoot route = new(project) { Name = "Route" };
        project.MidiChannelRoots.Add(route);
        PureMidiTrack firstTrack = AddMidiTrack(project, route, "First");
        PureMidiTrack secondTrack = AddMidiTrack(project, route, "Second");
        MidiSegment firstSegment = AddMidiSegment(project, firstTrack);
        MidiSegment secondSegment = AddMidiSegment(project, secondTrack);
        DirectMidiNote firstNote = AddDirectNote(project, firstSegment, 0, 20, 60);
        DirectMidiNote secondNote = AddDirectNote(project, secondSegment, 0, 20, 62);
        return (project, firstTrack, secondTrack, firstSegment, secondSegment, firstNote, secondNote);
    }
}
