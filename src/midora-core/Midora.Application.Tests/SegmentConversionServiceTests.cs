using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class SegmentConversionServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossTypeTransferPreservesHiddenNotesCropAndPreparedSelection(bool sourceDirect, bool copy)
    {
        using Fixture f = new();
        MidoraId source = sourceDirect ? f.Direct.Id : f.Logical.Id;
        MidoraId target = sourceDirect ? f.LogicalTrackId : f.DirectTrackId;
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [source], source, target, 1000, copy);
        Assert.False(plan.RequiresConfirmation);
        using var command = new CommandLifetime(plan.CreateCommand());
        Assert.Equal(ProjectObjectClipboardKind.ArrangementSegments,
            Assert.IsAssignableFrom<IProjectClipboardPasteCommand>(command.Command).PasteTarget.Kind);
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        PreparedTimelineSelection selection = Assert.IsType<PreparedTimelineSelection>(edit.PreparedSelection);
        Assert.Equal(copy ? [] : [source], selection.OriginalSelectionIds);
        MidoraId resultId = Assert.Single(selection.ResultSelectionIds);
        f.Document.ExecutePrepared(edit);
        if (sourceDirect)
        {
            Segment result = f.Project.Tracks[0].Segments.Single(value => value.Id == resultId);
            Assert.Equal((1000L, 100L, 40L), (result.ProjectStartTick, result.LengthTicks, result.ContentOffsetTick));
            Assert.Equal(new long[] { 0, 60, 300 }, result.Notes.Select(static note => note.StartTick).Order());
            Assert.Equal(new[] { 30, 60, 90 }, result.Notes.Select(static note => note.Velocity).Order());
            Assert.Empty(result.ParameterLanes);
            Assert.Equal(copy ? 1 : 0, f.Project.PureMidiTracks[0].Segments.Count);
        }
        else
        {
            MidiSegment result = f.Project.PureMidiTracks[0].Segments.Single(value => value.Id == resultId);
            Assert.Equal((1000L, 100L, 40L), (result.ProjectStartTick, result.LengthTicks, result.ContentOffsetTick));
            Assert.Equal(new long[] { 0, 60, 300 }, result.Notes.Select(static note => note.StartTick).Order());
            Assert.All(result.Notes, static note => Assert.Equal(0, note.NoteOffVelocity));
            Assert.Empty(result.ChannelEvents);
            Assert.Empty(result.OpaqueEvents);
            Assert.Equal(copy ? 1 : 0, f.Project.Tracks[0].Segments.Count);
        }
        f.Document.Undo();
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        f.Document.Redo();
        Assert.Contains(resultId, f.Project.Tracks.SelectMany(static track => track.Segments).Select(static segment => segment.Id)
            .Concat(f.Project.PureMidiTracks.SelectMany(static track => track.Segments).Select(static segment => segment.Id)));
    }

    [Fact]
    public void DirectToLogicalWarnsAllLossesAndUsesFormalOnOrderForDuplicateWinner()
    {
        using Fixture f = new();
        f.Direct.ChannelEvents.Add(new(f.Project) { Tick = 500, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 40, Order = 100 });
        f.Direct.OpaqueEvents.Add(new(f.Project) { Tick = 600, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [65], Order = 101 });
        DirectMidiNote original = f.Direct.Notes.First();
        original.NoteOffVelocity = 24;
        original.NoteOnOrder = 20;
        f.Direct.Notes.Add(new(f.Project) { StartTick = original.StartTick, LengthTicks = 25, Key = original.Key,
            NoteOnVelocity = 111, NoteOffVelocity = 80, NoteOnOrder = 1, NoteOffOrder = 2 });
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, copy: false);
        Assert.Equal(new SegmentConversionLossSummary(0, 1, 1, 2, 1), plan.Losses);
        Assert.Throws<InvalidOperationException>(() => plan.CreateCommand());
        Assert.Equal(4, f.Direct.Notes.Count);
        using var command = new CommandLifetime(plan.CreateCommand(confirmDataLoss: true));
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        f.Document.ExecutePrepared(edit);
        Segment result = f.Project.Tracks[0].Segments.Single(static segment => segment.ProjectStartTick == 1000);
        Assert.Equal(3, result.Notes.Count);
        Assert.Equal(111, result.Notes.Single(note => note.StartTick == original.StartTick).Velocity);
        f.Document.Undo();
        Assert.Equal(4, f.Project.PureMidiTracks[0].Segments[0].Notes.Count);
        Assert.Single(f.Project.PureMidiTracks[0].Segments[0].ChannelEvents);
        Assert.Single(f.Project.PureMidiTracks[0].Segments[0].OpaqueEvents);
    }

    [Fact]
    public void LogicalToDirectCountsAllParameterPointsIncludingHiddenContent()
    {
        using Fixture f = new();
        LogicalParameterLane lane = new(f.Project) { ParameterId = f.Project.AllocateStableId() };
        lane.Points.Add(new(f.Project, 0, 40, CurveInterpolation.Step));
        lane.Points.Add(new(f.Project, 800, 80, CurveInterpolation.Step));
        f.Logical.ParameterLanes.Add(lane);
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Logical.Id], f.Logical.Id, f.DirectTrackId, 1000, copy: true);
        Assert.Equal(new SegmentConversionLossSummary(LogicalParameterPoints: 2, LogicalParameterLanes: 1), plan.Losses);
        using var command = new CommandLifetime(plan.CreateCommand(true));
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        f.Document.ExecutePrepared(edit);
        Assert.Empty(f.Project.PureMidiTracks[0].Segments.Single(static segment => segment.ProjectStartTick == 1000).ChannelEvents);
        Assert.Equal(2, f.Project.Tracks[0].Segments[0].ParameterLanes[0].Points.Count);
    }

    [Fact]
    public void SameTypeMovePreservesIdentityAndNonNotePayloads()
    {
        using Fixture f = new();
        f.Direct.ChannelEvents.Add(new(f.Project) { Tick = 12, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 40, Order = 100 });
        MidoraId note = f.Direct.Notes.First().Id;
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.DirectTrackId, 1000, copy: false);
        Assert.False(plan.RequiresConfirmation);
        using var command = new CommandLifetime(plan.CreateCommand());
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        Assert.Equal(f.Direct.Id, Assert.Single(edit.PreparedSelection!.ResultSelectionIds));
        f.Document.ExecutePrepared(edit);
        MidiSegment result = Assert.Single(f.Project.PureMidiTracks[0].Segments);
        Assert.Equal(f.Direct.Id, result.Id);
        Assert.Contains(result.Notes, value => value.Id == note);
        Assert.Single(result.ChannelEvents);
        f.Document.Undo();
        Assert.Equal(0, Assert.Single(f.Project.PureMidiTracks[0].Segments).ProjectStartTick);
    }

    [Fact]
    public void EmptyLogicalLaneStillRequiresDataLossConfirmation()
    {
        using Fixture f = new();
        f.Logical.ParameterLanes.Add(new(f.Project) { ParameterId = f.Project.AllocateStableId() });
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Logical.Id], f.Logical.Id, f.DirectTrackId, 1000, false);
        Assert.Equal(new SegmentConversionLossSummary(LogicalParameterLanes: 1), plan.Losses);
        Assert.True(plan.RequiresConfirmation);
        Assert.Throws<InvalidOperationException>(() => plan.CreateCommand());
        Assert.Single(f.Logical.ParameterLanes);
    }

    [Fact]
    public void MixedClipboardConvertsBothDirectionsWithOneUndoAndRelativePlacement()
    {
        using Fixture f = new();
        f.Document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Target logical", f.Project.EventInstruments[0].Id));
        MidoraId targetLogical = f.Project.Tracks.Single(static track => track.Name == "Target logical").Id;
        // Global order is Logical source -> MIDI source -> Logical destination;
        // paste the two source rows one track down, converting both directions.
        using ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyArrangementSegments(f.Document,
            [f.Logical.Id, f.Direct.Id], f.Logical.Id);
        Assert.Equal(ProjectObjectClipboardKind.ArrangementSegments, payload.Kind);
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzePaste(f.Document, payload, f.DirectTrackId, 1000);
        using var command = new CommandLifetime(plan.CreateCommand());
        payload.Dispose(); // Command owns immutable capture pages independently.
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        Assert.Equal(2, edit.PreparedSelection!.ResultSelectionIds.Count);
        f.Document.ExecutePrepared(edit);
        Assert.Equal(2, f.Project.PureMidiTracks[0].Segments.Count);
        Assert.Single(f.Project.Tracks.Single(track => track.Id == targetLogical).Segments);
        f.Document.Undo();
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        Assert.Empty(f.Project.Tracks.Single(track => track.Id == targetLogical).Segments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingTypedClipboardIsAccepted(bool direct)
    {
        using Fixture f = new();
        using ProjectObjectClipboardPayload payload = direct
            ? ProjectObjectClipboard.CopyMidiSegments(f.Document, [f.Direct.Id], f.Direct.Id)
            : ProjectObjectClipboard.CopySegments(f.Document, [f.Logical.Id], f.Logical.Id);
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzePaste(f.Document, payload,
            direct ? f.LogicalTrackId : f.DirectTrackId, 1000);
        using var command = new CommandLifetime(plan.CreateCommand());
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        Assert.Single(edit.PreparedSelection!.ResultSelectionIds);
        f.Document.ExecutePrepared(edit);
        Assert.Equal(2, direct ? f.Project.Tracks[0].Segments.Count : f.Project.PureMidiTracks[0].Segments.Count);
    }

    [Fact]
    public void OverlapAndUnboundTargetsRejectWithoutRemovingAnySource()
    {
        using Fixture f = new();
        long nextId = f.Project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 0, false));
        Assert.Equal(nextId, f.Project.NextStableId);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        f.Document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Unbound", null));
        MidoraId target = f.Project.Tracks.Single(static track => track.Name == "Unbound").Id;
        Assert.Throws<InvalidOperationException>(() => SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, target, 1000, false));
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
    }

    [Fact]
    public void CancelAndRevisionRaceNeverPublish()
    {
        using Fixture f = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false, cts.Token));
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false);
        using var command = new CommandLifetime(plan.CreateCommand());
        Assert.Throws<OperationCanceledException>(() => f.Document.PrepareEdit(command.Command, cts.Token));
        f.Document.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(f.Direct.Id, 500, 10, 99, 80));
        Assert.Throws<InvalidOperationException>(() => f.Document.PrepareEdit(command.Command));
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
    }

    [Fact]
    public void LateTargetConstructionFailureIsAtomic()
    {
        using Fixture f = new();
        // Malformed fixture is deliberately rejected during conversion, after review.
        f.Logical.Notes.First().Velocity = 0;
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Logical.Id], f.Logical.Id, f.DirectTrackId, 1000, false);
        using var command = new CommandLifetime(plan.CreateCommand());
        Assert.ThrowsAny<Exception>(() => f.Document.PrepareEdit(command.Command));
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
    }

    [Fact]
    public void LargeDuplicateReductionSpillsWithinTheSharedBudget()
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 4 * 1024 * 1024, maximumResidentBytes: 32 * 1024,
            pageRecordCount: 256));
        using (Fixture f = new())
        using (var context = BulkEditPreparationContext.Enter(resources: resources, project: f.Project))
        {
            const int count = 10_000;
            for (int index = 0; index < count; index++)
                f.Direct.Notes.Add(new(f.Project)
                {
                    StartTick = 1000 + index / 2, LengthTicks = 1, Key = 70, NoteOnVelocity = 50,
                    NoteOnOrder = count - index, NoteOffOrder = count * 2L + index
                });
            using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
                [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false);
            Assert.Equal(count / 2, plan.Losses.DuplicateNotes);
            using var command = new CommandLifetime(plan.CreateCommand(true));
            using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
            f.Document.ExecutePrepared(edit);
            Assert.Equal(count / 2 + 3,
                f.Project.Tracks[0].Segments.Single(static segment => segment.ProjectStartTick == 1000).Notes.Count);
            f.Document.Undo();
            Assert.Equal(count + 3, f.Project.PureMidiTracks[0].Segments[0].Notes.Count);
            f.Document.Redo();
            Assert.InRange(resources.PeakWorkingBytes, 1, resources.Budget.MaximumWorkingBytes);
            Assert.InRange(resources.PeakResidentBytes, 1, resources.Budget.MaximumResidentBytes);
            Assert.True(resources.PeakSpillBytes > 0);
        }
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    [Fact]
    public void CancellingTheLossConfirmationReleasesTheCaptureWithoutChangingIds()
    {
        using Fixture f = new();
        f.Direct.Notes.First().NoteOffVelocity = 10;
        long nextId = f.Project.NextStableId;
        using (SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false))
            Assert.True(plan.RequiresConfirmation);
        Assert.Equal(nextId, f.Project.NextStableId);
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
    }

    [Fact]
    public void EmptySegmentCanBeConvertedWithoutInventingNotesOrEvents()
    {
        using Fixture f = new();
        f.Direct.Notes.Clear();
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false);
        Assert.False(plan.RequiresConfirmation);
        using var command = new CommandLifetime(plan.CreateCommand());
        using StagedProjectEdit edit = f.Document.PrepareEdit(command.Command);
        f.Document.ExecutePrepared(edit);
        Assert.Empty(f.Project.Tracks[0].Segments.Single(static segment => segment.ProjectStartTick == 1000).Notes);
        Assert.Empty(f.Project.PureMidiTracks[0].Segments);
    }

    [Fact]
    public void StorageBudgetFailureDuringTargetPreparationReleasesProvisionalResources()
    {
        using Fixture f = new();
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false);
        using var command = new CommandLifetime(plan.CreateCommand());
        var resources = new BoundedEditResources(new PagedEditResourceBudget(maximumSpillBytes: 0));
        long nextId = f.Project.NextStableId;
        using (var context = BulkEditPreparationContext.Enter(resources: resources, project: f.Project))
            Assert.Throws<InvalidOperationException>(() => f.Document.PrepareEdit(command.Command));
        Assert.Equal(nextId, f.Project.NextStableId);
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    [Fact]
    public void CancellationAfterTargetIsReadyStillPublishesNothing()
    {
        using Fixture f = new();
        using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(f.Document,
            [f.Direct.Id], f.Direct.Id, f.LogicalTrackId, 1000, false);
        using var command = new CommandLifetime(plan.CreateCommand());
        using var cts = new CancellationTokenSource();
        long nextId = f.Project.NextStableId;
        var resources = new BoundedEditResources();
        using (var context = BulkEditPreparationContext.Enter(resources: resources, project: f.Project))
            Assert.Throws<OperationCanceledException>(() => f.Document.PrepareEdit(command.Command, cts.Token,
                new InlineProgress(value => { if (value.Phase == TimelineEditPreparationPhase.Ready) cts.Cancel(); })));
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(nextId, f.Project.NextStableId);
        Assert.Single(f.Project.Tracks[0].Segments);
        Assert.Single(f.Project.PureMidiTracks[0].Segments);
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> report)
        : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => report(value); }

    private sealed class CommandLifetime(IProjectEditCommand command) : IDisposable
    {
        public IProjectEditCommand Command => command;
        public void Dispose() => (command as IDisposable)?.Dispose();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly ProjectCompilationSession _compilation;
        public Fixture()
        {
            Project = new(480);
            _compilation = new(Project);
            Document = new(_compilation, ProjectDocumentOrigin.Persisted);
            Document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            Document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", Project.EventInstruments[0].Id));
            LogicalTrackId = Project.Tracks[0].Id;
            Document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            DirectTrackId = Project.PureMidiTracks[0].Id;
            Document.Execute(ProjectDomainEditCommands.CreateSegment(LogicalTrackId, 0, 100));
            Document.Execute(ProjectDomainEditCommands.CreateMidiSegment(DirectTrackId, 0, 100));
            Logical = Project.Tracks[0].Segments[0];
            Direct = Project.PureMidiTracks[0].Segments[0];
            Logical.ContentOffsetTick = Direct.ContentOffsetTick = 40;
            for (int index = 0; index < 3; index++)
            {
                long tick = index == 0 ? 0 : index == 1 ? 60 : 300;
                Logical.Notes.Add(new(Project) { StartTick = tick, LengthTicks = 20, Note = 60 + index, Velocity = 30 + index * 30 });
                Direct.Notes.Add(new(Project) { StartTick = tick, LengthTicks = 20, Key = 60 + index,
                    NoteOnVelocity = 30 + index * 30, NoteOnOrder = index * 2, NoteOffOrder = index * 2 + 1 });
            }
        }
        public MidoraProject Project { get; }
        public ProjectDocumentSession Document { get; }
        public MidoraId LogicalTrackId { get; }
        public MidoraId DirectTrackId { get; }
        public Segment Logical { get; }
        public MidiSegment Direct { get; }
        public void Dispose() { Document.Dispose(); _compilation.Dispose(); Project.Dispose(); }
    }
}
