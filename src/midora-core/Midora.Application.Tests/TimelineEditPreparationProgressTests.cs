using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class TimelineEditPreparationProgressTests
{
    [Fact]
    public void ProgressFractionsAreMonotonicAcrossFormalPhases()
    {
        TimelineEditPreparationProgress[] values =
        [
            new(TimelineEditPreparationPhase.ResolvingSelection, 0, 10),
            new(TimelineEditPreparationPhase.ResolvingSelection, 10, 10),
            new(TimelineEditPreparationPhase.Planning, 0, 20),
            new(TimelineEditPreparationPhase.Planning, 20, 20),
            new(TimelineEditPreparationPhase.ResolvingCollisions, 0, 5),
            new(TimelineEditPreparationPhase.ResolvingCollisions, 5, 5),
            new(TimelineEditPreparationPhase.BuildingResult, 0, 10),
            new(TimelineEditPreparationPhase.BuildingResult, 10, 10),
            new(TimelineEditPreparationPhase.Ready, 1, 1)
        ];

        Assert.Equal(0, values[0].OverallFraction);
        Assert.Equal(1, values[^1].OverallFraction);
        Assert.True(values.Zip(values.Skip(1), static (left, right) =>
            left.OverallFraction <= right.OverallFraction).All(static value => value));
    }

    [Fact]
    public void ProgressRejectsInvalidCounters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TimelineEditPreparationProgress(
                TimelineEditPreparationPhase.Planning,
                -1,
                1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TimelineEditPreparationProgress(
                TimelineEditPreparationPhase.Planning,
                2,
                1));
    }

    [Fact]
    public void AdvancedNoteCommandReportsRealPhasesAndFinishesReady()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 1_000 };
        LogicalNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                segment.Id,
                [note.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Add(1, 1),
                    Seed: 7));
        IProgressReportingProjectEditCommand reporting =
            Assert.IsAssignableFrom<IProgressReportingProjectEditCommand>(command);
        List<TimelineEditPreparationProgress> observed = [];

        IPreparedProjectEdit prepared = reporting.Prepare(
            project,
            CancellationToken.None,
            new InlineProgress<TimelineEditPreparationProgress>(observed.Add));

        Assert.True(prepared.HasChanges);
        Assert.NotEmpty(observed);
        Assert.Equal(TimelineEditPreparationPhase.ResolvingSelection, observed[0].Phase);
        Assert.Contains(observed, value =>
            value.Phase == TimelineEditPreparationPhase.Planning
            && value.Total == 1);
        Assert.Contains(observed, value =>
            value.Phase == TimelineEditPreparationPhase.ResolvingCollisions
            && value.Total == 1);
        Assert.Equal(TimelineEditPreparationPhase.Ready, observed[^1].Phase);
        Assert.True(observed.Zip(observed.Skip(1), static (left, right) =>
            left.OverallFraction <= right.OverallFraction).All(static value => value));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
