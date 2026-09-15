using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct LogicalPointDrawValue(long Tick, double Value);
    private readonly record struct LogicalPointDrawReplacement(int Ordinal, CurvePointSnapshotValue Value);

    private static IProjectEditCommand UpsertBoundedLogicalParameterPoints(MidoraId segmentId, MidoraId laneId,
        IReadOnlyCollection<LogicalParameterPointEdit> points) =>
        new BoundedProjectEditCommand("Draw logical parameter points", (project, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            if (points.Count == 0) throw new ArgumentException("At least one Logical Parameter point is required.", nameof(points));
            var location = FindSegment(project, segmentId);
            var lane = FindLogicalParameterLane(location.Segment, laneId);
            var definition = FindBoundLogicalParameter(project, location.Track, lane.ParameterId);
            var source = lane.Points.CreateQuerySnapshot();
            source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, token);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            using var ordered = BoundedEditSort.Sort(points.Select(static p => new LogicalPointDrawValue(p.Tick, p.Value)),
                Comparer<LogicalPointDrawValue>.Create(static (a, b) => a.Tick.CompareTo(b.Tick)), scope.Resources, token);
            using var additions = new BoundedEditRecordStore<CurvePointSnapshotValue>(scope.Resources);
            using var replacements = new BoundedEditRecordStore<LogicalPointDrawReplacement>(scope.Resources);
            using var affected = new BoundedEditRecordStore<MidoraId>(scope.Resources);
            long previousTick = -1, initialId = project.NextStableId, nextId = initialId;
            foreach (var edit in ordered.ReadValues(token))
            {
                if (edit.Tick == previousTick) throw new ArgumentException("Logical Parameter point ticks must be unique.", nameof(points));
                if (edit.Tick < 0 || edit.Tick == long.MaxValue || !double.IsFinite(edit.Value)) throw new ArgumentOutOfRangeException(nameof(points));
                previousTick = edit.Tick;
                ValidatePointValue(definition, edit.Value, CurveInterpolation.Step);
                using var matching = source.EnumerateExactTick(edit.Tick).GetEnumerator();
                if (matching.MoveNext())
                {
                    var old = matching.Current;
                    if (matching.MoveNext()) throw new InvalidOperationException("A Logical Parameter lane contains duplicate ticks.");
                    if (old.Value == edit.Value && old.Interpolation == CurveInterpolation.Step) continue;
                    if (!source.TryFindOrdinalById(old.Id, out int ordinal)) throw new InvalidOperationException("Missing Logical Parameter point ordinal.");
                    replacements.Add(new(ordinal, old with { Value = edit.Value, Interpolation = CurveInterpolation.Step }), token);
                    affected.Add(old.Id, token);
                }
                else
                {
                    var value = new CurvePointSnapshotValue(MidoraId.FromSequence(checked(nextId++)), edit.Tick, edit.Value, CurveInterpolation.Step);
                    additions.Add(value, token); affected.Add(value.Id, token);
                }
            }
            additions.Seal(); replacements.Seal(); affected.Seal();
            if (affected.Count == 0) return ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                project, location.Track, location.Segment, TrackChange(location.Track.Id), stamp);
            using var byOrdinal = BoundedEditSort.Sort(replacements, Comparer<LogicalPointDrawReplacement>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)), scope.Resources, token);
            using var splices = new BoundedEditRecordStore<TimelineValueSplice>(scope.Resources);
            using var values = new BoundedEditRecordStore<CurvePointSnapshotValue>(scope.Resources);
            using var newValues = additions.ReadValues(token).GetEnumerator();
            using var updated = byOrdinal.ReadValues(token).GetEnumerator();
            bool haveNew = newValues.MoveNext(), haveUpdate = updated.MoveNext();
            // Preserve InsertCurvePoints' formal insertion order, even if an old
            // lane is not tick-sorted following earlier move or clipboard edits.
            for (int ordinal = 0; ordinal < source.Count && (haveNew || haveUpdate); ordinal++)
            {
                scope.Checkpoint(ordinal, source.Count, TimelineEditPreparationPhase.BuildingResult);
                var old = source.GetByOrdinal(ordinal);
                int first = values.Count;
                while (haveNew && Compare(newValues.Current, old) < 0)
                {
                    values.Add(newValues.Current, token); haveNew = newValues.MoveNext();
                }
                bool changed = haveUpdate && updated.Current.Ordinal == ordinal;
                bool appendTail = ordinal == source.Count - 1 && haveNew;
                if (values.Count != first || changed || appendTail)
                {
                    values.Add(changed ? updated.Current.Value : old, token);
                    if (changed) haveUpdate = updated.MoveNext();
                    if (appendTail)
                        do { values.Add(newValues.Current, token); haveNew = newValues.MoveNext(); } while (haveNew);
                    splices.Add(new(ordinal, first, values.Count - first), token);
                }
            }
            if (source.Count == 0)
                while (haveNew) { values.Add(newValues.Current, token); haveNew = newValues.MoveNext(); }
            splices.Seal(); values.Seal();
            List<IDisposable> owned = [];
            try
            {
                var spliceSource = Freeze(splices);
                var valueSource = new BoundedIndexedTimelineValueSource<CurvePointSnapshotValue>(Freeze(values), static v => v.Id, scope.Resources, token);
                owned.Add(valueSource);
                var replacement = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, token);
                var newLane = new LogicalParameterLane(project, lane.Id) { ParameterId = lane.ParameterId };
                if (source.Count == 0) newLane.Points.AdoptSource(project, valueSource, token);
                else newLane.Points.AdoptSplicedSnapshot(project, source, spliceSource, valueSource, token);
                replacement.ParameterLanes[location.Segment.ParameterLanes.IndexOf(lane)] = newLane;
                var changes = TrackChange(location.Track.Id);
                ProjectTimelineOwnerChangeSetBuilder.AddLogicalParameterPoints(changes, location.Segment.Id, lane, newLane, affected);
                var edit = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track, location.Segment,
                    replacement, changes, initialId == nextId ? null : initialId, initialId == nextId ? null : nextId, stamp);
                return new OwnedBoundedPreparedEdit(edit, owned);
            }
            catch { foreach (var value in owned) value.Dispose(); throw; }

            BoundedImmutableValueSource<T> Freeze<T>(BoundedEditRecordStore<T> input) where T : unmanaged
            {
                var result = new BoundedEditRecordStore<T>(scope.Resources);
                try { result.AddRange(input.ReadValues(token), token); result.Seal(); result.SpillResidentPages(token); var frozen = new BoundedImmutableValueSource<T>(result); owned.Add(frozen); return frozen; }
                catch { result.Dispose(); throw; }
            }
            static int Compare(CurvePointSnapshotValue a, CurvePointSnapshotValue b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Id.CompareTo(b.Id);
        });
}
