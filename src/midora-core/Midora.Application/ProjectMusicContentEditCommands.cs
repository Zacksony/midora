using Midora.Domain;

namespace Midora.Application;

public enum LogicalParameterLaneRebindMode
{
    Clamp,
    DiscardInvalidValues
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateLogicalNote(
        MidoraId segmentId,
        MidoraId logicalNoteId,
        long startTick,
        long lengthTicks,
        int note,
        int velocity) =>
        Command("Change logical note", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalNote logicalNote = FindLogicalNote(segment.Segment, logicalNoteId);
            ValidateLogicalNote(startTick, lengthTicks, note, velocity);
            LogicalNoteValue old = new(
                logicalNote.StartTick,
                logicalNote.LengthTicks,
                logicalNote.Note,
                logicalNote.Velocity);
            LogicalNoteValue replacement = new(startTick, lengthTicks, note, velocity);
            IPreparedProjectEdit prepared = Prepared(
                old != replacement,
                TrackChange(segment.Track.Id),
                _ => SetLogicalNote(logicalNote, replacement),
                _ => SetLogicalNote(logicalNote, old));
            return old.StartTick == replacement.StartTick && old.Note == replacement.Note
                ? prepared
                : ResolveTargetedExactLogicalNoteCollisions(
                    prepared,
                    [new(segment.Segment, replacement.StartTick, replacement.Note)]);
        });

    public static IProjectEditCommand DeleteLogicalNote(
        MidoraId segmentId,
        MidoraId logicalNoteId) =>
        Command("Delete logical note", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalNote logicalNote = FindLogicalNote(segment.Segment, logicalNoteId);
            int originalIndex = segment.Segment.Notes.IndexOf(logicalNote);
            return Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                _ => RemoveRequired(segment.Segment.Notes, logicalNote, "Logical Note"),
                _ => InsertAt(
                    segment.Segment.Notes,
                    originalIndex,
                    logicalNote,
                    "Logical Note"));
        });

    public static IProjectEditCommand RebindLogicalParameterLane(
        MidoraId segmentId,
        MidoraId laneId,
        MidoraId parameterId,
        LogicalParameterLaneRebindMode mode,
        bool enumSemanticWarningAcknowledged) =>
        Command("Rebind logical parameter lane", project =>
        {
            using var preparation = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            MidoraId? instrumentId = project.ResolveEventInstrumentDefinitionId(segment.Track);
            EventInstrument instrument = instrumentId.HasValue
                ? FindEventInstrument(project, instrumentId.Value)
                : throw new InvalidOperationException(
                    "A Logical Parameter Lane can only be rebound on a bound Logical Track.");
            LogicalParameterDefinition definition = instrument.LogicalParameters
                .SingleOrDefault(value => value.Id == parameterId)
                ?? throw new ArgumentOutOfRangeException(nameof(parameterId));
            using var enumMetadata = preparation.Resources.ReserveWorking(checked((long)definition.EnumItems.Count * 2048));
            if (segment.Segment.ParameterLanes.Any(
                value => !ReferenceEquals(value, lane) && value.ParameterId == parameterId))
            {
                throw new InvalidOperationException(
                    "The Segment already has a Lane for the target Logical Parameter.");
            }

            LogicalParameterTarget target = ValidateRebindTarget(definition);
            if (target.Type == LogicalParameterType.Enum
                && !enumSemanticWarningAcknowledged)
            {
                throw new InvalidOperationException(
                    "Rebinding to an Enum requires acknowledging that integer compatibility does not preserve semantic meaning.");
            }
            if (lane.Points.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalParameterLaneRebind(project, segment, lane, parameterId, target, mode);
            CurvePoint[] oldPoints = lane.Points.ToArray();
            CurvePoint[] replacementPoints = oldPoints
                .Select(point => ConvertPoint(project, point, target, mode))
                .Where(point => point is not null)
                .Cast<CurvePoint>()
                .ToArray();
            MidoraId oldParameterId = lane.ParameterId;
            bool pointsChanged = oldPoints.Length != replacementPoints.Length
                || oldPoints.Where((point, index) => !ReferenceEquals(point, replacementPoints[index])).Any();
            return Prepared(
                oldParameterId != parameterId || pointsChanged,
                TrackChange(segment.Track.Id),
                _ => SetLane(lane, parameterId, replacementPoints),
                _ => SetLane(lane, oldParameterId, oldPoints));
        });

    public static IProjectEditCommand DeleteLogicalParameterLane(
        MidoraId segmentId,
        MidoraId laneId,
        bool deletionConfirmed) =>
        Command("Delete logical parameter lane", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (!deletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting Logical Parameter Lane data requires explicit confirmation.");
            }
            int originalIndex = segment.Segment.ParameterLanes.IndexOf(lane);
            return Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                _ => RemoveRequired(
                    segment.Segment.ParameterLanes,
                    lane,
                    "Logical Parameter Lane"),
                _ => InsertAt(
                    segment.Segment.ParameterLanes,
                    originalIndex,
                    lane,
                    "Logical Parameter Lane"));
        });

    public static IProjectEditCommand UpdateLogicalParameterPoint(
        MidoraId segmentId,
        MidoraId laneId,
        MidoraId pointId,
        long tick,
        double value,
        CurveInterpolation interpolation) =>
        Command("Change logical parameter point", project =>
        {
            interpolation = CurveInterpolation.Step;
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            CurvePoint point = FindCurvePoint(lane, pointId);
            if (tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tick));
            }
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (!Enum.IsDefined(interpolation))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            ValidatePointValue(definition, value, interpolation);
            CurvePoint replacement = new(project, point.Id, tick, value, interpolation);
            return ResolveTargetedExactLogicalParameterPointCollisions(Prepared(
                point != replacement,
                TrackChange(segment.Track.Id),
                _ => ReplaceRequired(lane.Points, point, replacement, "Logical Parameter point"),
                _ => ReplaceRequired(lane.Points, replacement, point, "Logical Parameter point")),
                lane,
                [replacement.Tick]);
        });

    public static IProjectEditCommand DeleteLogicalParameterPoint(
        MidoraId segmentId,
        MidoraId laneId,
        MidoraId pointId) =>
        Command("Delete logical parameter point", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            CurvePoint point = FindCurvePoint(lane, pointId);
            int originalIndex = lane.Points.IndexOf(point);
            return Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                _ => RemoveRequired(lane.Points, point, "Logical Parameter point"),
                _ => InsertAt(lane.Points, originalIndex, point, "Logical Parameter point"));
        });

    public static IProjectEditCommand UpdateEventInstrumentDescription(
        MidoraId eventInstrumentId,
        string? description) =>
        Command("Change event instrument description", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            string? validated = ProjectTextRules.ValidateDescription(description, nameof(description));
            string? oldDescription = instrument.Description;
            return Prepared(
                !string.Equals(oldDescription, validated, StringComparison.Ordinal),
                EventInstrumentPresentationChange(eventInstrumentId),
                _ => instrument.Description = validated,
                _ => instrument.Description = oldDescription);
        });

    public static IProjectEditCommand UpdateEventInstrumentColor(
        MidoraId eventInstrumentId,
        MidoraColor color) =>
        Command("Change event instrument color", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MidoraColor oldColor = instrument.Color;
            return Prepared(
                oldColor != color,
                EventInstrumentPresentationChange(eventInstrumentId),
                _ => instrument.Color = color,
                _ => instrument.Color = oldColor);
        });

    public static IProjectEditCommand UpdateEventInstrumentRootNote(
        MidoraId eventInstrumentId,
        int rootNote) =>
        Command("Change event instrument root note", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (rootNote is < 0 or > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(rootNote));
            }
            int oldRootNote = instrument.RootNote;
            return Prepared(
                oldRootNote != rootNote,
                EventInstrumentChange(eventInstrumentId),
                _ => instrument.RootNote = rootNote,
                _ => instrument.RootNote = oldRootNote);
        });

    private static LogicalNote FindLogicalNote(Segment segment, MidoraId logicalNoteId) =>
        segment.Notes.TryGetById(logicalNoteId, out LogicalNote? value) && value is not null
            ? value
            : throw new ArgumentOutOfRangeException(nameof(logicalNoteId));

    private static LogicalParameterLane FindLogicalParameterLane(Segment segment, MidoraId laneId) =>
        segment.ParameterLanes.SingleOrDefault(value => value.Id == laneId)
        ?? throw new ArgumentOutOfRangeException(nameof(laneId));

    private static CurvePoint FindCurvePoint(LogicalParameterLane lane, MidoraId pointId) =>
        lane.Points.TryGetById(pointId, out CurvePoint? value) && value is not null
            ? value
            : throw new ArgumentOutOfRangeException(nameof(pointId));

    private static LogicalParameterDefinition FindBoundLogicalParameter(
        MidoraProject project,
        LogicalTrack track,
        MidoraId parameterId)
    {
        MidoraId? instrumentId = project.ResolveEventInstrumentDefinitionId(track);
        EventInstrument instrument = instrumentId.HasValue
            ? FindEventInstrument(project, instrumentId.Value)
            : throw new InvalidOperationException(
                "A broken Logical Parameter Lane cannot be edited until it is rebound.");
        return instrument.LogicalParameters.SingleOrDefault(value => value.Id == parameterId)
            ?? throw new InvalidOperationException(
                "A broken Logical Parameter Lane cannot be edited until it is rebound.");
    }

    private static void ValidateLogicalNote(
        long startTick,
        long lengthTicks,
        int note,
        int velocity)
    {
        if (startTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startTick));
        }
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        }
        if (startTick > long.MaxValue - lengthTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lengthTicks),
                "The Logical Note end tick exceeds Int64.");
        }
        if (note is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(note));
        }
        if (velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity));
        }
    }

    private static void ValidatePointValue(
        LogicalParameterDefinition definition,
        double value,
        CurveInterpolation interpolation)
    {
        LogicalParameterTarget target = ValidateRebindTarget(definition);
        if (value < target.Minimum || value > target.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (target.Type == LogicalParameterType.Integer && value != Math.Truncate(value))
        {
            throw new ArgumentException(
                "An Integer Logical Parameter point must have an integer value.",
                nameof(value));
        }
        if (target.Type == LogicalParameterType.Enum
            && (value < int.MinValue || value > int.MaxValue
                || value != Math.Truncate(value)
                || !target.EnumValues!.Contains((int)value)))
        {
            throw new ArgumentException(
                "An Enum Logical Parameter point must use a defined enum value.",
                nameof(value));
        }
        if (interpolation != CurveInterpolation.Step)
        {
            throw new ArgumentException(
                "Logical Parameter points only support discrete Step changes.",
                nameof(interpolation));
        }
    }

    private static LogicalParameterTarget ValidateRebindTarget(
        LogicalParameterDefinition definition)
    {
        if (!Enum.IsDefined(definition.Type)
            || !double.IsFinite(definition.Minimum)
            || !double.IsFinite(definition.Maximum)
            || definition.Maximum < definition.Minimum)
        {
            throw new InvalidOperationException(
                "The target Logical Parameter definition has an invalid range or type.");
        }
        if (definition.Type == LogicalParameterType.Integer
            && (definition.Minimum != Math.Truncate(definition.Minimum)
                || definition.Maximum != Math.Truncate(definition.Maximum)))
        {
            throw new InvalidOperationException(
                "The target Integer Logical Parameter has a non-integer range.");
        }

        int[]? enumValues = null;
        if (definition.Type == LogicalParameterType.Enum)
        {
            enumValues = definition.EnumItems
                .Select((item, index) => definition.UsesExplicitEnumValues ? item.Value : index)
                .Distinct()
                .Order()
                .ToArray();
            if (enumValues.Length == 0
                || enumValues.Any(value => value < definition.Minimum || value > definition.Maximum))
            {
                throw new InvalidOperationException(
                    "The target Enum Logical Parameter has no valid enum value or an inconsistent range.");
            }
        }
        return new(definition.Type, definition.Minimum, definition.Maximum, enumValues);
    }

    private static CurvePoint? ConvertPoint(
        MidoraProject project,
        CurvePoint source,
        LogicalParameterTarget target,
        LogicalParameterLaneRebindMode mode)
    {
        double? value = ConvertLogicalParameterValue(source.Value, target, mode);
        if (value is not double converted) return null;
        const CurveInterpolation interpolation = CurveInterpolation.Step;
        if (converted == source.Value && interpolation == source.Interpolation) return source;
        return new(project, source.Id, source.Tick, converted, interpolation);
    }

    private static double? ConvertLogicalParameterValue(double value, LogicalParameterTarget target, LogicalParameterLaneRebindMode mode)
    {
        double converted = target.Type == LogicalParameterType.Double
            ? value
            : Math.Round(value, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(converted))
        {
            return mode == LogicalParameterLaneRebindMode.DiscardInvalidValues
                ? null
                : throw new InvalidOperationException(
                    "A Logical Parameter point cannot be clamped from a non-finite value.");
        }

        if (mode == LogicalParameterLaneRebindMode.DiscardInvalidValues)
        {
            if (converted < target.Minimum || converted > target.Maximum
                || target.Type == LogicalParameterType.Enum
                && (converted < int.MinValue || converted > int.MaxValue
                    || Array.BinarySearch(target.EnumValues!, (int)converted) < 0))
            {
                return null;
            }
        }
        else
        {
            converted = Math.Clamp(converted, target.Minimum, target.Maximum);
            if (target.Type == LogicalParameterType.Enum)
            {
                int[] candidates = target.EnumValues!;
                int low = 0, high = candidates.Length;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (candidates[middle] < converted) low = middle + 1; else high = middle;
                }
                converted = low == 0 ? candidates[0] : low == candidates.Length ? candidates[^1]
                    : converted - candidates[low - 1] <= candidates[low] - converted
                        ? candidates[low - 1] : candidates[low];
            }
        }
        return converted;
    }

    private static void SetLogicalNote(LogicalNote note, LogicalNoteValue value)
        => note.SetValues(
            value.StartTick,
            value.LengthTicks,
            value.Note,
            value.Velocity);

    private static void SetLane(
        LogicalParameterLane lane,
        MidoraId parameterId,
        IReadOnlyCollection<CurvePoint> points)
    {
        lane.ParameterId = parameterId;
        lane.Points.Clear();
        lane.Points.AddRange(points);
    }

    private readonly record struct LogicalNoteValue(
        long StartTick,
        long LengthTicks,
        int Note,
        int Velocity);

    private readonly record struct LogicalParameterTarget(
        LogicalParameterType Type,
        double Minimum,
        double Maximum,
        int[]? EnumValues);
}
