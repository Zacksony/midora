using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiSplitJoinTests
{
    [Fact]
    public void LargeDirectSplitJoinPreservesEndpointPayloadFormalOrderAndUndo()
    {
        using MidoraProject project = new(480);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 100_000 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
        for (int index = 0; index < 4_100; index++)
            segment.Notes.Add(new DirectMidiNote(project)
            {
                StartTick = index * 10, LengthTicks = 2, Key = 60,
                NoteOnVelocity = 80, NoteOffVelocity = index % 128,
                NoteOnOrder = 10_000 - index, NoteOffOrder = 10_001 - index
            });
        var before = segment.Notes.Select(n => new DirectMidiNoteValue(n.Id, n.StartTick, n.LengthTicks, n.Key,
            n.NoteOnVelocity, n.NoteOffVelocity, n.NoteOnOrder, n.NoteOffOrder)).ToArray();
        var split = ProjectDomainEditCommands.SplitDirectMidiNotes(segment.Id, before.Select(v => v.Id).ToArray(),
            new() { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 1 });
        var prepared = split.Prepare(project);
        prepared.Apply(project);
        Assert.Equal(8_200, track.Segments[0].Notes.Count);
        Assert.Equal(8_200, split.ResultSelectionIds.Count);
        Assert.Equal(before[^1].Id, split.ResultSelectionIds[0]);
        var halves = track.Segments[0].Notes.QueryValues(0, 2).OrderBy(v => v.StartTick).ToArray();
        Assert.Equal([1L, 1L], halves.Select(v => v.LengthTicks));
        Assert.Equal((10_000L, 0L), (halves[0].NoteOnOrder, halves[0].NoteOffOrder));
        Assert.Equal((1L, 10_001L), (halves[1].NoteOnOrder, halves[1].NoteOffOrder));
        var join = ProjectDomainEditCommands.JoinDirectMidiNotes(segment.Id, split.ResultSelectionIds.ToArray(), new());
        var joined = join.Prepare(project);
        joined.Apply(project);
        Assert.Equal(4_100, track.Segments[0].Notes.Count);
        foreach (var old in before.Where((_, index) => index % 79 == 0))
        {
            Assert.True(track.Segments[0].Notes.TryGetById(old.Id, out var value));
            Assert.Equal(old.LengthTicks, value!.LengthTicks);
            Assert.Equal(old.NoteOffVelocity, value.NoteOffVelocity);
            Assert.Equal(old.NoteOnOrder, value.NoteOnOrder);
            Assert.Equal(old.NoteOffOrder, value.NoteOffOrder);
        }
        joined.Undo(project);
        Assert.Equal(8_200, track.Segments[0].Notes.Count);
        prepared.Undo(project);
        Assert.Same(segment, track.Segments[0]);
    }
}
