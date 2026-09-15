using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMusicContentEditCommandsTests
{
    [Fact]
    public void LogicalNoteUpdateAllowsHiddenContentAndUndoRestoresExactValues()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        LogicalNote note = segment.Notes[0];
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalNote(
            segment.Id,
            note.Id,
            startTick: 1_200,
            lengthTicks: 240,
            note: 72,
            velocity: 127));

        Assert.Equal(1_200, note.StartTick);
        Assert.Equal(240, note.LengthTicks);
        Assert.Equal(72, note.Note);
        Assert.Equal(127, note.Velocity);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(0, note.StartTick);
        Assert.Equal(480, note.LengthTicks);
        Assert.Equal(60, note.Note);
        Assert.Equal(100, note.Velocity);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalNote(segment.Id, note.Id, -1, 1, 60, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalNote(segment.Id, note.Id, 0, 0, 60, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalNote(
                segment.Id,
                note.Id,
                long.MaxValue,
                1,
                60,
                1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalNote(segment.Id, note.Id, 0, 1, 128, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalNote(segment.Id, note.Id, 0, 1, 60, 0)));
    }

    [Fact]
    public void LogicalNoteAndLaneDeletionRestoreExactObjectsAndOrdering()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        LogicalNote firstNote = segment.Notes[0];
        LogicalNote secondNote = new(project)
        {
            StartTick = 480,
            LengthTicks = 120,
            Note = 64,
            Velocity = 80
        };
        segment.Notes.Add(secondNote);
        LogicalParameterLane lane = segment.ParameterLanes[0];
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteLogicalNote(segment.Id, firstNote.Id));
        Assert.Equal([secondNote], segment.Notes);
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteLogicalParameterLane(
                segment.Id,
                lane.Id,
                deletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteLogicalParameterLane(
            segment.Id,
            lane.Id,
            deletionConfirmed: true));
        Assert.Empty(segment.ParameterLanes);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        Assert.Equal([firstNote, secondNote], segment.Notes);
        Assert.Same(lane, Assert.Single(segment.ParameterLanes));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalParameterPointUpdateValidatesDefinitionAndPreservesStableId()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalParameterDefinition parameter = instrument.LogicalParameters[0];
        parameter.Type = LogicalParameterType.Integer;
        parameter.Minimum = 0;
        parameter.Maximum = 10;
        parameter.DefaultValue = 5;
        Segment segment = project.Tracks[0].Segments[0];
        LogicalParameterLane lane = segment.ParameterLanes[0];
        lane.Points.Clear();
        CurvePoint point = new(project, 0, 5);
        CurvePoint blocker = new(project, 480, 6);
        lane.Points.Add(point);
        lane.Points.Add(blocker);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            point.Id,
            240,
            7,
            CurveInterpolation.Linear));

        CurvePoint replacement = lane.Points[0];
        Assert.NotSame(point, replacement);
        Assert.Equal(point.Id, replacement.Id);
        Assert.Equal(240, replacement.Tick);
        Assert.Equal(7, replacement.Value);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(point, lane.Points[0]);
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                point.Id,
                0,
                7.5,
                CurveInterpolation.Linear)));
        document.Execute(ProjectDomainEditCommands.UpdateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            point.Id,
            480,
            7,
            CurveInterpolation.Linear));
        CurvePoint collisionReplacement = Assert.Single(lane.Points);
        Assert.Equal(point.Id, collisionReplacement.Id);
        Assert.Equal((480L, 7d), (collisionReplacement.Tick, collisionReplacement.Value));
        Assert.DoesNotContain(blocker, lane.Points);
        document.Undo();
        Assert.Equal([point, blocker], lane.Points);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                point.Id,
                0,
                double.NaN,
                CurveInterpolation.Linear)));

        document.Execute(ProjectDomainEditCommands.DeleteLogicalParameterPoint(
            segment.Id,
            lane.Id,
            point.Id));
        Assert.Equal([blocker], lane.Points);
        document.Undo();
        Assert.Same(point, lane.Points[0]);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LaneRebindImplementsExplicitClampAndDiscardPoliciesAtomically()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalParameterDefinition source = instrument.LogicalParameters[0];
        source.Minimum = -100;
        source.Maximum = 100;
        LogicalParameterDefinition targetEnum = new(project)
        {
            Name = "Mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 10,
            DefaultValue = 0,
            UsesExplicitEnumValues = true
        };
        targetEnum.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "Low", Value = 0 });
        targetEnum.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "High", Value = 10 });
        instrument.LogicalParameters.Add(targetEnum);
        LogicalParameterDefinition targetInteger = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 10,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(targetInteger);
        Segment segment = project.Tracks[0].Segments[0];
        LogicalParameterLane lane = segment.ParameterLanes[0];
        lane.Points.Clear();
        CurvePoint below = new(project, 0, -2.6);
        CurvePoint tie = new(project, 240, 5);
        CurvePoint upper = new(project, 480, 8.2);
        lane.Points.AddRange([below, tie, upper]);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.RebindLogicalParameterLane(
                segment.Id,
                lane.Id,
                targetEnum.Id,
                LogicalParameterLaneRebindMode.Clamp,
                enumSemanticWarningAcknowledged: false)));
        document.Execute(ProjectDomainEditCommands.RebindLogicalParameterLane(
            segment.Id,
            lane.Id,
            targetEnum.Id,
            LogicalParameterLaneRebindMode.Clamp,
            enumSemanticWarningAcknowledged: true));

        Assert.Equal(targetEnum.Id, lane.ParameterId);
        Assert.Equal([0, 0, 10], lane.Points.Select(value => value.Value));
        Assert.All(lane.Points, value => Assert.Equal(CurveInterpolation.Step, value.Interpolation));
        Assert.Equal([below.Id, tie.Id, upper.Id], lane.Points.Select(value => value.Id));
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(source.Id, lane.ParameterId);
        Assert.Equal([below, tie, upper], lane.Points);

        document.Execute(ProjectDomainEditCommands.RebindLogicalParameterLane(
            segment.Id,
            lane.Id,
            targetInteger.Id,
            LogicalParameterLaneRebindMode.DiscardInvalidValues,
            enumSemanticWarningAcknowledged: false));
        Assert.Equal(targetInteger.Id, lane.ParameterId);
        Assert.Equal([tie.Id, upper.Id], lane.Points.Select(value => value.Id));
        Assert.Equal([5, 8], lane.Points.Select(value => value.Value));
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LaneRebindRejectsDuplicateTargetsAndPointEditingRejectsBrokenLane()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        LogicalParameterLane lane = segment.ParameterLanes[0];
        LogicalParameterLane duplicate = new(project) { ParameterId = lane.ParameterId };
        segment.ParameterLanes.Add(duplicate);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.RebindLogicalParameterLane(
                segment.Id,
                duplicate.Id,
                lane.ParameterId,
                LogicalParameterLaneRebindMode.Clamp,
                enumSemanticWarningAcknowledged: false)));

        segment.ParameterLanes.Remove(duplicate);
        track.EventInstrumentUsageId = null;
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                lane.Points[0].Id,
                0,
                0.5,
                CurveInterpolation.Linear)));
    }

    [Fact]
    public void EventInstrumentDescriptionColorAndRootNoteHaveCorrectCompilationScope()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent templateNote = voice.Events[0];
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long initialFingerprint = compilation.LastAttempt.Fingerprint;
        long initialSourceRevision = compilation.SourceRevision;
        ProjectContentChangedEventArgs? contentChange = null;
        document.ContentChanged += (_, args) => contentChange = args;

        string description = "First line\n\tSecond line\r\n";
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentDescription(
            instrument.Id,
            description));
        Assert.Equal(description, instrument.Description);
        Assert.Equal(initialFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(initialSourceRevision, compilation.SourceRevision);
        Assert.Contains(instrument.Id, Assert.IsType<ProjectContentChangedEventArgs>(contentChange)
            .PresentationEventInstrumentIds);
        Assert.Empty(contentChange.EventInstrumentIds);

        MidoraColor color = new(1, 2, 3);
        contentChange = null;
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentColor(instrument.Id, color));
        Assert.Equal(color, instrument.Color);
        Assert.Equal(initialFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(initialSourceRevision, compilation.SourceRevision);
        Assert.Contains(instrument.Id, Assert.IsType<ProjectContentChangedEventArgs>(contentChange)
            .PresentationEventInstrumentIds);
        Assert.Empty(contentChange.EventInstrumentIds);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentRootNote(instrument.Id, 72));
        Assert.Equal(72, instrument.RootNote);
        Assert.Null(voice.RootNoteOverride);
        Assert.Equal(60, templateNote.Number);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        document.Undo();
        Assert.Equal(60, instrument.RootNote);
        Assert.Equal(MidoraColor.DefaultInstrument, instrument.Color);
        Assert.Null(instrument.Description);
        Assert.False(document.IsModified);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentRootNote(instrument.Id, 128)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentDescription(instrument.Id, "bad\0text")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentDescription(
                instrument.Id,
                new string('x', 65_537))));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentDescription(instrument.Id, "\ud800")));
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

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
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        instrument.LogicalParameters.Add(parameter);
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
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(project, 0, 0.5));
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        return project;
    }

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
}
