using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class ClipboardSelectionRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CutAndPasteNotesPublishesDestinationRoutingEvenWhenSelectionWasCleared(
        bool sourceIsDirect, bool targetIsDirect)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId sourceId = sourceIsDirect ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId targetId = targetIsDirect ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId[] original = fixture.NoteIds(sourceIsDirect);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(targetId);
        ProjectObjectClipboardCutPreparation cut = sourceIsDirect
            ? ProjectObjectClipboard.PrepareCutDirectMidiNotes(session.Document!, sourceId, original)
            : ProjectObjectClipboard.PrepareCutLogicalNotes(session.Document!, sourceId, original);
        using var payload = cut.Payload;
        Publish(session, workspace, cut.DeleteAfterSuccessfulClipboardWrite);
        workspace.Selection.Clear();
        Assert.Null(workspace.Selection.HomogeneousTimelineSource);

        long firstNewId = session.Project!.NextStableId;
        Publish(session, workspace, ProjectObjectClipboard.CreatePasteNotesCommand(
            session.Document!, payload, targetId, 240, targetIsDirect));

        MidoraId[] pasted = fixture.NoteIds(targetIsDirect)
            .Where(id => id.Value >= firstNewId).ToArray();
        Assert.Equal(2, pasted.Length);
        Assert.DoesNotContain(pasted, original.Contains);
        AssertSelection(workspace, pasted, new(
            targetIsDirect ? WorkspaceTimelineSelectionKind.DirectMidiNote : WorkspaceTimelineSelectionKind.LogicalNote,
            targetId));
    }

    [Fact]
    public async Task SubVoiceNotePasteReplacesStaleOwnerInsteadOfReusingPreviousSelectionRouting()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        EventInstrument instrument = session.Project!.EventInstruments.Single();
        MidoraId sourceVoiceId = instrument.SubVoices[0].Id;
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(instrument.Id, sourceVoiceId, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(instrument.Id, sourceVoiceId, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Target"));
        MidoraId targetVoiceId = session.Project.EventInstruments.Single().SubVoices[1].Id;
        MidoraId[] originals = Voice(session, sourceVoiceId).Events.Select(value => value.Id).ToArray();
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.ApplyRange(originals, WorkspaceSelectionRangeMode.Replace,
            new(WorkspaceTimelineSelectionKind.TemplateNote, instrument.Id, sourceVoiceId));
        using var payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
            session.Document!, instrument.Id, sourceVoiceId, originals);

        Publish(session, workspace, ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            session.Document!, payload, instrument.Id, targetVoiceId, 240));

        MidoraId[] pasted = Voice(session, targetVoiceId).Events.Select(value => value.Id).ToArray();
        Assert.Equal(2, pasted.Length);
        AssertSelection(workspace, pasted,
            new(WorkspaceTimelineSelectionKind.TemplateNote, instrument.Id, targetVoiceId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EventPasteRestoresExactLaneOrMixedLaneQuantizeScope(bool subVoice, bool mixedLanes)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        EventInstrument instrument = session.Project!.EventInstruments.Single();
        MidoraId voiceId = instrument.SubVoices[0].Id;
        MidoraId secondOwner = voiceId;
        if (subVoice)
        {
            session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(instrument.Id, voiceId, 12, 11, 40));
            session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(instrument.Id, voiceId, 36, mixedLanes ? 7 : 11, 80));
            session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Event target"));
            secondOwner = session.Project.EventInstruments.Single().SubVoices[1].Id;
        }
        else
        {
            session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
                fixture.MidiSegmentId, 12, DirectMidiChannelEventKind.ControlChange, 11, 40));
            session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
                fixture.MidiSegmentId, 36, DirectMidiChannelEventKind.ControlChange, mixedLanes ? 7 : 11, 80));
        }
        MidoraId[] original = subVoice
            ? Voice(session, voiceId).Events.Select(value => value.Id).ToArray()
            : fixture.MidiSegment.ChannelEvents.Select(value => value.Id).ToArray();
        WorkspaceViewModel workspace = subVoice ? session.OpenInstrument(instrument.Id) : session.OpenSegment(fixture.MidiSegmentId);
        workspace.Selection.Clear();
        using var payload = subVoice
            ? ProjectObjectClipboard.CopySubVoiceTimelineEvents(session.Document!, instrument.Id, voiceId, original)
            : ProjectObjectClipboard.CopyDirectMidiEvents(session.Document!, fixture.MidiSegmentId, original);
        long firstNewId = session.Project.NextStableId;

        Publish(session, workspace, subVoice
            ? ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(session.Document!, payload, instrument.Id, secondOwner, 240)
            : ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(session.Document!, payload, fixture.MidiSegmentId, 240));

        MidoraId[] pasted = (subVoice
                ? Voice(session, secondOwner).Events.Select(value => value.Id)
                : fixture.MidiSegment.ChannelEvents.Select(value => value.Id))
            .Where(id => id.Value >= firstNewId).ToArray();
        Assert.Equal(2, pasted.Length);
        Assert.True(workspace.Selection.IdSet.SetEquals(pasted));
        WorkspaceTimelineSelectionSource exact = subVoice
            ? new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint, instrument.Id, secondOwner, MidiValueTarget.ControlChange(11))
            : new(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, fixture.MidiSegmentId,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange, DirectMidiData1: 11);
        Assert.Equal(mixedLanes ? null : exact, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(exact.QuantizeScope, workspace.Selection.HomogeneousTimelineQuantizeScope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullyCollidingNotePasteDoesNotLeaveAnyEligibleSelection(bool direct)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId segmentId = direct ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId[] original = fixture.NoteIds(direct);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segmentId);
        workspace.Selection.ApplyRange(original, WorkspaceSelectionRangeMode.Replace,
            new(direct ? WorkspaceTimelineSelectionKind.DirectMidiNote : WorkspaceTimelineSelectionKind.LogicalNote, segmentId));
        using var payload = direct
            ? ProjectObjectClipboard.CopyDirectMidiNotes(session.Document!, segmentId, original)
            : ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segmentId, original);
        Publish(session, workspace, ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload, segmentId, 12, direct));

        Assert.Empty(workspace.Selection.Ids);
        Assert.Null(workspace.Selection.HomogeneousTimelineSource);
        Assert.Null(workspace.Selection.HomogeneousTimelineQuantizeScope);
        Assert.Equal(original, fixture.NoteIds(direct));
    }

    [Fact]
    public async Task MixedSubVoiceNoteAndEventPasteCannotInheritAnOldNoteSelectionContext()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId instrumentId = session.Project!.EventInstruments.Single().Id;
        MidoraId voiceId = session.Project.EventInstruments.Single().SubVoices.Single().Id;
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(instrumentId, voiceId, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(instrumentId, voiceId, 36, 11, 40));
        TemplateEvent[] original = Voice(session, voiceId).Events.ToArray();
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrumentId);
        workspace.Selection.Replace(original.Single(value => value.Kind == TemplateEventKind.Note).Id,
            new(WorkspaceTimelineSelectionKind.TemplateNote, instrumentId, voiceId));
        Assert.NotNull(workspace.Selection.HomogeneousTimelineSource);
        using var payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
            session.Document!, instrumentId, voiceId, original.Select(value => value.Id).ToArray());
        long firstNewId = session.Project.NextStableId;

        Publish(session, workspace, ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            session.Document!, payload, instrumentId, voiceId, 240));

        MidoraId[] pasted = Voice(session, voiceId).Events.Where(value => value.Id.Value >= firstNewId)
            .Select(value => value.Id).ToArray();
        Assert.Equal(2, pasted.Length);
        Assert.True(workspace.Selection.IdSet.SetEquals(pasted));
        Assert.Null(workspace.Selection.HomogeneousTimelineSource);
        Assert.Null(workspace.Selection.HomogeneousTimelineQuantizeScope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompoundSubVoiceEventsUseTheExplicitDestinationLane(bool pitchRange)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId instrumentId = session.Project!.EventInstruments.Single().Id;
        MidoraId voiceId = session.Project.EventInstruments.Single().SubVoices.Single().Id;
        session.Execute(pitchRange
            ? ProjectDomainEditCommands.CreateTemplatePitchBendRange(instrumentId, voiceId, 12, 2, 50)
            : ProjectDomainEditCommands.CreateTemplateBank(instrumentId, voiceId, 12, 3, 4));
        MidoraId original = Voice(session, voiceId).Events.Single().Id;
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrumentId);
        workspace.Selection.Clear();
        MidiValueTarget target = pitchRange ? MidiValueTarget.PitchBendRangeCents : MidiValueTarget.BankLsb;
        using var payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
            session.Document!, instrumentId, voiceId, [original]);

        Publish(session, workspace, ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            session.Document!, payload, instrumentId, voiceId, 240, target));

        MidoraId pasted = Voice(session, voiceId).Events.Single(value => value.Id != original).Id;
        AssertSelection(workspace, [pasted], new(WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
            instrumentId, voiceId, target, PointMaximum: pitchRange ? 99 : 127));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoAndRedoRestorePastedSelectionRoutingWithoutASelectionGesture(bool direct)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId segmentId = direct ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId[] original = fixture.NoteIds(direct);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segmentId);
        workspace.Selection.Clear();
        using var payload = direct
            ? ProjectObjectClipboard.CopyDirectMidiNotes(session.Document!, segmentId, original)
            : ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segmentId, original);
        Publish(session, workspace, ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload, segmentId, 240, direct));
        MidoraId[] pasted = workspace.Selection.Ids.ToArray();
        WorkspaceTimelineSelectionSource expected = new(
            direct ? WorkspaceTimelineSelectionKind.DirectMidiNote : WorkspaceTimelineSelectionKind.LogicalNote, segmentId);
        AssertSelection(workspace, pasted, expected);

        session.Undo();
        Assert.Empty(workspace.Selection.Ids);
        Assert.Equal(original, fixture.NoteIds(direct));
        session.Redo();
        AssertSelection(workspace, pasted, expected);
        Assert.Equal(4, fixture.NoteIds(direct).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateBatchFlipAfterPasteEditsOnlyTheNewObjects(bool direct)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId segmentId = direct ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId[] original = fixture.NoteIds(direct);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segmentId);
        workspace.Selection.Clear();
        using var payload = direct
            ? ProjectObjectClipboard.CopyDirectMidiNotes(session.Document!, segmentId, original)
            : ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segmentId, original);
        Publish(session, workspace, ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload, segmentId, 240, direct));
        MidoraId[] pasted = workspace.Selection.Ids.ToArray();
        WorkspaceTimelineSelectionSource source = Assert.IsType<WorkspaceTimelineSelectionSource>(
            workspace.Selection.HomogeneousTimelineSource);

        Publish(session, workspace, source.Kind == WorkspaceTimelineSelectionKind.DirectMidiNote
            ? ProjectDomainEditCommands.FlipDirectMidiNotesVertical(source.OwnerId!.Value, pasted)
            : ProjectDomainEditCommands.FlipLogicalNotesVertical(source.OwnerId!.Value, pasted));

        Assert.Equal([60, 64], Keys(original));
        Assert.Equal([64, 60], Keys(pasted));
        AssertSelection(workspace, pasted, source);

        int[] Keys(MidoraId[] ids) => ids.Select(id => direct
            ? fixture.MidiSegment.Notes.Single(note => note.Id == id).Key
            : fixture.LogicalSegment.Notes.Single(note => note.Id == id).Note).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrangementSegmentPastePublishesBatchRoutingFromEmptySelection(bool direct)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId segmentId = direct ? fixture.MidiSegmentId : fixture.LogicalSegmentId;
        MidoraId trackId = direct ? session.Project!.PureMidiTracks.Single().Id : session.Project!.Tracks.Single().Id;
        TimelineWorkspaceViewModel workspace = session.OpenArrangement();
        workspace.Selection.Clear();
        using var payload = direct
            ? ProjectObjectClipboard.CopyMidiSegments(session.Document!, [segmentId], segmentId)
            : ProjectObjectClipboard.CopySegments(session.Document!, [segmentId], segmentId);

        Publish(session, workspace, direct
            ? ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(session.Document!, payload, trackId, 960)
            : ProjectObjectClipboard.CreatePasteSegmentsCommand(session.Document!, payload, trackId, 960));

        MidoraId pasted = direct
            ? session.Project!.PureMidiTracks.Single().Segments.Single(value => value.Id != segmentId).Id
            : session.Project!.Tracks.Single().Segments.Single(value => value.Id != segmentId).Id;
        AssertSelection(workspace, [pasted], new(WorkspaceTimelineSelectionKind.ArrangementSegment));
    }

    [Fact]
    public async Task LogicalParameterPointPastePreservesTheDestinationLaneAndItsSignedValueRange()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        MidoraId instrumentId = session.Project!.EventInstruments.Single().Id;
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrumentId, "Signed", LogicalParameterType.Integer, -64, 64, -64, 64, 0));
        MidoraId parameterId = session.Project.EventInstruments.Single().LogicalParameters.Single().Id;
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(fixture.LogicalSegmentId, parameterId));
        MidoraId laneId = fixture.LogicalSegment.ParameterLanes.Single().Id;
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(fixture.LogicalSegmentId, laneId, 12, -32, CurveInterpolation.Step));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(fixture.LogicalSegmentId, laneId, 36, 32, CurveInterpolation.Step));
        MidoraId[] original = fixture.LogicalSegment.ParameterLanes.Single().Points.Select(value => value.Id).ToArray();
        TimelineWorkspaceViewModel workspace = session.OpenSegment(fixture.LogicalSegmentId);
        workspace.Selection.Clear();
        using var payload = ProjectObjectClipboard.CopyLogicalParameterLaneContent(
            session.Document!, fixture.LogicalSegmentId, laneId, original);
        long firstNewId = session.Project.NextStableId;

        Publish(session, workspace, ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
            session.Document!, payload, fixture.LogicalSegmentId, laneId, 240));

        MidoraId[] pasted = fixture.LogicalSegment.ParameterLanes.Single().Points
            .Where(value => value.Id.Value >= firstNewId).Select(value => value.Id).ToArray();
        Assert.Equal(2, pasted.Length);
        AssertSelection(workspace, pasted, new(WorkspaceTimelineSelectionKind.LogicalParameterPoint,
            fixture.LogicalSegmentId, laneId, PointMinimum: -64, PointMaximum: 64));
    }

    private static void Publish(DesktopSessionController session, WorkspaceViewModel workspace, IProjectEditCommand command)
    {
        using var commandLifetime = command as IDisposable;
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
    }

    private static void AssertSelection(WorkspaceViewModel workspace, IReadOnlyCollection<MidoraId> ids,
        WorkspaceTimelineSelectionSource expected)
    {
        Assert.NotEmpty(ids);
        Assert.True(workspace.Selection.IdSet.SetEquals(ids));
        Assert.Equal(expected, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(expected.QuantizeScope, workspace.Selection.HomogeneousTimelineQuantizeScope);
    }

    private static SubVoice Voice(DesktopSessionController session, MidoraId voiceId) =>
        session.Project!.EventInstruments.SelectMany(value => value.SubVoices).Single(value => value.Id == voiceId);

    private sealed class Fixture(DesktopSessionController session, MidoraId logicalSegmentId, MidoraId midiSegmentId) : IAsyncDisposable
    {
        public DesktopSessionController Session { get; } = session;
        public MidoraId LogicalSegmentId { get; } = logicalSegmentId;
        public MidoraId MidiSegmentId { get; } = midiSegmentId;
        public Segment LogicalSegment => Session.Project!.Tracks.SelectMany(value => value.Segments).Single(value => value.Id == LogicalSegmentId);
        public MidiSegment MidiSegment => Session.Project!.PureMidiTracks.SelectMany(value => value.Segments).Single(value => value.Id == MidiSegmentId);
        public MidoraId[] NoteIds(bool direct) => direct
            ? MidiSegment.Notes.Select(value => value.Id).ToArray()
            : LogicalSegment.Notes.Select(value => value.Id).ToArray();
        public ValueTask DisposeAsync() => Session.DisposeAsync();

        public static async Task<Fixture> CreateAsync()
        {
            DesktopSessionController session = new();
            await session.CreateProjectAsync(new NewProjectCreationRequest
            {
                ProjectName = "Clipboard selection routing",
                PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
            });
            session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            EventInstrument instrument = session.Project!.EventInstruments.Single();
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
            LogicalTrack logical = session.Project.Tracks.Single();
            session.Execute(ProjectDomainEditCommands.CreateSegment(logical.Id, 0, 480));
            MidoraId logicalId = logical.Segments.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateLogicalNote(logicalId, 12, 12, 60, 90));
            session.Execute(ProjectDomainEditCommands.CreateLogicalNote(logicalId, 36, 12, 64, 90));
            session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            PureMidiTrack midi = session.Project.PureMidiTracks.Single();
            session.Execute(ProjectDomainEditCommands.CreateMidiSegment(midi.Id, 0, 480));
            MidoraId midiId = midi.Segments.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(midiId, 12, 12, 60, 90));
            session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(midiId, 36, 12, 64, 90));
            return new(session, logicalId, midiId);
        }
    }
}
