using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class InstrumentChangeListTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WrappedListHasOneRowAndResolvesEveryMemberWithoutDuplicatingIt(bool subVoice)
    {
        using var project = new MidoraProject(480);
        var root = new MidiChannelRoot(project) { Name = "R" }; project.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(project) { Name = "T", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var segment = new MidiSegment(project) { LengthTicks = 960 }; track.Segments.Add(segment);
        var instrument = EventInstrumentLibrary.Create(project, "I"); var voice = instrument.SubVoices[0]; voice.Events.Clear();
        var command = subVoice ? ProjectDomainEditCommands.SetSubVoiceInstrumentChange(instrument.Id, voice.Id, 10, new(1, 2, 3))
            : ProjectDomainEditCommands.SetMidiInstrumentChange(segment.Id, 10, new(1, 2, 3));
        var prepared = command.Prepare(project); prepared.Apply(project);
        segment = project.PureMidiTracks.Single().Segments.Single(); voice = project.EventInstruments[0].SubVoices.Single();
        InstrumentChange group = (subVoice ? voice.InstrumentChanges : segment.InstrumentChanges).Values.Single();
        if (subVoice)
        {
            voice.Events.Add(TemplateEvent.Note(project, 20, 5, 60, 100));
            voice.Events.Add(TemplateEvent.Program(project, 30, 5)); // Explicit raw PC must stay raw.
        }
        else
        {
            segment.Notes.Add(new(project) { StartTick = 20, LengthTicks = 5, Key = 60, NoteOnVelocity = 100 });
            segment.ChannelEvents.Add(new(project) { Tick = 30, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 5 });
        }
        using var source = subVoice ? TimelineObjectListSource.CreateSubVoice(project, voice, instrument.Id)
            : TimelineObjectListSource.CreateMidi(project, segment);
        Assert.Equal(3, source.Count);
        var rows = await source.ReadRowsAsync(0, 3);
        Assert.Equal(new long[] { 10, 20, 30 }, rows.Select(r => r.Tick));
        Assert.True(rows[0].IsInstrumentChange); Assert.True(rows[1].IsNote); Assert.False(rows[2].IsInstrumentChange);
        Assert.Equal(group.Id, rows[0].InstrumentChange.Id);
        Assert.Equal(group.MemberIds.Order(), (await source.ReadSelectionAsync(0, 0)).Order());
        var ids = CompressedMidoraIdSet.Create(group.MemberIds);
        Assert.True(rows[0].IsSelected(ids)); Assert.False(rows[0].IsSelected(new HashSet<MidoraId> { group.ProgramEventId }));
        var owner = subVoice ? new TimelineObjectOwner(ProjectTimelineOwnerKind.SubVoice, voice.Id, instrument.Id)
            : new TimelineObjectOwner(ProjectTimelineOwnerKind.DirectMidiSegment, segment.Id);
        var partition = TimelineObjectSelection.Capture(project, owner, ids);
        Assert.False(partition.IsMixed); Assert.Empty(partition.Events); Assert.Empty(partition.Notes);
        Assert.Equal(ids.Count, partition.InstrumentMembers.Count); Assert.True(partition.CanCopyOrCut);
        // The old lazy projection must still be a frozen revision after raw edits.
        if (subVoice) voice.Events.Single(e => e.Id == group.ProgramEventId).Value = 99;
        else segment.ChannelEvents.Single(e => e.Id == group.ProgramEventId).Data1 = 99;
        Assert.Equal(3, (await source.ReadRowsAsync(0, 3))[0].InstrumentChange.Program);
        (prepared as IDisposable)?.Dispose();
    }
}
