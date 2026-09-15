using Midora.Domain;

namespace Midora.Application;

public sealed record LogicalParameterPointEdit(long Tick, double Value);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpsertLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<LogicalParameterPointEdit> points) =>
        Command("Draw logical parameter points", project =>
        {
            ArgumentNullException.ThrowIfNull(points);
            if (points.Count == 0)
            {
                throw new ArgumentException(
                    "At least one Logical Parameter point is required.",
                    nameof(points));
            }

            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (points.Count >= BoundedPointThreshold || lane.Points.Count >= BoundedPointThreshold)
                return UpsertBoundedLogicalParameterPoints(segmentId, laneId, points).Prepare(project);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            LogicalParameterPointEdit[] edits = points
                .OrderBy(value => value.Tick)
                .ToArray();
            if (edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
            {
                throw new ArgumentException(
                    "Logical Parameter point ticks must be unique.",
                    nameof(points));
            }

            List<ExistingLogicalParameterPointEdit> replacements = [];
            List<LogicalParameterPointEdit> additions = [];
            const CurveInterpolation additionInterpolation = CurveInterpolation.Step;
            HashSet<long> editedTicks = edits.Select(static value => value.Tick).ToHashSet();
            HashSet<MidoraId> existingIds = lane.Points.CreateQuerySnapshot()
                .QueryTicks(editedTicks)
                .Select(static value => value.Id)
                .ToHashSet();
            Dictionary<long, CurvePoint> existingByTick = lane.Points
                .ResolveByIdsInCollectionOrder(existingIds)
                .GroupBy(static value => value.Tick)
                .ToDictionary(static values => values.Key, static values => values.Single());
            foreach (LogicalParameterPointEdit edit in edits)
            {
                if (edit.Tick < 0 || edit.Tick == long.MaxValue || !double.IsFinite(edit.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(points));
                }
                ValidatePointValue(definition, edit.Value, additionInterpolation);
                if (!existingByTick.TryGetValue(edit.Tick, out CurvePoint? existing))
                {
                    additions.Add(edit);
                }
                else
                {
                    ValidatePointValue(definition, edit.Value, CurveInterpolation.Step);
                    replacements.Add(new(existing, edit.Value));
                }
            }

            CurvePoint[]? created = null;
            CurvePoint[] replacementPoints = replacements
                .Select(value => new CurvePoint(
                    project,
                    value.Point.Id,
                    value.Point.Tick,
                    value.Value,
                    CurveInterpolation.Step))
                .ToArray();
            bool changesExisting = replacements
                .Select((value, index) => value.Point.Value != replacementPoints[index].Value)
                .Any(value => value);

            return Prepared(
                changesExisting || additions.Count != 0,
                TrackChange(segment.Track.Id),
                owner =>
                {
                    using IDisposable batch = lane.Points.BeginBatchChange();
                    lane.Points.ReplaceRange(
                        replacements.Select(static value => value.Point).ToArray(),
                        replacementPoints);
                    created ??= additions
                        .Select(value => new CurvePoint(
                            owner,
                            value.Tick,
                            value.Value,
                            CurveInterpolation.Step))
                        .ToArray();
                    InsertCurvePoints(lane.Points, created);
                },
                _ =>
                {
                    if (created is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Parameter points do not exist before the first Apply.");
                    }
                    using IDisposable batch = lane.Points.BeginBatchChange();
                    int removed = lane.Points.RemoveRange(created);
                    if (removed != created.Length)
                        throw new InvalidOperationException(
                            "The drawn Logical Parameter point set is no longer present.");
                    lane.Points.ReplaceRange(
                        replacementPoints,
                        replacements.Select(static value => value.Point).ToArray());
                });
        });

    private static void InsertCurvePoints(
        CurvePointCollection points,
        IReadOnlyCollection<CurvePoint> additions)
    {
        if (additions.Count == 0) return;

        CurvePoint[] ordered = additions
            .OrderBy(static value => value.Tick)
            .ThenBy(static value => value.Id.Value)
            .ToArray();
        List<CurvePointInsertionRun> runs = [];
        int existingIndex = 0;
        int additionIndex = 0;
        while (additionIndex < ordered.Length)
        {
            CurvePoint addition = ordered[additionIndex];
            while (existingIndex < points.Count
                && CompareCurvePointOrder(points[existingIndex], addition) < 0)
            {
                existingIndex++;
            }

            int runStart = additionIndex;
            while (additionIndex < ordered.Length
                && (existingIndex >= points.Count
                    || CompareCurvePointOrder(ordered[additionIndex], points[existingIndex]) < 0))
            {
                additionIndex++;
            }
            if (additionIndex == runStart)
            {
                throw new InvalidOperationException(
                    "The Logical Parameter point insertion set conflicts with existing ordering.");
            }
            runs.Add(new(
                existingIndex,
                ordered[runStart..additionIndex]));
        }

        for (int index = runs.Count - 1; index >= 0; index--)
        {
            CurvePointInsertionRun run = runs[index];
            points.InsertRange(run.Index, run.Points);
        }
    }

    private static int CompareCurvePointOrder(CurvePoint left, CurvePoint right)
    {
        int tick = left.Tick.CompareTo(right.Tick);
        return tick != 0 ? tick : left.Id.Value.CompareTo(right.Id.Value);
    }

    private sealed record ExistingLogicalParameterPointEdit(CurvePoint Point, double Value);

    private sealed record CurvePointInsertionRun(int Index, CurvePoint[] Points);
}
