using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit ForwardBoundedSelection(IProjectEditCommand command, MidoraProject project,
        SelectionPublisher publish, CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        IPreparedProjectEdit prepared = command is IProgressReportingProjectEditCommand reporting
            ? reporting.Prepare(project, token, progress) : command.Prepare(project);
        if (prepared is not IPreparedTimelineSelectionEdit { HasPreparedSelection: true } selection)
            throw new InvalidOperationException("The bounded Timeline command did not return its selection mapping.");
        return WithSelectionPublication(prepared, selection.PreparedSelection.OriginalSelectionIds,
            selection.PreparedSelection.ResultSelectionIds, publish);
    }
    private static IProjectEditCommand ChooseBoundedPointCommand(Func<MidoraProject, bool> useBounded,
        IProjectEditCommand bounded, IProjectEditCommand small) => new BoundedProjectEditCommand(bounded.Name,
        (project, token, progress) =>
        {
            var command = useBounded(project) ? bounded : small;
            return command is IProgressReportingProjectEditCommand reporting ? reporting.Prepare(project, token, progress)
                : command is ICancellableProjectEditCommand cancellable ? cancellable.Prepare(project, token) : command.Prepare(project);
        });
    private const int BoundedPointThreshold = 4096;

    private enum BoundedPointOperation { Move, Adjust, SetValue, Set, Delete, Flip, Scale, Batch, Quantize }

    private static ITimelineSelectionResultEditCommand BoundedLogicalPoints(string name, MidoraId segmentId,
        MidoraId laneId, IReadOnlyCollection<MidoraId> pointIds, BoundedPointOperation operation,
        long tickDelta = 0, double valueDelta = 0, long? tick = null, double? value = null,
        ProjectBatchValueEditMode mode = ProjectBatchValueEditMode.ExactSet, double factor = 1,
        bool duplicate = false, BatchEditExpressionProgram? program = null, TimelineQuantizeGrid? grid = null,
        CurveInterpolation? interpolation = null) =>
        ResultCommand(name, (project, publish, token, progress) =>
        {
            using BulkEditPreparationContext scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            ProjectTimelineOwnerSourceStamp stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(project, location.Track, lane.ParameterId);
            if (!double.IsFinite(valueDelta) || !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(valueDelta));
            if (operation == BoundedPointOperation.Adjust
                || operation == BoundedPointOperation.SetValue && mode != ProjectBatchValueEditMode.ExactSet)
                ValidateLogicalParameterRelativeDelta(definition, valueDelta);
            if (interpolation is not null && interpolation != CurveInterpolation.Step)
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            if (operation == BoundedPointOperation.Set && tick is null && value is null && interpolation is null)
                throw new ArgumentException("At least one point property must be provided.");
            if (operation == BoundedPointOperation.Scale) ValidateScaleFactor(factor);
            if (operation == BoundedPointOperation.Batch)
            {
                ArgumentNullException.ThrowIfNull(program);
                ValidatePointBatchProgram(program);
                ValidateDirectRange(program, BatchEditField.PointValue, definition.Minimum, definition.Maximum);
            }
            long step = operation == BoundedPointOperation.Quantize
                ? ResolveQuantizeStep(project, grid ?? throw new ArgumentNullException(nameof(grid))) : 1;
            long expectedId = project.NextStableId, nextId = expectedId;
            using BoundedCurvePointPlan plan = BoundedCurvePointPlan.Transform(lane.Points.CreateQuerySnapshot(), pointIds,
                rows =>
                {
                    long left = rows.Min(static row => row.Value.Tick);
                    long right = rows.Max(static row => row.Value.Tick);
                    Stopwatch clock = Stopwatch.StartNew();
                    return old =>
                    {
                        if (operation == BoundedPointOperation.Delete) return null;
                        CurvePointSnapshotValue result = operation switch
                        {
                            BoundedPointOperation.Move => old with { Tick = checked(old.Tick + tickDelta) },
                            BoundedPointOperation.Adjust => old with { Tick = checked(old.Tick + tickDelta), Value = old.Value + valueDelta },
                            BoundedPointOperation.SetValue => old with { Value = mode == ProjectBatchValueEditMode.ExactSet ? valueDelta : old.Value + valueDelta },
                            BoundedPointOperation.Set => old with { Tick = tick ?? old.Tick, Value = value ?? old.Value },
                            BoundedPointOperation.Flip => old with { Tick = checked(left + (right - old.Tick)) },
                            BoundedPointOperation.Scale => old with { Tick = ScaleTick(left, old.Tick, factor) },
                            BoundedPointOperation.Quantize => old with { Tick = SegmentAbsoluteToLocal(
                                SnapProjectAbsolute(SegmentLocalToAbsolute(old.Tick, location.Segment.ProjectStartTick,
                                    location.Segment.ContentOffsetTick), step), location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick) },
                            _ => old
                        };
                        if (operation == BoundedPointOperation.Batch)
                        {
                            BatchEditValues calculated = program!.Evaluate(new(0, old.Value, 0, 0, old.Tick,
                                checked(old.Tick - left)), clock, BatchExpressionTimeout);
                            if (RoundTickOrDiscard(calculated.Tick) is not long batchTick
                                || FallsBeforeProjectStart(location.Segment, batchTick)) return null;
                            result = old with { Tick = batchTick, Value = NormalizeLogicalParameterBatchValue(definition, calculated.PointValue) };
                        }
                        if (operation == BoundedPointOperation.Quantize && result.Tick < 0) return null;
                        return result with { Interpolation = CurveInterpolation.Step };
                    };
                },
                resolveCollisions: duplicate || operation is BoundedPointOperation.Move or BoundedPointOperation.Flip
                    or BoundedPointOperation.Scale or BoundedPointOperation.Batch or BoundedPointOperation.Quantize
                    || operation == BoundedPointOperation.Adjust && tickDelta != 0
                    || operation == BoundedPointOperation.Set && tick is not null,
                duplicate, () => MidoraId.FromSequence(checked(nextId++)),
                point =>
                {
                    if (point.Tick < 0 || point.Tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(point));
                    ValidatePointValue(definition, point.Value, point.Interpolation);
                }, formalOrderWins: operation == BoundedPointOperation.Quantize);
            return PublishBoundedLogicalPoints(project, location, lane, stamp, plan,
                expectedId, nextId, publish, operation == BoundedPointOperation.Batch);
        });

    internal static IProjectEditCommand AppendBoundedLogicalParameterPoints(MidoraId segmentId, MidoraId laneId,
        IEnumerable<CurvePointSnapshotValue> values) =>
        ResultCommand("Paste logical parameter points", (project, publish, token, progress) =>
        {
            using BulkEditPreparationContext scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            ProjectTimelineOwnerSourceStamp stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(project, location.Track, lane.ParameterId);
            long expectedId = project.NextStableId, nextId = expectedId;
            using BoundedCurvePointPlan plan = BoundedCurvePointPlan.Append(lane.Points.CreateQuerySnapshot(), values,
                () => MidoraId.FromSequence(checked(nextId++)), point =>
                {
                    if (point.Tick < 0 || point.Tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(values));
                    ValidatePointValue(definition, point.Value, CurveInterpolation.Step);
                });
            return PublishBoundedLogicalPoints(project, location, lane, stamp, plan, expectedId, nextId, publish, false);
        });

    private static IPreparedProjectEdit PublishBoundedLogicalPoints(MidoraProject project, SegmentLocation location,
        LogicalParameterLane lane, ProjectTimelineOwnerSourceStamp stamp, BoundedCurvePointPlan plan,
        long expectedId, long nextId, SelectionPublisher publish, bool expandLeft)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        if (!plan.HasChanges)
            return WithSelectionPublication(plan.Own(ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                project, location.Track, location.Segment, TrackChange(location.Track.Id), stamp)),
                plan.OriginalSelection, plan.ResultSelection, publish);
        scope.Checkpoint(0, 1, TimelineEditPreparationPhase.BuildingResult);
        Segment replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, scope.Token);
        LogicalParameterLane newLane = new(project, lane.Id) { ParameterId = lane.ParameterId };
        newLane.Points.AdoptEditedSnapshot(project, lane.Points.CreateQuerySnapshot(), plan.Changes, plan.Appended, scope.Token);
        replacement.ParameterLanes[location.Segment.ParameterLanes.IndexOf(lane)] = newLane;
        if (expandLeft && plan.MinimumResultTick != long.MaxValue)
        {
            var entry = new SegmentTransformEntry(location.Track, location.Segment, location.Index);
            SegmentWindowTransform window = PlanBatchWindow(entry, [plan.MinimumResultTick]);
            ValidateSegmentTransformWindows(project, [window]);
            SetWindow(replacement, window.Replacement);
        }
        ProjectChangeSet changes = TrackChange(location.Track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalParameterPoints(changes, location.Segment.Id, lane, newLane, plan.AffectedIds);
        IPreparedProjectEdit root = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track,
            location.Segment, replacement, changes, nextId == expectedId ? null : expectedId,
            nextId == expectedId ? null : nextId, stamp);
        scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
        return WithSelectionPublication(plan.Own(root), plan.OriginalSelection, plan.ResultSelection, publish);
    }
}
