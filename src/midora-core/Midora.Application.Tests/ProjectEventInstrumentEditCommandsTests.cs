using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectEventInstrumentEditCommandsTests
{
    [Fact]
    public void TemplateLengthCannotCrossContentOrLoopBoundariesAndUndoIsExact()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(project, 479, 64));
        instrument.SubVoices[0].Curves.Add(curve);
        instrument.RequiresChannelIsolation = true;
        instrument.LoopStartTick = 120;
        instrument.LoopEndTick = 480;
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(instrument.Id, 479)));
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(
            instrument.Id,
            960));

        Assert.Equal(960, instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void IsolationPreservesIncompatibleDataAndLoopCanBeDisabledWhileRestricted()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        instrument.RequiresChannelIsolation = true;
        instrument.LoopStartTick = 0;
        instrument.LoopEndTick = 480;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentIsolation(
            instrument.Id,
            requiresChannelIsolation: false));

        Assert.False(instrument.RequiresChannelIsolation);
        Assert.Equal(0, instrument.LoopStartTick);
        Assert.Equal(480, instrument.LoopEndTick);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1213");
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, 120, 360)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentOverlap(
                instrument.Id,
                OverlapPolicy.LetOverlap,
                OverlapScope.SamePitch)));

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            null,
            null));
        Assert.Null(instrument.LoopStartTick);
        Assert.Null(instrument.LoopEndTick);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(0, instrument.LoopStartTick);
        Assert.Equal(480, instrument.LoopEndTick);
        document.Undo();
        Assert.True(instrument.RequiresChannelIsolation);
        Assert.True(compilation.LastAttempt.IsConsumable);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void PreRollIsAnAtomicDefinitionEditWithExactUndoAndCompilationInvalidation()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalNote note = project.Tracks[0].Segments[0].Notes[0];
        note.StartTick = 120;
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long beforeFingerprint = compilation.LastAttempt.Fingerprint;
        ProjectContentChangedEventArgs? contentChange = null;
        document.ContentChanged += (_, args) => contentChange = args;

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(
            instrument.Id,
            120));

        Assert.Equal(120, instrument.PreRollTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.NotEqual(beforeFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Contains(
            instrument.Id,
            Assert.IsType<ProjectContentChangedEventArgs>(contentChange).EventInstrumentIds);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(0, instrument.PreRollTicks);
        Assert.Equal(beforeFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(instrument.Id, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(instrument.Id, 481)));
    }

    [Fact]
    public void TimingEditCanAtomicallyShrinkTemplateAndPreRoll()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        instrument.SubVoices[0].Events[0].LengthTicks = 120;
        instrument.PreRollTicks = 360;
        project.Tracks[0].Segments[0].Notes[0].StartTick = 360;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentTiming(
            instrument.Id,
            templateLengthTicks: 240,
            preRollTicks: 120));

        Assert.Equal(240, instrument.TemplateLengthTicks);
        Assert.Equal(120, instrument.PreRollTicks);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.Equal(360, instrument.PreRollTicks);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TimingLoopAndIsolationEditValidatesAndCommitsTheFinalTupleAtomically()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        instrument.SubVoices[0].Events[0].LengthTicks = 120;
        instrument.RequiresChannelIsolation = true;
        instrument.PreRollTicks = 360;
        instrument.LoopStartTick = 0;
        instrument.LoopEndTick = 480;
        instrument.RequiresChannelIsolation = false;
        project.Tracks[0].Segments[0].Notes[0].StartTick = 360;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentTimingLoopAndIsolation(
            instrument.Id,
            templateLengthTicks: 240,
            preRollTicks: 120,
            loopStartTick: 0,
            loopEndTick: 240,
            requiresChannelIsolation: true));

        Assert.Equal(240, instrument.TemplateLengthTicks);
        Assert.Equal(120, instrument.PreRollTicks);
        Assert.Equal(0, instrument.LoopStartTick);
        Assert.Equal(240, instrument.LoopEndTick);
        Assert.True(instrument.RequiresChannelIsolation);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.Equal(360, instrument.PreRollTicks);
        Assert.Equal(0, instrument.LoopStartTick);
        Assert.Equal(480, instrument.LoopEndTick);
        Assert.False(instrument.RequiresChannelIsolation);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void IncompleteLoopEndpointsAreEditableButCompilationRejectsThemUntilCompleted()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        instrument.RequiresChannelIsolation = true;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            120,
            null));

        Assert.Equal(120, instrument.LoopStartTick);
        Assert.Null(instrument.LoopEndTick);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value =>
            value.Code == "MIDORA1212"
            && value.Message == "Loop Start and Loop End must both be present or both be absent.");
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(instrument.Id, 120)));
        AssertCurrentCompilationMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            120,
            360));

        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.Equal(120, instrument.LoopStartTick);
        Assert.Equal(360, instrument.LoopEndTick);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(120, instrument.LoopStartTick);
        Assert.Null(instrument.LoopEndTick);
        Assert.False(compilation.LastAttempt.IsConsumable);

        document.Undo();
        Assert.Null(instrument.LoopStartTick);
        Assert.Null(instrument.LoopEndTick);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.False(document.IsModified);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            null,
            360));

        Assert.Null(instrument.LoopStartTick);
        Assert.Equal(360, instrument.LoopEndTick);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1212");
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void OverlapAndLifecycleSettingsAreAtomicValidatedHistoryEdits()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        instrument.RequiresChannelIsolation = true;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentOverlap(
            instrument.Id,
            OverlapPolicy.CutPrevious,
            OverlapScope.AnyPitch));
        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
            instrument.Id,
            ShortNoteLifecycle.Tail,
            LongNoteLifecycle.EndAtTemplate));

        Assert.Equal(OverlapPolicy.CutPrevious, instrument.OverlapPolicy);
        Assert.Equal(OverlapScope.AnyPitch, instrument.OverlapScope);
        Assert.Equal(ShortNoteLifecycle.Tail, instrument.ShortLifecycle);
        Assert.Equal(LongNoteLifecycle.EndAtTemplate, instrument.LongLifecycle);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(ShortNoteLifecycle.CutAtNoteOff, instrument.ShortLifecycle);
        Assert.Equal(LongNoteLifecycle.HoldLastState, instrument.LongLifecycle);
        document.Undo();
        Assert.Equal(OverlapPolicy.Warn, instrument.OverlapPolicy);
        Assert.Equal(OverlapScope.SamePitch, instrument.OverlapScope);
        Assert.False(document.IsModified);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentOverlap(
                instrument.Id,
                (OverlapPolicy)999,
                OverlapScope.SamePitch)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                instrument.Id,
                ShortNoteLifecycle.CutAtNoteOff,
                (LongNoteLifecycle)999)));
    }

    [Fact]
    public void SubVoiceNameRootAndOrderPreserveIdentityAndDoNotRewriteNotes()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice first = instrument.SubVoices[0];
        SubVoice second = new(project);
        instrument.SubVoices.Add(second);
        TemplateEvent templateNote = first.Events[0];
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateSubVoiceName(
            instrument.Id,
            second.Id,
            "  Layer  "));
        document.Execute(ProjectDomainEditCommands.UpdateSubVoiceRootNote(
            instrument.Id,
            second.Id,
            72));
        document.Execute(ProjectDomainEditCommands.ReorderSubVoice(
            instrument.Id,
            second.Id,
            0));

        Assert.Equal([second, first], instrument.SubVoices);
        Assert.Equal("Layer", second.Name);
        Assert.Equal(72, second.RootNoteOverride);
        Assert.Equal(60, templateNote.Number);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        document.Undo();
        Assert.Equal([first, second], instrument.SubVoices);
        Assert.Null(second.Name);
        Assert.Null(second.RootNoteOverride);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SubVoiceDeleteRequiresDataConfirmationRemovesMappingsAndRestoresOrder()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice first = instrument.SubVoices[0];
        SubVoice second = new(project) { Name = "Layer", RootNoteOverride = 72 };
        second.InitialState.Program = 10;
        second.Events.Add(TemplateEvent.ControlChange(project, 0, 1, 64));
        instrument.SubVoices.Add(second);
        LogicalParameterDefinition parameter = instrument.LogicalParameters[0];
        LogicalParameterMapping firstMapping = CreateMapping(project, parameter.Id, first.Id, 1);
        LogicalParameterMapping secondMapping = CreateMapping(project, parameter.Id, second.Id, 2);
        LogicalParameterMapping thirdMapping = CreateMapping(project, parameter.Id, first.Id, 3);
        instrument.ParameterMappings.AddRange([firstMapping, secondMapping, thirdMapping]);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteSubVoice(
                instrument.Id,
                second.Id,
                nonEmptyDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteSubVoice(
            instrument.Id,
            second.Id,
            nonEmptyDeletionConfirmed: true));

        Assert.Equal([first], instrument.SubVoices);
        Assert.Equal([firstMapping, thirdMapping], instrument.ParameterMappings);
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteSubVoice(
                instrument.Id,
                first.Id,
                nonEmptyDeletionConfirmed: true)));
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal([first, second], instrument.SubVoices);
        Assert.Equal([firstMapping, secondMapping, thirdMapping], instrument.ParameterMappings);
        Assert.Same(second, instrument.SubVoices[1]);
        Assert.Same(secondMapping, instrument.ParameterMappings[1]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EmptyNonLastSubVoiceCanBeDeletedWithoutConfirmation()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice empty = new(project);
        instrument.SubVoices.Add(empty);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteSubVoice(
            instrument.Id,
            empty.Id,
            nonEmptyDeletionConfirmed: false));

        Assert.DoesNotContain(empty, instrument.SubVoices);
        document.Undo();
        Assert.Same(empty, instrument.SubVoices[1]);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static LogicalParameterMapping CreateMapping(
        MidoraProject project,
        MidoraId parameterId,
        MidoraId subVoiceId,
        int controller) =>
        new(project)
        {
            ParameterId = parameterId,
            SubVoiceId = subVoiceId,
            Target = MidiValueTarget.ControlChange(controller)
        };

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
