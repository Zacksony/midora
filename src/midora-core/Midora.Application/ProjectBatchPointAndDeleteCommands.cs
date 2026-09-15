using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MoveLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Move logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Move, tickDelta)
        :
        Command("Move logical parameter points", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Move logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Move, tickDelta).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value,
                CurveInterpolation.Step)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return ResolveTargetedExactLogicalParameterPointCollisions(
                PrepareCurvePointReplacementBatch(
                    segment.Track.Id,
                    lane.Points,
                    selected,
                    replacement),
                lane,
                replacement.Select(static value => value.Tick));
        });

    public static IProjectEditCommand AdjustLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta,
        double valueDelta) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Adjust logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Adjust, tickDelta, valueDelta)
        :
        Command("Adjust logical parameter points", project =>
        {
            if (!double.IsFinite(valueDelta))
            {
                throw new ArgumentOutOfRangeException(nameof(valueDelta));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Adjust logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Adjust, tickDelta, valueDelta).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            ValidateLogicalParameterRelativeDelta(definition, valueDelta);
            CurvePoint[] replacement = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value + valueDelta,
                CurveInterpolation.Step)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            IPreparedProjectEdit prepared = PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
            return tickDelta == 0
                ? prepared
                : ResolveTargetedExactLogicalParameterPointCollisions(
                    prepared,
                    lane,
                    replacement.Select(static value => value.Tick));
        });

    public static IProjectEditCommand DuplicateLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta,
        double valueDelta) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Duplicate logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Adjust,
            tickDelta, valueDelta, duplicate: true)
        :
        Command("Duplicate logical parameter points", project =>
        {
            if (!double.IsFinite(valueDelta))
            {
                throw new ArgumentOutOfRangeException(nameof(valueDelta));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Duplicate logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Adjust, tickDelta, valueDelta, duplicate: true).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            ValidateLogicalParameterRelativeDelta(definition, valueDelta);
            CurvePoint[] replacements = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value + valueDelta,
                CurveInterpolation.Step)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacements);

            int insertionIndex = lane.Points.Count;
            CurvePoint[]? copies = null;
            return ResolveTargetedExactLogicalParameterPointCollisions(Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                owner =>
                {
                    copies ??= replacements.Select(value => new CurvePoint(
                        owner,
                        value.Tick,
                        value.Value,
                        value.Interpolation)).ToArray();
                    lane.Points.AddRange(copies);
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Parameter point copies do not exist before the first Apply.");
                    }
                    RemoveCurvePointBatch(lane.Points, copies, "Logical Parameter point copy");
                }), lane, replacements.Select(static value => value.Tick));
        });

    public static IProjectEditCommand SetLogicalParameterPointValues(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        double value,
        ProjectBatchValueEditMode mode) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Change logical parameter point values", segmentId, laneId, pointIds,
            BoundedPointOperation.SetValue, valueDelta: value, mode: mode)
        :
        Command("Change logical parameter point values", project =>
        {
            if (!Enum.IsDefined(mode) || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(
                    !Enum.IsDefined(mode) ? nameof(mode) : nameof(value));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Change logical parameter point values", segmentId, laneId, pointIds, BoundedPointOperation.SetValue, valueDelta: value, mode: mode).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            if (mode != ProjectBatchValueEditMode.ExactSet) ValidateLogicalParameterRelativeDelta(definition, value);
            CurvePoint[] replacement = selected.Select(item => new CurvePoint(
                project,
                item.Point.Id,
                item.Point.Tick,
                mode == ProjectBatchValueEditMode.ExactSet
                    ? value
                    : item.Point.Value + value,
                CurveInterpolation.Step)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    private static void ValidateLogicalParameterRelativeDelta(LogicalParameterDefinition definition, double delta)
    {
        if (definition.Type == LogicalParameterType.Enum && delta != 0)
            throw new InvalidOperationException("Enum parameters support exact values, not relative value changes.");
    }

    public static IProjectEditCommand SetLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long? tick = null,
        double? value = null,
        CurveInterpolation? interpolation = null) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Set logical parameter points", segmentId, laneId, pointIds,
            BoundedPointOperation.Set, tick: tick, value: value, interpolation: interpolation)
        :
        Command("Set logical parameter points", project =>
        {
            if (tick is null && value is null && interpolation is null)
            {
                throw new ArgumentException(
                    "At least one Logical Parameter point value must be provided.",
                    nameof(tick));
            }
            if (value is double pointValue && !double.IsFinite(pointValue))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (interpolation is CurveInterpolation interpolationValue
                && interpolationValue != CurveInterpolation.Step)
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Set logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Set, tick: tick, value: value, interpolation: interpolation).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(item => new CurvePoint(
                project,
                item.Point.Id,
                tick ?? item.Point.Tick,
                value ?? item.Point.Value,
                CurveInterpolation.Step)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            IPreparedProjectEdit prepared = PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
            return tick is null
                ? prepared
                : ResolveTargetedExactLogicalParameterPointCollisions(
                    prepared,
                    lane,
                    replacement.Select(static value => value.Tick));
        });

    public static IProjectEditCommand DeleteLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Delete logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Delete)
        :
        Command("Delete logical parameter points", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Delete logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Delete).Prepare(project);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            return PrepareCurvePointDeleteBatch(
                TrackChange(segment.Track.Id),
                lane.Points,
                selected,
                "Logical Parameter point");
        });

    public static IProjectEditCommand DeleteValueCurvePoints(
        MidoraId eventInstrumentId, MidoraId subVoiceId, MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        Command("Delete value curve points", project => PrepareBoundedValueCurveTransform(
            project, eventInstrumentId, subVoiceId, curveId, pointIds, static _ => null));

    public static IProjectEditCommand AdjustValueCurvePoints(
        MidoraId eventInstrumentId, MidoraId subVoiceId, MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds, long tickDelta, double valueDelta) =>
        Command("Adjust value curve points", project =>
        {
            if (!double.IsFinite(valueDelta)) throw new ArgumentOutOfRangeException(nameof(valueDelta));
            return PrepareBoundedValueCurveTransform(project, eventInstrumentId, subVoiceId, curveId, pointIds,
                point => point with { Tick = checked(point.Tick + tickDelta), Value = point.Value + valueDelta });
        });

    public static IProjectEditCommand SetValueCurvePoints(
        MidoraId eventInstrumentId, MidoraId subVoiceId, MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds, long? tick = null, double? value = null,
        CurveInterpolation? interpolation = null) =>
        Command("Set value curve points", project =>
        {
            if (tick is null && value is null && interpolation is null)
                throw new ArgumentException("At least one Value Curve point value must be provided.", nameof(tick));
            if (value is double pointValue && !double.IsFinite(pointValue))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (interpolation is CurveInterpolation mode && !Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            return PrepareBoundedValueCurveTransform(project, eventInstrumentId, subVoiceId, curveId, pointIds,
                point => point with { Tick = tick ?? point.Tick, Value = value ?? point.Value,
                    Interpolation = interpolation ?? point.Interpolation });
        });

    public static IProjectEditCommand DeleteTemplateEvents(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds) =>
        templateEventIds.Count >= BoundedPointThreshold
        ? BoundedTemplatePoints("Delete template events", eventInstrumentId, subVoiceId,
            templateEventIds, BoundedPointOperation.Delete)
        :
        Command("Delete template events", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return BoundedTemplatePoints("Delete template events", eventInstrumentId, subVoiceId, templateEventIds, BoundedPointOperation.Delete).Prepare(project);
            ArgumentNullException.ThrowIfNull(templateEventIds);
            HashSet<MidoraId> requested = ValidateBatchIds(
                templateEventIds,
                nameof(templateEventIds),
                "Template Event");
            IndexedTemplateEvent[] selected = voice.Events
                .ResolveByIdsWithIndicesInCollectionOrder(requested)
                .Select(static value => new IndexedTemplateEvent(value.Value, value.Index))
                .ToArray();
            if (selected.Length != requested.Count)
            {
                throw new ArgumentException(
                    "Every selected Template Event must belong to the target SubVoice.",
                    nameof(templateEventIds));
            }
            TemplateEvent[] values = selected.Select(static value => value.Event).ToArray();
            Action? restore = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => restore = voice.Events.RemoveRangeWithUndo(values),
                _ =>
                {
                    (restore ?? throw new InvalidOperationException(
                        "Template Events do not have a pending removal to restore."))();
                    restore = null;
                });
        });

    public static IProjectEditCommand DeleteConductorEvents(IReadOnlyCollection<MidoraId> eventIds) =>
        DeleteConductorSelection(eventIds);

    private static IPreparedProjectEdit PrepareCurvePointReplacementBatch(
        MidoraId trackId,
        CurvePointCollection points,
        SelectedCurvePoint[] selected,
        CurvePoint[] replacement) =>
        Prepared(
            selected.Where((value, index) => value.Point != replacement[index]).Any(),
            TrackChange(trackId),
            _ => ReplaceCurvePointBatch(points, selected.Select(value => value.Point).ToArray(), replacement),
            _ => ReplaceCurvePointBatch(points, replacement, selected.Select(value => value.Point).ToArray()));

    private static void ReplaceCurvePointBatch(
        CurvePointCollection points,
        CurvePoint[] expected,
        CurvePoint[] replacement)
    {
        points.ReplaceRange(expected, replacement);
    }

    private static void ReplaceRequired(
        CurvePointCollection points,
        CurvePoint expected,
        CurvePoint replacement,
        string objectName)
    {
        try
        {
            points.ReplaceRange([expected], [replacement]);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"The {objectName} is no longer present at its expected position.",
                exception);
        }
    }

    private static void ValidateLogicalParameterPointBatch(
        LogicalParameterDefinition definition,
        IReadOnlyCollection<CurvePoint> allPoints,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        IReadOnlyCollection<CurvePoint> replacement)
    {
        foreach (CurvePoint point in replacement)
        {
            if (point.Tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            ValidatePointValue(definition, point.Value, point.Interpolation);
        }
        // Exact same-lane tick collisions are resolved transactionally by
        // ExactTimelineCollisionPolicy. Duration overlap is not involved here.
    }

    private static void ValidateValueCurvePointBatch(
        ValueCurve curve,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        IReadOnlyCollection<CurvePoint> replacement)
    {
        (double minimum, double maximum) = ValueCurveTargetRange(curve.Target);
        foreach (CurvePoint point in replacement)
        {
            if (point.Tick < 0 || point.Tick == long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            if (!double.IsFinite(point.Value) || !Enum.IsDefined(point.Interpolation))
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            if ((point.Value < minimum || point.Value > maximum)
                && curve.TargetSettings.Overflow == MappingOverflow.Fail)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
        }
        HashSet<MidoraId> selectedIds = selected.Select(value => value.Point.Id).ToHashSet();
        HashSet<long> ticks = replacement.Select(value => value.Tick).ToHashSet();
        bool collidesWithUnselected = ticks.Count != 0
            && curve.Points.CreateQuerySnapshot()
                .QueryTicks(ticks)
                .Any(value => !selectedIds.Contains(value.Id));
        if (ticks.Count != replacement.Count
            || collidesWithUnselected)
        {
            throw new InvalidOperationException(
                "The Value Curve point batch would create duplicate point ticks.");
        }
    }

    private static SelectedCurvePoint[] SelectCurvePoints(
        CurvePointCollection points,
        IReadOnlyCollection<MidoraId> pointIds)
    {
        ArgumentNullException.ThrowIfNull(pointIds);
        HashSet<MidoraId> requested = ValidateBatchIds(
            pointIds,
            nameof(pointIds),
            "Curve Point");
        SelectedCurvePoint[] selected = points
            .ResolveByIdsWithIndicesInCollectionOrder(requested)
            .Select(static value => new SelectedCurvePoint(value.Value, value.Index))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every selected Curve Point must belong to the target Curve.",
                nameof(pointIds));
        }
        return selected;
    }

    private static IPreparedProjectEdit PrepareCurvePointDeleteBatch(
        ProjectChangeSet changes,
        CurvePointCollection points,
        SelectedCurvePoint[] selected,
        string objectName)
    {
        Action? restore = null;
        return Prepared(
            hasChanges: true,
            changes,
            _ => restore = RemoveCurvePointsWithUndo(points, selected, objectName),
            _ =>
            {
                (restore ?? throw new InvalidOperationException(
                    $"{objectName} values do not have a pending removal to restore."))();
                restore = null;
            });
    }

    private static Action RemoveCurvePointsWithUndo(
        CurvePointCollection points,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        string objectName)
    {
        CurvePoint[] values = selected.Select(static value => value.Point).ToArray();
        return points.RemoveRangeWithUndo(values);
    }

    private static void RemoveCurvePointBatch(
        CurvePointCollection points,
        IReadOnlyCollection<CurvePoint> values,
        string objectName)
    {
        int removed = points.RemoveRange(values);
        if (removed != values.Count)
            throw new InvalidOperationException($"The {objectName} batch is no longer fully present.");
    }

    private static HashSet<MidoraId> ValidateBatchIds(
        IReadOnlyCollection<MidoraId> values,
        string parameterName,
        string objectName)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException(
                $"At least one {objectName} must be selected.",
                parameterName);
        }
        HashSet<MidoraId> result = [];
        foreach (MidoraId value in values)
        {
            if (value == default || !result.Add(value))
            {
                throw new ArgumentException(
                    $"{objectName} selections must contain distinct valid stable IDs.",
                    parameterName);
            }
        }
        return result;
    }


    private readonly record struct SelectedCurvePoint(CurvePoint Point, int Index);
}
