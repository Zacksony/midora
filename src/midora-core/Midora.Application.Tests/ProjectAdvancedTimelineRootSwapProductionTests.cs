using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectAdvancedTimelineRootSwapProductionTests
{
    [Fact]
    public void DirectMidiProductionCommandsPublishOnlyByRootExchange()
    {
        using MidoraProject project = new(480);
        MidiChannelRoot channelRoot = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "Track",
            MidiChannelRootId = channelRoot.Id
        };
        MidiSegment root = new(project) { LengthTicks = 1_000 };
        DirectMidiNote humanize = AddDirectNote(project, root, 10, 5, 60);
        DirectMidiNote split = AddDirectNote(project, root, 100, 10, 61);
        DirectMidiNote joinFirst = AddDirectNote(project, root, 200, 5, 62);
        DirectMidiNote joinSecond = AddDirectNote(project, root, 205, 5, 62);
        DirectMidiNote quantize = AddDirectNote(project, root, 311, 5, 63);
        DirectMidiChannelEvent channelEvent = new(project)
        {
            Tick = 411,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 90
        };
        root.ChannelEvents.Add(channelEvent);
        track.Segments.Add(root);
        project.MidiChannelRoots.Add(channelRoot);
        project.PureMidiTracks.Add(track);

        AssertDirectRootExchange(
            project,
            track,
            ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                root.Id,
                [humanize.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(99, 99),
                    Seed: 1)));
        AssertDirectRootExchange(
            project,
            track,
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                root.Id,
                [split.Id],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 }));
        AssertDirectRootExchange(
            project,
            track,
            ProjectDomainEditCommands.JoinDirectMidiNotes(
                root.Id,
                [joinFirst.Id, joinSecond.Id],
                new()));
        AssertDirectRootExchange(
            project,
            track,
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                root.Id,
                [quantize.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10))));
        AssertDirectRootExchange(
            project,
            track,
            ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                root.Id,
                [channelEvent.Id],
                TimelineQuantizeGrid.FromCustomTicks(10)));
    }

    [Fact]
    public void SubVoiceProductionCommandsPublishOnlyByRootExchange()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 1_000
        };
        SubVoice root = new(project) { Name = "Voice" };
        TemplateEvent humanize = TemplateEvent.Note(project, 10, 5, 60, 80);
        TemplateEvent split = TemplateEvent.Note(project, 100, 10, 61, 80);
        TemplateEvent joinFirst = TemplateEvent.Note(project, 200, 5, 62, 80);
        TemplateEvent joinSecond = TemplateEvent.Note(project, 205, 5, 62, 80);
        TemplateEvent quantize = TemplateEvent.Note(project, 311, 5, 63, 80);
        TemplateEvent channelEvent = TemplateEvent.ControlChange(project, 411, 11, 90);
        root.Events.AddRange([humanize, split, joinFirst, joinSecond, quantize, channelEvent]);
        instrument.SubVoices.Add(root);
        project.EventInstruments.Add(instrument);

        AssertSubVoiceRootExchange(
            project,
            instrument,
            ProjectDomainEditCommands.HumanizeTemplateNotes(
                instrument.Id,
                root.Id,
                [humanize.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(99, 99),
                    Seed: 1)));
        AssertSubVoiceRootExchange(
            project,
            instrument,
            ProjectDomainEditCommands.SplitTemplateNotes(
                instrument.Id,
                root.Id,
                [split.Id],
                new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 5 }));
        AssertSubVoiceRootExchange(
            project,
            instrument,
            ProjectDomainEditCommands.JoinTemplateNotes(
                instrument.Id,
                root.Id,
                [joinFirst.Id, joinSecond.Id],
                new()));
        AssertSubVoiceRootExchange(
            project,
            instrument,
            ProjectDomainEditCommands.QuantizeTemplateNotes(
                instrument.Id,
                root.Id,
                [quantize.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10))));
        AssertSubVoiceRootExchange(
            project,
            instrument,
            ProjectDomainEditCommands.QuantizeTemplateEvents(
                instrument.Id,
                root.Id,
                [channelEvent.Id],
                TimelineQuantizeGrid.FromCustomTicks(10)));
    }

    [Fact]
    public void ProductionCommandRejectsAStaleRootWithoutPublishingItsPreparedReplacement()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment root = new(project) { LengthTicks = 1_000 };
        LogicalNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 5,
            Note = 60,
            Velocity = 80
        };
        root.Notes.Add(note);
        track.Segments.Add(root);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                root.Id,
                [note.Id],
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(99, 99),
                    Seed: 1));

        IPreparedProjectEdit prepared = command.Prepare(project);
        Segment interveningRoot = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, root);
        track.Segments[0] = interveningRoot;

        Assert.Throws<InvalidOperationException>(() => prepared.Apply(project));
        Assert.Same(interveningRoot, track.Segments[0]);
        Assert.Equal(80, Assert.Single(root.Notes).Velocity);
        Assert.Equal(80, Assert.Single(interveningRoot.Notes).Velocity);
        Assert.Equal([note.Id], command.ResultSelectionIds);
    }

    private static void AssertDirectRootExchange(
        MidoraProject project,
        PureMidiTrack owner,
        ITimelineSelectionResultEditCommand command)
    {
        MidiSegment oldRoot = Assert.Single(owner.Segments);
        var oldNotes = oldRoot.Notes.Select(static value => (
            value.Id,
            value.StartTick,
            value.LengthTicks,
            value.Key,
            value.NoteOnVelocity)).ToArray();
        var oldEvents = oldRoot.ChannelEvents.Select(static value => (
            value.Id,
            value.Tick,
            value.Kind,
            value.Data1,
            value.Data2)).ToArray();
        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Same(oldRoot, Assert.Single(owner.Segments));
        MidoraId[] originalSelection = command.ResultSelectionIds.ToArray();

        prepared.Apply(project);
        MidiSegment replacementRoot = Assert.Single(owner.Segments);
        Assert.NotSame(oldRoot, replacementRoot);
        Assert.Equal(oldNotes, oldRoot.Notes.Select(static value => (
            value.Id,
            value.StartTick,
            value.LengthTicks,
            value.Key,
            value.NoteOnVelocity)));
        Assert.Equal(oldEvents, oldRoot.ChannelEvents.Select(static value => (
            value.Id,
            value.Tick,
            value.Kind,
            value.Data1,
            value.Data2)));
        MidoraId[] resultSelection = command.ResultSelectionIds.ToArray();

        prepared.Undo(project);
        Assert.Same(oldRoot, Assert.Single(owner.Segments));
        Assert.Equal(originalSelection, command.ResultSelectionIds);
        prepared.Apply(project);
        Assert.Same(replacementRoot, Assert.Single(owner.Segments));
        Assert.Equal(resultSelection, command.ResultSelectionIds);
    }

    private static void AssertSubVoiceRootExchange(
        MidoraProject project,
        EventInstrument owner,
        ITimelineSelectionResultEditCommand command)
    {
        SubVoice oldRoot = Assert.Single(owner.SubVoices);
        var oldEvents = oldRoot.Events.Select(static value => (
            value.Id,
            value.Kind,
            value.Tick,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue)).ToArray();
        IPreparedProjectEdit prepared = command.Prepare(project);
        Assert.Same(oldRoot, Assert.Single(owner.SubVoices));
        MidoraId[] originalSelection = command.ResultSelectionIds.ToArray();

        prepared.Apply(project);
        SubVoice replacementRoot = Assert.Single(owner.SubVoices);
        Assert.NotSame(oldRoot, replacementRoot);
        Assert.Equal(oldEvents, oldRoot.Events.Select(static value => (
            value.Id,
            value.Kind,
            value.Tick,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue)));
        MidoraId[] resultSelection = command.ResultSelectionIds.ToArray();

        prepared.Undo(project);
        Assert.Same(oldRoot, Assert.Single(owner.SubVoices));
        Assert.Equal(originalSelection, command.ResultSelectionIds);
        prepared.Apply(project);
        Assert.Same(replacementRoot, Assert.Single(owner.SubVoices));
        Assert.Equal(resultSelection, command.ResultSelectionIds);
    }

    private static DirectMidiNote AddDirectNote(
        MidoraProject project,
        MidiSegment segment,
        long tick,
        long gate,
        int key)
    {
        DirectMidiNote result = new(project)
        {
            StartTick = tick,
            LengthTicks = gate,
            Key = key,
            NoteOnVelocity = 80
        };
        segment.Notes.Add(result);
        return result;
    }
}
