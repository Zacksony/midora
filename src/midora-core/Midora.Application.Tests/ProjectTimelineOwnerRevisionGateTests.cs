using System.Collections.Immutable;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTimelineOwnerRevisionGateTests
{
    [Fact]
    public void LogicalNoteRevisionChangeRejectsPreparedPublicationAndCreatesNoHistory()
    {
        using MidoraProject project = new(480);
        (LogicalTrack track, Segment root, LogicalNote note) = AddLogicalSegment(project);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        StagedProjectEdit staged = document.PrepareEdit(new DeferredPreparedCommand(
            "Replace Logical Segment",
            value => ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
                value,
                track,
                root,
                ProjectTimelineOwnerRootClone.CloneLogicalSegment(value, root),
                new())));

        note.Velocity++;

        Assert.Throws<InvalidOperationException>(() => document.ExecutePrepared(staged));
        Assert.Same(root, Assert.Single(track.Segments));
        Assert.False(document.CanUndo);
        Assert.Equal(0, document.CurrentStateId);
    }

    [Fact]
    public void LogicalParameterLaneRevisionChangeRejectsPreparedPublication()
    {
        using MidoraProject project = new(480);
        (LogicalTrack track, Segment root, _) = AddLogicalSegment(project);
        LogicalParameterLane lane = new(project) { ParameterId = project.AllocateStableId() };
        lane.Points.Add(new CurvePoint(project, 10, 0.25, CurveInterpolation.Step));
        root.ParameterLanes.Add(lane);
        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, root);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            root,
            replacement,
            new());

        lane.Points.Add(new CurvePoint(project, 20, 0.5, CurveInterpolation.Step));

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(root, Assert.Single(track.Segments));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryDirectMidiSourceRevisionParticipatesInPublicationGate(int sourceKind)
    {
        using MidoraProject project = new(480);
        (PureMidiTrack track, MidiSegment root) = AddDirectSegment(project);
        DirectMidiNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 80
        };
        DirectMidiChannelEvent channelEvent = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80
        };
        OpaqueMidiEvent opaqueEvent = new(project)
        {
            Tick = 10,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 1,
            Payload = [0x41]
        };
        root.Notes.Add(note);
        root.ChannelEvents.Add(channelEvent);
        root.OpaqueEvents.Add(opaqueEvent);
        MidiSegment replacement = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(project, root);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
            project,
            track,
            root,
            replacement,
            new());

        switch (sourceKind)
        {
            case 0:
                note.NoteOnVelocity++;
                break;
            case 1:
                channelEvent.Data2++;
                break;
            case 2:
                opaqueEvent.Tick++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(root, Assert.Single(track.Segments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubVoiceEventAndCurveRevisionsParticipateInPublicationGate(bool mutateCurve)
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice root = new(project) { Name = "Voice" };
        TemplateEvent templateEvent = TemplateEvent.Note(project, 10, 20, 60, 80);
        root.Events.Add(templateEvent);
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(11) };
        curve.Points.Add(new CurvePoint(project, 10, 0.25, CurveInterpolation.Step));
        root.Curves.Add(curve);
        instrument.SubVoices.Add(root);
        project.EventInstruments.Add(instrument);
        SubVoice replacement = ProjectTimelineOwnerRootClone.CloneSubVoice(project, root);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            root,
            replacement,
            new());

        if (mutateCurve)
            curve.Points.Add(new CurvePoint(project, 20, 0.5, CurveInterpolation.Step));
        else
            templateEvent.Value++;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(root, Assert.Single(instrument.SubVoices));
    }

    [Fact]
    public void OneStaleSourceInMultiOwnerBatchPreventsEveryRootWrite()
    {
        using MidoraProject project = new(480);
        (LogicalTrack firstTrack, Segment firstRoot, _) = AddLogicalSegment(project, "First");
        (LogicalTrack secondTrack, Segment secondRoot, LogicalNote secondNote) =
            AddLogicalSegment(project, "Second");
        Segment firstReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, firstRoot);
        Segment secondReplacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, secondRoot);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareMultiple(
            project,
            [
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    firstTrack,
                    firstRoot,
                    firstReplacement),
                ProjectTimelineOwnerRootReplacementSpec.LogicalSegment(
                    secondTrack,
                    secondRoot,
                    secondReplacement)
            ],
            new());

        secondNote.StartTick++;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(firstRoot, Assert.Single(firstTrack.Segments));
        Assert.Same(secondRoot, Assert.Single(secondTrack.Segments));
    }

    [Fact]
    public void DetachedReplacementMutationBeforeFirstApplyIsRejected()
    {
        using MidoraProject project = new(480);
        (LogicalTrack track, Segment root, _) = AddLogicalSegment(project);
        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, root);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            root,
            replacement,
            new());

        replacement.Notes[0].Velocity++;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(root, Assert.Single(track.Segments));
    }

    [Fact]
    public void ProductionCommandRejectsSourceMutationDuringPlanningBeforeClone()
    {
        using MidoraProject project = new(480);
        (LogicalTrack track, Segment root, LogicalNote note) = AddLogicalSegment(project);
        note.StartTick = 11;
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        ITimelineSelectionResultEditCommand command = ProjectDomainEditCommands.QuantizeLogicalNotes(
            root.Id,
            [note.Id],
            new(
                TimelineQuantizeGrid.FromCustomTicks(10),
                NoteQuantizeMode.StartOnly));
        bool mutated = false;
        InlineProgress progress = new(value =>
        {
            if (mutated || value.Phase != TimelineEditPreparationPhase.Planning) return;
            mutated = true;
            note.Velocity++;
        });

        Assert.Throws<InvalidOperationException>(() => document.PrepareEdit(
            command,
            progress: progress));

        Assert.True(mutated);
        Assert.Same(root, Assert.Single(track.Segments));
        Assert.Equal(11, note.StartTick);
        Assert.False(document.CanUndo);
        Assert.Equal(0, document.CurrentStateId);
    }

    [Fact]
    public void SubVoiceCommandRejectsTemplateLengthMutationDuringPlanning()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 1_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent note = TemplateEvent.Note(project, 11, 20, 60, 80);
        voice.Events.Add(note);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                instrument.Id,
                voice.Id,
                [note.Id],
                new(
                    TimelineQuantizeGrid.FromCustomTicks(10),
                    NoteQuantizeMode.StartOnly));
        bool mutated = false;
        InlineProgress progress = new(value =>
        {
            if (mutated || value.Phase != TimelineEditPreparationPhase.Planning) return;
            mutated = true;
            instrument.TemplateLengthTicks = 2_000;
        });

        Assert.Throws<InvalidOperationException>(() => document.PrepareEdit(
            command,
            progress: progress));

        Assert.True(mutated);
        Assert.Same(voice, Assert.Single(instrument.SubVoices));
        Assert.Equal(11, note.Tick);
        Assert.False(document.CanUndo);
        Assert.Equal(0, document.CurrentStateId);
    }

    [Fact]
    public void ExactTimelineChangeSetIsClonedCarriedAndPublished()
    {
        using MidoraProject project = new(480);
        (LogicalTrack track, Segment root, _) = AddLogicalSegment(project);
        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, root);
        ProjectTimelineOwnerChangeSet exact = new(
            root.Id,
            ProjectTimelineOwnerKind.LogicalSegment,
            [new(
                ProjectTimelineSourceKind.LogicalNotes,
                null,
                root.Notes.Generation,
                replacement.Notes.Generation,
                [new TimelineOrdinalRange(0, 1)],
                [0],
                [new ProjectTimelineContentChangeRange(10, 30, 60, 60)])]);
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);
        changes.TimelineOwnerChanges.Add(exact);
        IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            root,
            replacement,
            changes);

        changes.TimelineOwnerChanges.Clear();

        Assert.Equal([exact], prepared.Changes.TimelineOwnerChanges);
        ProjectContentChangedEventArgs eventArgs = new(prepared.Changes);
        Assert.Equal([exact], eventArgs.TimelineOwnerChanges);
        Assert.False(eventArgs.IsEmpty);
    }

    private static (LogicalTrack Track, Segment Root, LogicalNote Note) AddLogicalSegment(
        MidoraProject project,
        string name = "Track")
    {
        LogicalTrack track = new(project) { Name = name };
        Segment root = new(project) { LengthTicks = 1_000 };
        LogicalNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 80
        };
        root.Notes.Add(note);
        track.Segments.Add(root);
        project.Tracks.Add(track);
        return (track, root, note);
    }

    private static (PureMidiTrack Track, MidiSegment Root) AddDirectSegment(MidoraProject project)
    {
        MidiChannelRoot channelRoot = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "Track",
            MidiChannelRootId = channelRoot.Id
        };
        MidiSegment root = new(project) { LengthTicks = 1_000 };
        track.Segments.Add(root);
        project.MidiChannelRoots.Add(channelRoot);
        project.PureMidiTracks.Add(track);
        return (track, root);
    }

    private sealed class DeferredPreparedCommand(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) : IProjectEditCommand
    {
        public string Name { get; } = name;
        public IPreparedProjectEdit Prepare(MidoraProject project) => prepare(project);
    }

    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> report)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => report(value);
    }
}
