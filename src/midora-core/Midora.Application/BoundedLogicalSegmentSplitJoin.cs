using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedLogicalSegmentSplit(MidoraProject project, SegmentLocation location, long splitTick)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        using var metadata = scope.Resources.ReserveWorking(checked(location.Track.Segments.Count * 256L + location.Segment.ParameterLanes.Count * 384L));
        Segment source = location.Segment;
        Segment[] old = location.Track.Segments.ToArray();
        var stamps = old.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray();
        long firstId = project.NextStableId, nextId = firstId;
        var progress = new LogicalStructureProgress(scope, LogicalSegmentRecordCount(source) + source.Notes.Count);
        progress.Advance();
        long leftLength = checked(splitTick - source.ProjectStartTick), contentSplit = checked(source.ContentOffsetTick + leftLength);
        Segment left = new(project, source.Id) { ProjectStartTick = source.ProjectStartTick, LengthTicks = leftLength, ContentOffsetTick = source.ContentOffsetTick };
        Segment right = new(project, Allocate()) { ProjectStartTick = splitTick, LengthTicks = source.LengthTicks - leftLength, ContentOffsetTick = contentSplit };
        var notes = source.Notes.CreateQuerySnapshot();
        left.Notes.AdoptSource(project, FreezeClipboardSource(progress.Read(notes.EnumerateAll()).Where(value => value.StartTick < contentSplit)
            .Select(value => value with { LengthTicks = Math.Min(value.LengthTicks, contentSplit - value.StartTick) }), static value => value.Id), scope.Token);
        right.Notes.AdoptSource(project, FreezeClipboardSource(progress.Read(notes.EnumerateAll()).Where(value => value.StartTick >= contentSplit), static value => value.Id), scope.Token);
        foreach (var lane in source.ParameterLanes)
        {
            scope.Token.ThrowIfCancellationRequested();
            progress.Advance();
            LogicalParameterLane leftLane = new(project, lane.Id) { ParameterId = lane.ParameterId };
            LogicalParameterLane rightLane = new(project, Allocate()) { ParameterId = lane.ParameterId };
            using var ordered = SortLogicalStructurePoints(progress.Read(lane.Points.CreateQuerySnapshot().EnumerateAll()), scope);
            CurvePointSnapshotValue? previous = null; bool exact = false;
            foreach (var row in ordered)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (row.Value.Tick < contentSplit) previous = row.Value;
                else { exact = row.Value.Tick == contentSplit; break; }
            }
            CurvePointSnapshotValue? initial = previous is { } held && !exact
                ? new(Allocate(), contentSplit, held.Value, CurveInterpolation.Step) : null;
            leftLane.Points.AdoptSource(project, FreezeClipboardSource(ordered.Where(row => row.Value.Tick < contentSplit)
                .Select(row => row.Value with { Interpolation = CurveInterpolation.Step }), static value => value.Id), scope.Token);
            rightLane.Points.AdoptSource(project, FreezeClipboardSource(RightPoints(), static value => value.Id), scope.Token);
            left.ParameterLanes.Add(leftLane); right.ParameterLanes.Add(rightLane);
            IEnumerable<CurvePointSnapshotValue> RightPoints()
            {
                if (initial is { } value) yield return value;
                foreach (var row in ordered)
                    if (row.Value.Tick >= contentSplit) yield return row.Value with { Interpolation = CurveInterpolation.Step };
            }
        }
        var next = old.ToList(); next.RemoveAt(location.Index); next.Insert(location.Index, right); next.Insert(location.Index, left);
        progress.Complete();
        return new BoundedLogicalSegmentCopyRoots(project, [(location.Track, old, next.ToArray(), stamps)], TrackChange(location.Track.Id), firstId, nextId);
        MidoraId Allocate() { var id = new MidoraId(nextId); nextId = checked(nextId + 1); return id; }
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalSegmentJoin(MidoraProject project, SegmentLocation first, SegmentLocation second)
    {
        if (first.Segment.Id == second.Segment.Id) throw new ArgumentException("A Segment cannot be joined with itself.");
        Segment left = first.Segment.ProjectStartTick <= second.Segment.ProjectStartTick ? first.Segment : second.Segment;
        Segment right = ReferenceEquals(left, first.Segment) ? second.Segment : first.Segment;
        if (left.ProjectRange.EndTick > right.ProjectStartTick) throw new ArgumentException("Only non-overlapping Segments can be joined.");
        long start = left.ProjectStartTick, end = right.ProjectRange.EndTick;
        EnsureNoSegmentOverlap(first.Track, first.Segment, start, checked(end - start), second.Segment);
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        using var metadata = scope.Resources.ReserveWorking(checked(first.Track.Segments.Count * 256L + (left.ParameterLanes.Count + right.ParameterLanes.Count) * 384L));
        Segment[] old = first.Track.Segments.ToArray(); var stamps = old.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray();
        var progress = new LogicalStructureProgress(scope, LogicalSegmentRecordCount(left) + LogicalSegmentRecordCount(right));
        progress.Advance(2);
        long leftOrigin = checked(left.ProjectStartTick - left.ContentOffsetTick), rightOrigin = checked(right.ProjectStartTick - right.ContentOffsetTick);
        long origin = Math.Min(leftOrigin, rightOrigin);
        Segment result = new(project, left.Id) { ProjectStartTick = start, LengthTicks = checked(end - start), ContentOffsetTick = checked(start - origin) };
        var notes = FirstClipboardValues(MoveNotes(left, leftOrigin).Concat(MoveNotes(right, rightOrigin)), static value => value.StartTick, static value => value.Note);
        result.Notes.AdoptSource(project, FreezeClipboardSource(notes, static value => value.Id), scope.Token);
        var lanes = left.ParameterLanes.Select(lane => (Lane: lane, Origin: leftOrigin))
            .Concat(right.ParameterLanes.Select(lane => (Lane: lane, Origin: rightOrigin))).GroupBy(value => value.Lane.ParameterId).OrderBy(group => group.Key);
        foreach (var group in lanes)
        {
            scope.Token.ThrowIfCancellationRequested();
            progress.Advance(group.Count());
            LogicalParameterLane target = new(project, group.First().Lane.Id) { ParameterId = group.Key };
            var winners = FirstClipboardValues(group.SelectMany(entry => progress.Read(entry.Lane.Points.CreateQuerySnapshot().EnumerateAll())
                .Select(value => value with { Tick = checked(checked(entry.Origin + value.Tick) - origin), Interpolation = CurveInterpolation.Step })),
                static value => value.Tick, static _ => 0);
            using var ordered = SortLogicalStructurePoints(winners, scope);
            target.Points.AdoptSource(project, FreezeClipboardSource(ordered.Select(row => row.Value), static value => value.Id), scope.Token);
            result.ParameterLanes.Add(target);
        }
        var next = old.ToList(); next.RemoveAt(Math.Max(first.Index, second.Index)); next.RemoveAt(Math.Min(first.Index, second.Index));
        next.Insert(Math.Min(first.Index, second.Index), result);
        progress.Complete();
        return new BoundedLogicalSegmentCopyRoots(project, [(first.Track, old, next.ToArray(), stamps)], TrackChange(first.Track.Id), project.NextStableId, project.NextStableId);
        IEnumerable<LogicalNoteSnapshotValue> MoveNotes(Segment source, long contentOrigin) => progress.Read(source.Notes.CreateQuerySnapshot().EnumerateAll())
            .Select(value => value with { StartTick = checked(checked(contentOrigin + value.StartTick) - origin) });
    }

    private static BoundedEditRecordStore<ClipboardOrderedValue<CurvePointSnapshotValue>> SortLogicalStructurePoints(
        IEnumerable<CurvePointSnapshotValue> points, BulkEditPreparationContext scope) =>
        BoundedEditSort.Sort(points.Select((value, index) => new ClipboardOrderedValue<CurvePointSnapshotValue>(index, value)),
            Comparer<ClipboardOrderedValue<CurvePointSnapshotValue>>.Create((a, b) =>
            { int result = a.Value.Tick.CompareTo(b.Value.Tick); return result == 0 ? a.Ordinal.CompareTo(b.Ordinal) : result; }), scope.Resources, scope.Token);
}
