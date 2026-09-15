using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectSelectionTransformEditCommandsTests
{
    [Fact]
    public void LogicalNoteTransformsUseVisualBoundsAndUndoExactly()
    {
        (MidoraProject project, Segment segment, LogicalNote first, LogicalNote second) =
            CreateLogicalNoteProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipLogicalNotesHorizontal(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((40L, 10L), (first.StartTick, second.StartTick));
        Assert.Equal((20L, 10L), (first.LengthTicks, second.LengthTicks));
        document.Undo();
        Assert.Equal((10L, 50L), (first.StartTick, second.StartTick));

        document.Execute(ProjectDomainEditCommands.FlipLogicalNotesVertical(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((64, 60), (first.Note, second.Note));
        document.Undo();
        Assert.Equal((60, 64), (first.Note, second.Note));

        document.Execute(ProjectDomainEditCommands.ScaleLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            factor: 2));
        Assert.Equal((10L, 90L), (first.StartTick, second.StartTick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            semitones: 64));
        Assert.Same(first, Assert.Single(segment.Notes));
        Assert.Equal(124, first.Note);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
        Assert.Equal((60, 64), (first.Note, second.Note));
    }

    [Fact]
    public void TemplateNoteTransformsUseVisualBoundsAndRestoreDiscardedNotes()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 10, 20, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 50, 10, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipTemplateNotesHorizontal(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id]));
        Assert.Equal((40L, 10L), (first.Tick, second.Tick));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipTemplateNotesVertical(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id]));
        Assert.Equal((64, 60), (first.Number, second.Number));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.ScaleTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            factor: 2));
        Assert.Equal((10L, 90L), (first.Tick, second.Tick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            semitones: 64));
        Assert.Same(first, Assert.Single(voice.Events));
        Assert.Equal(124, first.Number);
        document.Undo();
        Assert.Equal([first, second], voice.Events);
        Assert.Equal((60, 64), (first.Number, second.Number));
    }

    [Fact]
    public void DirectMidiNoteTransformsMatchPianoRollSemanticsAndPreserveNoteOffVelocity()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 480 };
        DirectMidiNote first = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 17,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        };
        DirectMidiNote second = new(project)
        {
            StartTick = 50,
            LengthTicks = 10,
            Key = 64,
            NoteOnVelocity = 80,
            NoteOffVelocity = 29,
            NoteOnOrder = 3,
            NoteOffOrder = 4
        };
        segment.Notes.AddRange([first, second]);
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((40L, 10L), (first.StartTick, second.StartTick));
        Assert.Equal((17, 29), (first.NoteOffVelocity, second.NoteOffVelocity));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipDirectMidiNotesVertical(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((64, 60), (first.Key, second.Key));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.ScaleDirectMidiNotes(
            segment.Id,
            [first.Id, second.Id],
            factor: 2));
        Assert.Equal((10L, 90L), (first.StartTick, second.StartTick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        document.Undo();

        using (BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "+10",
                [BatchEditField.KeyNumber] = "+1",
                [BatchEditField.Gate] = "*2",
                [BatchEditField.Tick] = "+5"
            }))
        {
            document.Execute(ProjectDomainEditCommands.BatchEditDirectMidiNotes(
                segment.Id,
                [first.Id, second.Id],
                program));
        }
        Assert.Equal((15L, 55L), (first.StartTick, second.StartTick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        Assert.Equal((61, 65), (first.Key, second.Key));
        Assert.Equal((110, 90), (first.NoteOnVelocity, second.NoteOnVelocity));
        Assert.Equal((17, 29), (first.NoteOffVelocity, second.NoteOffVelocity));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeDirectMidiNotes(
            segment.Id,
            [first.Id, second.Id],
            semitones: 64));
        Assert.Same(first, Assert.Single(segment.Notes));
        Assert.Equal(124, first.Key);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
    }

    [Fact]
    public void DirectMidiEventTransformsKeepLastEditedPointAtExactTick()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 480 };
        DirectMidiChannelEvent first = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 20,
            Order = 1
        };
        DirectMidiChannelEvent second = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80,
            Order = 2
        };
        DirectMidiChannelEvent incumbent = new(project)
        {
            Tick = 30,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 100,
            Order = 3
        };
        segment.ChannelEvents.AddRange([first, second, incumbent]);
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleDirectMidiEventPoints(
            segment.Id,
            [first.Id, second.Id],
            factor: 2));

        Assert.Equal([10L, 30L], segment.ChannelEvents.Select(value => value.Tick));
        Assert.Equal([first, second], segment.ChannelEvents);
        document.Undo();
        Assert.Equal([10L, 20L, 30L], segment.ChannelEvents.Select(value => value.Tick));

        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "*2",
                [BatchEditField.Tick] = "30"
            });
        document.Execute(ProjectDomainEditCommands.BatchEditDirectMidiEventPoints(
            segment.Id,
            [first.Id, second.Id],
            program));

        Assert.Same(second, Assert.Single(segment.ChannelEvents));
        Assert.Equal((30L, 127), (second.Tick, second.Data2));
        document.Undo();
        Assert.Equal([first, second, incumbent], segment.ChannelEvents);
    }

    [Fact]
    public void DirectMidiNoteTransformDiscardsLaterExactStartAndKeyObject()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 480 };
        DirectMidiNote first = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 100
        };
        DirectMidiNote second = new(project)
        {
            StartTick = 10,
            LengthTicks = 40,
            Key = 60,
            NoteOnVelocity = 80
        };
        segment.Notes.AddRange([first, second]);
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveDirectMidiNotes(
            segment.Id,
            [first.Id, second.Id],
            tickDelta: 10,
            keyDelta: 1));

        Assert.Same(first, Assert.Single(segment.Notes));
        Assert.Equal(20, first.StartTick);
        Assert.Equal(61, first.Key);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
    }

    [Fact]
    public void SegmentTransformsIgnoreHiddenContentAndCanMirrorSegmentWindows()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment first = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalNote hidden = new(project)
        {
            StartTick = 10,
            LengthTicks = 10,
            Note = 30,
            Velocity = 80
        };
        LogicalNote exposed = new(project)
        {
            StartTick = 60,
            LengthTicks = 10,
            Note = 70,
            Velocity = 90
        };
        LogicalParameterLane lane = new(project) { ParameterId = MidoraId.FromSequence(900_001) };
        CurvePoint hiddenPoint = new(project, 40, 0.25);
        CurvePoint exposedPoint = new(project, 70, 0.75);
        lane.Points.AddRange([hiddenPoint, exposedPoint]);
        first.Notes.AddRange([hidden, exposed]);
        first.ParameterLanes.Add(lane);
        Segment second = new(project)
        {
            ProjectStartTick = 300,
            LengthTicks = 50,
            ContentOffsetTick = 0
        };
        track.Segments.AddRange([first, second]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipSegmentsHorizontal(
            [first.Id],
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal(10, hidden.StartTick);
        Assert.Equal(130, exposed.StartTick);
        Assert.Equal(40, lane.Points.Single(value => value.Id == hiddenPoint.Id).Tick);
        Assert.Equal(129, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);
        Assert.Equal(100, first.ProjectStartTick);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipSegmentsHorizontal(
            [first.Id, second.Id],
            SegmentSelectionTransformScope.ExposedContentAndSegments));
        Assert.Equal((250L, 100L), (first.ProjectStartTick, second.ProjectStartTick));
        Assert.Equal(10, hidden.StartTick);
        document.Undo();
        Assert.Equal((100L, 300L), (first.ProjectStartTick, second.ProjectStartTick));
        Assert.Equal(60, exposed.StartTick);
        Assert.Equal(70, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);

        document.Execute(ProjectDomainEditCommands.FlipSegmentsVertical([first.Id]));
        Assert.Equal(30, hidden.Note);
        Assert.Equal(57, exposed.Note);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.ScaleSegments(
            [first.Id],
            factor: 2,
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal((100L, 100L, 50L), (
            first.ProjectStartTick,
            first.LengthTicks,
            first.ContentOffsetTick));
        Assert.Equal((10L, 70L, 20L), (
            hidden.StartTick,
            exposed.StartTick,
            exposed.LengthTicks));
        Assert.Equal(40, lane.Points.Single(value => value.Id == hiddenPoint.Id).Tick);
        Assert.Equal(90, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeSegments([first.Id], semitones: 60));
        Assert.Same(hidden, Assert.Single(first.Notes));
        document.Undo();
        Assert.Equal([hidden, exposed], first.Notes);
    }

    [Fact]
    public void MidiSegmentTransformsPreserveHiddenAndOpaqueContentAndUndoExactly()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment first = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        DirectMidiNote hidden = new(project)
        {
            StartTick = 10,
            LengthTicks = 10,
            Key = 30,
            NoteOnVelocity = 80
        };
        DirectMidiNote exposed = new(project)
        {
            StartTick = 60,
            LengthTicks = 10,
            Key = 70,
            NoteOnVelocity = 90
        };
        DirectMidiChannelEvent hiddenEvent = new(project)
        {
            Tick = 40,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 10
        };
        DirectMidiChannelEvent exposedEvent = new(project)
        {
            Tick = 70,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 20
        };
        OpaqueMidiEvent hiddenOpaque = new(project)
        {
            Tick = 45,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 1,
            Payload = [1]
        };
        OpaqueMidiEvent exposedOpaque = new(project)
        {
            Tick = 80,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 1,
            Payload = [2]
        };
        first.Notes.AddRange([hidden, exposed]);
        first.ChannelEvents.AddRange([hiddenEvent, exposedEvent]);
        first.OpaqueEvents.AddRange([hiddenOpaque, exposedOpaque]);
        MidiSegment second = new(project)
        {
            ProjectStartTick = 300,
            LengthTicks = 50
        };
        track.Segments.AddRange([first, second]);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipMidiSegmentsHorizontal(
            [first.Id],
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal((10L, 130L), (Note(hidden.Id).StartTick, Note(exposed.Id).StartTick));
        Assert.Equal((40L, 129L), (Event(hiddenEvent.Id).Tick, Event(exposedEvent.Id).Tick));
        Assert.Equal((45L, 119L), (Opaque(hiddenOpaque.Id).Tick, Opaque(exposedOpaque.Id).Tick));
        Assert.Equal(100, Current(first.Id).ProjectStartTick);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipMidiSegmentsHorizontal(
            [first.Id, second.Id],
            SegmentSelectionTransformScope.ExposedContentAndSegments));
        Assert.Equal((250L, 100L), (Current(first.Id).ProjectStartTick, Current(second.Id).ProjectStartTick));
        document.Undo();
        Assert.Equal((100L, 300L), (Current(first.Id).ProjectStartTick, Current(second.Id).ProjectStartTick));
        Assert.Equal((60L, 70L, 80L), (Note(exposed.Id).StartTick, Event(exposedEvent.Id).Tick, Opaque(exposedOpaque.Id).Tick));

        document.Execute(ProjectDomainEditCommands.ScaleMidiSegments(
            [first.Id],
            factor: 2,
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal((10L, 70L, 90L, 110L), (
            Note(hidden.Id).StartTick,
            Note(exposed.Id).StartTick,
            Event(exposedEvent.Id).Tick,
            Opaque(exposedOpaque.Id).Tick));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeMidiSegments([first.Id], semitones: 60));
        Assert.Equal(hidden.Id, Assert.Single(Current(first.Id).Notes).Id);
        document.Undo();
        Assert.Equal([hidden.Id, exposed.Id], Current(first.Id).Notes.Select(static value => value.Id));
        Assert.Equal(Enumerable.Range(0, first.Notes.Count).Select(first.Notes.CreateObjectSource().GetByOrdinal),
            Enumerable.Range(0, Current(first.Id).Notes.Count).Select(Current(first.Id).Notes.CreateObjectSource().GetByOrdinal));

        MidiSegment Current(MidoraId id) => project.PureMidiTracks.SelectMany(static value => value.Segments).Single(value => value.Id == id);
        DirectMidiNote Note(MidoraId id) => Current(first.Id).Notes.Single(value => value.Id == id);
        DirectMidiChannelEvent Event(MidoraId id) => Current(first.Id).ChannelEvents.Single(value => value.Id == id);
        OpaqueMidiEvent Opaque(MidoraId id) => Current(first.Id).OpaqueEvents.Single(value => value.Id == id);
    }

    [Fact]
    public void SegmentContentScaleUsesLaterPointWhenRoundingCollapsesTicks()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100 };
        LogicalParameterLane lane = new(project) { ParameterId = MidoraId.FromSequence(900_002) };
        CurvePoint first = new(project, 10, 0.25);
        CurvePoint second = new(project, 11, 0.75);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleSegments(
            [segment.Id],
            factor: 0.01,
            SegmentSelectionTransformScope.ExposedContentOnly));

        CurvePoint replacement = Assert.Single(lane.Points);
        Assert.Equal(second.Id, replacement.Id);
        Assert.Equal(0, replacement.Tick);
        document.Undo();
        Assert.Equal([first, second], lane.Points);
        document.Redo();
        Assert.Equal(second.Id, Assert.Single(lane.Points).Id);
    }

    [Fact]
    public void PointTransformUsesLaterEditedPointForSameTickCollision()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 0, 0.1);
        CurvePoint second = new(project, 10, 0.2);
        CurvePoint incumbent = new(project, 20, 0.3);
        lane.Points.AddRange([first, second, incumbent]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [first.Id, second.Id],
            factor: 2));

        Assert.Equal([first.Id, second.Id], lane.Points.Select(value => value.Id));
        Assert.Equal([0L, 20L], lane.Points.Select(value => value.Tick));
        document.Undo();
        Assert.Equal([first, second, incumbent], lane.Points);
        Assert.Equal([0L, 10L, 20L], lane.Points.Select(value => value.Tick));
        document.Redo();
        Assert.Equal([first.Id, second.Id], lane.Points.Select(value => value.Id));
    }

    [Fact]
    public void SubVoicePointTransformUsesExactTargetAndLaterEditedPointWins()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 0, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 10, 11, 40);
        TemplateEvent incumbent = TemplateEvent.ControlChange(project, 20, 11, 60);
        voice.Events.AddRange([first, second, incumbent]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11),
            factor: 2));

        Assert.Equal([first, second], voice.Events);
        Assert.Equal([0L, 20L], voice.Events.Select(value => value.Tick));
        document.Undo();
        Assert.Equal([first, second, incumbent], voice.Events);
        Assert.Equal([0L, 10L, 20L], voice.Events.Select(value => value.Tick));

        document.Execute(ProjectDomainEditCommands.FlipSubVoiceEventPointsHorizontal(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11)));
        Assert.Equal((10L, 0L, 20L), (first.Tick, second.Tick, incumbent.Tick));
        document.Undo();
        Assert.Equal((0L, 10L, 20L), (first.Tick, second.Tick, incumbent.Tick));
    }

    [Fact]
    public void BatchEditExpressionsApplyInDependencyOrderAndClampOrDiscardResults()
    {
        (MidoraProject project, Segment segment, LogicalNote first, LogicalNote second) =
            CreateLogicalNoteProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=k1 * 2",
                [BatchEditField.KeyNumber] = "=k0 + 1",
                [BatchEditField.Gate] = "50%",
                [BatchEditField.Tick] = "=t0 + tr"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            program));

        Assert.Equal((61, 65), (first.Note, second.Note));
        Assert.Equal((122, 127), (first.Velocity, second.Velocity));
        Assert.Equal((10L, 5L), (first.LengthTicks, second.LengthTicks));
        Assert.Equal((10L, 90L), (first.StartTick, second.StartTick));

        document.Undo();
        Assert.Equal((60, 64), (first.Note, second.Note));
        Assert.Equal((10L, 50L), (first.StartTick, second.StartTick));

        using BatchEditExpressionProgram discard = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = "+100",
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            });
        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            discard));
        Assert.Empty(segment.Notes);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
    }

    [Fact]
    public void BatchExpressionValidationRejectsSelfAndIndirectCycles()
    {
        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=v1 + 1",
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=k1",
                [BatchEditField.KeyNumber] = "=g1",
                [BatchEditField.Gate] = "=v1",
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "=new Random().Next()",
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=p0",
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "=v0",
                [BatchEditField.Tick] = string.Empty
            }));
    }

    [Fact]
    public void BatchEditRoundsBeforeTickBoundaryAndDeletesArbitrarilyLargeKeys()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote note = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram nearZeroTick = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = "=-0.49"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [note.Id],
            nearZeroTick));
        Assert.Equal(0, note.StartTick);
        document.Undo();

        using BatchEditExpressionProgram hugeKey = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = "=1e300",
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            });
        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [note.Id],
            hugeKey));
        Assert.Empty(segment.Notes);
        document.Undo();
        Assert.Same(note, Assert.Single(segment.Notes));
    }

    [Fact]
    public void SegmentNoteBatchTickCanExpandLeftWithoutMovingHiddenContent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalNote hidden = new(project)
        {
            StartTick = 10,
            LengthTicks = 10,
            Note = 60,
            Velocity = 80
        };
        LogicalNote selected = new(project)
        {
            StartTick = 60,
            LengthTicks = 10,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([hidden, selected]);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = "-40"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [selected.Id],
            program));

        Assert.Equal((70L, 130L, 20L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(20, selected.StartTick);
        Assert.Equal(60, checked(segment.ProjectStartTick + hidden.StartTick - segment.ContentOffsetTick));
        Assert.Equal(70, checked(segment.ProjectStartTick + selected.StartTick - segment.ContentOffsetTick));

        document.Undo();
        Assert.Equal((100L, 100L, 50L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(60, selected.StartTick);
        Assert.Equal([hidden, selected], segment.Notes);
    }

    [Fact]
    public void SegmentPointBatchLeftExpansionRejectsTrackOverlapAtomically()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment blocker = new(project)
        {
            ProjectStartTick = 40,
            LengthTicks = 50
        };
        Segment segment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint point = new(project, 60, 0.5);
        lane.Points.Add(point);
        segment.ParameterLanes.Add(lane);
        track.Segments.AddRange([blocker, segment]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "-40"
            });

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
                segment.Id,
                lane.Id,
                [point.Id],
                program)));

        Assert.Equal((100L, 100L, 50L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(60, point.Tick);
        Assert.Empty(document.History);
    }

    [Fact]
    public void LogicalParameterPointBatchReplacesSameTickIncumbent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
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
        CurvePoint first = new(project, 10, 0.25);
        CurvePoint second = new(project, 20, 0.75);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "10"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [second.Id],
            program));

        CurvePoint replacement = Assert.Single(lane.Points);
        Assert.Equal(second.Id, replacement.Id);
        Assert.Equal(10, replacement.Tick);
        document.Undo();
        Assert.Equal([first, second], lane.Points);
        document.Redo();
        Assert.Equal(second.Id, Assert.Single(lane.Points).Id);
    }

    [Fact]
    public void SubVoiceEventPointBatchReplacesSameTargetAndTickIncumbent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 10, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 20, 11, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "10"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [second.Id],
            MidiValueTarget.ControlChange(11),
            program));

        Assert.Same(second, Assert.Single(voice.Events));
        Assert.Equal(10, second.Tick);
        document.Undo();
        Assert.Equal([first, second], voice.Events);
        Assert.Equal(20, second.Tick);
        document.Redo();
        Assert.Same(second, Assert.Single(voice.Events));
    }

    [Fact]
    public void SubVoiceEventPointBatchClampsValueAndDeletesNegativeTickResult()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 10, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 20, 11, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram clamp = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "*10",
                [BatchEditField.Tick] = string.Empty
            });

        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id],
            MidiValueTarget.ControlChange(11),
            clamp));
        Assert.Equal(127, first.Value);
        document.Undo();
        Assert.Equal(20, first.Value);

        using BatchEditExpressionProgram discard = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "-30"
            });
        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [second.Id],
            MidiValueTarget.ControlChange(11),
            discard));
        Assert.Same(first, Assert.Single(voice.Events));
        document.Undo();
        Assert.Equal([first, second], voice.Events);
    }

    private static (MidoraProject Project, Segment Segment, LogicalNote First, LogicalNote Second)
        CreateLogicalNoteProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote first = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        LogicalNote second = new(project)
        {
            StartTick = 50,
            LengthTicks = 10,
            Note = 64,
            Velocity = 80
        };
        segment.Notes.AddRange([first, second]);
        track.Segments.Add(segment);
        return (project, segment, first, second);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }
}
