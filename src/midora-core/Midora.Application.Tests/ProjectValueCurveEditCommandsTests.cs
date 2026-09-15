using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectValueCurveEditCommandsTests
{
    [Fact]
    public void PointBatchDragChangesTickAndValueAsOneAtomicUndoUnit()
    {
        Fixture fixture = CreateFixture();
        CurvePoint first = fixture.Curve.Points[0];
        CurvePoint second = fixture.Curve.Points[1];
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustValueCurvePoints(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            [second.Id, first.Id],
            tickDelta: 20,
            valueDelta: 5));

        ValueCurve published = fixture.Project.EventInstruments.Single(item => item.Id == fixture.Instrument.Id)
            .SubVoices.Single(item => item.Id == fixture.Voice.Id).Curves.Single(item => item.Id == fixture.Curve.Id);
        Assert.Equal([140L, 260L], published.Points.Select(value => value.Tick));
        Assert.Equal([15d, 25d], published.Points.Select(value => value.Value));
        Assert.Single(document.History);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(first, fixture.Curve.Points[0]);
        Assert.Same(second, fixture.Curve.Points[1]);
        Assert.Equal([120L, 240L], fixture.Curve.Points.Select(value => value.Tick));
        Assert.Equal([10d, 20d], fixture.Curve.Points.Select(value => value.Value));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ExactSetPointBatchIsAtomicAndUndoRestoresEachOriginalValue()
    {
        Fixture fixture = CreateFixture();
        CurvePoint first = fixture.Curve.Points[0];
        CurvePoint second = fixture.Curve.Points[1];
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.SetValueCurvePoints(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            [second.Id, first.Id],
            value: 64,
            interpolation: CurveInterpolation.Step));

        ValueCurve published = fixture.Project.EventInstruments.Single(item => item.Id == fixture.Instrument.Id)
            .SubVoices.Single(item => item.Id == fixture.Voice.Id).Curves.Single(item => item.Id == fixture.Curve.Id);
        Assert.Equal([64d, 64d], published.Points.Select(item => item.Value));
        Assert.All(published.Points, item => Assert.Equal(CurveInterpolation.Step, item.Interpolation));
        Assert.Single(document.History);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(first, fixture.Curve.Points[0]);
        Assert.Same(second, fixture.Curve.Points[1]);
        Assert.Equal([10d, 20d], fixture.Curve.Points.Select(item => item.Value));
        Assert.All(fixture.Curve.Points, item => Assert.Equal(CurveInterpolation.Linear, item.Interpolation));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void PointUpdatePreservesStableIdentityAutoExtendsTemplateAndUndoIsExact()
    {
        Fixture fixture = CreateFixture();
        CurvePoint original = fixture.Curve.Points[0];
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateValueCurvePoint(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            original.Id,
            tick: 600,
            value: 62.5,
            interpolation: CurveInterpolation.Step));

        CurvePoint replacement = Assert.Single(
            fixture.Curve.Points,
            value => value.Id == original.Id);
        Assert.NotSame(original, replacement);
        Assert.Equal((600, 62.5, CurveInterpolation.Step),
            (replacement.Tick, replacement.Value, replacement.Interpolation));
        Assert.Equal(601, fixture.Instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(original, fixture.Curve.Points[0]);
        Assert.Equal(480, fixture.Instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void PointEditValidatesTickValueInterpolationConflictAndTargetOverflow()
    {
        Fixture fixture = CreateFixture();
        CurvePoint point = fixture.Curve.Points[0];
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurvePoint(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                point.Id,
                -1,
                64,
                CurveInterpolation.Linear)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurvePoint(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                point.Id,
                120,
                double.NaN,
                CurveInterpolation.Linear)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurvePoint(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                point.Id,
                120,
                64,
                (CurveInterpolation)999)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurvePoint(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                point.Id,
                fixture.Curve.Points[1].Tick,
                64,
                CurveInterpolation.Linear)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurvePoint(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                point.Id,
                120,
                200,
                CurveInterpolation.Linear)));
        Assert.False(document.IsModified);

        document.Execute(ProjectDomainEditCommands.UpdateValueCurveTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            MappingRounding.Floor,
            MappingOverflow.Clamp));
        document.Execute(ProjectDomainEditCommands.UpdateValueCurvePoint(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            point.Id,
            120,
            200,
            CurveInterpolation.Linear));

        Assert.Equal(MappingRounding.Floor, fixture.Curve.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.Curve.TargetSettings.Overflow);
        Assert.Contains(compilation.LastAttempt.Events.ToArray(), value =>
            value.Tick == 120
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 127);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        Assert.Same(point, fixture.Curve.Points[0]);
        Assert.Equal(MappingRounding.Round, fixture.Curve.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.Curve.TargetSettings.Overflow);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void TargetSettingsEditKeepsSettingsObjectAndUsesConfiguredRounding()
    {
        Fixture fixture = CreateFixture(firstValue: 62.5, firstTick: 0);
        MidiIntegerTargetSettings settings = fixture.Curve.TargetSettings;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateValueCurveTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            MappingRounding.Floor,
            MappingOverflow.Fail));

        Assert.Same(settings, fixture.Curve.TargetSettings);
        CanonicalMidiEvent controller = Assert.Single(
            compilation.LastAttempt.Events.ToArray(),
            value => value.Tick == 0
                && value.Role == CanonicalEventRole.ControlChange
                && value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 1);
        Assert.Equal((byte)62, controller.Message.Byte2);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(settings, fixture.Curve.TargetSettings);
        Assert.Equal(MappingRounding.Round, settings.Rounding);
        Assert.False(document.IsModified);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateValueCurveTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Curve.Id,
                (MappingRounding)999,
                MappingOverflow.Fail)));
    }

    [Fact]
    public void PointAndCurveDeletionRestoreExactObjectsWithoutRemovingDiscreteEvents()
    {
        Fixture fixture = CreateFixture();
        TemplateEvent discrete = TemplateEvent.ControlChange(fixture.Project, 120, 1, 80);
        fixture.Voice.Events.Add(discrete);
        CurvePoint removedPoint = fixture.Curve.Points[0];
        CurvePoint[] originalPoints = fixture.Curve.Points.ToArray();
        ValueCurve[] originalCurves = fixture.Voice.Curves.ToArray();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteValueCurvePoint(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id,
            removedPoint.Id));
        Assert.DoesNotContain(removedPoint, fixture.Curve.Points);
        Assert.Contains(discrete, fixture.Voice.Events);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalPoints, fixture.Curve.Points);
        Assert.Same(removedPoint, fixture.Curve.Points[0]);

        document.Execute(ProjectDomainEditCommands.DeleteValueCurve(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Curve.Id));
        Assert.DoesNotContain(fixture.Curve, fixture.Voice.Curves);
        Assert.Contains(discrete, fixture.Voice.Events);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalCurves, fixture.Voice.Curves);
        Assert.Same(fixture.Curve, fixture.Voice.Curves[0]);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static Fixture CreateFixture(double firstValue = 10, long firstTick = 120)
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
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(project, firstTick, firstValue));
        curve.Points.Add(new CurvePoint(project, 240, 20));
        voice.Curves.Add(curve);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return new(project, instrument, voice, curve);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        SubVoice Voice,
        ValueCurve Curve);
}
