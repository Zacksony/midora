using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectDocumentSessionTests
{
    [Fact]
    public void FreshUnsavedDocumentNeedsSaveWithoutBeingModified()
    {
        using ProjectCompilationSession compilation = new(CreateProject());
        ProjectDocumentSession document = new(compilation);

        Assert.False(document.HasPersistentOrigin);
        Assert.False(document.IsModified);
        Assert.True(document.NeedsSaveBeforeClose);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);

        document.MarkSaveSucceeded();

        Assert.True(document.HasPersistentOrigin);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
    }

    [Fact]
    public void PropertyEditUndoAndRedoKeepCompilationAndHistoryInSync()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult original = compilation.LastAttempt;
        int compilationEvents = 0;
        int historyEvents = 0;
        List<ProjectContentChangedEventArgs> contentEvents = [];
        compilation.CompilationChanged += (_, _) => compilationEvents++;
        document.HistoryChanged += (_, _) => historyEvents++;
        document.ContentChanged += (_, args) => contentEvents.Add(args);

        ProjectEditExecution edit = document.Execute(SetPitch(track.Id, note.Id, 72));

        Assert.True(edit.Changed);
        Assert.True(document.IsModified);
        Assert.True(document.NeedsSaveBeforeClose);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal("Change note pitch", document.UndoName);
        Assert.Equal(72, note.Note);
        Assert.NotEqual(original.Fingerprint, edit.CompilationResult.Fingerprint);

        CanonicalCompiledResult undone = document.Undo();

        Assert.Equal(60, note.Note);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
        Assert.Equal("Change note pitch", document.RedoName);
        AssertFormallyEqual(original, undone);

        CanonicalCompiledResult redone = document.Redo();

        Assert.Equal(72, note.Note);
        Assert.True(document.IsModified);
        Assert.Equal(edit.CompilationResult.Fingerprint, redone.Fingerprint);
        Assert.Equal(3, compilationEvents);
        Assert.Equal(3, historyEvents);
        Assert.Equal(3, contentEvents.Count);
        Assert.All(contentEvents, change => Assert.Contains(track.Id, change.TrackIds));
    }

    [Fact]
    public void PreparedEditPublishesOnlyWhenExecutedAndSupportsUndoRedo()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult original = compilation.LastAttempt;
        long originalStateId = document.CurrentStateId;
        int compilationEvents = 0;
        int historyEvents = 0;
        int contentEvents = 0;
        compilation.CompilationChanged += (_, _) => compilationEvents++;
        document.HistoryChanged += (_, _) => historyEvents++;
        document.ContentChanged += (_, _) => contentEvents++;

        StagedProjectEdit staged = document.PrepareEdit(SetPitch(track.Id, note.Id, 72));

        Assert.Equal("Change note pitch", staged.Name);
        Assert.Equal(60, note.Note);
        Assert.Equal(originalStateId, document.CurrentStateId);
        Assert.Same(original, compilation.LastAttempt);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal(0, compilationEvents);
        Assert.Equal(0, historyEvents);
        Assert.Equal(0, contentEvents);

        ProjectEditExecution executed = document.ExecutePrepared(staged);

        Assert.True(executed.Changed);
        Assert.Equal(72, note.Note);
        Assert.NotEqual(originalStateId, document.CurrentStateId);
        Assert.NotEqual(original.Fingerprint, executed.CompilationResult.Fingerprint);
        Assert.Single(document.History);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        CanonicalCompiledResult undone = document.Undo();

        Assert.Equal(60, note.Note);
        Assert.Equal(originalStateId, document.CurrentStateId);
        AssertFormallyEqual(original, undone);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);

        CanonicalCompiledResult redone = document.Redo();

        Assert.Equal(72, note.Note);
        Assert.Equal(executed.CompilationResult.Fingerprint, redone.Fingerprint);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal(3, compilationEvents);
        Assert.Equal(3, historyEvents);
        Assert.Equal(3, contentEvents);
    }

    [Fact]
    public void PreparedNoOpDoesNotPublishCompileNotifyOrCreateHistory()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;
        long beforeStateId = document.CurrentStateId;
        int compilationEvents = 0;
        int historyEvents = 0;
        int contentEvents = 0;
        compilation.CompilationChanged += (_, _) => compilationEvents++;
        document.HistoryChanged += (_, _) => historyEvents++;
        document.ContentChanged += (_, _) => contentEvents++;

        StagedProjectEdit staged = document.PrepareEdit(
            SetVelocity(track.Id, note.Id, note.Velocity));
        ProjectEditExecution result = document.ExecutePrepared(staged);

        Assert.False(result.Changed);
        Assert.Same(before, result.CompilationResult);
        Assert.Equal(100, note.Velocity);
        Assert.Equal(beforeStateId, document.CurrentStateId);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal(0, compilationEvents);
        Assert.Equal(0, historyEvents);
        Assert.Equal(0, contentEvents);
    }

    [Fact]
    public void CancellationBeforePreparationDoesNotInvokeCommandOrPublish()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        TrackingCancellableCommand command = new(SetVelocity(track.Id, note.Id, 80));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            document.PrepareEdit(command, cancellation.Token));

        Assert.Equal(0, command.CancellablePrepareCalls);
        Assert.Equal(0, command.NonCancellablePrepareCalls);
        Assert.Equal(100, note.Velocity);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void CancellationRaisedDuringPreparationDiscardsPreparedResultWithoutPublishing()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        using CancellationTokenSource cancellation = new();
        TrackingCancellableCommand command = new(
            SetVelocity(track.Id, note.Id, 80),
            cancellation.Cancel);
        CanonicalCompiledResult before = compilation.LastAttempt;
        int historyEvents = 0;
        int contentEvents = 0;
        document.HistoryChanged += (_, _) => historyEvents++;
        document.ContentChanged += (_, _) => contentEvents++;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            document.PrepareEdit(command, cancellation.Token));

        Assert.Equal(1, command.CancellablePrepareCalls);
        Assert.Equal(0, command.NonCancellablePrepareCalls);
        Assert.Equal(100, note.Velocity);
        Assert.Same(before, compilation.LastAttempt);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.Equal(0, historyEvents);
        Assert.Equal(0, contentEvents);
    }

    [Fact]
    public void RevisionChangeDuringPreparationRejectsDetachedResult()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CallbackPrepareCommand command = new(
            "Prepared stale velocity change",
            () => document.Execute(SetVelocity(track.Id, note.Id, 90)),
            SetVelocity(track.Id, note.Id, 80));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.PrepareEdit(command));

        Assert.Contains("changed while the edit was being prepared", error.Message);
        Assert.Equal(90, note.Velocity);
        Assert.Single(document.History);
        Assert.Equal("Change note velocity", document.UndoName);
        Assert.True(document.IsModified);
    }

    [Fact]
    public void RevisionChangeAfterPreparationRejectsPublication()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        StagedProjectEdit staged = document.PrepareEdit(SetVelocity(track.Id, note.Id, 80));
        ProjectEditExecution intervening = document.Execute(SetVelocity(track.Id, note.Id, 90));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.ExecutePrepared(staged));

        Assert.Contains("changed after the edit was prepared", error.Message);
        Assert.Equal(90, note.Velocity);
        Assert.Same(intervening.CompilationResult, compilation.LastAttempt);
        Assert.Single(document.History);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void PublicationRevisionAdvancesAcrossExecuteUndoAndRedoWithoutRewinding()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        long initialStateId = document.CurrentStateId;

        Assert.Equal(0, document.PublicationRevision);

        Assert.True(document.Execute(SetVelocity(track.Id, note.Id, 80)).Changed);
        Assert.Equal(1, document.PublicationRevision);

        document.Undo();
        Assert.Equal(initialStateId, document.CurrentStateId);
        Assert.Equal(2, document.PublicationRevision);

        document.Redo();
        Assert.Equal(3, document.PublicationRevision);
    }

    [Fact]
    public void ExecuteUndoAbaAfterPreparationRejectsStalePublication()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        long initialStateId = document.CurrentStateId;
        StagedProjectEdit staged = document.PrepareEdit(
            SetVelocity(track.Id, note.Id, 80));

        Assert.True(document.Execute(SetVelocity(track.Id, note.Id, 90)).Changed);
        document.Undo();
        Assert.Equal(initialStateId, document.CurrentStateId);
        Assert.Equal(2, document.PublicationRevision);
        Assert.Equal(100, note.Velocity);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.ExecutePrepared(staged));

        Assert.Contains("changed after the edit was prepared", error.Message);
        Assert.Equal(2, document.PublicationRevision);
        Assert.Equal(100, note.Velocity);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void ExecuteUndoAbaDuringPreparationRejectsDetachedResult()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        long initialStateId = document.CurrentStateId;
        CallbackPrepareCommand command = new(
            "Prepared ABA velocity change",
            () =>
            {
                document.Execute(SetVelocity(track.Id, note.Id, 90));
                document.Undo();
            },
            SetVelocity(track.Id, note.Id, 80));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.PrepareEdit(command));

        Assert.Contains("changed while the edit was being prepared", error.Message);
        Assert.Equal(initialStateId, document.CurrentStateId);
        Assert.Equal(2, document.PublicationRevision);
        Assert.Equal(100, note.Velocity);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void NoOpDoesNotAdvanceOrInvalidatePublicationRevisionGate()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        StagedProjectEdit staged = document.PrepareEdit(
            SetVelocity(track.Id, note.Id, 80));

        ProjectEditExecution noOp = document.Execute(
            SetVelocity(track.Id, note.Id, note.Velocity));

        Assert.False(noOp.Changed);
        Assert.Equal(0, document.PublicationRevision);
        Assert.True(document.ExecutePrepared(staged).Changed);
        Assert.Equal(1, document.PublicationRevision);
        Assert.Equal(80, note.Velocity);
    }

    [Fact]
    public void FailedEditDoesNotAdvanceOrInvalidatePublicationRevisionGate()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        StagedProjectEdit staged = document.PrepareEdit(
            SetVelocity(track.Id, note.Id, 80));

        Assert.Throws<InvalidOperationException>(() =>
            document.Execute(new FailingVelocityCommand(track.Id, note.Id)));

        Assert.Equal(0, document.PublicationRevision);
        Assert.True(document.ExecutePrepared(staged).Changed);
        Assert.Equal(1, document.PublicationRevision);
        Assert.Equal(80, note.Velocity);
    }

    [Fact]
    public void PublicationRevisionOverflowFailsBeforeExecuteUndoAndRedoMutation()
    {
        MidoraProject executeProject = CreateProject();
        LogicalTrack executeTrack = executeProject.Tracks[0];
        LogicalNote executeNote = executeTrack.Segments[0].Notes[0];
        using ProjectCompilationSession executeCompilation = new(executeProject);
        using ProjectDocumentSession executeDocument = new(
            executeCompilation,
            ProjectDocumentOrigin.Persisted);
        SetPublicationRevision(executeDocument, long.MaxValue);

        InvalidOperationException executeError = Assert.Throws<InvalidOperationException>(() =>
            executeDocument.Execute(SetVelocity(executeTrack.Id, executeNote.Id, 80)));
        Assert.Contains("publication revision counter is exhausted", executeError.Message);
        Assert.Equal(100, executeNote.Velocity);
        Assert.Empty(executeDocument.History);

        MidoraProject undoProject = CreateProject();
        LogicalTrack undoTrack = undoProject.Tracks[0];
        LogicalNote undoNote = undoTrack.Segments[0].Notes[0];
        using ProjectCompilationSession undoCompilation = new(undoProject);
        using ProjectDocumentSession undoDocument = new(
            undoCompilation,
            ProjectDocumentOrigin.Persisted);
        Assert.True(undoDocument.Execute(SetVelocity(undoTrack.Id, undoNote.Id, 80)).Changed);
        SetPublicationRevision(undoDocument, long.MaxValue);

        InvalidOperationException undoError = Assert.Throws<InvalidOperationException>(
            undoDocument.Undo);
        Assert.Contains("publication revision counter is exhausted", undoError.Message);
        Assert.Equal(80, undoNote.Velocity);
        Assert.True(undoDocument.CanUndo);

        MidoraProject redoProject = CreateProject();
        LogicalTrack redoTrack = redoProject.Tracks[0];
        LogicalNote redoNote = redoTrack.Segments[0].Notes[0];
        using ProjectCompilationSession redoCompilation = new(redoProject);
        using ProjectDocumentSession redoDocument = new(
            redoCompilation,
            ProjectDocumentOrigin.Persisted);
        Assert.True(redoDocument.Execute(SetVelocity(redoTrack.Id, redoNote.Id, 80)).Changed);
        redoDocument.Undo();
        SetPublicationRevision(redoDocument, long.MaxValue);

        InvalidOperationException redoError = Assert.Throws<InvalidOperationException>(
            redoDocument.Redo);
        Assert.Contains("publication revision counter is exhausted", redoError.Message);
        Assert.Equal(100, redoNote.Velocity);
        Assert.True(redoDocument.CanRedo);
    }

    [Fact]
    public void NoOpAtMaximumPublicationRevisionStillSucceedsWithoutMutation()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        SetPublicationRevision(document, long.MaxValue);

        ProjectEditExecution result = document.Execute(
            SetVelocity(track.Id, note.Id, note.Velocity));

        Assert.False(result.Changed);
        Assert.Equal(long.MaxValue, document.PublicationRevision);
        Assert.Empty(document.History);
        Assert.Equal(100, note.Velocity);
    }

    [Fact]
    public void StagedEditCannotBeExecutedByAnotherDocument()
    {
        MidoraProject firstProject = CreateProject();
        LogicalTrack firstTrack = firstProject.Tracks[0];
        LogicalNote firstNote = firstTrack.Segments[0].Notes[0];
        using ProjectCompilationSession firstCompilation = new(firstProject);
        ProjectDocumentSession firstDocument = new(
            firstCompilation,
            ProjectDocumentOrigin.Persisted);
        StagedProjectEdit staged = firstDocument.PrepareEdit(
            SetVelocity(firstTrack.Id, firstNote.Id, 80));

        MidoraProject secondProject = CreateProject();
        LogicalNote secondNote = secondProject.Tracks[0].Segments[0].Notes[0];
        using ProjectCompilationSession secondCompilation = new(secondProject);
        ProjectDocumentSession secondDocument = new(
            secondCompilation,
            ProjectDocumentOrigin.Persisted);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            secondDocument.ExecutePrepared(staged));

        Assert.Equal("staged", error.ParamName);
        Assert.Contains("another Project document", error.Message);
        Assert.Equal(100, firstNote.Velocity);
        Assert.Equal(100, secondNote.Velocity);
        Assert.Empty(firstDocument.History);
        Assert.Empty(secondDocument.History);
        Assert.False(firstDocument.IsModified);
        Assert.False(secondDocument.IsModified);

        Assert.True(firstDocument.ExecutePrepared(staged).Changed);
        Assert.Equal(80, firstNote.Velocity);
        Assert.Equal(100, secondNote.Velocity);
    }

    [Fact]
    public void AbandonedAndPostPrepareCancelledEditsReleaseOwnedPreparationResources()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);

        DisposablePreparedCommand abandonedCommand = new(
            SetVelocity(track.Id, note.Id, 80));
        StagedProjectEdit abandoned = document.PrepareEdit(abandonedCommand);
        Assert.Equal(0, abandonedCommand.DisposeCount);

        abandoned.Dispose();
        Assert.Equal(1, abandonedCommand.DisposeCount);
        Assert.Equal(100, note.Velocity);
        Assert.Empty(document.History);

        using CancellationTokenSource cancellation = new();
        DisposablePreparedCommand cancelledCommand = new(
            SetVelocity(track.Id, note.Id, 70),
            cancellation.Cancel);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            document.PrepareEdit(cancelledCommand, cancellation.Token));
        Assert.Equal(1, cancelledCommand.DisposeCount);
        Assert.Equal(100, note.Velocity);
        Assert.Empty(document.History);
    }

    [Fact]
    public void SuccessfulStagedEditTransfersResourcesToHistoryUntilDocumentDisposal()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        DisposablePreparedCommand command = new(SetVelocity(track.Id, note.Id, 80));
        using StagedProjectEdit staged = document.PrepareEdit(command);

        Assert.True(document.ExecutePrepared(staged).Changed);
        Assert.Equal(0, command.DisposeCount);
        Assert.Equal(80, note.Velocity);

        staged.Dispose();
        Assert.Equal(0, command.DisposeCount);

        document.Dispose();
        Assert.Equal(1, command.DisposeCount);
    }

    [Fact]
    public void NoOpAndStalePreparedEditsReleaseOwnedPreparationResources()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);

        DisposablePreparedCommand noOp = new(
            SetVelocity(track.Id, note.Id, note.Velocity));
        Assert.False(document.Execute(noOp).Changed);
        Assert.Equal(1, noOp.DisposeCount);

        DisposablePreparedCommand stale = new(SetVelocity(track.Id, note.Id, 80));
        StagedProjectEdit staged = document.PrepareEdit(stale);
        Assert.True(document.Execute(SetVelocity(track.Id, note.Id, 90)).Changed);

        Assert.Throws<InvalidOperationException>(() => document.ExecutePrepared(staged));
        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(90, note.Velocity);
    }

    [Fact]
    public void ReplacingRedoBranchReleasesItsOwnedPreparationResources()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(
            compilation,
            ProjectDocumentOrigin.Persisted);
        DisposablePreparedCommand oldBranch = new(SetVelocity(track.Id, note.Id, 80));
        DisposablePreparedCommand replacement = new(SetVelocity(track.Id, note.Id, 70));

        Assert.True(document.Execute(oldBranch).Changed);
        document.Undo();
        Assert.Equal(0, oldBranch.DisposeCount);

        Assert.True(document.Execute(replacement).Changed);
        Assert.Equal(1, oldBranch.DisposeCount);
        Assert.Equal(0, replacement.DisposeCount);

        document.Dispose();
        Assert.Equal(1, replacement.DisposeCount);
    }

    [Fact]
    public void NoOpDoesNotCompileCreateHistoryOrMarkModified()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;
        int events = 0;
        document.HistoryChanged += (_, _) => events++;

        ProjectEditExecution result = document.Execute(SetVelocity(track.Id, note.Id, note.Velocity));

        Assert.False(result.Changed);
        Assert.Same(before, result.CompilationResult);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SavePointTracksUndoBackToSavedStateAndSaveCopyNeedsNoMutation()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));

        document.MarkSaveSucceeded();
        Assert.False(document.IsModified);
        document.Execute(SetVelocity(track.Id, note.Id, 90));
        Assert.True(document.IsModified);

        document.Undo();

        Assert.Equal(80, note.Velocity);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void EditingAfterUndoDiscardsRedoBranchAndCannotReachDiscardedSavePoint()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        document.MarkSaveSucceeded();
        document.Undo();
        Assert.True(document.IsModified);

        document.Execute(SetVelocity(track.Id, note.Id, 70));

        Assert.False(document.CanRedo);
        Assert.True(document.IsModified);
        Assert.Single(document.History);
        Assert.Equal(70, note.Velocity);
    }

    [Fact]
    public void ExternalDirtyReasonSurvivesUndoAndClearsOnlyAfterSuccessfulSave()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.MarkExternallyModified("RecoveredConductorDefaults");
        document.MarkExternallyModified("RecoveredConductorDefaults");
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        document.Undo();

        Assert.True(document.IsModified);
        Assert.Equal(["RecoveredConductorDefaults"], document.ExternalDirtyReasons);

        document.MarkSaveSucceeded();

        Assert.False(document.IsModified);
        Assert.Empty(document.ExternalDirtyReasons);
    }

    [Fact]
    public void ProjectEditLockRejectsExecuteUndoAndRedoWithoutMutatingHistory()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        using IDisposable editLock = compilation.AcquireProjectEditLock();

        Assert.Throws<InvalidOperationException>(() => document.Undo());
        Assert.Throws<InvalidOperationException>(() =>
            document.Execute(SetVelocity(track.Id, note.Id, 70)));

        Assert.Equal(80, note.Velocity);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Single(document.History);
    }

    [Fact]
    public void FailedApplyRollsBackProjectAndCompilerWithoutCreatingHistory()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(new FailingVelocityCommand(track.Id, note.Id)));

        Assert.Equal("Injected edit failure.", error.Message);
        Assert.Equal(100, note.Velocity);
        AssertFormallyEqual(before, compilation.LastAttempt);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void InvalidCommandNameAndUnavailableUndoRedoFailBeforeMutation()
    {
        using ProjectCompilationSession compilation = new(CreateProject());
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        Assert.Throws<InvalidOperationException>(document.Undo);
        Assert.Throws<InvalidOperationException>(document.Redo);
        Assert.Throws<ArgumentException>(() => document.Execute(new EmptyNameCommand()));
        Assert.Throws<ArgumentException>(() => document.PrepareEdit(new EmptyNameCommand()));
        Assert.Empty(document.History);
    }

    [Fact]
    public void ChangeNotificationsCannotReenterProjectHistoryMutation()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        bool rejected = false;
        document.HistoryChanged += (_, _) =>
        {
            _ = Assert.Throws<InvalidOperationException>(() =>
                document.Execute(SetVelocity(track.Id, note.Id, 90)));
            rejected = true;
        };

        document.Execute(SetVelocity(track.Id, note.Id, 80));

        Assert.True(rejected);
        Assert.Equal(80, note.Velocity);
        Assert.Single(document.History);
    }

    private static ProjectPropertyEditCommand<int> SetVelocity(
        MidoraId trackId,
        MidoraId noteId,
        int velocity)
    {
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(trackId);
        return new(
            "Change note velocity",
            project => FindNote(project, trackId, noteId).Velocity,
            (project, value) => FindNote(project, trackId, noteId).Velocity = value,
            velocity,
            changes);
    }

    private static ProjectPropertyEditCommand<int> SetPitch(
        MidoraId trackId,
        MidoraId noteId,
        int note)
    {
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(trackId);
        return new(
            "Change note pitch",
            project => FindNote(project, trackId, noteId).Note,
            (project, value) => FindNote(project, trackId, noteId).Note = value,
            note,
            changes);
    }

    private static LogicalNote FindNote(MidoraProject project, MidoraId trackId, MidoraId noteId) =>
        project.Tracks.Single(track => track.Id == trackId)
            .Segments.SelectMany(segment => segment.Notes)
            .Single(note => note.Id == noteId);

    private static void SetPublicationRevision(
        ProjectDocumentSession document,
        long revision)
    {
        System.Reflection.FieldInfo field = typeof(ProjectDocumentSession).GetField(
            "_publicationRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ProjectDocumentSession publication revision field was not found.");
        field.SetValue(document, revision);
    }

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
        LogicalTrack track = new(project) { Name = "Track"};
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

    private static void AssertFormallyEqual(
        CanonicalCompiledResult expected,
        CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Conductor.Tempos.ToArray(), actual.Conductor.Tempos.ToArray());
        Assert.Equal(
            expected.Conductor.TimeSignatures.ToArray(),
            actual.Conductor.TimeSignatures.ToArray());
        Assert.Equal(
            expected.Conductor.KeySignatures.ToArray(),
            actual.Conductor.KeySignatures.ToArray());
        Assert.Equal(expected.Conductor.Markers.ToArray(), actual.Conductor.Markers.ToArray());
        Assert.Equal(expected.Conductor.EndMarker, actual.Conductor.EndMarker);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed class FailingVelocityCommand(MidoraId trackId, MidoraId noteId)
        : IProjectEditCommand
    {
        public string Name => "Failing velocity change";

        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            new FailingPrepared(trackId, noteId, FindNote(project, trackId, noteId).Velocity);

        private sealed class FailingPrepared(
            MidoraId trackId,
            MidoraId noteId,
            int oldVelocity) : IPreparedProjectEdit
        {
            public bool HasChanges => true;
            public ProjectChangeSet Changes { get; } = TrackChange(trackId);

            public void Apply(MidoraProject project)
            {
                FindNote(project, trackId, noteId).Velocity = 1;
                throw new InvalidOperationException("Injected edit failure.");
            }

            public void Undo(MidoraProject project) =>
                FindNote(project, trackId, noteId).Velocity = oldVelocity;

            private static ProjectChangeSet TrackChange(MidoraId id)
            {
                ProjectChangeSet result = new();
                result.TrackIds.Add(id);
                return result;
            }
        }
    }

    private sealed class EmptyNameCommand : IProjectEditCommand
    {
        public string Name => " ";
        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            throw new InvalidOperationException("Prepare must not run for an invalid name.");
    }

    private sealed class TrackingCancellableCommand(
        IProjectEditCommand inner,
        Action? duringPrepare = null) : ICancellableProjectEditCommand
    {
        public string Name => inner.Name;
        public int NonCancellablePrepareCalls { get; private set; }
        public int CancellablePrepareCalls { get; private set; }

        public IPreparedProjectEdit Prepare(MidoraProject project)
        {
            NonCancellablePrepareCalls++;
            return inner.Prepare(project);
        }

        public IPreparedProjectEdit Prepare(
            MidoraProject project,
            CancellationToken cancellationToken)
        {
            CancellablePrepareCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            duringPrepare?.Invoke();
            return inner.Prepare(project);
        }
    }

    private sealed class CallbackPrepareCommand(
        string name,
        Action duringPrepare,
        IProjectEditCommand inner) : IProjectEditCommand
    {
        public string Name { get; } = name;

        public IPreparedProjectEdit Prepare(MidoraProject project)
        {
            duringPrepare();
            return inner.Prepare(project);
        }
    }

    private sealed class DisposablePreparedCommand(
        IProjectEditCommand inner,
        Action? afterPrepare = null) : ICancellableProjectEditCommand
    {
        public string Name => inner.Name;
        public int DisposeCount { get; private set; }

        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            Create(project);

        public IPreparedProjectEdit Prepare(
            MidoraProject project,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Create(project);
        }

        private IPreparedProjectEdit Create(MidoraProject project)
        {
            IPreparedProjectEdit prepared = inner.Prepare(project);
            DisposablePrepared wrapper = new(prepared, () => DisposeCount++);
            afterPrepare?.Invoke();
            return wrapper;
        }

        private sealed class DisposablePrepared(
            IPreparedProjectEdit inner,
            Action disposed) : IPreparedProjectEdit, IDisposable
        {
            private bool _disposed;

            public bool HasChanges => inner.HasChanges;
            public ProjectChangeSet Changes => inner.Changes;
            public void Apply(MidoraProject project) => inner.Apply(project);
            public void Undo(MidoraProject project) => inner.Undo(project);

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                disposed();
                if (inner is IDisposable disposable) disposable.Dispose();
            }
        }
    }
}
