using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectStage4TimelineChangeSetTests
{
    [Fact]
    public void HumanizeAndNoteQuantizePublishOldAndNewLogicalNoteFootprints()
    {
        using MidoraProject project = new(480);
        (LogicalTrack _, Segment segment) = AddLogicalSegment(project);
        LogicalNote note = new(project)
        {
            StartTick = 11,
            LengthTicks = 5,
            Note = 60,
            Velocity = 80
        };
        segment.Notes.Add(note);

        IPreparedProjectEdit humanize = ProjectDomainEditCommands.HumanizeLogicalNotes(
            segment.Id,
            [note.Id],
            new(
                TimelineHumanizeField.Override(21, 21),
                TimelineHumanizeField.Disabled,
                TimelineHumanizeField.Disabled,
                Seed: 1)).Prepare(project);

        AssertSource(
            humanize,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalNotes,
            segment.Id,
            [new(11, 16, 60, 60)],
            [new(21, 26, 60, 60)]);

        IPreparedProjectEdit quantize = ProjectDomainEditCommands.QuantizeLogicalNotes(
            segment.Id,
            [note.Id],
            new(TimelineQuantizeGrid.FromCustomTicks(10), NoteQuantizeMode.StartOnly))
            .Prepare(project);

        AssertSource(
            quantize,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalNotes,
            segment.Id,
            [new(11, 16, 60, 60)],
            [new(10, 15, 60, 60)]);
    }

    [Fact]
    public void SplitDirectNotesPublishesCreatedCurrentOrdinalsWithoutInventingPages()
    {
        using MidoraProject project = new(480);
        (_, MidiSegment segment) = AddMidiSegment(project);
        DirectMidiNote note = new(project)
        {
            StartTick = 0,
            LengthTicks = 10,
            Key = 64,
            NoteOnVelocity = 90,
            NoteOffVelocity = 12,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        };
        segment.Notes.Add(note);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.SplitDirectMidiNotes(
            segment.Id,
            [note.Id],
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 })
            .Prepare(project);

        ProjectTimelineSourceChange source = AssertSource(
            prepared,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiNotes,
            segment.Id,
            [new(0, 10, 64, 64)],
            [new(0, 10, 64, 64)]);
        Assert.Equal(new TimelineOrdinalRange(0, 1), Assert.Single(source.OrdinalRanges));
        Assert.Equal(new TimelineOrdinalRange(0, 2), Assert.Single(source.CurrentOrdinalRanges));
        Assert.Empty(source.PageIndices);
        Assert.Empty(source.CurrentPageIndices);
    }

    [Fact]
    public void JoinSubVoiceNotesPublishesRemovedAndExtendedFootprints()
    {
        using MidoraProject project = new(480);
        (EventInstrument instrument, SubVoice voice) = AddSubVoice(project);
        TemplateEvent first = TemplateEvent.Note(project, 10, 5, 67, 80);
        TemplateEvent second = TemplateEvent.Note(project, 15, 5, 67, 90);
        voice.Events.Add(first);
        voice.Events.Add(second);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.JoinTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            new()).Prepare(project);

        AssertSource(
            prepared,
            ProjectTimelineOwnerKind.SubVoice,
            ProjectTimelineSourceKind.SubVoiceEvents,
            voice.Id,
            [new(10, 20, 67, 67)],
            [new(10, 20, 67, 67)]);
    }

    [Fact]
    public void LogicalParameterQuantizePublishesOneExactSourcePerChangedLane()
    {
        using MidoraProject project = new(480);
        (_, Segment segment) = AddLogicalSegment(project);
        LogicalParameterLane firstLane = AddParameterLane(project, segment, 11, 0.25);
        LogicalParameterLane secondLane = AddParameterLane(project, segment, 19, 0.75);
        MidoraId[] pointIds =
        [
            firstLane.Points[0].Id,
            secondLane.Points[0].Id
        ];

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
            segment.Id,
            pointIds,
            TimelineQuantizeGrid.FromCustomTicks(10)).Prepare(project);

        ProjectTimelineOwnerChangeSet owner = Assert.Single(prepared.Changes.TimelineOwnerChanges);
        Assert.Equal(segment.Id, owner.OwnerId);
        Assert.Equal(ProjectTimelineOwnerKind.LogicalSegment, owner.OwnerKind);
        Assert.Equal(2, owner.Sources.Length);
        Assert.Equal(
            [firstLane.Id, secondLane.Id],
            owner.Sources.Select(static source => source.LaneOrCurveId).Order().ToArray());
        Assert.All(owner.Sources, source =>
        {
            Assert.Equal(ProjectTimelineSourceKind.LogicalParameterPoints, source.SourceKind);
            Assert.Single(source.PreviousContentRanges);
            Assert.Single(source.CurrentContentRanges);
            Assert.Empty(source.PageIndices);
        });
    }

    [Fact]
    public void DirectEventQuantizePublishesExactOldAndNewPointTicks()
    {
        using MidoraProject project = new(480);
        (_, MidiSegment segment) = AddMidiSegment(project);
        DirectMidiChannelEvent value = new(project)
        {
            Tick = 19,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80,
            Order = 1
        };
        segment.ChannelEvents.Add(value);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.QuantizeDirectMidiEvents(
            segment.Id,
            [value.Id],
            TimelineQuantizeGrid.FromCustomTicks(10)).Prepare(project);

        AssertSource(
            prepared,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiChannelEvents,
            segment.Id,
            [new(19, 20, 779, 779)],
            [new(20, 21, 779, 779)]);
    }

    [Fact]
    public void SubVoiceEventQuantizePublishesExactOldAndNewPointTicks()
    {
        using MidoraProject project = new(480);
        (EventInstrument instrument, SubVoice voice) = AddSubVoice(project);
        TemplateEvent value = new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = 11,
            Number = 11,
            Value = 80
        };
        voice.Events.Add(value);

        IPreparedProjectEdit prepared = ProjectDomainEditCommands.QuantizeTemplateEvents(
            instrument.Id,
            voice.Id,
            [value.Id],
            TimelineQuantizeGrid.FromCustomTicks(10)).Prepare(project);

        AssertSource(
            prepared,
            ProjectTimelineOwnerKind.SubVoice,
            ProjectTimelineSourceKind.SubVoiceEvents,
            voice.Id,
            [new(11, 12, 267, 267)],
            [new(10, 11, 267, 267)]);
    }

    private static ProjectTimelineSourceChange AssertSource(
        IPreparedProjectEdit prepared,
        ProjectTimelineOwnerKind ownerKind,
        ProjectTimelineSourceKind sourceKind,
        MidoraId ownerId,
        ProjectTimelineContentChangeRange[] previous,
        ProjectTimelineContentChangeRange[] current)
    {
        ProjectTimelineOwnerChangeSet owner = Assert.Single(prepared.Changes.TimelineOwnerChanges);
        Assert.Equal(ownerId, owner.OwnerId);
        Assert.Equal(ownerKind, owner.OwnerKind);
        ProjectTimelineSourceChange source = Assert.Single(owner.Sources);
        Assert.Equal(sourceKind, source.SourceKind);
        Assert.Equal(previous, source.PreviousContentRanges);
        Assert.Equal(current, source.CurrentContentRanges);
        Assert.NotEmpty(source.OrdinalRanges);
        Assert.NotEmpty(source.CurrentOrdinalRanges);
        Assert.All(previous.Concat(current), footprint => Assert.Contains(
            source.ContentRanges,
            invalidation => invalidation.MinimumLane == footprint.MinimumLane
                && invalidation.MaximumLane == footprint.MaximumLane
                && invalidation.StartTick <= footprint.StartTick
                && invalidation.EndTick >= footprint.EndTick));
        return source;
    }

    private static (LogicalTrack Track, Segment Segment) AddLogicalSegment(MidoraProject project)
    {
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 1_000 };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (track, segment);
    }

    private static (PureMidiTrack Track, MidiSegment Segment) AddMidiSegment(MidoraProject project)
    {
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        return (track, segment);
    }

    private static (EventInstrument Instrument, SubVoice Voice) AddSubVoice(MidoraProject project)
    {
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 1_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        return (instrument, voice);
    }

    private static LogicalParameterLane AddParameterLane(
        MidoraProject project,
        Segment segment,
        long tick,
        double value)
    {
        LogicalParameterLane lane = new(project)
        {
            ParameterId = project.AllocateStableId()
        };
        lane.Points.Add(new CurvePoint(project, tick, value, CurveInterpolation.Step));
        segment.ParameterLanes.Add(lane);
        return lane;
    }
}
