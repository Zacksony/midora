using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class SnapAwareResizeMinimumTests
{
    [Fact]
    public void LogicalAndMixedSegmentResizeClampEachObjectToExplicitMinimum()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project) { ProjectStartTick = 100, LengthTicks = 100 };
        LogicalNote logicalNote = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Note = 60,
            Velocity = 100
        };
        logicalSegment.Notes.Add(logicalNote);
        logicalTrack.Segments.Add(logicalSegment);

        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack midiTrack = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        MidiSegment midiSegment = new(project) { ProjectStartTick = 300, LengthTicks = 100 };
        root.RoutingMode = MidiChannelRootRoutingMode.Auto;
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(midiTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midiTrack.Id));
        midiTrack.Segments.Add(midiSegment);

        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
            logicalSegment.Id,
            [logicalNote.Id],
            startDelta: 0,
            endDelta: -90,
            minimumLengthTicks: 48));
        Assert.Equal(48, logicalNote.LengthTicks);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.AdjustArrangementSegmentEdges(
            [logicalSegment.Id, midiSegment.Id],
            startDelta: 90,
            endDelta: 0,
            minimumLengthTicks: 48));
        Segment changedLogical = project.Tracks.Single(value => value.Id == logicalTrack.Id).Segments.Single();
        MidiSegment changedMidi = project.PureMidiTracks.Single(value => value.Id == midiTrack.Id).Segments.Single();
        Assert.Equal((152L, 48L), (changedLogical.ProjectStartTick, changedLogical.LengthTicks));
        Assert.Equal((352L, 48L), (changedMidi.ProjectStartTick, changedMidi.LengthTicks));
        document.Undo();
        Assert.Same(logicalSegment, project.Tracks.Single(value => value.Id == logicalTrack.Id).Segments.Single());
        Assert.Same(midiSegment, project.PureMidiTracks.Single(value => value.Id == midiTrack.Id).Segments.Single());
        document.Redo();
        Assert.Same(changedLogical, project.Tracks.Single(value => value.Id == logicalTrack.Id).Segments.Single());
        Assert.Same(changedMidi, project.PureMidiTracks.Single(value => value.Id == midiTrack.Id).Segments.Single());
    }

    [Fact]
    public void DirectAndTemplateNoteResizeUseExplicitMinimumWithoutExpandingShortSourceNotes()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 480 };
        DirectMidiNote direct = new(project)
        {
            StartTick = 100,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 100
        };
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        track.Segments.Add(segment);
        segment.Notes.Add(direct);

        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent template = TemplateEvent.Note(project, 100, 100, 64, 100);
        voice.Events.Add(template);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
            segment.Id,
            [direct.Id],
            startDelta: 0,
            endDelta: -19,
            minimumLengthTicks: 48));
        Assert.Equal(20, direct.LengthTicks);

        document.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
            instrument.Id,
            voice.Id,
            [template.Id],
            startDelta: 0,
            endDelta: -90,
            minimumLengthTicks: 48));
        Assert.Equal(48, template.LengthTicks);
    }
}
