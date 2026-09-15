using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectLogicalParameterPointEditCommandsTests
{
    [Fact]
    public void UpsertReplacesExistingValuesAddsSortedPointsAndIsOneUndoUnit()
    {
        Fixture fixture = CreateFixture(LogicalParameterType.Double);
        CurvePoint original = new(
            fixture.Project,
            tick: 0,
            value: 0.25,
            interpolation: CurveInterpolation.Step);
        fixture.Lane.Points.Add(original);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.UpsertLogicalParameterPoints(
            fixture.Segment.Id,
            fixture.Lane.Id,
            [
                new LogicalParameterPointEdit(20, 0.8),
                new LogicalParameterPointEdit(0, 0.6),
                new LogicalParameterPointEdit(10, 0.4)
            ]));

        Assert.Equal([0L, 10L, 20L], fixture.Lane.Points.Select(value => value.Tick));
        CurvePoint replacement = fixture.Lane.Points[0];
        CurvePoint firstAdded = fixture.Lane.Points[1];
        CurvePoint secondAdded = fixture.Lane.Points[2];
        Assert.Equal(original.Id, replacement.Id);
        Assert.Equal(CurveInterpolation.Step, replacement.Interpolation);
        Assert.Equal(0.6, replacement.Value);
        Assert.All(
            new[] { firstAdded, secondAdded },
            value => Assert.Equal(CurveInterpolation.Step, value.Interpolation));
        Assert.Single(document.History);

        document.Undo();
        Assert.Same(original, Assert.Single(fixture.Lane.Points));
        document.Redo();
        Assert.Same(replacement, fixture.Lane.Points[0]);
        Assert.Same(firstAdded, fixture.Lane.Points[1]);
        Assert.Same(secondAdded, fixture.Lane.Points[2]);
    }

    [Fact]
    public void EnumPointSetUsesStepInterpolationAndRejectsValuesOutsideDefinition()
    {
        Fixture fixture = CreateFixture(LogicalParameterType.Enum);
        fixture.Parameter.Minimum = 0;
        fixture.Parameter.Maximum = 2;
        fixture.Parameter.UsesExplicitEnumValues = true;
        fixture.Parameter.EnumItems.AddRange(
        [
            new LogicalParameterEnumItem(fixture.Project) { Name = "Off", Value = 0 },
            new LogicalParameterEnumItem(fixture.Project) { Name = "On", Value = 1 },
            new LogicalParameterEnumItem(fixture.Project) { Name = "Auto", Value = 2 }
        ]);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.UpsertLogicalParameterPoints(
            fixture.Segment.Id,
            fixture.Lane.Id,
            [new LogicalParameterPointEdit(10, 1)]));

        Assert.Equal(CurveInterpolation.Step, Assert.Single(fixture.Lane.Points).Interpolation);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpsertLogicalParameterPoints(
                fixture.Segment.Id,
                fixture.Lane.Id,
                [new LogicalParameterPointEdit(20, 3)])));
        Assert.Single(document.History);
    }

    [Fact]
    public void SparseUpsertAcrossDistantTicksPreservesOrderAndUndoRedoIdentity()
    {
        Fixture fixture = CreateFixture(LogicalParameterType.Double);
        CurvePoint middle = new(
            fixture.Project,
            tick: 500_000_000,
            value: 0.5,
            interpolation: CurveInterpolation.Step);
        fixture.Lane.Points.Add(middle);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long generationBeforeApply = fixture.Lane.Points.CreateQuerySnapshot().Generation;

        document.Execute(ProjectDomainEditCommands.UpsertLogicalParameterPoints(
            fixture.Segment.Id,
            fixture.Lane.Id,
            [
                new LogicalParameterPointEdit(1_000_000_000, 0.75),
                new LogicalParameterPointEdit(10, 0.25)
            ]));

        Assert.Equal(
            [10L, 500_000_000L, 1_000_000_000L],
            fixture.Lane.Points.Select(static value => value.Tick));
        Assert.Equal(
            generationBeforeApply + 1,
            fixture.Lane.Points.CreateQuerySnapshot().Generation);
        CurvePoint first = fixture.Lane.Points[0];
        CurvePoint last = fixture.Lane.Points[2];

        document.Undo();
        Assert.Same(middle, Assert.Single(fixture.Lane.Points));

        document.Redo();
        Assert.Same(first, fixture.Lane.Points[0]);
        Assert.Same(middle, fixture.Lane.Points[1]);
        Assert.Same(last, fixture.Lane.Points[2]);
    }

    private static Fixture CreateFixture(LogicalParameterType type)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        instrument.SubVoices.Add(new SubVoice(project));
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Parameter",
            Type = type,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        return new(project, parameter, segment, lane);
    }

    private sealed record Fixture(
        MidoraProject Project,
        LogicalParameterDefinition Parameter,
        Segment Segment,
        LogicalParameterLane Lane);
}
