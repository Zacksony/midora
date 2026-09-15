using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedLogicalNoteCommandTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LargeStructureCommandsReportScalarProgressAndCanCancelBeforePublication(int operation)
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100); fixture.Bind(track);
        source.ProjectStartTick = 100;
        long nextId = fixture.Project.NextStableId;
        using var cancellation = new CancellationTokenSource();
        List<TimelineEditPreparationProgress> reports = [];
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, new InlineProgress(value =>
        { reports.Add(value); if (value.Completed > 0 && value.Completed < value.Total) cancellation.Cancel(); }), fixture.Resources, fixture.Project);
        IProjectEditCommand command = operation switch
        {
            0 => ProjectDomainEditCommands.AdjustSegmentEdges([source.Id], -10, 0),
            1 => ProjectDomainEditCommands.DuplicateSegment(source.Id, track.Id, 500_000),
            2 => ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id),
            _ => ProjectDomainEditCommands.SplitSegment(source.Id, 200)
        };
        Assert.Throws<OperationCanceledException>(() => command.Prepare(fixture.Project));
        Assert.Contains(reports, value => value.Completed > 0 && value.Completed < value.Total && value.Total >= 4_100);
        Assert.Same(source, Assert.Single(track.Segments)); Assert.Single(fixture.Project.Tracks);
        Assert.Equal(nextId, fixture.Project.NextStableId);
    }

    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> report) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => report(value); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeSegmentSplitAndJoinMatchTheExistingDomainAlgorithmsExactly(bool exactPointAtSplit)
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100);
        source.ProjectStartTick = 100; source.ContentOffsetTick = 20; source.LengthTicks = 100;
        source.Notes[0].LengthTicks = 100;
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = new(900_000) };
        lane.Points.Add(new CurvePoint(fixture.Project, 70, .7));
        lane.Points.Add(new CurvePoint(fixture.Project, 10, .1));
        lane.Points.Add(new CurvePoint(fixture.Project, 10, .2));
        if (exactPointAtSplit) lane.Points.Add(new CurvePoint(fixture.Project, 50, .5));
        source.ParameterLanes.Add(lane);
        using var expectedProject = ProjectCompilationSnapshot.Create(fixture.Project);
        var expected = SegmentEditing.Split(expectedProject, expectedProject.Tracks[0].Segments[0], 130);
        long oldNext = fixture.Project.NextStableId;
        var prepared = ProjectDomainEditCommands.SplitSegment(source.Id, 130).Prepare(fixture.Project);
        Assert.Same(source, Assert.Single(track.Segments)); Assert.Equal(oldNext, fixture.Project.NextStableId);
        prepared.Apply(fixture.Project);
        AssertSegmentEqual(expected.Left, track.Segments[0]); AssertSegmentEqual(expected.Right, track.Segments[1]);
        Assert.Equal(expectedProject.NextStableId, fixture.Project.NextStableId);
        var expectedJoin = SegmentEditing.Join(expectedProject, expected.Left, expected.Right);
        var join = ProjectDomainEditCommands.JoinSegments(track.Segments[0].Id, track.Segments[1].Id).Prepare(fixture.Project);
        join.Apply(fixture.Project); AssertSegmentEqual(expectedJoin, Assert.Single(track.Segments));
        join.Undo(fixture.Project); Assert.Equal(2, track.Segments.Count);
        prepared.Undo(fixture.Project); Assert.Same(source, Assert.Single(track.Segments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeTrackDuplicationPreservesIndependentOrSharedUsageAndPreparedAllocator(bool shared)
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100);
        EventInstrument instrument = new(fixture.Project) { Name = "Instrument" };
        fixture.Project.EventInstruments.Add(instrument);
        EventInstrumentUsage usage = new(fixture.Project) { EventInstrumentId = instrument.Id };
        fixture.Project.EventInstrumentUsages.Add(usage); track.EventInstrumentUsageId = usage.Id;
        long oldNext = fixture.Project.NextStableId;
        using var expectedProject = ProjectCompilationSnapshot.Create(fixture.Project);
        LogicalTrack expectedTrack = new(expectedProject) { Name = "Logical Copy" };
        var expectedSegment = SegmentEditing.Duplicate(expectedProject, expectedProject.Tracks[0].Segments[0]);
        var command = shared ? ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(track.Id)
            : ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id);
        var edit = command.Prepare(fixture.Project);
        Assert.Single(fixture.Project.Tracks); Assert.Equal(oldNext, fixture.Project.NextStableId);
        edit.Apply(fixture.Project);
        var copy = fixture.Project.Tracks[1]; Assert.Equal(expectedTrack.Id, copy.Id);
        AssertSegmentEqual(expectedSegment, Assert.Single(copy.Segments));
        Assert.Equal(shared, copy.EventInstrumentUsageId == usage.Id);
        Assert.Equal(shared ? 1 : 2, fixture.Project.EventInstrumentUsages.Count);
        Assert.Equal(copy.Id, fixture.Project.ArrangementTracks[1].TrackId);
        edit.Undo(fixture.Project); Assert.Same(track, Assert.Single(fixture.Project.Tracks));
        Assert.Single(fixture.Project.EventInstrumentUsages); Assert.Single(fixture.Project.ArrangementTracks);
        edit.Apply(fixture.Project); Assert.Same(copy, fixture.Project.Tracks[1]);
    }

    private static void AssertSegmentEqual(Segment expected, Segment actual)
    {
        Assert.Equal(expected.Id, actual.Id); Assert.Equal(expected.ProjectStartTick, actual.ProjectStartTick);
        Assert.Equal(expected.LengthTicks, actual.LengthTicks); Assert.Equal(expected.ContentOffsetTick, actual.ContentOffsetTick);
        Assert.Equal(expected.Notes.CreateQuerySnapshot().EnumerateAll(), actual.Notes.CreateQuerySnapshot().EnumerateAll());
        Assert.Equal(expected.ParameterLanes.Count, actual.ParameterLanes.Count);
        for (int i = 0; i < expected.ParameterLanes.Count; i++)
        {
            Assert.Equal(expected.ParameterLanes[i].Id, actual.ParameterLanes[i].Id);
            Assert.Equal(expected.ParameterLanes[i].ParameterId, actual.ParameterLanes[i].ParameterId);
            Assert.Equal(expected.ParameterLanes[i].Points.CreateQuerySnapshot().EnumerateAll(), actual.ParameterLanes[i].Points.CreateQuerySnapshot().EnumerateAll());
        }
    }

    [Fact]
    public void SegmentLeftExpansionShiftsAllContentDuringPreparationAndUndoSwapsTheOriginalRoot()
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100);
        source.ProjectStartTick = 100; source.LengthTicks = 100; source.ContentOffsetTick = 3;
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = new(900_000) };
        lane.Points.Add(new CurvePoint(fixture.Project, 50_000, .5)); source.ParameterLanes.Add(lane);
        long next = fixture.Project.NextStableId;
        var prepared = ProjectDomainEditCommands.AdjustSegmentEdges([source.Id], -10, 0).Prepare(fixture.Project);
        Assert.Same(source, track.Segments[0]); Assert.Equal(0, source.Notes[0].StartTick);
        prepared.Apply(fixture.Project);
        Segment result = track.Segments[0];
        Assert.NotSame(source, result); Assert.Equal(90, result.ProjectStartTick);
        Assert.Equal(110, result.LengthTicks); Assert.Equal(0, result.ContentOffsetTick);
        Assert.Equal(7, result.Notes[0].StartTick); Assert.Equal(40_997, result.Notes[^1].StartTick);
        Assert.Equal(50_007, result.ParameterLanes[0].Points[0].Tick);
        Assert.Equal(next, fixture.Project.NextStableId);
        prepared.Undo(fixture.Project); Assert.Same(source, track.Segments[0]);
        prepared.Apply(fixture.Project); Assert.Same(result, track.Segments[0]);
        Assert.True(fixture.Resources.PeakResidentBytes <= fixture.Resources.Budget.MaximumResidentBytes);
    }

    [Fact]
    public void SegmentCopiesPrepareNewIdsAndPreserveFormalFirstWinsWithBoundedContent()
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100);
        source.LengthTicks = 100;
        source.Notes.Add(new LogicalNote(fixture.Project) { StartTick = 0, LengthTicks = 12, Note = 60, Velocity = 20 });
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = new(900_000) };
        lane.Points.Add(new CurvePoint(fixture.Project, 50_000, .5));
        lane.Points.Add(new CurvePoint(fixture.Project, 50_000, .8)); source.ParameterLanes.Add(lane);
        LogicalTrack target = new(fixture.Project) { Name = "Destination" }; fixture.Project.Tracks.Add(target);
        fixture.Project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, target.Id));
        fixture.Bind(track); target.EventInstrumentUsageId = track.EventInstrumentUsageId;
        long next = fixture.Project.NextStableId;
        var prepared = ProjectDomainEditCommands.DuplicateSegments([source.Id], source.Id, target.Id, 500).Prepare(fixture.Project);
        Assert.Empty(target.Segments); Assert.Equal(next, fixture.Project.NextStableId);
        prepared.Apply(fixture.Project);
        Segment copy = Assert.Single(target.Segments);
        Assert.Equal(next, copy.Id.Value); Assert.Equal(next + 4_103, fixture.Project.NextStableId);
        Assert.Equal(4_100, copy.Notes.Count); Assert.Equal(100, copy.Notes[0].Velocity);
        Assert.Equal(40_990, copy.Notes[^1].StartTick);
        Assert.Equal(.5, Assert.Single(copy.ParameterLanes[0].Points).Value);
        Assert.Equal(500, copy.ProjectStartTick); Assert.Same(source, track.Segments[0]);
        prepared.Undo(fixture.Project); Assert.Empty(target.Segments);
        prepared.Apply(fixture.Project); Assert.Same(copy, Assert.Single(target.Segments));
        Assert.True(fixture.Resources.PeakResidentBytes <= fixture.Resources.Budget.MaximumResidentBytes);
    }

    [Fact]
    public void LargeSegmentStructurePreparationCancelsWithoutContentOrAllocatorChanges()
    {
        using var fixture = new Fixture();
        var (track, source) = fixture.Logical(4_100); source.ProjectStartTick = 100;
        fixture.Bind(track);
        long next = fixture.Project.NextStableId;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, resources: fixture.Resources, project: fixture.Project);
        Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.AdjustSegmentEdges([source.Id], -10, 0).Prepare(fixture.Project));
        Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.DuplicateSegments([source.Id], source.Id, track.Id, 500_000).Prepare(fixture.Project));
        Assert.Same(source, Assert.Single(track.Segments)); Assert.Equal(next, fixture.Project.NextStableId);
        Assert.Equal(0, source.Notes[0].StartTick);
    }

    [Fact]
    public void ValueOnlyReceiptKeepsSparseOrdinalSpillAliveAndMatchesGenericReceipt()
    {
        using var fixture = new Fixture();
        var (track, original) = fixture.Logical(10_000);
        var ids = original.Notes.Where((_, index) => index % 2 == 0).Select(v => v.Id).ToArray();
        var prepared = ProjectDomainEditCommands.SetLogicalNoteVelocities(original.Id, ids, 80,
            ProjectBatchValueEditMode.ExactSet).Prepare(fixture.Project);
        prepared.Apply(fixture.Project);
        Segment current = track.Segments[0];
        ProjectChangeSet generic = new();
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(generic, original, current, ids);
        var actual = Assert.Single(Assert.Single(prepared.Changes.TimelineOwnerChanges).Sources);
        var expected = Assert.Single(Assert.Single(generic.TimelineOwnerChanges).Sources);
        Assert.Equal(5_000, actual.OrdinalRanges.Count);
        Assert.Equal(expected.OrdinalRanges, actual.OrdinalRanges);
        Assert.Equal(expected.CurrentOrdinalRanges, actual.CurrentOrdinalRanges);
        Assert.Equal(expected.ContentRanges, actual.ContentRanges);
        Assert.Same(actual.PreviousContentRanges, actual.CurrentContentRanges);
        Assert.Same(actual.ContentRanges, actual.CurrentContentRanges);
        prepared.Undo(fixture.Project);
        Assert.Same(original, track.Segments[0]);
    }

    [Fact]
    public void SingleNoteInLargeOwnerUsesDetachedRootAndNoChangeHasRevisionGate()
    {
        using var fixture = new Fixture();
        var (track, original) = fixture.Logical(4_100);
        MidoraId id = original.Notes[0].Id;
        var prepared = ProjectDomainEditCommands.MoveLogicalNotes(original.Id, [id], 1, 0).Prepare(fixture.Project);
        prepared.Apply(fixture.Project);
        Assert.NotSame(original, track.Segments[0]);
        Assert.Equal(0, original.Notes[0].StartTick);
        Assert.Equal(1, track.Segments[0].Notes[0].StartTick);
        prepared.Undo(fixture.Project);
        var noChange = ProjectDomainEditCommands.MoveLogicalNotes(original.Id, [id], 0, 0).Prepare(fixture.Project);
        original.Notes[0].Velocity = 5;
        Assert.Throws<InvalidOperationException>(() => noChange.Apply(fixture.Project));
    }

    [Fact]
    public void LargeSegmentFlipMovesWindowsAndPointsAndUndoRestoresFormalTrackOrder()
    {
        using var fixture = new Fixture();
        var (track, first) = fixture.Logical(4_100);
        first.LengthTicks = 50_000;
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = new MidoraId(900_000) };
        lane.Points.Add(new CurvePoint(fixture.Project, 10, 0.5));
        first.ParameterLanes.Add(lane);
        Segment second = new(fixture.Project) { ProjectStartTick = 60_000, LengthTicks = 1_000 };
        track.Segments.Add(second);
        var original = first.Notes.CreateQuerySnapshot().EnumerateAll().ToArray();
        var prepared = ProjectDomainEditCommands.FlipSegmentsHorizontal([first.Id, second.Id],
            SegmentSelectionTransformScope.ExposedContentAndSegments).Prepare(fixture.Project);
        Assert.Same(first, track.Segments[0]);
        prepared.Apply(fixture.Project);
        Assert.Equal([second.Id, first.Id], track.Segments.Select(v => v.Id));
        Segment result = track.Segments[1];
        Assert.Equal(11_000, result.ProjectStartTick);
        Assert.Equal(49_997, result.Notes[0].StartTick);
        Assert.Equal(49_989, Assert.Single(result.ParameterLanes[0].Points).Tick);
        prepared.Undo(fixture.Project);
        Assert.Equal([first, second], track.Segments);
        Assert.Equal(original, first.Notes.CreateQuerySnapshot().EnumerateAll());
        prepared.Apply(fixture.Project);
        Assert.Equal([second.Id, first.Id], track.Segments.Select(v => v.Id));
    }

    [Fact]
    public void LargeSplitAndJoinPreserveSelectionFormalOrderAndUndoRoot()
    {
        using var fixture = new Fixture();
        var (track, segment) = fixture.Logical(4_100);
        var ids = segment.Notes.Select(v => v.Id).ToArray();
        var split = ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, ids,
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 1 });
        var edit = split.Prepare(fixture.Project);
        edit.Apply(fixture.Project);
        Assert.Equal(12_300, track.Segments[0].Notes.Count);
        Assert.Equal(12_300, split.ResultSelectionIds.Count);
        Assert.Equal(ids[0], track.Segments[0].Notes[0].Id);
        Assert.Equal(ids[1], track.Segments[0].Notes[3].Id);
        var join = ProjectDomainEditCommands.JoinLogicalNotes(segment.Id, split.ResultSelectionIds.ToArray(), new());
        var joinEdit = join.Prepare(fixture.Project);
        joinEdit.Apply(fixture.Project);
        Assert.Equal(4_100, track.Segments[0].Notes.Count);
        Assert.All(track.Segments[0].Notes, n => Assert.Equal(3, n.LengthTicks));
        joinEdit.Undo(fixture.Project);
        Assert.Equal(12_300, track.Segments[0].Notes.Count);
        edit.Undo(fixture.Project);
        Assert.Same(segment, track.Segments[0]);
    }

    [Fact]
    public void LargeMoveIsDetachedHasGlobalCollisionAndRestoresTheOriginalRoot()
    {
        using var fixture = new Fixture();
        var (track, segment) = fixture.Logical(5_000);
        var original = segment.Notes.CreateQuerySnapshot().EnumerateAll().ToArray();
        MidoraId[] ids = original.Take(4_999).Select(v => v.Id).ToArray();
        var command = ProjectDomainEditCommands.MoveLogicalNotes(segment.Id, ids, 10, 0);
        var prepared = command.Prepare(fixture.Project);
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(original, segment.Notes.CreateQuerySnapshot().EnumerateAll());
        prepared.Apply(fixture.Project);
        Assert.Equal(4_999, track.Segments[0].Notes.Count);
        Assert.False(track.Segments[0].Notes.TryGetById(ids[^1], out _));
        Assert.True(track.Segments[0].Notes.TryGetById(ids[0], out var moved));
        Assert.Equal(10, moved!.StartTick);
        prepared.Undo(fixture.Project);
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(original, segment.Notes.CreateQuerySnapshot().EnumerateAll());
        prepared.Apply(fixture.Project);
        Assert.Equal(4_999, track.Segments[0].Notes.Count);
        Assert.True(fixture.Resources.PeakResidentBytes <= fixture.Resources.Budget.MaximumResidentBytes);
        Assert.True(fixture.Resources.PeakWorkingBytes <= fixture.Resources.Budget.MaximumWorkingBytes);
    }

    [Fact]
    public void LargeDuplicateKeepsFormalOrderAndIndexedIdsWhileUndoDropsTheNewRoot()
    {
        using var fixture = new Fixture();
        var (track, segment) = fixture.Logical(4_100);
        MidoraId[] original = segment.Notes.Select(n => n.Id).ToArray();
        long next = fixture.Project.NextStableId;
        var prepared = ProjectDomainEditCommands.DuplicateLogicalNotes(segment.Id, original, segment.Id, 100_000).Prepare(fixture.Project);
        Assert.Equal(next, fixture.Project.NextStableId);
        prepared.Apply(fixture.Project);
        var final = track.Segments[0].Notes.CreateQuerySnapshot();
        Assert.Equal(8_200, final.Count);
        for (int i = 0; i < 4_100; i += 17)
        {
            var id = new MidoraId(next + i);
            Assert.True(final.TryFindOrdinalById(id, out int ordinal));
            Assert.Equal(4_100 + i, ordinal);
            Assert.Equal(100_000 + i * 10, final.GetByOrdinal(ordinal).StartTick);
        }
        prepared.Undo(fixture.Project);
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(4_100, segment.Notes.Count);
    }

    [Fact]
    public void LargeTemplateResizeSharesOtherEventsAndUpdatesTemplateBoundaryAtomically()
    {
        using var fixture = new Fixture();
        var project = fixture.Project;
        EventInstrument instrument = new(project) { Name = "Piano", TemplateLengthTicks = 50_000 };
        SubVoice voice = new(project) { Name = "Voice" };
        project.EventInstruments.Add(instrument);
        instrument.SubVoices.Add(voice);
        voice.Events.AddRange(Enumerable.Range(0, 4_100).Select(i => new TemplateEvent(project)
        { Kind = TemplateEventKind.Note, Tick = i * 10, LengthTicks = 3, Number = 60, Value = 100 }));
        var ids = voice.Events.Select(v => v.Id).ToArray();
        var before = voice.Events.CreateQuerySnapshot().EnumerateAll().ToArray();
        var prepared = ProjectDomainEditCommands.AdjustTemplateNoteEdges(instrument.Id, voice.Id, ids, 0, 20_000).Prepare(project);
        Assert.Equal(50_000, instrument.TemplateLengthTicks);
        prepared.Apply(project);
        Assert.Equal(60_993, instrument.TemplateLengthTicks);
        Assert.Equal(20_003, instrument.SubVoices[0].Events[0].LengthTicks);
        Assert.Equal(before, voice.Events.CreateQuerySnapshot().EnumerateAll());
        prepared.Undo(project);
        Assert.Same(voice, instrument.SubVoices[0]);
        Assert.Equal(50_000, instrument.TemplateLengthTicks);
    }

    [Fact]
    public void LargePreparationCancellationNeverChangesMusicOrStableIdAllocator()
    {
        using var fixture = new Fixture();
        var (track, segment) = fixture.Logical(4_100);
        var ids = segment.Notes.Select(v => v.Id).ToArray();
        long next = fixture.Project.NextStableId;
        using var token = new CancellationTokenSource();
        token.Cancel();
        using var scope = BulkEditPreparationContext.Enter(token.Token, resources: fixture.Resources, project: fixture.Project);
        Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(segment.Id, ids).Prepare(fixture.Project));
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(next, fixture.Project.NextStableId);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "midora-bounded-logical-" + Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480);
        public BoundedEditResources Resources { get; }
        private readonly BulkEditPreparationContext _scope;
        public Fixture()
        {
            Directory.CreateDirectory(_path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024,
                maximumWorkingBytes: 8 * 1024 * 1024), _path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
        }
        public (LogicalTrack, Segment) Logical(int count)
        {
            LogicalTrack track = new(Project) { Name = "Logical" };
            Segment segment = new(Project) { LengthTicks = 200_000 };
            Project.Tracks.Add(track); track.Segments.Add(segment);
            Project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
            segment.Notes.AddRange(Enumerable.Range(0, count).Select(i => new LogicalNote(Project)
            { StartTick = i * 10, LengthTicks = 3, Note = 60, Velocity = 100 }));
            return (track, segment);
        }
        public void Bind(LogicalTrack track)
        {
            EventInstrument instrument = new(Project) { Name = "Instrument" }; Project.EventInstruments.Add(instrument);
            EventInstrumentUsage usage = new(Project) { EventInstrumentId = instrument.Id }; Project.EventInstrumentUsages.Add(usage);
            track.EventInstrumentUsageId = usage.Id;
        }
        public void Dispose()
        {
            _scope.Dispose(); Project.Dispose(); Directory.Delete(_path, true);
        }
    }
}
