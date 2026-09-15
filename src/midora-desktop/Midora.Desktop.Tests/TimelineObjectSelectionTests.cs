using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class TimelineObjectSelectionTests
{
    [Fact]
    public void MixedDirectSelectionFreezesSubsetsAndOpaqueRemovesNumericCapabilities()
    {
        using var project = new MidoraProject(480);
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = 100 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
        var note = new DirectMidiNote(project) { StartTick = 0, LengthTicks = 10, Key = 60, NoteOnVelocity = 100 };
        var first = new DirectMidiChannelEvent(project) { Tick = 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 80 };
        var second = new DirectMidiChannelEvent(project) { Tick = 10, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 90 };
        var otherLane = new DirectMidiChannelEvent(project) { Tick = 0, Kind = DirectMidiChannelEventKind.PitchBend, Data1 = 8192 };
        var opaque = new OpaqueMidiEvent(project) { Tick = 0, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [42] };
        segment.Notes.Add(note); segment.ChannelEvents.AddRange([first, second, otherLane]); segment.OpaqueEvents.Add(opaque);
        TimelineObjectOwner owner = new(ProjectTimelineOwnerKind.DirectMidiSegment, segment.Id);
        var ids = CompressedMidoraIdSet.Create([note.Id, first.Id, second.Id]);
        var mixed = TimelineObjectSelection.Capture(project, owner, ids);
        Assert.Same(ids, mixed.Ids);
        Assert.True(mixed.IsMixed); Assert.False(mixed.CanCopyOrCut); Assert.True(mixed.CanUseEventValueTools);
        Assert.Equal([note.Id], mixed.Notes);
        Assert.True(mixed.Events.SetEquals([first.Id, second.Id]));
        var numeric = TimelineObjectSelection.Capture(project, owner, mixed.Events);
        Assert.True(numeric.CanCopyOrCut);
        Assert.Equal(11, numeric.EventSource!.Value.DirectMidiData1);
        var crossLane = TimelineObjectSelection.Capture(project, owner, CompressedMidoraIdSet.Create([first.Id, otherLane.Id]));
        Assert.False(crossLane.CanUseEventValueTools); Assert.True(crossLane.CanQuantizeEvents); Assert.True(crossLane.CanCopyOrCut);
        var withOpaque = TimelineObjectSelection.Capture(project, owner, CompressedMidoraIdSet.Create([first.Id, opaque.Id]));
        Assert.True(withOpaque.CanDelete); Assert.True(withOpaque.ContainsOpaque);
        Assert.False(withOpaque.CanUseEventValueTools); Assert.False(withOpaque.CanQuantizeEvents); Assert.False(withOpaque.CanCopyOrCut);
        Assert.True(withOpaque.Events.Contains(opaque.Id)); Assert.Equal([opaque.Id], withOpaque.Opaque);
    }

    [Theory]
    [InlineData(TemplateEventKind.Bank, true, false, true)]
    [InlineData(TemplateEventKind.Bank, false, true, true)]
    [InlineData(TemplateEventKind.Bank, true, true, false)]
    [InlineData(TemplateEventKind.PitchBendRange, false, false, false)]
    public void CompositeTemplateRowsNeverSilentlyChooseOneOfTheirValues(
        TemplateEventKind kind, bool msb, bool lsb, bool canScalarEdit)
    {
        using var project = new MidoraProject(480);
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        var voice = new SubVoice(project) { Name = "Voice" }; instrument.SubVoices.Add(voice);
        if (kind == TemplateEventKind.Bank)
            ProjectDomainEditCommands.CreateTemplateBank(instrument.Id, voice.Id, 0, msb ? 2 : null, lsb ? 3 : null)
                .Prepare(project).Apply(project);
        else voice.Events.Add(new(project) { Kind = kind, Value = 2, SecondaryValue = 3 });
        var point = voice.Events.Single();
        var selection = TimelineObjectSelection.Capture(project,
            new(ProjectTimelineOwnerKind.SubVoice, voice.Id, instrument.Id), CompressedMidoraIdSet.Create([point.Id]));
        Assert.Equal(canScalarEdit, selection.CanUseEventValueTools);
        Assert.True(selection.CanQuantizeEvents); Assert.True(selection.CanCopyOrCut);
        if (canScalarEdit) Assert.Equal(msb ? MidiValueTarget.BankMsb : MidiValueTarget.BankLsb, selection.EventSource!.Value.MidiTarget);
    }

    [Fact]
    public async Task TypedEditRetainsUntouchedSelectionAndUndoRestoresTheWholeMixedSelection()
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var original = workspace.Selection.SharedIds;
        var command = ProjectDomainEditCommands.DeleteTimelineObjects(new(ProjectTimelineOwnerKind.LogicalSegment, segment.Id), [note.Id]);
        using var staged = session.PrepareProjectEdit(command, workspace,
            retainedSelectionIds: CompressedMidoraIdSet.Create([point.Id]));
        Assert.Same(original, workspace.Selection.SharedIds);
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace, command);
        Assert.Equal([point.Id], workspace.Selection.Ids);
        Assert.Null(workspace.Selection.HomogeneousTimelineSource);
        Assert.Empty(session.Project!.Tracks[0].Segments[0].Notes);
        session.Undo();
        Assert.True(original.SetEquals(workspace.Selection.Ids));
        Assert.Single(session.Project.Tracks[0].Segments[0].Notes);
        session.Redo();
        Assert.Equal([point.Id], workspace.Selection.Ids);
    }

    [Fact]
    public async Task TypedEditRejectsChangedSelectionBeforePublicationWithoutResurrectingIds()
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var command = ProjectDomainEditCommands.DeleteTimelineObjects(new(ProjectTimelineOwnerKind.LogicalSegment, segment.Id), [note.Id]);
        using var staged = session.PrepareProjectEdit(command, workspace,
            retainedSelectionIds: CompressedMidoraIdSet.Create([point.Id]));
        workspace.Selection.Clear();
        int history = session.Document!.History.Count;
        Assert.Throws<InvalidOperationException>(() => session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace, command));
        Assert.Empty(workspace.Selection.Ids);
        Assert.Single(session.Project!.Tracks[0].Segments[0].Notes);
        Assert.Equal(history, session.Document.History.Count);
    }

    [Fact]
    public async Task TypedPasteSelectsCopiesPlusUntouchedEventsAndRestoresOriginalOnUndo()
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var original = workspace.Selection.SharedIds;
        using var payload = ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segment.Id, [note.Id]);
        var command = ProjectObjectClipboard.CreatePasteLogicalNotesCommand(session.Document!, payload, segment.Id, 200);
        using var commandLifetime = command as IDisposable;
        using var staged = session.PrepareProjectEdit(command, workspace,
            retainedSelectionIds: CompressedMidoraIdSet.Create([point.Id]));
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
        var after = workspace.Selection.SharedIds;
        Assert.Equal(2, after.Count); Assert.Contains(point.Id, after); Assert.DoesNotContain(note.Id, after);
        Assert.Contains(Assert.Single(staged.PreparedSelection!.ResultSelectionIds), after);
        session.Undo(); Assert.True(original.SetEquals(workspace.Selection.Ids));
        session.Redo(); Assert.True(after.SetEquals(workspace.Selection.Ids));
    }

    [Fact]
    public async Task TypedCutKeepsUntouchedIdsAndUndoRedoRestoresCompleteBookmarks()
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var original = workspace.Selection.SharedIds;
        var document = session.Document!;
        var delete = ProjectDomainEditCommands.DeleteTimelineObjects(
            new(ProjectTimelineOwnerKind.LogicalSegment, segment.Id), [note.Id]);
        using var transfer = ProjectObjectClipboard.PrepareTransfer(document,
            () => ProjectObjectClipboard.CopyLogicalNotes(document, segment.Id, [note.Id]), delete);
        Assert.NotNull(transfer.Deletion);
        session.PrepareWorkspaceSelectionForStagedEdit(transfer.Deletion, workspace,
            CompressedMidoraIdSet.Create([point.Id]));
        Assert.Same(original, workspace.Selection.SharedIds);
        transfer.ValidatePublication(document);
        session.ExecutePreparedPreservingWorkspaceSelection(transfer.Deletion, workspace);
        Assert.Equal([point.Id], workspace.Selection.Ids);
        session.Undo();
        Assert.True(original.SetEquals(workspace.Selection.Ids));
        session.Redo();
        Assert.Equal([point.Id], workspace.Selection.Ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacySmallToolsKeepEventsAndUndoRestoresFullSelectionEvenAfterCollision(int operation)
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var other = new LogicalNote(session.Project!) { StartTick = 200, LengthTicks = 120, Note = 60, Velocity = 100 };
        segment.Notes.Add(other);
        MidoraId[] ids = operation == 3 ? [note.Id] : [note.Id, other.Id];
        var original = CompressedMidoraIdSet.Create(ids.Append(point.Id));
        workspace.Selection.AdoptMaterialized(original, note.Id, note.Id);
        IProjectEditCommand source = operation switch
        {
            0 => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(segment.Id, ids),
            1 => ProjectDomainEditCommands.ScaleLogicalNotes(segment.Id, ids, 2),
            2 => ProjectDomainEditCommands.TransposeLogicalNotes(segment.Id, ids, 1),
            _ => ProjectDomainEditCommands.MoveLogicalNotes(segment.Id, ids, 176, 0)
        };
        var wrapped = ProjectDomainEditCommands.WithTimelineObjectSelection(source,
            new(ProjectTimelineOwnerKind.LogicalSegment, segment.Id), ids);
        using var staged = session.PrepareProjectEdit(wrapped, workspace,
            retainedSelectionIds: CompressedMidoraIdSet.Create([point.Id]));
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace, wrapped);
        Assert.Contains(point.Id, workspace.Selection.Ids);
        Assert.Equal(operation == 3 ? 1 : 3, workspace.Selection.Ids.Count);
        var after = workspace.Selection.SharedIds;
        session.Undo(); Assert.True(original.SetEquals(workspace.Selection.Ids));
        session.Redo(); Assert.True(after.SetEquals(workspace.Selection.Ids));
    }

    [Fact]
    public async Task TypedEditCancellationAndForeignRetainedIdsDoNotAffectSelectionOrHistory()
    {
        var (session, workspace, segment, note, point) = await CreateSession();
        await using var lifetime = session;
        var original = workspace.Selection.SharedIds;
        var command = ProjectDomainEditCommands.DeleteTimelineObjects(new(ProjectTimelineOwnerKind.LogicalSegment, segment.Id), [note.Id]);
        int history = session.Document!.History.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.PrepareProjectEdit(command, workspace, cancellation.Token,
            retainedSelectionIds: CompressedMidoraIdSet.Create([point.Id])));
        Assert.Throws<InvalidOperationException>(() => session.PrepareProjectEdit(command, workspace,
            retainedSelectionIds: CompressedMidoraIdSet.Create([new MidoraId(123456789)])));
        Assert.Same(original, workspace.Selection.SharedIds);
        Assert.Single(session.Project!.Tracks[0].Segments[0].Notes);
        Assert.Equal(history, session.Document.History.Count);
    }

    [Fact]
    public void MergedProjectionDropsStaleHomogeneousSourceAndDeduplicatesWithoutAnIdArray()
    {
        var selection = new WorkspaceSelection();
        selection.Replace(new MidoraId(1), new(WorkspaceTimelineSelectionKind.LogicalNote, new MidoraId(90)));
        var projection = TimelineObjectSelection.MergeResult(selection, new[] { new MidoraId(2), new MidoraId(3) },
            CompressedMidoraIdSet.Create([new(3), new(4)]), default);
        selection.AdoptPrepared(projection);
        Assert.Equal([new MidoraId(2), new(3), new(4)], selection.Ids);
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Null(selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void SparsePartitionBudgetIsCheckedBeforeAnyOwnerOrSourceRead()
    {
        using var project = new MidoraProject(480);
        var ids = CompressedMidoraIdSet.Create([new MidoraId(1), new MidoraId(4097), new MidoraId(8193)]);
        var error = Assert.Throws<InvalidOperationException>(() => TimelineObjectSelection.Capture(project,
            new(ProjectTimelineOwnerKind.LogicalSegment, new MidoraId(999)), ids,
            maximumBuilderWorkingBytes: 8192));
        Assert.Contains("working-memory budget", error.Message);
        Assert.Equal(3, ids.Count);
    }

    private static async Task<(DesktopSessionController Session, TimelineWorkspaceViewModel Workspace,
        Segment Segment, LogicalNote Note, CurvePoint Point)> CreateSession()
    {
        var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        { ProjectName = "Mixed list selection", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        var track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        var segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 24, 120, 60, 100));
        var note = Assert.Single(segment.Notes);
        var lane = new LogicalParameterLane(session.Project) { ParameterId = new(80000001) };
        var point = new CurvePoint(session.Project, 24, .25); lane.Points.Add(point); segment.ParameterLanes.Add(lane);
        var workspace = session.OpenSegment(segment.Id);
        workspace.Selection.AdoptMaterialized(CompressedMidoraIdSet.Create([note.Id, point.Id]), note.Id, note.Id);
        session.RefreshWorkspaceSelection(workspace);
        session.Document!.MarkSaveSucceeded();
        return (session, workspace, segment, note, point);
    }
}
