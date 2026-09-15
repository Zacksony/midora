using System.Runtime.ExceptionServices;
using System.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class TimelineTypedSelectionSourceMappingTests
{
    [Fact]
    public async Task EveryEditableTimelineSurfaceProducesAnOwnerScopedSource()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Typed Timeline selection sources",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id,
            voice.Id,
            tick: 12,
            controller: 11,
            value: 64));
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            tick: 24,
            lengthTicks: 48,
            note: 60,
            velocity: 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Expression",
            LogicalParameterType.Integer,
            0,
            127,
            0,
            127,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
        LogicalTrack logicalTrack = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(logicalTrack.Id, 0, 480));
        Segment logicalSegment = Assert.Single(logicalTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            logicalSegment.Id,
            parameter.Id));
        LogicalParameterLane logicalLane = Assert.Single(logicalSegment.ParameterLanes);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            logicalSegment.Id,
            startTick: 24,
            lengthTicks: 48,
            note: 60,
            velocity: 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            logicalSegment.Id,
            logicalLane.Id,
            tick: 24,
            value: 64,
            CurveInterpolation.Step));

        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
        PureMidiTrack midiTrack = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(midiTrack.Id, 0, 480));
        MidiSegment midiSegment = Assert.Single(midiTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            midiSegment.Id,
            tick: 24,
            DirectMidiChannelEventKind.ControlChange,
            data1: 74,
            data2: 96));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            midiSegment.Id,
            startTick: 24,
            lengthTicks: 48,
            key: 60,
            noteOnVelocity: 90));

        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        TimelineWorkspaceViewModel logical = session.OpenSegment(logicalSegment.Id);
        TimelineWorkspaceViewModel midi = session.OpenSegment(midiSegment.Id);
        DirectMidiEventLaneTarget midiTarget = new(
            DirectMidiChannelEventKind.ControlChange,
            74);
        midi.AddDirectMidiLaneTarget(midiTarget);
        session.RefreshWorkspace(midi);
        InstrumentWorkspaceViewModel editor = session.OpenInstrument(instrument.Id);

        RunOnSta(() =>
        {
            WorkspaceTimelineSelectionSource arrangementSource = AssertSource(
                arrangement,
                new TimelineSurface { SurfaceMode = TimelineSurfaceMode.Arrangement },
                arrangement.Snapshot!.Items.Single(item => item.Id == logicalSegment.Id));
            Assert.Equal(
                WorkspaceTimelineSelectionKind.ArrangementSegment,
                arrangementSource.Kind);

            WorkspaceTimelineSelectionSource logicalNoteSource = AssertSource(
                logical,
                new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll },
                logical.Snapshot!.Items.Single());
            Assert.Equal(
                new WorkspaceTimelineSelectionSource(
                    WorkspaceTimelineSelectionKind.LogicalNote,
                    logicalSegment.Id),
                logicalNoteSource);

            WorkspaceTimelineSelectionSource logicalPointSource = AssertSource(
                logical,
                new TimelineSurface
                {
                    SurfaceMode = TimelineSurfaceMode.EventLanes,
                    Tag = "ParameterLanes"
                },
                logical.ParameterSnapshot!.Items.Single());
            Assert.Equal(WorkspaceTimelineSelectionKind.LogicalParameterPoint, logicalPointSource.Kind);
            Assert.Equal(logicalSegment.Id, logicalPointSource.OwnerId);
            Assert.Equal(logicalLane.Id, logicalPointSource.SecondaryOwnerId);

            WorkspaceTimelineSelectionSource midiNoteSource = AssertSource(
                midi,
                new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll },
                midi.Snapshot!.Items.Single());
            Assert.Equal(
                new WorkspaceTimelineSelectionSource(
                    WorkspaceTimelineSelectionKind.DirectMidiNote,
                    midiSegment.Id),
                midiNoteSource);

            WorkspaceTimelineSelectionSource midiPointSource = AssertSource(
                midi,
                new TimelineSurface
                {
                    SurfaceMode = TimelineSurfaceMode.EventLanes,
                    Tag = "ParameterLanes"
                },
                midi.ParameterSnapshot!.Items.Single());
            Assert.Equal(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, midiPointSource.Kind);
            Assert.Equal(midiSegment.Id, midiPointSource.OwnerId);
            Assert.Equal(midiTarget.Kind, midiPointSource.DirectMidiEventKind);
            Assert.Equal(midiTarget.Data1, midiPointSource.DirectMidiData1);

            WorkspaceTimelineSelectionSource templateNoteSource = AssertSource(
                editor,
                new TimelineSurface
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    Tag = "SubVoiceNotes"
                },
                editor.SubVoiceNoteSnapshot!.Items.Single());
            Assert.Equal(WorkspaceTimelineSelectionKind.TemplateNote, templateNoteSource.Kind);
            Assert.Equal(instrument.Id, templateNoteSource.OwnerId);
            Assert.Equal(voice.Id, templateNoteSource.SecondaryOwnerId);

            WorkspaceTimelineSelectionSource templatePointSource = AssertSource(
                editor,
                new TimelineSurface
                {
                    SurfaceMode = TimelineSurfaceMode.EventLanes,
                    Tag = "SubVoiceEvents"
                },
                editor.SubVoiceEventSnapshot!.Items.Single());
            Assert.Equal(WorkspaceTimelineSelectionKind.SubVoiceEventPoint, templatePointSource.Kind);
            Assert.Equal(instrument.Id, templatePointSource.OwnerId);
            Assert.Equal(voice.Id, templatePointSource.SecondaryOwnerId);
            Assert.Equal(MidiValueTarget.ControlChange(11), templatePointSource.MidiTarget);

            Assert.NotEqual(logicalNoteSource, midiNoteSource);
            Assert.NotEqual(logicalPointSource.QuantizeScope, midiPointSource.QuantizeScope);
        });
    }

    private static WorkspaceTimelineSelectionSource AssertSource(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        TimelineRenderItem? item = null) =>
        Assert.IsType<WorkspaceTimelineSelectionSource>(
            MainWindow.GetTimelineSelectionSource(workspace, surface, item));

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
