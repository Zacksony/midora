using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class Stage4DesktopSelectionIntegrationTests
{
    [Fact]
    public void ColdSelectionFilteringCountsActualCompressedPagesBeforeBuilding()
    {
        using MidoraProject project = new(480);
        var dense = new MetricsSource(4096, 1);
        var denseIds = CompressedMidoraIdSet.Create(Enumerable.Range(1, 4096).Select(i => new MidoraId(i)));
        var result = MainWindow.ReadSelectionInputMetrics(project, dense, denseIds,
            id => (id.Value, id.Value + 1, 60, 100d), default,
            id => id.Value != 4096, maximumBuilderWorkingBytes: 6144);
        Assert.Equal(4095, result.Ids.Count);
        Assert.False(result.Ids.Contains(new(4096)));

        var sparse = new MetricsSource(3, 4096);
        var sparseIds = CompressedMidoraIdSet.Create([new(1), new(4097), new(8193)]);
        var unchanged = MainWindow.ReadSelectionInputMetrics(project, sparse, sparseIds,
            id => (id.Value, id.Value + 1, 60, 100d), default,
            maximumBuilderWorkingBytes: 1);
        Assert.Same(sparseIds, unchanged.Ids);
        Assert.Throws<InvalidOperationException>(() => MainWindow.ReadSelectionInputMetrics(
            project, sparse, sparseIds, id => (id.Value, id.Value + 1, 60, 100d), default,
            id => id.Value != 8193, maximumBuilderWorkingBytes: 6144));
        Assert.Equal(3, sparseIds.Count);
    }

    private sealed class MetricsSource(int count, int stride) : ITimelineObjectSource<MidoraId>
    {
        public int Count => count;
        public long SourceRevision => 0;
        public int PageCapacity => 4096;
        public MidoraId GetByOrdinal(int ordinal) => new(1L + ordinal * (long)stride);
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            ordinal = checked((int)((id.Value - 1) / stride));
            return id.Value > 0 && (id.Value - 1) % stride == 0 && ordinal < count;
        }
        public bool TryGetPageByOrdinal(int firstOrdinal, int requestedCount, out TimelineObjectPage<MidoraId> page)
        {
            if (firstOrdinal < 0 || firstOrdinal >= count) { page = default; return false; }
            int length = Math.Min(Math.Min(requestedCount, PageCapacity), count - firstOrdinal);
            page = new(SourceRevision, firstOrdinal,
                Enumerable.Range(firstOrdinal, length).Select(GetByOrdinal).ToArray());
            return true;
        }
        public int FindOrdinalAtOrAfterTick(long tick) => throw new NotSupportedException();
        public IEnumerable<MidoraId> QueryTickRange(TimelineObjectRangeQuery query) => throw new NotSupportedException();
        public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) { }
    }

    [Fact]
    public async Task ColdSelectionMetricsAreReadOnceAndCancellationDoesNotPublishASelection()
    {
        var (session, workspace, segment, first) = await CreateSessionAsync();
        await using var ownedSession = session;
        segment.Notes.Add(new(session.Project!) { StartTick = 5, LengthTicks = 20, Note = 12, Velocity = 9 });
        segment.Notes.Add(new(session.Project!) { StartTick = 80, LengthTicks = 200, Note = 100, Velocity = 126 });
        var source = segment.Notes.CreateQuerySnapshot();
        var ids = CompressedMidoraIdSet.Create(segment.Notes.Select(value => value.Id));
        int reads = 0;
        var result = await Task.Run(() => MainWindow.ReadSelectionInputMetrics(session.Project!, source, ids, value =>
        {
            reads++;
            return (value.StartTick, value.StartTick + value.LengthTicks, value.Note, (double)value.Velocity);
        }, default));
        Assert.Equal(3, reads);
        Assert.Equal((5L, 280L, 12, 100, 9d, 126d, 200L),
            (result.MinimumTick, result.MaximumTick, result.MinimumPitch, result.MaximumPitch,
                result.MinimumValue, result.MaximumValue, result.MaximumLength));
        Assert.True(ids.SetEquals(result.Ids));
        Assert.Same(ids, result.Ids);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => MainWindow.ReadSelectionInputMetrics(session.Project!, source, ids, value =>
        {
            cancellation.Cancel();
            return (value.StartTick, value.StartTick + value.LengthTicks, value.Note, (double)value.Velocity);
        }, cancellation.Token));
        Assert.Equal([first.Id], workspace.Selection.Ids);
        Assert.Equal(3, segment.Notes.Count);
    }

    [Theory]
    [InlineData(TimelineItemEditKind.Move)]
    [InlineData(TimelineItemEditKind.ResizeEnd)]
    public async Task ArrangementGesturePreparationUsesFrozenIdsAndExistingAtomicCommands(TimelineItemEditKind kind)
    {
        var (session, _, segment, _) = await CreateSessionAsync();
        await using var ownedSession = session;
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        var target = new ArrangementLaneDescriptor(1, ArrangementLaneKind.LogicalTrack, track.Id,
            null, 0, true, false, true);
        var command = new ArrangementGestureEditCommand(
            CompressedMidoraIdSet.Create([segment.Id]), segment.Id, segment.ProjectStartTick,
            kind, false, 48, 48, 1, target);
        using StagedProjectEdit prepared = session.Document!.PrepareEdit(command);
        Assert.Equal((0L, 480L), (segment.ProjectStartTick, segment.LengthTicks));
        session.Document.ExecutePrepared(prepared);
        Segment current = session.Project.Tracks.SelectMany(value => value.Segments).Single();
        Assert.Equal(kind == TimelineItemEditKind.Move ? (48L, 480L) : (0L, 528L),
            (current.ProjectStartTick, current.LengthTicks));
        session.Document.Undo();
        Segment restored = session.Project.Tracks.SelectMany(value => value.Segments).Single();
        Assert.Equal((0L, 480L), (restored.ProjectStartTick, restored.LengthTicks));
    }

    [Fact]
    public async Task ArrangementGestureDirectoryScanCanCancelBeforeAllocatingOrPublishingSelectionArrays()
    {
        var (session, _, segment, _) = await CreateSessionAsync();
        await using var ownedSession = session;
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        for (int index = 0; index < 5000; index++)
            track.Segments.Add(new(session.Project!) { ProjectStartTick = 1000 + index * 10L, LengthTicks = 5 });
        var root = session.Project.Tracks;
        long nextId = session.Project.NextStableId;
        int history = session.Document!.History.Count;
        using var cancellation = new CancellationTokenSource();
        var progress = new InlinePropertiesProgress(_ => cancellation.Cancel());
        var command = new ArrangementGestureEditCommand(
            CompressedMidoraIdSet.Create([segment.Id]), segment.Id, segment.ProjectStartTick,
            TimelineItemEditKind.ResizeEnd, false, 0, 1, 1, null);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            session.Document.PrepareEdit(command, cancellation.Token, progress));
        Assert.Same(root, session.Project.Tracks);
        Assert.Equal(nextId, session.Project.NextStableId);
        Assert.Equal(history, session.Document.History.Count);
        Assert.Equal(480, segment.LengthTicks);
    }

    [Fact]
    public async Task FullyCollidingPastePublishesEmptySelection()
    {
        var (session, workspace, segment, first) = await CreateSessionAsync();
        await using var ownedSession = session;
        workspace.Selection.Replace(first.Id, new(WorkspaceTimelineSelectionKind.LogicalNote, segment.Id));
        session.RefreshWorkspaceSelection(workspace);
        using var payload = ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segment.Id, [first.Id]);
        var command = ProjectObjectClipboard.CreatePasteLogicalNotesCommand(session.Document!, payload, segment.Id, first.StartTick);
        using var commandLifetime = command as IDisposable;
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
        Assert.NotNull(staged.PreparedSelection);
        Assert.Empty(staged.PreparedSelection.ResultSelectionIds);
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
        Assert.Empty(workspace.Selection.Ids);
        Assert.Single(segment.Notes);
        Assert.Single(session.Project!.Tracks.SelectMany(track => track.Segments)
            .Single(value => value.Id == segment.Id).Notes);
    }

    [Fact]
    public async Task UnchangedSplitPublishesPreparedSelectionWithoutAddingHistory()
    {
        var (session, workspace, segment, first) = await CreateSessionAsync();
        await using var ownedSession = session;
        workspace.Selection.Replace(first.Id, new(WorkspaceTimelineSelectionKind.LogicalNote, segment.Id));
        session.RefreshWorkspaceSelection(workspace);
        using StagedProjectEdit staged = session.PrepareProjectEdit(
            ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [first.Id],
                new NoteSplitOptions
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 1000
                }), workspace);
        Assert.NotNull(staged.PreparedSelection);
        Assert.Equal([first.Id], staged.PreparedSelection.ResultSelectionIds);
        workspace.Selection.Clear();
        long state = session.Document!.CurrentStateId;
        int historyCount = session.Document.History.Count;
        bool modified = session.Document.IsModified;
        ProjectEditExecution result = session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
        Assert.False(result.Changed);
        Assert.Equal([first.Id], workspace.Selection.Ids);
        Assert.Equal(state, session.Document.CurrentStateId);
        Assert.Equal(historyCount, session.Document.History.Count);
        Assert.Equal(modified, session.Document.IsModified);
    }

    [Fact]
    public async Task BulkPropertiesReadsFrozenSelectionOffThreadAndCanCancel()
    {
        var (session, workspace, segment, first) = await CreateSessionAsync();
        await using var ownedSession = session;
        for (int index = 0; index < 5000; index++)
            segment.Notes.Add(new(session.Project!)
            {
                StartTick = 1000 + index, LengthTicks = 12, Note = 62, Velocity = 100
            });
        workspace.Selection.ApplyRange(segment.Notes.Select(note => note.Id), WorkspaceSelectionRangeMode.Replace,
            new(WorkspaceTimelineSelectionKind.LogicalNote, segment.Id));
        ObjectPropertiesSelectionContext frozen = ObjectPropertiesSelectionContext.Capture(workspace);
        workspace.Selection.Replace(first.Id);
        ObjectPropertiesViewModel properties = await Task.Run(() =>
            ObjectPropertiesProjection.ReadMultiSelection(session.Project!, frozen, default, null));
        Assert.Equal("5001 Logical Notes", properties.Title);
        Assert.Equal("Mixed", Assert.Single(properties.Fields, field => field.Key == "batch.note.number").Value);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlinePropertiesProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            ObjectPropertiesProjection.ReadMultiSelection(session.Project!, frozen, cancellation.Token, progress)));
        Assert.Equal([first.Id], workspace.Selection.Ids);
        Assert.Equal(5001, segment.Notes.Count);
    }

    private sealed class InlinePropertiesProgress(Action<TimelineEditPreparationProgress> action)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => action(value);
    }

    [Fact]
    public async Task PreparationReportsFinalReadyOnlyAfterSelectionProjection()
    {
        var (session, workspace, segment, note) = await CreateSessionAsync();
        await using var ownedSession = session;
        List<TimelineEditPreparationProgress> updates = [];
        using StagedProjectEdit staged = session.PrepareProjectEdit(
            ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [note.Id],
                new NoteSplitOptions { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 40 }),
            workspace, progress: new InlinePropertiesProgress(updates.Add));
        Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(staged, out _));
        Assert.Single(updates, value => value.Phase == TimelineEditPreparationPhase.Ready);
        Assert.Equal(TimelineEditPreparationPhase.Ready, updates[^1].Phase);
        Assert.Equal(1, updates[^1].OverallFraction);
        Assert.All(updates.Take(updates.Count - 1), value => Assert.True(value.OverallFraction < 1));
        Assert.Contains(updates, value => value.IsIndeterminate && value.OverallFraction == 0.97);
    }

    [Fact]
    public async Task CancelAtSelectionProjectionDoesNotReportReadyOrPublishSelection()
    {
        var (session, workspace, segment, note) = await CreateSessionAsync();
        await using var ownedSession = session;
        using var cancellation = new CancellationTokenSource();
        List<TimelineEditPreparationProgress> updates = [];
        var before = workspace.Selection.SharedIds;
        int history = session.Document!.History.Count;
        var progress = new InlinePropertiesProgress(value =>
        {
            updates.Add(value);
            if (value.IsIndeterminate && value.OverallFraction == 0.97) cancellation.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() => session.PrepareProjectEdit(
            ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [note.Id],
                new NoteSplitOptions { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 40 }),
            workspace, cancellation.Token, progress));
        Assert.DoesNotContain(updates, value => value.Phase == TimelineEditPreparationPhase.Ready);
        Assert.Same(before, workspace.Selection.SharedIds);
        Assert.Equal(history, session.Document.History.Count);
        Assert.Single(segment.Notes);
    }

    [Fact]
    public async Task ClipboardPastePublishesSurvivingSelectionWithoutACommandSelectionFacet()
    {
        var (session, workspace, segment, first) = await CreateSessionAsync();
        await using var ownedSession = session;
        LogicalNote second = new(session.Project!) { StartTick = 48, LengthTicks = 1, Note = 62, Velocity = 100 };
        LogicalNote blocker = new(session.Project!) { StartTick = 48, LengthTicks = 1, Note = 60, Velocity = 100 };
        segment.Notes.Add(second);
        segment.Notes.Add(blocker);
        workspace.Selection.ApplyRange([first.Id, second.Id], WorkspaceSelectionRangeMode.Replace,
            new(WorkspaceTimelineSelectionKind.LogicalNote, segment.Id));
        session.RefreshWorkspaceSelection(workspace);
        var originalSelection = workspace.Selection.SharedIds;
        using var payload = ProjectObjectClipboard.CopyLogicalNotes(session.Document!, segment.Id,
            workspace.Selection.SharedIds);
        var command = ProjectObjectClipboard.CreatePasteLogicalNotesCommand(session.Document!, payload, segment.Id, 48);
        using var commandLifetime = command as IDisposable;
        using StagedProjectEdit prepared = session.PrepareProjectEdit(command, workspace);
        Assert.NotNull(prepared.PreparedSelection);
        MidoraId survivor = Assert.Single(prepared.PreparedSelection.ResultSelectionIds);
        session.ExecutePreparedPreservingWorkspaceSelection(prepared, workspace);
        Assert.Equal([survivor], workspace.Selection.Ids);
        Segment result = session.Project!.Tracks.SelectMany(track => track.Segments).Single(value => value.Id == segment.Id);
        LogicalNote pasted = Assert.Single(result.Notes, note => note.Id == survivor);
        Assert.Equal((72L, 62), (pasted.StartTick, pasted.Note));
        Assert.Contains(result.Notes, note => note.Id == blocker.Id);
        session.Undo();
        Assert.Equal(originalSelection, workspace.Selection.SharedIds);
        session.Redo();
        Assert.Equal([survivor], workspace.Selection.Ids);
    }

    [Fact]
    public async Task LargePropertiesTickAndKeyAreResolvedTogetherBeforeCollisions()
    {
        var (session, workspace, segment, existing) = await CreateSessionAsync();
        await using var ownedSession = session;
        var ids = new List<MidoraId>();
        for (int index = 0; index < 4100; index++)
        {
            LogicalNote note = new(session.Project!)
            {
                StartTick = 1000 + index, LengthTicks = 1, Note = existing.Note, Velocity = 100
            };
            segment.Notes.Add(note);
            ids.Add(note.Id);
        }
        workspace.Selection.ApplyRange(ids, WorkspaceSelectionRangeMode.Replace,
            new(WorkspaceTimelineSelectionKind.LogicalNote, segment.Id));
        var command = ObjectPropertiesProjection.CreateEditCommand(session.Project!, workspace,
            new Dictionary<string, string>
            {
                ["batch.note.start"] = existing.StartTick.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["batch.note.number"] = "61"
            });
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
        Assert.Equal(4101, segment.Notes.Count);
        session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
        Segment result = session.Project!.Tracks.SelectMany(track => track.Segments).Single(value => value.Id == segment.Id);
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains(result.Notes, value => value.Id == existing.Id && value.Note == 60);
        LogicalNote winner = Assert.Single(result.Notes, value => value.Id != existing.Id);
        Assert.Equal(61, winner.Note);
        Assert.Equal(existing.StartTick, winner.StartTick);
        session.Undo();
        Assert.Equal(4101, session.Project.Tracks.SelectMany(track => track.Segments).Single(value => value.Id == segment.Id).Notes.Count);
    }

    [Fact]
    public async Task PreparedSplitPublishesResultSelectionAndUndoRedoRestoreBothSides()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 selection publication",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            segment.Id,
            startTick: 24,
            lengthTicks: 120,
            note: 60,
            velocity: 100));
        LogicalNote original = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        WorkspaceTimelineSelectionSource selectionSource = new(
            WorkspaceTimelineSelectionKind.LogicalNote,
            segment.Id);
        workspace.Selection.Replace(original.Id, selectionSource);
        session.RefreshWorkspaceSelection(workspace);
        CompressedMidoraIdSet originalSelectionRoot = workspace.Selection.SharedIds;

        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [original.Id],
                new NoteSplitOptions
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 40
                });
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
        PreparedTimelineSelection preparedTransition =
            Assert.IsType<PreparedTimelineSelection>(staged.PreparedSelection);
        Assert.Equal([original.Id], preparedTransition.OriginalSelectionIds);
        Assert.Equal(3, preparedTransition.ResultSelectionIds.Count);
        Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
            staged,
            out PreparedWorkspaceSelectionProjection? preparedProjection));
        Assert.NotNull(preparedProjection);
        CompressedMidoraIdSet preparedSelectionRoot = preparedProjection.Ids;
        ProjectEditExecution execution = session.ExecutePreparedPreservingWorkspaceSelection(
            staged,
            workspace,
            command);

        Assert.True(execution.Changed);
        Assert.Same(preparedSelectionRoot, workspace.Selection.SharedIds);
        MidoraId[] splitSelection = workspace.Selection.Ids.ToArray();
        Assert.Equal(3, splitSelection.Length);
        Assert.Contains(original.Id, splitSelection);
        Assert.Equal(original.Id, workspace.Selection.Primary);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(splitSelection, ResolveCurrentNotes(session, segment.Id));

        session.Undo();
        Assert.Same(originalSelectionRoot, workspace.Selection.SharedIds);
        Assert.Equal([original.Id], workspace.Selection.Ids);
        Assert.Equal(original.Id, workspace.Selection.Primary);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal([original.Id], ResolveCurrentNotes(session, segment.Id));

        session.Redo();
        Assert.Same(preparedSelectionRoot, workspace.Selection.SharedIds);
        Assert.Equal(splitSelection, workspace.Selection.Ids);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(splitSelection, ResolveCurrentNotes(session, segment.Id));
    }

    [Fact]
    public async Task CancelledPreparationNeverPublishesProjectOrWorkspaceSelection()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            int beforeHistoryCount = session.Document!.History.Count;
            long beforeStateId = session.Document.CurrentStateId;
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.QuantizeLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new(TimelineQuantizeGrid.FromCustomTicks(10)));

            Assert.Throws<OperationCanceledException>(() =>
                session.PrepareProjectEdit(command, workspace, cancellation.Token));

            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Equal(24, Assert.Single(segment.Notes).StartTick);
            Assert.Equal(beforeHistoryCount, session.Document.History.Count);
            Assert.Equal(beforeStateId, session.Document.CurrentStateId);
        }
    }

    [Fact]
    public async Task StalePreparedEditDoesNotAdoptItsFrozenSelection()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            ITimelineSelectionResultEditCommand stagedCommand =
                ProjectDomainEditCommands.SplitLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new NoteSplitOptions
                    {
                        Mode = NoteSplitMode.FixedPieceLength,
                        FixedPieceLengthTicks = 40
                    });
            using StagedProjectEdit staged = session.PrepareProjectEdit(
                stagedCommand,
                workspace);
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            session.Execute(ProjectDomainEditCommands.SetSegmentWindow(
                segment.Id,
                projectStartTick: 1,
                lengthTicks: segment.LengthTicks,
                contentOffsetTick: segment.ContentOffsetTick));

            Assert.Throws<InvalidOperationException>(() =>
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    stagedCommand));

            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Single(session.Project!.Tracks.Single().Segments.Single().Notes);
        }
    }

    [Fact]
    public async Task PreparedNoOpCarriesProjectionButDoesNotReplaceSelectionOrHistory()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            int beforeHistoryCount = session.Document!.History.Count;
            long beforeStateId = session.Document.CurrentStateId;
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.QuantizeLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new(TimelineQuantizeGrid.FromCustomTicks(24)));

            using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
            Assert.NotNull(staged.PreparedSelection);
            Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
                staged,
                out PreparedWorkspaceSelectionProjection? preparedProjection));
            Assert.NotNull(preparedProjection);

            ProjectEditExecution execution =
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    command);

            Assert.False(execution.Changed);
            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Equal(
                new WorkspaceTimelineSelectionSource(
                    WorkspaceTimelineSelectionKind.LogicalNote,
                    segment.Id),
                workspace.Selection.HomogeneousTimelineSource);
            Assert.Equal(beforeHistoryCount, session.Document.History.Count);
            Assert.Equal(beforeStateId, session.Document.CurrentStateId);
            Assert.Equal(24, Assert.Single(segment.Notes).StartTick);
        }
    }

    [Fact]
    public async Task PreparedMultiOwnerEditAdoptsOnePrecompressedResultRoot()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment firstSegment, LogicalNote firstNote) = await CreateSessionAsync();
        await using (session)
        {
            EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                "Second Track",
                instrument.Id));
            LogicalTrack secondTrack = session.Project.Tracks.Single(
                value => value.Name == "Second Track");
            session.Execute(ProjectDomainEditCommands.CreateSegment(
                secondTrack.Id,
                projectStartTick: 600,
                lengthTicks: 480));
            Segment secondSegment = Assert.Single(secondTrack.Segments);
            session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                secondSegment.Id,
                startTick: 24,
                lengthTicks: 120,
                note: 64,
                velocity: 90));
            LogicalNote secondNote = Assert.Single(secondSegment.Notes);
            workspace.Selection.Replace(firstNote.Id);
            workspace.Selection.Add(secondNote.Id);
            session.RefreshWorkspaceSelection(workspace);
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.SplitLogicalNotes(
                [
                    new(firstSegment.Id, [firstNote.Id]),
                    new(secondSegment.Id, [secondNote.Id])
                ],
                new NoteSplitOptions
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 40
                });
            using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
            PreparedTimelineSelection transition = Assert.IsType<PreparedTimelineSelection>(
                staged.PreparedSelection);
            Assert.Equal(2, transition.OriginalSelectionIds.Count);
            Assert.Equal(6, transition.ResultSelectionIds.Count);
            Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
                staged,
                out PreparedWorkspaceSelectionProjection? preparedProjection));
            Assert.NotNull(preparedProjection);
            CompressedMidoraIdSet resultSelectionRoot = preparedProjection.Ids;

            ProjectEditExecution execution =
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    command);

            Assert.True(execution.Changed);
            Assert.Same(resultSelectionRoot, workspace.Selection.SharedIds);
            Assert.Equal(6, workspace.Selection.Ids.Count);
            Assert.Equal(6, session.Project.Tracks
                .SelectMany(static value => value.Segments)
                .Where(value => value.Id == firstSegment.Id || value.Id == secondSegment.Id)
                .Sum(static value => value.Notes.Count));
        }
    }

    [Fact]
    public async Task MultiObjectCopiesPublishEveryStage4NoteAndEventSelectionSource()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 copied selection routing",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id, voice.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id, voice.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id, voice.Id, 12, 11, 40));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id, voice.Id, 36, 11, 80));
        TemplateEvent[] templateNotes = voice.Events
            .Where(static value => value.Kind == TemplateEventKind.Note)
            .ToArray();
        TemplateEvent[] templateEvents = voice.Events
            .Where(static value => value.Kind == TemplateEventKind.ControlChange)
            .ToArray();

        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Expression",
            LogicalParameterType.Integer,
            0,
            127,
            0,
            127,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
        LogicalTrack logicalTrack = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(logicalTrack.Id, 0, 480));
        Segment logicalSegment = Assert.Single(logicalTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            logicalSegment.Id,
            parameter.Id));
        LogicalParameterLane logicalLane = Assert.Single(logicalSegment.ParameterLanes);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            logicalSegment.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            logicalSegment.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            logicalSegment.Id, logicalLane.Id, 12, 40, CurveInterpolation.Step));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            logicalSegment.Id, logicalLane.Id, 36, 80, CurveInterpolation.Step));
        LogicalNote[] logicalNotes = logicalSegment.Notes.ToArray();
        CurvePoint[] logicalPoints = logicalLane.Points.ToArray();

        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
        PureMidiTrack midiTrack = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(midiTrack.Id, 0, 480));
        MidiSegment midiSegment = Assert.Single(midiTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            midiSegment.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            midiSegment.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            midiSegment.Id, 12, DirectMidiChannelEventKind.ControlChange, 11, 40));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            midiSegment.Id, 36, DirectMidiChannelEventKind.ControlChange, 11, 80));
        DirectMidiNote[] directNotes = midiSegment.Notes.ToArray();
        DirectMidiChannelEvent[] directEvents = midiSegment.ChannelEvents.ToArray();

        TimelineWorkspaceViewModel logicalWorkspace = session.OpenSegment(logicalSegment.Id);
        TimelineWorkspaceViewModel midiWorkspace = session.OpenSegment(midiSegment.Id);
        InstrumentWorkspaceViewModel instrumentWorkspace = session.OpenInstrument(instrument.Id);
        MidiValueTarget subVoiceTarget = MidiValueTarget.ControlChange(11);

        DuplicateAndAssert(
            logicalWorkspace,
            new(WorkspaceTimelineSelectionKind.LogicalNote, logicalSegment.Id),
            ProjectDomainEditCommands.DuplicateLogicalNotes(
                logicalSegment.Id,
                logicalNotes.Select(static value => value.Id).ToArray(),
                logicalSegment.Id,
                200));
        DuplicateAndAssert(
            logicalWorkspace,
            new(
                WorkspaceTimelineSelectionKind.LogicalParameterPoint,
                logicalSegment.Id,
                logicalLane.Id,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.DuplicateLogicalParameterPoints(
                logicalSegment.Id,
                logicalLane.Id,
                logicalPoints.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                valueDelta: 0));
        DuplicateAndAssert(
            midiWorkspace,
            new(WorkspaceTimelineSelectionKind.DirectMidiNote, midiSegment.Id),
            ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                midiSegment.Id,
                directNotes.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                keyDelta: 0));
        DuplicateAndAssert(
            midiWorkspace,
            new(
                WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
                midiSegment.Id,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange,
                DirectMidiData1: 11,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
                midiSegment.Id,
                directEvents.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                data1Delta: 0,
                data2Delta: 0,
                duplicate: true));
        DuplicateAndAssert(
            instrumentWorkspace,
            new(
                WorkspaceTimelineSelectionKind.TemplateNote,
                instrument.Id,
                voice.Id),
            ProjectDomainEditCommands.DuplicateTemplateNotes(
                instrument.Id,
                voice.Id,
                templateNotes.Select(static value => value.Id).ToArray(),
                newEarliestTick: 200,
                pitchDelta: 0));
        DuplicateAndAssert(
            instrumentWorkspace,
            new(
                WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                instrument.Id,
                voice.Id,
                subVoiceTarget,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                instrument.Id,
                voice.Id,
                templateEvents.Select(static value => value.Id).ToArray(),
                subVoiceTarget,
                tickDelta: 200,
                valueDelta: 0,
                duplicate: true));

        void DuplicateAndAssert(
            WorkspaceViewModel workspace,
            WorkspaceTimelineSelectionSource source,
            IProjectEditCommand command)
        {
            long firstNewStableId = session.Project!.NextStableId;
            session.Execute(command);
            MainWindow.SelectCreatedWorkspaceObjects(
                session,
                workspace,
                firstNewStableId,
                timelineSource: source);
            Assert.Equal(2, workspace.Selection.Ids.Count);
            Assert.Equal(source, workspace.Selection.HomogeneousTimelineSource);
            Assert.Equal(
                source.QuantizeScope,
                workspace.Selection.HomogeneousTimelineQuantizeScope);
        }
    }

    private static async Task<(DesktopSessionController Session,
        TimelineWorkspaceViewModel Workspace, Segment Segment, LogicalNote Note)>
        CreateSessionAsync()
    {
        DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 prepared projection",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            segment.Id,
            startTick: 24,
            lengthTicks: 120,
            note: 60,
            velocity: 100));
        LogicalNote note = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Replace(
            note.Id,
            new(
                WorkspaceTimelineSelectionKind.LogicalNote,
                segment.Id));
        session.RefreshWorkspaceSelection(workspace);
        session.Document!.MarkSaveSucceeded();
        return (session, workspace, segment, note);
    }

    private static MidoraId[] ResolveCurrentNotes(
        DesktopSessionController session,
        MidoraId segmentId) =>
        session.Project!.Tracks
            .SelectMany(static value => value.Segments)
            .Single(value => value.Id == segmentId)
            .Notes.Select(static note => note.Id)
            .ToArray();
}
