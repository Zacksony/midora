using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMixedArrangementSegmentEditCommandsTests
{
    [Fact]
    public void MixedLogicalAndMidiSegmentWindowsMoveAndResizeAsOneUndoUnit()
    {
        using Fixture fixture = CreateFixture();
        MidoraId[] ids = [fixture.LogicalSegment.Id, fixture.MidiSegment.Id];

        fixture.Document.Execute(ProjectDomainEditCommands.MoveArrangementSegmentsHorizontal(
            ids,
            tickDelta: 50));

        Assert.Equal((150L, 350L), (
            fixture.LogicalSegment.ProjectStartTick,
            fixture.MidiSegment.ProjectStartTick));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();
        Assert.Same(fixture.OriginalLogicalSegment, fixture.LogicalSegment);
        Assert.Same(fixture.OriginalMidiSegment, fixture.MidiSegment);
        Assert.Equal((100L, 300L), (
            fixture.LogicalSegment.ProjectStartTick,
            fixture.MidiSegment.ProjectStartTick));

        fixture.Document.Execute(ProjectDomainEditCommands.AdjustArrangementSegmentEdges(
            ids,
            startDelta: -20,
            endDelta: 0));

        Assert.Equal((80L, 280L), (
            fixture.LogicalSegment.ProjectStartTick,
            fixture.MidiSegment.ProjectStartTick));
        Assert.Equal((120L, 120L), (
            fixture.LogicalSegment.LengthTicks,
            fixture.MidiSegment.LengthTicks));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();

        fixture.Document.Execute(ProjectDomainEditCommands.AdjustArrangementSegmentEdges(
            ids,
            startDelta: 0,
            endDelta: 40));

        Assert.Equal((140L, 140L), (
            fixture.LogicalSegment.LengthTicks,
            fixture.MidiSegment.LengthTicks));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();
        Assert.Equal((100L, 100L), (
            fixture.LogicalSegment.LengthTicks,
            fixture.MidiSegment.LengthTicks));
        Assert.False(fixture.Document.IsModified);
    }

    [Fact]
    public void MixedLogicalAndMidiSegmentsSupportEverySharedTransformAtomically()
    {
        using Fixture fixture = CreateFixture();
        MidoraId[] ids = [fixture.LogicalSegment.Id, fixture.MidiSegment.Id];

        fixture.Document.Execute(ProjectDomainEditCommands.FlipArrangementSegmentsHorizontal(
            ids,
            SegmentSelectionTransformScope.ExposedContentAndSegments));

        Assert.Equal((300L, 100L), (
            fixture.LogicalSegment.ProjectStartTick,
            fixture.MidiSegment.ProjectStartTick));
        Assert.Equal((70L, 70L, 79L), (
            fixture.LogicalNote.StartTick,
            fixture.MidiNote.StartTick,
            fixture.MidiEvent.Tick));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();

        fixture.Document.Execute(ProjectDomainEditCommands.FlipArrangementSegmentsVertical(ids));

        Assert.Equal((67, 67), (fixture.LogicalNote.Note, fixture.MidiNote.Key));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();

        fixture.Document.Execute(ProjectDomainEditCommands.ScaleArrangementSegments(
            ids,
            factor: 2,
            SegmentSelectionTransformScope.ExposedContentOnly));

        Assert.Equal((20L, 40L), (
            fixture.LogicalNote.StartTick,
            fixture.LogicalNote.LengthTicks));
        Assert.Equal((20L, 40L, 40L), (
            fixture.MidiNote.StartTick,
            fixture.MidiNote.LengthTicks,
            fixture.MidiEvent.Tick));
        Assert.Equal((100L, 300L), (
            fixture.LogicalSegment.ProjectStartTick,
            fixture.MidiSegment.ProjectStartTick));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();

        fixture.Document.Execute(ProjectDomainEditCommands.TransposeArrangementSegments(
            ids,
            semitones: 12));

        Assert.Equal((72, 72), (fixture.LogicalNote.Note, fixture.MidiNote.Key));
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();

        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "+5",
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            });
        fixture.Document.Execute(ProjectDomainEditCommands.BatchEditArrangementSegmentExposedNotes(
            ids,
            program));

        Assert.Equal(85, fixture.LogicalNote.Velocity);
        Assert.Equal(95, fixture.MidiNote.NoteOnVelocity);
        Assert.Equal(12, fixture.MidiNote.NoteOffVelocity);
        Assert.Single(fixture.Document.History);
        fixture.Document.Undo();
        Assert.Equal((80, 90), (
            fixture.LogicalNote.Velocity,
            fixture.MidiNote.NoteOnVelocity));
        Assert.False(fixture.Document.IsModified);
    }

    [Fact]
    public void MixedSegmentPropertiesKeepBothOwnerCollectionsSortedAcrossUndo()
    {
        using Fixture fixture = CreateFixture();
        Segment logicalSibling = new(fixture.Project)
        {
            ProjectStartTick = 500,
            LengthTicks = 50
        };
        MidiSegment midiSibling = new(fixture.Project)
        {
            ProjectStartTick = 500,
            LengthTicks = 50
        };
        fixture.LogicalTrack.Segments.Add(logicalSibling);
        fixture.MidiTrack.Segments.Add(midiSibling);

        fixture.Document.Execute(ProjectDomainEditCommands.SetArrangementSegmentValues(
            [fixture.LogicalSegment.Id, fixture.MidiSegment.Id],
            projectStartTick: 600));

        Assert.Equal(
            [logicalSibling.Id, fixture.LogicalSegment.Id],
            fixture.LogicalTrack.Segments.Select(value => value.Id));
        Assert.Equal(
            [midiSibling.Id, fixture.MidiSegment.Id],
            fixture.MidiTrack.Segments.Select(value => value.Id));

        fixture.Document.Undo();

        Assert.Equal(
            [fixture.LogicalSegment.Id, logicalSibling.Id],
            fixture.LogicalTrack.Segments.Select(value => value.Id));
        Assert.Equal(
            [fixture.MidiSegment.Id, midiSibling.Id],
            fixture.MidiTrack.Segments.Select(value => value.Id));
    }

    [Fact]
    public void MixedSegmentTransposeDiscardsOutOfRangeNotesAndUndoRestoresBothModels()
    {
        using Fixture fixture = CreateFixture();
        fixture.LogicalNote.Note = 120;
        fixture.MidiNote.Key = 120;

        fixture.Document.Execute(ProjectDomainEditCommands.TransposeArrangementSegments(
            [fixture.LogicalSegment.Id, fixture.MidiSegment.Id],
            semitones: 12));

        Assert.Empty(fixture.LogicalSegment.Notes);
        Assert.Empty(fixture.MidiSegment.Notes);
        Assert.Single(fixture.Document.History);

        fixture.Document.Undo();

        Assert.Same(fixture.OriginalLogicalSegment, fixture.LogicalSegment);
        Assert.Same(fixture.OriginalMidiSegment, fixture.MidiSegment);
        Assert.Same(fixture.OriginalLogicalNote, Assert.Single(fixture.LogicalSegment.Notes));
        Assert.Same(fixture.OriginalMidiNote, Assert.Single(fixture.MidiSegment.Notes));
        Assert.Equal(120, fixture.LogicalNote.Note);
        Assert.Equal(120, fixture.MidiNote.Key);
        Assert.False(fixture.Document.IsModified);
    }

    [Fact]
    public void MixedSegmentWindowEditRejectsAnyOwnerOverlapWithoutPartialMutation()
    {
        using Fixture fixture = CreateFixture();
        Segment blocker = new(fixture.Project)
        {
            ProjectStartTick = 230,
            LengthTicks = 80
        };
        fixture.LogicalTrack.Segments.Add(blocker);

        Assert.Throws<InvalidOperationException>(() => fixture.Document.Execute(
            ProjectDomainEditCommands.MoveArrangementSegmentsHorizontal(
                [fixture.LogicalSegment.Id, fixture.MidiSegment.Id],
                tickDelta: 50)));

        Assert.Equal(100, fixture.LogicalSegment.ProjectStartTick);
        Assert.Equal(300, fixture.MidiSegment.ProjectStartTick);
        Assert.Empty(fixture.Document.History);
        Assert.False(fixture.Document.IsModified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeArrangementWindowAndDeleteUseAtomicDirectoryPublication(bool delete)
    {
        using Fixture fixture = CreateFixture();
        for (int index = 0; index < 5000; index++)
            fixture.LogicalTrack.Segments.Add(new(fixture.Project)
            {
                ProjectStartTick = 1000 + index * 4, LengthTicks = 2
            });
        LogicalTrack original = fixture.LogicalTrack;
        MidoraId[] ids = original.Segments.Skip(1).Select(segment => segment.Id).ToArray();
        var command = delete
            ? ProjectDomainEditCommands.DeleteArrangementSegments(ids)
            : ProjectDomainEditCommands.SetArrangementSegmentValues(ids, lengthTicks: 3);
        using StagedProjectEdit prepared = fixture.Document.PrepareEdit(command);
        Assert.Equal(5001, original.Segments.Count);
        Assert.Equal(2, original.Segments[1].LengthTicks);
        fixture.Document.ExecutePrepared(prepared);
        LogicalTrack published = fixture.LogicalTrack;
        Assert.NotSame(original, published);
        Assert.Equal(delete ? 1 : 5001, published.Segments.Count);
        if (!delete) Assert.All(published.Segments.Skip(1), segment => Assert.Equal(3, segment.LengthTicks));
        fixture.Document.Undo();
        Assert.Same(original, fixture.LogicalTrack);
        fixture.Document.Redo();
        Assert.Same(published, fixture.LogicalTrack);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100
        };
        LogicalNote logicalNote = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 80
        };
        logicalSegment.Notes.Add(logicalNote);
        logicalTrack.Segments.Add(logicalSegment);

        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack midiTrack = new(project)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment midiSegment = new(project)
        {
            ProjectStartTick = 300,
            LengthTicks = 100
        };
        DirectMidiNote midiNote = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 90,
            NoteOffVelocity = 12
        };
        DirectMidiChannelEvent midiEvent = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 64
        };
        midiSegment.Notes.Add(midiNote);
        midiSegment.ChannelEvents.Add(midiEvent);
        midiTrack.Segments.Add(midiSegment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(midiTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midiTrack.Id));

        ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.MarkSaveSucceeded();
        return new(
            project,
            logicalTrack,
            logicalSegment,
            logicalNote,
            midiTrack,
            midiSegment,
            midiNote,
            midiEvent,
            compilation,
            document);
    }

    private sealed record Fixture(
        MidoraProject Project,
        LogicalTrack OriginalLogicalTrack,
        Segment OriginalLogicalSegment,
        LogicalNote OriginalLogicalNote,
        PureMidiTrack OriginalMidiTrack,
        MidiSegment OriginalMidiSegment,
        DirectMidiNote OriginalMidiNote,
        DirectMidiChannelEvent OriginalMidiEvent,
        ProjectCompilationSession Compilation,
        ProjectDocumentSession Document) : IDisposable
    {
        public LogicalTrack LogicalTrack => Project.Tracks.Single(value => value.Id == OriginalLogicalTrack.Id);
        public PureMidiTrack MidiTrack => Project.PureMidiTracks.Single(value => value.Id == OriginalMidiTrack.Id);
        public Segment LogicalSegment => LogicalTrack.Segments.Single(value => value.Id == OriginalLogicalSegment.Id);
        public MidiSegment MidiSegment => MidiTrack.Segments.Single(value => value.Id == OriginalMidiSegment.Id);
        public LogicalNote LogicalNote => LogicalSegment.Notes.FirstOrDefault(value => value.Id == OriginalLogicalNote.Id) ?? OriginalLogicalNote;
        public DirectMidiNote MidiNote => MidiSegment.Notes.FirstOrDefault(value => value.Id == OriginalMidiNote.Id) ?? OriginalMidiNote;
        public DirectMidiChannelEvent MidiEvent => MidiSegment.ChannelEvents.First(value => value.Id == OriginalMidiEvent.Id);
        public void Dispose() => Compilation.Dispose();
    }
}
