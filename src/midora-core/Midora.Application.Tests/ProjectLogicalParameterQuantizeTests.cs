using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectLogicalParameterQuantizeTests
{
    [Fact]
    public void CrossLaneQuantizeIsOneAtomicEditWithLaneLocalLaterWins()
    {
        Fixture fixture = CreateFixture();
        CurvePoint laneAEarlierMover = Point(fixture.Project, 11, 0.1);
        CurvePoint laneALaterIncumbent = Point(fixture.Project, 10, 0.2);
        CurvePoint laneAUntouchedDuplicate1 = Point(fixture.Project, 30, 0.3);
        CurvePoint laneAUntouchedDuplicate2 = Point(fixture.Project, 30, 0.4);
        fixture.First.Points.AddRange([
            laneAEarlierMover,
            laneALaterIncumbent,
            laneAUntouchedDuplicate1,
            laneAUntouchedDuplicate2]);

        CurvePoint laneBEarlierIncumbent = Point(fixture.Project, 10, 0.5);
        CurvePoint laneBLaterMover = Point(fixture.Project, 11, 0.6);
        fixture.Second.Points.AddRange([laneBEarlierIncumbent, laneBLaterMover]);
        CurvePoint[] originalFirst = fixture.First.Points.ToArray();
        CurvePoint[] originalSecond = fixture.Second.Points.ToArray();

        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                fixture.Segment.Id,
                [laneAEarlierMover.Id, laneBLaterMover.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        IPreparedProjectEdit prepared = command.Prepare(fixture.Project);
        prepared.Apply(fixture.Project);

        Assert.Equal(
            [laneALaterIncumbent, laneAUntouchedDuplicate1, laneAUntouchedDuplicate2],
            fixture.First.Points);
        CurvePoint laneBResult = Assert.Single(fixture.Second.Points);
        Assert.Equal(laneBLaterMover.Id, laneBResult.Id);
        Assert.Equal(10, laneBResult.Tick);
        Assert.Equal([laneBLaterMover.Id], command.ResultSelectionIds);

        prepared.Undo(fixture.Project);

        Assert.Equal(originalFirst, fixture.First.Points);
        Assert.Equal(originalSecond, fixture.Second.Points);
        Assert.Equal(
            [laneAEarlierMover.Id, laneBLaterMover.Id],
            command.ResultSelectionIds);
    }

    [Fact]
    public void LegacySingleLaneOverloadStillRejectsPointsFromAnotherLane()
    {
        Fixture fixture = CreateFixture();
        CurvePoint first = Point(fixture.Project, 11, 0.1);
        CurvePoint second = Point(fixture.Project, 21, 0.2);
        fixture.First.Points.Add(first);
        fixture.Second.Points.Add(second);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                fixture.Segment.Id,
                fixture.First.Id,
                [first.Id, second.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));

        Assert.Throws<ArgumentException>(() => command.Prepare(fixture.Project));
        Assert.Same(first, Assert.Single(fixture.First.Points));
        Assert.Same(second, Assert.Single(fixture.Second.Points));
        Assert.Empty(command.ResultSelectionIds);
    }

    [Fact]
    public void CrossLaneQuantizeCancellationPublishesNoPartialResult()
    {
        Fixture fixture = CreateFixture();
        CurvePoint first = Point(fixture.Project, 11, 0.1);
        CurvePoint second = Point(fixture.Project, 21, 0.2);
        fixture.First.Points.Add(first);
        fixture.Second.Points.Add(second);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                fixture.Segment.Id,
                [first.Id, second.Id],
                TimelineQuantizeGrid.FromCustomTicks(10));
        ICancellableProjectEditCommand cancellable =
            Assert.IsAssignableFrom<ICancellableProjectEditCommand>(command);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            cancellable.Prepare(fixture.Project, cancellation.Token));

        Assert.Equal(11, first.Tick);
        Assert.Equal(21, second.Tick);
        Assert.Same(first, Assert.Single(fixture.First.Points));
        Assert.Same(second, Assert.Single(fixture.Second.Points));
        Assert.Empty(command.ResultSelectionIds);
    }

    [Fact]
    public void FixedFractionQuantizeKeepsProjectZeroGridAcrossTimeSignatureBoundary()
    {
        Fixture fixture = CreateFixture(projectStartTick: 900);
        fixture.Project.Conductor.TimeSignatures.Add(
            new TimeSignatureChange(fixture.Project, 960, 3, 4));
        CurvePoint point = Point(fixture.Project, 120, 0.5);
        fixture.First.Points.Add(point);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                fixture.Segment.Id,
                [point.Id],
                TimelineQuantizeGrid.MusicalFraction(1, 16));

        IPreparedProjectEdit prepared = command.Prepare(fixture.Project);
        prepared.Apply(fixture.Project);

        // Absolute tick 1020 is exactly halfway between the project-zero grid
        // ticks 960 and 1080. The new 3/4 signature does not re-anchor a fixed
        // 1/16 subdivision, and the formal tie rule chooses 960.
        Assert.Equal(60, Assert.Single(fixture.First.Points).Tick);
        prepared.Undo(fixture.Project);
        Assert.Equal(120, Assert.Single(fixture.First.Points).Tick);
    }

    [Fact]
    public void SharedTimelineGridUsesExactCeilingAndEarlierTie()
    {
        long step = ProjectTimelineGrid.ResolveWholeNoteFractionStep(191, 1, 16);

        Assert.Equal(48, step);
        Assert.Equal(0, ProjectTimelineGrid.Snap(24, step, tieDirection: 0));
        Assert.Equal(48, ProjectTimelineGrid.Snap(24, step, tieDirection: 1));
    }

    [Fact]
    public void SharedTimelineGridDoesNotEagerlyOverflowAnUnselectedUpperPoint()
    {
        long lower = long.MaxValue / 10 * 10;

        Assert.Equal(lower, ProjectTimelineGrid.Snap(lower + 1, 10, tieDirection: 0));
        Assert.Equal(lower, ProjectTimelineGrid.Snap(lower + 5, 10, tieDirection: 0));
        Assert.Equal(long.MaxValue, ProjectTimelineGrid.Snap(long.MaxValue, 1, tieDirection: 0));
        Assert.Throws<OverflowException>(() =>
            ProjectTimelineGrid.Snap(lower + 6, 10, tieDirection: 0));
    }

    private static Fixture CreateFixture(long projectStartTick = 0)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project)
        {
            ProjectStartTick = projectStartTick,
            LengthTicks = 10_000
        };
        track.Segments.Add(segment);

        LogicalParameterDefinition firstDefinition = Parameter(project, "First");
        LogicalParameterDefinition secondDefinition = Parameter(project, "Second");
        instrument.LogicalParameters.AddRange([firstDefinition, secondDefinition]);
        LogicalParameterLane first = new(project) { ParameterId = firstDefinition.Id };
        LogicalParameterLane second = new(project) { ParameterId = secondDefinition.Id };
        segment.ParameterLanes.AddRange([first, second]);
        return new(project, segment, first, second);
    }

    private static LogicalParameterDefinition Parameter(MidoraProject project, string name) =>
        new(project)
        {
            Name = name,
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };

    private static CurvePoint Point(MidoraProject project, long tick, double value) =>
        new(project, tick, value, CurveInterpolation.Step);

    private sealed class Fixture(
        MidoraProject project,
        Segment segment,
        LogicalParameterLane first,
        LogicalParameterLane second)
    {
        private readonly MidoraId _segmentId = segment.Id;
        private readonly MidoraId _firstId = first.Id;
        private readonly MidoraId _secondId = second.Id;

        public MidoraProject Project { get; } = project;
        public Segment Segment => Project.Tracks
            .SelectMany(static value => value.Segments)
            .Single(value => value.Id == _segmentId);
        public LogicalParameterLane First => Segment.ParameterLanes
            .Single(value => value.Id == _firstId);
        public LogicalParameterLane Second => Segment.ParameterLanes
            .Single(value => value.Id == _secondId);
    }
}
