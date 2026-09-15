using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct BoundedLanePoint(int Lane, MidoraId Id);
    private static ITimelineSelectionResultEditCommand BoundedQuantizeLogicalPoints(MidoraId segmentId,
        MidoraId? laneId, IReadOnlyCollection<MidoraId> pointIds, TimelineQuantizeGrid grid)
    {
        if (laneId is MidoraId singleLane)
            return BoundedLogicalPoints("Quantize logical parameter points", segmentId, singleLane,
                pointIds, BoundedPointOperation.Quantize, grid: grid);
        return ResultCommand("Quantize logical parameter points", (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            SegmentLocation location = FindSegment(project, segmentId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            long step = ResolveQuantizeStep(project, grid);
            var sources = location.Segment.ParameterLanes.Select(static lane => lane.Points.CreateQuerySnapshot()).ToArray();
            using var routed = BoundedEditSort.Sort(Route(), Comparer<BoundedLanePoint>.Create((a, b) =>
            {
                int c = a.Lane.CompareTo(b.Lane); return c == 0 ? a.Id.CompareTo(b.Id) : c;
            }), scope.Resources, token);
            using var orderedIds = new BoundedEditRecordStore<MidoraId>(scope.Resources);
            List<(int Lane, int Start, int Count)> groups = [];
            int previousLane = -1, start = 0, count = 0;
            foreach (var routedPoint in routed.ReadValues(token))
            {
                if (routedPoint.Lane != previousLane)
                {
                    if (count != 0) groups.Add((previousLane, start, count));
                    previousLane = routedPoint.Lane; start = orderedIds.Count; count = 0;
                }
                orderedIds.Add(routedPoint.Id, token); count++;
            }
            if (count != 0) groups.Add((previousLane, start, count));
            orderedIds.Seal();
            using var view = new BoundedImmutableValueSource<MidoraId>(orderedIds);
            Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, token);
            ProjectChangeSet changes = TrackChange(location.Track.Id);
            List<BoundedCurvePointPlan> plans = [];
            try
            {
                foreach (var group in groups)
                {
                    LogicalParameterLane lane = location.Segment.ParameterLanes[group.Lane];
                    var definition = FindBoundLogicalParameter(project, location.Track, lane.ParameterId);
                    var plan = BoundedCurvePointPlan.Transform(sources[group.Lane],
                        new BoundedReadOnlySlice<MidoraId>(view, group.Start, group.Count), _ => old =>
                        {
                            long tick = SegmentAbsoluteToLocal(SnapProjectAbsolute(SegmentLocalToAbsolute(old.Tick,
                                location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick), step),
                                location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick);
                            return tick < 0 ? null : old with { Tick = tick, Interpolation = CurveInterpolation.Step };
                        }, true, false, () => throw new InvalidOperationException("Quantize does not allocate IDs."),
                        point => ValidatePointValue(definition, point.Value, CurveInterpolation.Step), formalOrderWins: true);
                    plans.Add(plan);
                    var newLane = new LogicalParameterLane(project, lane.Id) { ParameterId = lane.ParameterId };
                    newLane.Points.AdoptEditedSnapshot(project, sources[group.Lane], plan.Changes, plan.Appended, token);
                    replacement.ParameterLanes[group.Lane] = newLane;
                    ProjectTimelineOwnerChangeSetBuilder.AddLogicalParameterPoints(changes, segmentId, lane, newLane, plan.AffectedIds);
                }
                IPreparedProjectEdit root = plans.Any(static p => p.HasChanges)
                    ? ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track, location.Segment,
                        replacement, changes, expectedSourceStamp: stamp)
                    : ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(project, location.Track,
                        location.Segment, changes, stamp);
                foreach (var plan in plans) root = plan.Own(root);
                return WithSelectionPublication(root,
                    CompactMidoraIdList.FreezeConcatenated(plans.Select(static p => p.OriginalSelection)),
                    CompactMidoraIdList.FreezeConcatenated(plans.Select(static p => p.ResultSelection)), publish);
            }
            finally { foreach (var plan in plans) plan.Dispose(); }

            IEnumerable<BoundedLanePoint> Route()
            {
                int processed = 0;
                foreach (MidoraId id in pointIds)
                {
                    scope.Checkpoint(processed++, pointIds.Count, TimelineEditPreparationPhase.ResolvingSelection);
                    int found = -1;
                    for (int lane = 0; lane < sources.Length; lane++)
                    {
                        if (!sources[lane].TryFindOrdinalById(id, out _)) continue;
                        if (found != -1) throw new ArgumentException("A point ID appears in more than one lane.");
                        found = lane;
                    }
                    if (id == default || found < 0) throw new ArgumentException("A selected point is not in this Segment.");
                    yield return new(found, id);
                }
            }
        });
    }
}
