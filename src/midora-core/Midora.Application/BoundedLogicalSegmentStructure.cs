using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static long LogicalSegmentRecordCount(Segment value) =>
        1L + value.Notes.Count + value.ParameterLanes.Sum(lane => 1L + lane.Points.Count);

    private static bool RequiresBoundedSegmentEdgeShift(IReadOnlyList<SegmentEdgeEdit> edits) =>
        edits.Where(edit => edit.ContentShift != 0).Sum(edit => LogicalSegmentRecordCount(edit.Location.Segment)) >= BoundedNoteThreshold;

    private static bool RequiresBoundedSegmentCopies(IReadOnlyList<SegmentBatchPlacement> placements) =>
        placements.Sum(value => LogicalSegmentRecordCount(value.Source.Segment)) >= BoundedNoteThreshold;

    private static IPreparedProjectEdit PrepareBoundedSegmentEdgeShift(MidoraProject project, IReadOnlyList<SegmentEdgeEdit> edits)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        LogicalTrack[] tracks = edits.Select(value => value.Location.Track).Distinct().ToArray();
        using var metadata = scope.Resources.ReserveWorking(checked(tracks.Sum(track => (long)track.Segments.Count) * 256L
            + edits.Sum(edit => (long)edit.Location.Segment.ParameterLanes.Count) * 256L + edits.Count * 128L));
        var originals = tracks.Select(track => (Track: track, Old: track.Segments.ToArray(),
            Stamps: track.Segments.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray())).ToArray();
        var progress = new LogicalStructureProgress(scope, edits.Sum(edit => edit.ContentShift != 0
            ? LogicalSegmentRecordCount(edit.Location.Segment) : 1L + edit.Location.Segment.ParameterLanes.Count));
        Dictionary<Segment, Segment> replacements = [];
        foreach (var edit in edits)
        {
            scope.Token.ThrowIfCancellationRequested();
            Segment source = edit.Location.Segment;
            Segment target = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, source, scope.Token);
            progress.Advance(1L + source.ParameterLanes.Count);
            if (edit.ContentShift != 0)
            {
                var notes = FreezeClipboardSource(progress.Read(source.Notes.CreateQuerySnapshot().EnumerateAll())
                    .Select(value => value with { StartTick = checked(value.StartTick + edit.ContentShift) }), static value => value.Id);
                target.Notes.Clear(); target.Notes.AdoptSource(project, notes, scope.Token);
                for (int index = 0; index < source.ParameterLanes.Count; index++)
                {
                    var points = FreezeClipboardSource(progress.Read(source.ParameterLanes[index].Points.CreateQuerySnapshot().EnumerateAll())
                        .Select(value => value with { Tick = checked(value.Tick + edit.ContentShift) }), static value => value.Id);
                    var targetLane = target.ParameterLanes[index];
                    targetLane.Points.Clear(); targetLane.Points.AdoptSource(project, points, scope.Token);
                }
            }
            SetWindow(target, edit.Replacement);
            replacements.Add(source, target);
        }
        progress.Complete();
        return new BoundedLogicalTrackSegmentRoots(project, replacements, TrackChange(tracks.Select(track => track.Id).ToArray()), originals, preserveOrder: true);
    }

    private static IPreparedProjectEdit PrepareBoundedSegmentCopies(MidoraProject project, IReadOnlyList<SegmentBatchPlacement> placements)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        LogicalTrack[] tracks = placements.SelectMany(value => new[] { value.Source.Track, value.TargetTrack }).Distinct().ToArray();
        using var metadata = scope.Resources.ReserveWorking(checked(tracks.Sum(track => (long)track.Segments.Count) * 256L
            + placements.Sum(value => (long)value.Source.Segment.ParameterLanes.Count) * 256L + placements.Count * 256L));
        var original = tracks.Select(track => (Track: track, Old: track.Segments.ToArray(),
            Stamps: track.Segments.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray())).ToArray();
        Dictionary<LogicalTrack, List<Segment>> additions = [];
        long firstId = project.NextStableId, nextId = firstId;
        var progress = new LogicalStructureProgress(scope, placements.Sum(value => LogicalSegmentRecordCount(value.Source.Segment)));
        foreach (var placement in placements)
        {
            scope.Token.ThrowIfCancellationRequested();
            Segment source = placement.Source.Segment;
            Segment copy = CloneBoundedLogicalSegment(project, source, Allocate, scope, progress);
            copy.ProjectStartTick = placement.NewProjectStartTick;
            if (!additions.TryGetValue(placement.TargetTrack, out var list)) additions.Add(placement.TargetTrack, list = []);
            list.Add(copy);
        }
        var roots = original.Select(entry =>
        {
            var next = entry.Old.ToList();
            if (additions.TryGetValue(entry.Track, out var extra))
                MergeClipboardSegments(next, extra, static value => value.ProjectStartTick, static value => value.Id);
            return (entry.Track, entry.Old, New: next.ToArray(), entry.Stamps);
        }).ToArray();
        progress.Complete();
        return new BoundedLogicalSegmentCopyRoots(project, roots,
            TrackChange(placements.Select(value => value.TargetTrack.Id).Distinct().ToArray()), firstId, nextId);

        MidoraId Allocate() { var id = new MidoraId(nextId); nextId = checked(nextId + 1); return id; }
    }

    private static Segment CloneBoundedLogicalSegment(MidoraProject project, Segment source, Func<MidoraId> allocate,
        BulkEditPreparationContext scope, LogicalStructureProgress progress)
    {
        progress.Advance();
        Segment copy = new(project, allocate()) { ProjectStartTick = source.ProjectStartTick,
            LengthTicks = source.LengthTicks, ContentOffsetTick = source.ContentOffsetTick };
        var notes = FreezeClipboardSource(FirstClipboardValues(progress.Read(source.Notes.CreateQuerySnapshot().EnumerateAll()),
            static value => value.StartTick, static value => value.Note).Select(value => value with { Id = allocate() }), static value => value.Id);
        copy.Notes.AdoptSource(project, notes, scope.Token);
        foreach (var lane in source.ParameterLanes)
        {
            scope.Token.ThrowIfCancellationRequested();
            progress.Advance();
            LogicalParameterLane target = new(project, allocate()) { ParameterId = lane.ParameterId };
            var points = FreezeClipboardSource(FirstClipboardValues(progress.Read(lane.Points.CreateQuerySnapshot().EnumerateAll()),
                static value => value.Tick, static _ => 0).Select(value => value with { Id = allocate(), Interpolation = CurveInterpolation.Step }),
                static value => value.Id);
            target.Points.AdoptSource(project, points, scope.Token);
            copy.ParameterLanes.Add(target);
        }
        return copy;
    }

    // Count real input records (including lightweight owner/lane records), not
    // elapsed timer ticks. One counter spans every Segment of a Track copy.
    private sealed class LogicalStructureProgress(BulkEditPreparationContext scope, long total)
    {
        private long _completed;
        public void Advance(long count = 1)
        {
            long before = _completed;
            _completed = checked(_completed + count);
            if (_completed == total || before / 256 != _completed / 256)
                scope.Checkpoint(_completed, total, TimelineEditPreparationPhase.Planning);
        }
        public IEnumerable<T> Read<T>(IEnumerable<T> source)
        {
            foreach (T value in source) { Advance(); yield return value; }
        }
        public void Complete()
        {
            if (_completed != total) throw new InvalidOperationException("The structural edit did not process its declared source extent.");
            scope.Checkpoint(total, total, TimelineEditPreparationPhase.BuildingResult);
        }
    }

    private sealed class BoundedLogicalSegmentCopyRoots(MidoraProject owner,
        (LogicalTrack Track, Segment[] Old, Segment[] New, ProjectTimelineOwnerSourceStamp[] Stamps)[] roots,
        ProjectChangeSet changes, long firstId, long nextId) : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private bool _applied, _published;
        private readonly (LogicalTrack Track, List<Segment> Old, List<Segment> New)[] _lists = roots.Select(root =>
            (root.Track, root.Track.Segments, root.New.ToList())).ToArray();
        public bool HasChanges => true;
        public ProjectChangeSet Changes => changes;
        public void ValidateForPublication(MidoraProject project) => Validate(project, false);
        private void Validate(MidoraProject project, bool after)
        {
            if (!ReferenceEquals(project, owner)) throw new InvalidOperationException("This Segment copy belongs to another project.");
            if (!_published && project.NextStableId != firstId)
                throw new InvalidOperationException("The allocator changed before Segment copies were published.");
            foreach (var root in roots)
            {
                var expected = after ? root.New : root.Old;
                if (!project.Tracks.Contains(root.Track) || root.Track.Segments.Count != expected.Length)
                    throw new InvalidOperationException("The Segment owner changed before publication.");
                for (int index = 0; index < expected.Length; index++)
                    if (!ReferenceEquals(expected[index], root.Track.Segments[index])
                        || (!_published && !root.Stamps[index].Matches(expected[index])))
                        throw new InvalidOperationException("The Segment source changed before publication.");
            }
        }
        public void Apply(MidoraProject project)
        {
            if (_applied) throw new InvalidOperationException("The Segment copy is already applied.");
            Validate(project, false);
            if (!_published) project.AdvanceNextStableId(nextId);
            foreach (var root in _lists) root.Track.Segments = root.New;
            _published = _applied = true;
        }
        public void Undo(MidoraProject project)
        {
            if (!_applied) return;
            Validate(project, true);
            foreach (var root in _lists) root.Track.Segments = root.Old;
            _applied = false;
        }
    }
}
