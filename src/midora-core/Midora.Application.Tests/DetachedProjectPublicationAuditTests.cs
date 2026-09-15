using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class DetachedProjectPublicationAuditTests
{
    [Fact]
    public void StaleRootBeforeFirstPublicationDoesNotReportASecondRollbackFailure()
    {
        using MidoraProject project = new(192);
        var track = new LogicalTrack(project) { Name = "Original" };
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        using StagedProjectEdit staged = document.PrepareEdit(new SequentialProjectEditCommand(
            "Rename track", [_ => ProjectDomainEditCommands.RenameLogicalTrack(track.Id, "Renamed")]));
        List<LogicalTrack> replacementDirectory = [track];
        project.Tracks = replacementDirectory;
        Assert.Throws<InvalidOperationException>(() => document.ExecutePrepared(staged));
        Assert.Same(replacementDirectory, project.Tracks);
        Assert.Same(track, Assert.Single(project.Tracks));
        Assert.Equal("Original", track.Name);
        Assert.Empty(document.History);
    }

    [Fact]
    public void SequentialSelectionOriginalDoesNotIncludeObjectsCreatedByEarlierChildren()
    {
        using MidoraProject project = new(192);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote note = new(project) { StartTick = 0, LengthTicks = 120, Note = 60, Velocity = 100 };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        using StagedProjectEdit staged = document.PrepareEdit(new SequentialProjectEditCommand(
            "Split then quantize", [
                _ => ProjectDomainEditCommands.SplitLogicalNotes(segment.Id, [note.Id],
                    new NoteSplitOptions { Mode = NoteSplitMode.FixedPieceLength, FixedPieceLengthTicks = 40 }),
                draft => ProjectDomainEditCommands.QuantizeLogicalNotes(segment.Id,
                    draft.Tracks.Single().Segments.Single().Notes.Select(value => value.Id).ToArray(),
                    new NoteQuantizeOptions(TimelineQuantizeGrid.FromCustomTicks(1)))
            ]));
        Assert.NotNull(staged.PreparedSelection);
        Assert.Equal([note.Id], staged.PreparedSelection.OriginalSelectionIds);
        Assert.Equal(3, staged.PreparedSelection.ResultSelectionIds.Count);
        document.ExecutePrepared(staged);
        Assert.Equal(3, project.Tracks.Single().Segments.Single().Notes.Count);
        document.Undo();
        Assert.Same(segment, project.Tracks.Single().Segments.Single());
        Assert.Same(note, Assert.Single(segment.Notes));
    }

    [Fact]
    public void DetachedDamagedTrackDeletionPublishesPlaceholderDirectoryAndUndoRestoresRoot()
    {
        using MidoraProject project = new(192);
        var placeholder = new DamagedProjectObject(project.AllocateStableId(), "Broken track",
            "logical-tracks/broken.pb", "Broken", 0);
        project.DamagedLogicalTracks.Add(placeholder);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, placeholder.Id));
        var original = project.DamagedLogicalTracks;
        var originalOrder = project.ArrangementTracks;
        long nextId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        using StagedProjectEdit staged = document.PrepareEdit(new SequentialProjectEditCommand(
            "Delete broken track", [_ => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(placeholder.Id)]));
        Assert.Same(placeholder, Assert.Single(project.DamagedLogicalTracks));
        document.ExecutePrepared(staged);
        Assert.Empty(project.DamagedLogicalTracks);
        Assert.Empty(project.ArrangementTracks);
        var published = project.DamagedLogicalTracks;
        document.Undo();
        Assert.Same(original, project.DamagedLogicalTracks);
        Assert.Same(originalOrder, project.ArrangementTracks);
        Assert.Same(placeholder, Assert.Single(project.DamagedLogicalTracks));
        document.Redo();
        Assert.Same(published, project.DamagedLogicalTracks);
        Assert.Empty(project.DamagedLogicalTracks);
        Assert.Equal(nextId, project.NextStableId);
    }

    [Fact]
    public void DetachedDamagedRootDeletionPublishesCascadingPlaceholderDirectories()
    {
        using MidoraProject project = new(192);
        MidoraId rootId = project.AllocateStableId();
        var retained = new PureMidiTrack(project) { Name = "Retained", MidiChannelRootId = rootId };
        var child = new DamagedProjectObject(project.AllocateStableId(), "Broken child",
            "pure-midi-tracks/broken.pb", "Broken", 1, rootId);
        var root = new DamagedProjectObject(rootId, "Broken root", "midi-channel-roots/broken.pb",
            "Broken", 0, ChildIds: Array.AsReadOnly(new[] { retained.Id, child.Id }));
        project.PureMidiTracks.Add(retained);
        project.DamagedPureMidiTracks.Add(child);
        project.DamagedMidiChannelRoots.Add(root);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, retained.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, child.Id));
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        document.Execute(new SequentialProjectEditCommand("Delete broken root",
            [_ => ProjectDomainEditCommands.DeleteDamagedMidiChannelRoot(rootId)]));
        Assert.Empty(project.DamagedMidiChannelRoots);
        Assert.Empty(project.DamagedPureMidiTracks);
        Assert.Empty(project.PureMidiTracks);
        Assert.Empty(project.ArrangementTracks);
        document.Undo();
        Assert.Same(root, Assert.Single(project.DamagedMidiChannelRoots));
        Assert.Same(child, Assert.Single(project.DamagedPureMidiTracks));
        Assert.Same(retained, Assert.Single(project.PureMidiTracks));
        Assert.Equal([retained.Id, child.Id], project.ArrangementTracks.Select(item => item.TrackId));
    }
}
