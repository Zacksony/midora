using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class SegmentConversionPlacementAndHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferAnchorsNonEarliestPrimaryAndPreservesBothSignsOfGlobalTrackOffset(bool copy)
    {
        using Fixture f = new();
        // The primary is on the middle source Track, but starts after both
        // siblings. Thus TrackOffset and StartOffset have different origins.
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.SourceIds[2], f.SourceIds[0], f.SourceIds[1]], f.SourceIds[1],
            f.TrackIds[4], 1000, copy);
        using var command = new CommandLifetime(plan.CreateCommand());
        using StagedProjectEdit staged = f.Document.PrepareEdit(command.Command);
        MidoraId[] resultIds = staged.PreparedSelection!.ResultSelectionIds.ToArray();
        Assert.Equal(3, resultIds.Length);
        f.Document.ExecutePrepared(staged);

        f.AssertTargetRows([800, 1000, 750], resultIds);
        for (int index = 0; index < 3; index++)
            Assert.Equal(copy ? 1 : 0, f.SegmentIds(index).Length);
        f.Document.Undo();
        f.AssertOriginalRows();
        f.Document.Redo();
        f.AssertTargetRows([800, 1000, 750], resultIds);
    }

    [Fact]
    public void MixedPasteAnchorsEarliestTickInsteadOfTheLaterPrimaryAndRetainsGlobalTrackOffsets()
    {
        using Fixture f = new();
        using ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyArrangementSegments(f.Document,
            [f.SourceIds[2], f.SourceIds[0], f.SourceIds[1]], f.SourceIds[1]);
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzePaste(f.Document,
            payload, f.TrackIds[4], 700);
        using var command = new CommandLifetime(plan.CreateCommand());
        payload.Dispose(); // The command, not the caller's payload, owns the pages now.
        using StagedProjectEdit staged = f.Document.PrepareEdit(command.Command);
        MidoraId[] resultIds = staged.PreparedSelection!.ResultSelectionIds.ToArray();
        f.Document.ExecutePrepared(staged);

        f.AssertTargetRows([750, 950, 700], resultIds);
        for (int index = 0; index < 3; index++) Assert.Single(f.SegmentIds(index));
        f.Document.Undo();
        f.AssertOriginalRows();
        f.Document.Redo();
        f.AssertTargetRows([750, 950, 700], resultIds);
    }

    [Fact]
    public void ManySmallSegmentsKeepEverySourceIndexAndRestoreAllIdsAcrossAtomicUndoRedo()
    {
        using Fixture f = new(backgroundCompilation: true);
        const int extraPerTrack = 256;
        // Descending storage order deliberately differs from chronological
        // order. Undo must restore the exact original collection, not rebuild it.
        for (int trackIndex = 0; trackIndex < 3; trackIndex++)
        {
            for (int index = extraPerTrack - 1; index >= 0; index--)
            {
                long tick = 1000 + index * 32L;
                if (trackIndex % 2 == 0)
                {
                    Segment segment = new(f.Project) { ProjectStartTick = tick, LengthTicks = 16 };
                    segment.Notes.Add(new(f.Project) { StartTick = 2, LengthTicks = 3, Note = index % 128, Velocity = 80 });
                    f.Project.Tracks.Single(track => track.Id == f.TrackIds[trackIndex]).Segments.Add(segment);
                }
                else
                {
                    MidiSegment segment = new(f.Project) { ProjectStartTick = tick, LengthTicks = 16 };
                    segment.Notes.Add(new(f.Project) { StartTick = 2, LengthTicks = 3, Key = index % 128,
                        NoteOnVelocity = 80, NoteOnOrder = 0, NoteOffOrder = 1 });
                    f.Project.PureMidiTracks.Single(track => track.Id == f.TrackIds[trackIndex]).Segments.Add(segment);
                }
            }
        }
        MidoraId[][] sourceIds = Enumerable.Range(0, 3).Select(f.SegmentIds).ToArray();
        MidoraId[] selected = sourceIds.SelectMany(static ids => ids).Reverse().ToArray();
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            selected, f.SourceIds[1], f.TrackIds[4], 100000, false);
        using var command = new CommandLifetime(plan.CreateCommand());
        using StagedProjectEdit staged = f.Document.PrepareEdit(command.Command);
        Assert.Equal(selected.Length, staged.PreparedSelection!.ResultSelectionIds.Count);
        Assert.Equal(selected.Length, staged.PreparedSelection.ResultSelectionIds.Distinct().Count());
        for (int index = 0; index < 3; index++) Assert.Equal(sourceIds[index], f.SegmentIds(index));
        f.Document.ExecutePrepared(staged);
        for (int index = 0; index < 3; index++) Assert.Empty(f.SegmentIds(index));
        MidoraId[][] targetIds = Enumerable.Range(3, 3).Select(f.SegmentIds).ToArray();
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(extraPerTrack + 1, targetIds[index].Length);
            if (index % 2 == 0)
            {
                var segments = f.Project.PureMidiTracks.Single(track => track.Id == f.TrackIds[index + 3]).Segments;
                Assert.All(segments, static segment => Assert.Single(segment.Notes));
                Assert.Equal(index == 0 ? 99800L : 99750L, segments[0].ProjectStartTick);
                Assert.Equal(100700L, segments[1].ProjectStartTick);
                Assert.Equal(100700L + (extraPerTrack - 1) * 32L, segments[^1].ProjectStartTick);
            }
            else
            {
                var segments = f.Project.Tracks.Single(track => track.Id == f.TrackIds[index + 3]).Segments;
                Assert.All(segments, static segment => Assert.Single(segment.Notes));
                Assert.Equal(100000L, segments[0].ProjectStartTick);
                Assert.Equal(100700L, segments[1].ProjectStartTick);
                Assert.Equal(100700L + (extraPerTrack - 1) * 32L, segments[^1].ProjectStartTick);
            }
        }
        f.Document.Undo();
        for (int index = 0; index < 3; index++) Assert.Equal(sourceIds[index], f.SegmentIds(index));
        for (int index = 3; index < 6; index++) Assert.Empty(f.SegmentIds(index));
        f.Document.Redo();
        for (int index = 0; index < 3; index++) Assert.Equal(targetIds[index], f.SegmentIds(index + 3));
    }

    [Theory]
    [InlineData(0)] // Abandon the data-loss confirmation.
    [InlineData(1)] // Cancel before reading a transfer proposal.
    [InlineData(2)] // Cancel before command preparation.
    [InlineData(3)] // Cancel after the detached target is fully built and flushed.
    [InlineData(4)] // Abandon an already-prepared command before publication.
    public void AbandonedConversionPreservesTheExistingUndoAndRedoBranch(int cancellationStage)
    {
        using Fixture f = new();
        MidoraId source = f.SourceIds[1];
        MidoraId sourceNote = f.Project.PureMidiTracks.Single(track => track.Id == f.TrackIds[1])
            .Segments.Single().Notes.Single().Id;
        f.Document.Execute(ProjectDomainEditCommands.SetDirectMidiNoteValues(source, [sourceNote], noteOffVelocity: 12));
        f.Document.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(source, 7, 2, 99, 100));
        f.Document.Undo();
        Assert.True(f.Document.CanRedo);
        long state = f.Document.CurrentStateId;
        long revision = f.Document.PublicationRevision;
        long nextId = f.Project.NextStableId;
        string? undoName = f.Document.UndoName;
        string? redoName = f.Document.RedoName;

        if (cancellationStage == 1)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => SegmentConversionService.AnalyzeTransfer(f.Document,
                [source], source, f.TrackIds[4], 1000, false, cancelled.Token));
        }
        else
        {
            using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
                [source], source, f.TrackIds[4], 1000, false);
            Assert.True(plan.RequiresConfirmation);
            if (cancellationStage != 0)
            {
                using var command = new CommandLifetime(plan.CreateCommand(confirmDataLoss: true));
                using var cancelled = new CancellationTokenSource();
                if (cancellationStage == 2)
                {
                    cancelled.Cancel();
                    Assert.Throws<OperationCanceledException>(() => f.Document.PrepareEdit(command.Command, cancelled.Token));
                }
                else if (cancellationStage == 3)
                {
                    Assert.Throws<OperationCanceledException>(() => f.Document.PrepareEdit(command.Command, cancelled.Token,
                        new InlineProgress(progress =>
                        {
                            if (progress.Phase == TimelineEditPreparationPhase.Ready) cancelled.Cancel();
                        })));
                    Assert.True(cancelled.IsCancellationRequested);
                }
                else
                {
                    using StagedProjectEdit abandoned = f.Document.PrepareEdit(command.Command);
                    Assert.Single(abandoned.PreparedSelection!.ResultSelectionIds);
                }
            }
        }

        Assert.Equal(state, f.Document.CurrentStateId);
        Assert.Equal(revision, f.Document.PublicationRevision);
        Assert.Equal(nextId, f.Project.NextStableId);
        Assert.True(f.Document.CanUndo);
        Assert.True(f.Document.CanRedo);
        Assert.Equal(undoName, f.Document.UndoName);
        Assert.Equal(redoName, f.Document.RedoName);
        f.AssertOriginalRows();
        f.Document.Redo();
        MidiSegment restored = f.Project.PureMidiTracks.Single(track => track.Id == f.TrackIds[1]).Segments.Single();
        Assert.Equal(2, restored.Notes.Count);
        Assert.Contains(restored.Notes, static note => note.Key == 99 && note.StartTick == 7);
        Assert.Equal(12, restored.Notes.Single(note => note.Id == sourceNote).NoteOffVelocity);
    }

    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> report)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => report(value);
    }

    private sealed class CommandLifetime(IProjectEditCommand command) : IDisposable
    {
        public IProjectEditCommand Command => command;
        public void Dispose() => (command as IDisposable)?.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ProjectCompilationSession _compilation;
        private static readonly long[] SourceStarts = [100, 300, 50];
        public Fixture(bool backgroundCompilation = false)
        {
            Project = new(480);
            _compilation = new(Project,
                executionMode: backgroundCompilation ? ProjectCompilationExecutionMode.Background : ProjectCompilationExecutionMode.Synchronous,
                backgroundDebounce: backgroundCompilation ? TimeSpan.FromHours(1) : null);
            Document = new(_compilation, ProjectDocumentOrigin.Persisted);
            Document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            for (int index = 0; index < 6; index++)
            {
                string name = $"Track {index}";
                if (index % 2 == 0)
                {
                    Document.Execute(ProjectDomainEditCommands.CreateLogicalTrack(name, Project.EventInstruments[0].Id));
                    TrackIds[index] = Project.Tracks.Single(track => track.Name == name).Id;
                    if (index >= 3) continue;
                    Document.Execute(ProjectDomainEditCommands.CreateSegment(TrackIds[index], SourceStarts[index], 20));
                    SourceIds[index] = Project.Tracks.Single(track => track.Id == TrackIds[index]).Segments.Single().Id;
                    Document.Execute(ProjectDomainEditCommands.CreateLogicalNote(SourceIds[index], 2, 7, 60 + index, 70 + index));
                }
                else
                {
                    Document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(name));
                    TrackIds[index] = Project.PureMidiTracks.Single(track => track.Name == name).Id;
                    if (index >= 3) continue;
                    Document.Execute(ProjectDomainEditCommands.CreateMidiSegment(TrackIds[index], SourceStarts[index], 20));
                    SourceIds[index] = Project.PureMidiTracks.Single(track => track.Id == TrackIds[index]).Segments.Single().Id;
                    Document.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(SourceIds[index], 2, 7, 60 + index, 70 + index));
                }
            }
            Assert.Equal(TrackIds, Project.ArrangementTracks.Select(static track => track.TrackId));
        }

        public MidoraProject Project { get; }
        public ProjectDocumentSession Document { get; }
        public MidoraId[] TrackIds { get; } = new MidoraId[6];
        public MidoraId[] SourceIds { get; } = new MidoraId[3];

        public MidoraId[] SegmentIds(int trackIndex) => trackIndex % 2 == 0
            ? Project.Tracks.Single(track => track.Id == TrackIds[trackIndex]).Segments.Select(static value => value.Id).ToArray()
            : Project.PureMidiTracks.Single(track => track.Id == TrackIds[trackIndex]).Segments.Select(static value => value.Id).ToArray();

        public void AssertOriginalRows()
        {
            for (int index = 0; index < 3; index++) Assert.Equal(SourceIds[index], Assert.Single(SegmentIds(index)));
            for (int index = 3; index < 6; index++) Assert.Empty(SegmentIds(index));
        }

        public void AssertTargetRows(long[] starts, MidoraId[] resultIds)
        {
            for (int index = 0; index < 3; index++)
            {
                MidoraId targetId = TrackIds[index + 3];
                if (index % 2 == 0)
                {
                    MidiSegment segment = Project.PureMidiTracks.Single(track => track.Id == targetId).Segments.Single();
                    Assert.Equal(resultIds[index], segment.Id);
                    Assert.Equal(starts[index], segment.ProjectStartTick);
                    DirectMidiNote note = Assert.Single(segment.Notes);
                    Assert.Equal((2L, 7L, 60 + index, 70 + index, 0),
                        (note.StartTick, note.LengthTicks, note.Key, note.NoteOnVelocity, note.NoteOffVelocity));
                }
                else
                {
                    Segment segment = Project.Tracks.Single(track => track.Id == targetId).Segments.Single();
                    Assert.Equal(resultIds[index], segment.Id);
                    Assert.Equal(starts[index], segment.ProjectStartTick);
                    LogicalNote note = Assert.Single(segment.Notes);
                    Assert.Equal((2L, 7L, 60 + index, 70 + index),
                        (note.StartTick, note.LengthTicks, note.Note, note.Velocity));
                }
            }
        }

        public void Dispose() { Document.Dispose(); _compilation.Dispose(); Project.Dispose(); }
    }
}
