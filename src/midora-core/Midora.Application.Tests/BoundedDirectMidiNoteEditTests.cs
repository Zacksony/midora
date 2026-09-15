using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiNoteEditTests
{
    [Fact]
    public void LargeMovePublishesSparseRootAndUndoRestoresOriginalRoot()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project, 12_000);
        MidoraId[] selected = segment.Notes.Take(5_000).Select(static x => x.Id).ToArray();
        using var context = BulkEditPreparationContext.Enter(project: project);
        var prepared = ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, selected, 100_000, 1).Prepare(project);
        Assert.Same(segment, track.Segments[0]);
        prepared.Apply(project);
        var replacement = track.Segments[0];
        Assert.NotSame(segment, replacement);
        Assert.Equal(12_000, replacement.Notes.Count);
        Assert.Equal(100_000, replacement.Notes[0].StartTick);
        Assert.Equal(61, replacement.Notes[0].Key);
        Assert.Equal(20_000, replacement.Notes[5_000].StartTick);
        Assert.Equal(23, replacement.Notes[0].NoteOffVelocity);
        Assert.Equal(0, segment.Notes[0].StartTick);
        Assert.IsType<BoundedDirectMidiNoteSource>(replacement.Notes.PagedSource);
        prepared.Undo(project);
        Assert.Same(segment, track.Segments[0]);
        prepared.Apply(project);
        Assert.Same(replacement, track.Segments[0]);
        Assert.True(context.Resources.PeakResidentBytes <= context.Resources.Budget.MaximumResidentBytes);
        Assert.True(context.Resources.PeakWorkingBytes <= context.Resources.Budget.MaximumWorkingBytes);
    }

    [Fact]
    public void LargeMoveHonorsIncumbentsWithoutRemovingUnrelatedImportedDuplicates()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project, 10_000);
        DirectMidiNote duplicate = new(project) { StartTick = 35_000, LengthTicks = 2, Key = 60 };
        segment.Notes.Add(duplicate);
        MidoraId[] selected = segment.Notes.Take(5_000).Select(static x => x.Id).ToArray();
        using var context = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, selected, 20_000, 0).Prepare(project);
        edit.Apply(project);
        var current = track.Segments[0];
        Assert.Equal(5_001, current.Notes.Count);
        Assert.Equal(segment.Notes[5_000].Id, current.Notes[0].Id);
        Assert.Equal(duplicate.Id, current.Notes[^1].Id);
        Assert.Equal(2, current.Notes.QueryValues(35_000, 35_001).Count());
        edit.Undo(project);
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(10_001, segment.Notes.Count);
    }

    [Fact]
    public void RepeatedLargeEditsAndInterveningPointEditKeepTheSameBaseline()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project, 12_000);
        MidoraId[] ids = segment.Notes.Take(5_000).Select(static x => x.Id).ToArray();
        using var context = BulkEditPreparationContext.Enter(project: project);
        ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 100_000, 1).Prepare(project).Apply(project);
        var first = Assert.IsType<BoundedDirectMidiNoteSource>(track.Segments[0].Notes.PagedSource);
        track.Segments[0].Notes[0].NoteOnVelocity = 49;
        ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 100_000, 1).Prepare(project).Apply(project);
        var second = Assert.IsType<BoundedDirectMidiNoteSource>(track.Segments[0].Notes.PagedSource);
        Assert.Same(first.BaselineIdentity, second.BaselineIdentity);
        Assert.Equal(200_000, second.GetNote(0).StartTick);
        Assert.Equal(49, second.GetNote(0).NoteOnVelocity);
        Assert.Equal(100_000, first.GetNote(0).StartTick);
        Assert.Equal(100, first.GetNote(0).NoteOnVelocity);
    }

    [Fact]
    public void CancelledPreparationLeavesRootAndAllocatorUnchanged()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project, 6_000);
        MidoraId[] ids = segment.Notes.Select(static x => x.Id).ToArray();
        long allocator = project.NextStableId;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var context = BulkEditPreparationContext.Enter(cancelled.Token, project: project);
        Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(segment.Id, ids).Prepare(project));
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(allocator, project.NextStableId);
        Assert.Equal(0, context.Resources.WorkingBytes);
    }

    private static (PureMidiTrack, MidiSegment) Create(MidoraProject project, int count)
    {
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Notes", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
        segment.Notes.AddRange(Enumerable.Range(0, count).Select(index => new DirectMidiNote(project)
        {
            StartTick = index * 4L, LengthTicks = 2, Key = 60,
            NoteOnVelocity = 100, NoteOffVelocity = 23, NoteOnOrder = index * 2, NoteOffOrder = index * 2 + 1
        }));
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return (track, segment);
    }
}
