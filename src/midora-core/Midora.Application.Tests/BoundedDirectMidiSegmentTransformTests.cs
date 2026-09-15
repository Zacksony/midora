using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiSegmentTransformTests
{
    [Fact]
    public void SplitDenseMidiSegmentPreservesHiddenPartitionAndTruncatesCrossingNote()
    {
        using var project = new MidoraProject(480);
        var (track, source) = CreateDenseSegment(project);
        var crossing = new DirectMidiNote(project) { StartTick = 49, LengthTicks = 4, Key = 99, NoteOnVelocity = 70, NoteOffVelocity = 23 };
        source.Notes.Add(crossing);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.SplitMidiSegment(source.Id, 150).Prepare(project);
        Assert.Same(source, Assert.Single(track.Segments));
        edit.Apply(project);
        var parts = project.PureMidiTracks[0].Segments;
        var left = parts[0]; var right = parts[1];
        Assert.Equal((100L, 50L, 0L), (left.ProjectStartTick, left.LengthTicks, left.ContentOffsetTick));
        Assert.Equal((150L, 50L, 50L), (right.ProjectStartTick, right.LengthTicks, right.ContentOffsetTick));
        Assert.Equal(source.Notes.Count, left.Notes.Count + right.Notes.Count);
        Assert.Equal(1, left.Notes.Single(value => value.Id == crossing.Id).LengthTicks);
        Assert.Equal(23, left.Notes.Single(value => value.Id == crossing.Id).NoteOffVelocity);
        Assert.DoesNotContain(right.Notes, value => value.Id == crossing.Id);
        Assert.Equal(2, left.Notes.Count(value => value.StartTick == 0 && value.Key == 0));
        Assert.Equal(2, left.ChannelEvents.Count); Assert.Single(right.ChannelEvents);
        Assert.Equal(50000, Assert.Single(right.OpaqueEvents).Tick);
        Assert.IsType<BoundedDirectMidiNoteSource>(right.Notes.PagedSource);
        edit.Undo(project); Assert.Same(source, Assert.Single(project.PureMidiTracks[0].Segments));
        edit.Apply(project); Assert.Same(right, project.PureMidiTracks[0].Segments[1]);
    }

    [Fact]
    public void DuplicateDensePureMidiTrackPublishesPreparedScalarSegmentRoots()
    {
        using var project = new MidoraProject(480);
        var (track, source) = CreateDenseSegment(project);
        var progress = new RecordedProgress();
        using var scope = BulkEditPreparationContext.Enter(progress: progress, project: project);
        var edit = ProjectDomainEditCommands.DuplicatePureMidiTrack(track.Id).Prepare(project);
        long total = (long)source.Notes.Count + source.ChannelEvents.Count + source.OpaqueEvents.Count;
        Assert.Contains(progress.Values, value => value.Total == total && value.Completed == total);
        Assert.Single(project.PureMidiTracks);
        edit.Apply(project);
        var copy = project.PureMidiTracks.Single(value => value.Id != track.Id);
        var copied = Assert.Single(copy.Segments);
        Assert.Equal(track.MidiChannelRootId, copy.MidiChannelRootId);
        Assert.Equal(source.Notes.Count, copied.Notes.Count);
        Assert.IsType<BoundedDirectMidiNoteSource>(copied.Notes.PagedSource);
        Assert.Equal(2, copied.Notes.Count(value => value.StartTick == 0 && value.Key == 0));
        Assert.Equal(source.ChannelEvents.Count, copied.ChannelEvents.Count);
        Assert.Equal(source.OpaqueEvents[0].Payload.ToArray(), copied.OpaqueEvents[0].Payload.ToArray());
        edit.Undo(project); Assert.Same(track, Assert.Single(project.PureMidiTracks));
        edit.Apply(project); Assert.Same(copy, project.PureMidiTracks.Single(value => value.Id == copy.Id));
    }

    [Fact]
    public void LeftExtensionShiftsAllThreeRootsIncludingHiddenRecordsAndPreservesImportedDuplicates()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = CreateDenseSegment(project);
        var notes = segment.Notes.CreateObjectSource();
        var events = segment.ChannelEvents.CreateObjectSource();
        var opaque = segment.OpaqueEvents.CreateObjectSource();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.AdjustMidiSegmentEdges([segment.Id], -20, 0).Prepare(project);
        Assert.Same(segment, project.PureMidiTracks[0].Segments[0]);
        edit.Apply(project);
        var current = project.PureMidiTracks[0].Segments[0];
        Assert.Equal((80L, 120L, 0L), (current.ProjectStartTick, current.LengthTicks, current.ContentOffsetTick));
        Assert.Equal(notes.Count, current.Notes.Count);
        for (int i = 0; i < notes.Count; i++)
            Assert.Equal(notes.GetByOrdinal(i) with { StartTick = notes.GetByOrdinal(i).StartTick + 20 }, current.Notes.CreateObjectSource().GetByOrdinal(i));
        Assert.Equal(events.Count, current.ChannelEvents.Count);
        for (int i = 0; i < events.Count; i++)
            Assert.Equal(events.GetByOrdinal(i) with { Tick = events.GetByOrdinal(i).Tick + 20 }, current.ChannelEvents.CreateObjectSource().GetByOrdinal(i));
        Assert.Equal(opaque.Count, current.OpaqueEvents.Count);
        for (int i = 0; i < opaque.Count; i++)
        {
            var old = opaque.GetByOrdinal(i); var changed = current.OpaqueEvents.CreateObjectSource().GetByOrdinal(i);
            Assert.Equal(old.Id, changed.Id); Assert.Equal(old.Tick + 20, changed.Tick); Assert.Equal(old.Payload.ToArray(), changed.Payload.ToArray());
        }
        edit.Undo(project); Assert.Same(segment, project.PureMidiTracks[0].Segments[0]);
        edit.Apply(project); Assert.Same(current, project.PureMidiTracks[0].Segments[0]);
    }

    [Fact]
    public void WholeSegmentDuplicateUsesPagedScalarRootsAndPreservesAllFormalRecords()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = CreateDenseSegment(project);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var prepared = ProjectDomainEditCommands.DuplicateMidiSegments([segment.Id], segment.Id, track.Id, 1000).Prepare(project);
        Assert.Single(track.Segments);
        prepared.Apply(project);
        var current = project.PureMidiTracks[0].Segments;
        var copy = current.Single(value => value.Id != segment.Id);
        Assert.Equal(1000, copy.ProjectStartTick);
        Assert.IsType<BoundedDirectMidiNoteSource>(copy.Notes.PagedSource);
        Assert.Equal(segment.Notes.Count, copy.Notes.Count);
        Assert.Equal(segment.ChannelEvents.Count, copy.ChannelEvents.Count);
        Assert.Equal(segment.OpaqueEvents.Count, copy.OpaqueEvents.Count);
        var copiedNotes = copy.Notes.CreateObjectSource(); var oldNotes = segment.Notes.CreateObjectSource();
        for (int i = 0; i < copiedNotes.Count; i++)
        {
            var old = oldNotes.GetByOrdinal(i); var value = copiedNotes.GetByOrdinal(i);
            Assert.NotEqual(old.Id, value.Id); Assert.Equal(old with { Id = value.Id }, value);
        }
        prepared.Undo(project); Assert.Single(project.PureMidiTracks[0].Segments);
        prepared.Apply(project); Assert.Same(copy, project.PureMidiTracks[0].Segments.Single(value => value.Id == copy.Id));
    }

    [Fact]
    public void CanceledWholeSegmentDuplicationLeavesSourceUntouched()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = CreateDenseSegment(project);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, project: project);
        Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.DuplicateMidiSegments([segment.Id], segment.Id, track.Id, 1000).Prepare(project));
        Assert.Same(segment, Assert.Single(track.Segments));
        Assert.Equal(6001, segment.Notes.Count);
    }

    [Fact]
    public void CancellationDuringScalarCopyDiscardsPrivateRootsAndProvisionalPages()
    {
        using var project = new MidoraProject(480);
        var (track, source) = CreateDenseSegment(project);
        long originalNextId = project.NextStableId;
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnRecords(cancellation);
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, progress, project: project);
        using (var lease = scope.Resources.BeginResourceLease())
            Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.DuplicatePureMidiTrack(track.Id).Prepare(project));
        Assert.Same(track, Assert.Single(project.PureMidiTracks));
        Assert.Same(source, Assert.Single(track.Segments));
        Assert.Equal(originalNextId, project.NextStableId);
        Assert.Equal(0, scope.Resources.ResidentBytes);
        Assert.Equal(0, scope.Resources.SpillBytes);
    }

    private static (PureMidiTrack Track, MidiSegment Segment) CreateDenseSegment(MidoraProject project)
    {
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { ProjectStartTick = 100, LengthTicks = 100 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new DirectMidiNote(project)
        { StartTick = i * 4L, LengthTicks = 2, Key = i % 128, NoteOnVelocity = 70, NoteOffVelocity = 31, NoteOnOrder = i * 2L, NoteOffOrder = i * 2L + 1 }));
        segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 2, Key = 0, NoteOnVelocity = 20 });
        segment.ChannelEvents.AddRange(Enumerable.Range(0, 3).Select(i => new DirectMidiChannelEvent(project)
        { Tick = i == 2 ? 50000 : 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 70 + i, Order = i }));
        segment.OpaqueEvents.Add(new(project) { Tick = 50000, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [42], Order = 17 });
        return (track, segment);
    }

    private sealed class RecordedProgress : IProgress<TimelineEditPreparationProgress>
    {
        public List<TimelineEditPreparationProgress> Values { get; } = [];
        public void Report(TimelineEditPreparationProgress value) => Values.Add(value);
    }

    private sealed class CancelOnRecords(CancellationTokenSource cancellation) : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value)
        { if (value.Total > 4096 && value.Completed >= 256) cancellation.Cancel(); }
    }

    [Fact]
    public void WholeSegmentFlipKeepsHiddenContentAndUndoRestoresAllThreeRoots()
    {
        using var project = new MidoraProject(480);
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { ProjectStartTick = 100, ContentOffsetTick = 10, LengthTicks = 24000 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id)); track.Segments.Add(segment);
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new DirectMidiNote(project)
        { StartTick = 10 + i * 4L, LengthTicks = 2, Key = i % 128, NoteOnVelocity = 70, NoteOffVelocity = 31, NoteOnOrder = i * 2L, NoteOffOrder = i * 2L + 1 }));
        var hidden = new DirectMidiNote(project) { StartTick = 40000, LengthTicks = 100, Key = 80, NoteOnVelocity = 44 };
        segment.Notes.Add(hidden);
        segment.ChannelEvents.AddRange(Enumerable.Range(0, 6000).Select(i => new DirectMidiChannelEvent(project)
        { Tick = 10 + i * 4L, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = i % 128, Order = i }));
        segment.OpaqueEvents.AddRange(Enumerable.Range(0, 12).Select(i => new OpaqueMidiEvent(project)
        { Tick = 10 + i * 4L, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [(byte)i], Order = i }));
        var originalNotes = segment.Notes.CreateObjectSource();
        var originalEvents = segment.ChannelEvents.CreateObjectSource();
        using var context = BulkEditPreparationContext.Enter(project: project);
        var prepared = ProjectDomainEditCommands.FlipMidiSegmentsHorizontal([segment.Id], SegmentSelectionTransformScope.ExposedContentOnly).Prepare(project);
        Assert.Same(segment, project.PureMidiTracks[0].Segments[0]);
        prepared.Apply(project);
        var changed = project.PureMidiTracks[0].Segments[0];
        Assert.NotSame(segment, changed);
        Assert.IsType<BoundedDirectMidiNoteSource>(changed.Notes.PagedSource);
        Assert.IsType<BoundedDirectMidiEventSource>(changed.ChannelEvents.PagedSource);
        Assert.IsType<BoundedOpaqueMidiSource>(changed.OpaqueEvents.PagedSource);
        for (int i = 0; i < 6000; i++)
        {
            var old = originalNotes.GetByOrdinal(i);
            var value = changed.Notes[i];
            Assert.Equal(old.Id, value.Id);
            Assert.Equal(24020 - old.StartTick - old.LengthTicks, value.StartTick);
            Assert.Equal(old.NoteOffVelocity, value.NoteOffVelocity);
            Assert.Equal(24019 - originalEvents.GetByOrdinal(i).Tick, changed.ChannelEvents[i].Tick);
        }
        Assert.Equal(40000, changed.Notes[^1].StartTick);
        Assert.Equal(new byte[] { 0 }, changed.OpaqueEvents[0].Payload.ToArray());
        prepared.Undo(project);
        Assert.Same(segment, project.PureMidiTracks[0].Segments[0]);
        prepared.Apply(project);
        Assert.Same(changed, project.PureMidiTracks[0].Segments[0]);
        Assert.True(context.Resources.PeakResidentBytes <= context.Resources.Budget.MaximumResidentBytes);
    }

    [Fact]
    public void OneEditedEventCanOverwriteThousandsOfImportedIncumbentsWithBoundedStorage()
    {
        using var project = new MidoraProject(480);
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = 1000 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var moved = new DirectMidiChannelEvent(project) { Tick = 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 77 };
        segment.ChannelEvents.Add(moved);
        segment.ChannelEvents.AddRange(Enumerable.Range(0, 10000).Select(i => new DirectMidiChannelEvent(project)
        { Tick = 20, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = i % 128 }));
        using var context = BulkEditPreparationContext.Enter(project: project);
        var prepared = ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segment.Id, [moved.Id], 20, 0, 0, false).Prepare(project);
        prepared.Apply(project);
        var value = Assert.Single(track.Segments[0].ChannelEvents);
        Assert.Equal(moved.Id, value.Id); Assert.Equal(77, value.Data2);
        prepared.Undo(project); Assert.Same(segment, track.Segments[0]); Assert.Equal(10001, segment.ChannelEvents.Count);
        Assert.True(context.Resources.PeakResidentBytes <= context.Resources.Budget.MaximumResidentBytes);
    }

    [Fact]
    public void PrefetchedDirectIdsAndOpaqueRangesBecomeCacheOnlyReadable()
    {
        using var project = new MidoraProject(480);
        var root = new MidiChannelRoot(project) { Name = "Root" };
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = 30000 };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        segment.Notes.AddRange(Enumerable.Range(0, 5000).Select(i => new DirectMidiNote(project)
        { StartTick = i * 4L, LengthTicks = 2, Key = 60, NoteOnVelocity = 70 }));
        segment.OpaqueEvents.AddRange(Enumerable.Range(0, 5000).Select(i => new OpaqueMidiEvent(project)
        { Tick = i * 4L, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [(byte)(i % 128)] }));
        var noteIds = segment.Notes.Take(3).Select(static x => x.Id).ToHashSet();
        var eventIds = segment.OpaqueEvents.Take(3).Select(static x => x.Id).ToHashSet();
        ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, noteIds, 1, 0).Prepare(project).Apply(project);
        ProjectDomainEditCommands.AdjustOpaqueMidiEvents(segment.Id, eventIds, 1, false).Prepare(project).Apply(project);
        var current = track.Segments[0];
        var notes = Assert.IsType<BoundedDirectMidiNoteSource>(current.Notes.PagedSource);
        notes.PrefetchNotesByIds(noteIds, default);
        List<DirectMidiNoteSourceMatch> noteMatches = [];
        Assert.True(notes.TryQueryCachedNotesByIds(noteIds, noteMatches)); Assert.Equal(3, noteMatches.Count);
        var opaque = Assert.IsType<BoundedOpaqueMidiSource>(current.OpaqueEvents.PagedSource);
        opaque.PrefetchOpaqueEvents(0, 12, default);
        List<OpaqueMidiEventValue> eventMatches = [];
        Assert.True(opaque.TryQueryCachedOpaqueEvents(0, 12, eventMatches)); Assert.Equal(3, eventMatches.Count);
        opaque.PrefetchOpaqueEventsByIds(eventIds, default);
        List<OpaqueMidiEventSourceMatch> byId = [];
        Assert.True(opaque.TryQueryCachedOpaqueEventsByIds(eventIds, byId)); Assert.Equal(3, byId.Count);
    }
}
