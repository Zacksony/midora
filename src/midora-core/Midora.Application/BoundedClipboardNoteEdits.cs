using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedValueCurveTransform(MidoraProject project,
        MidoraId instrumentId, MidoraId voiceId, MidoraId curveId, IReadOnlyCollection<MidoraId> pointIds,
        Func<CurvePointSnapshotValue, CurvePointSnapshotValue?> transform)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        EventInstrument instrument = FindEventInstrument(project, instrumentId);
        SubVoice voice = FindSubVoice(instrument, voiceId);
        ValueCurve curve = FindValueCurve(voice, curveId);
        CurvePointQuerySnapshot source = curve.Points.CreateQuerySnapshot();
        long length = instrument.TemplateLengthTicks;
        (double minimum, double maximum) = ValueCurveTargetRange(curve.Target);
        using BoundedCurvePointPlan plan = BoundedCurvePointPlan.Transform(source, pointIds,
            _ => transform, resolveCollisions: true, duplicate: false,
            static () => throw new InvalidOperationException("A Value Curve transformation cannot allocate IDs."),
            value =>
            {
                if (value.Tick < 0 || value.Tick == long.MaxValue || !double.IsFinite(value.Value)
                    || !Enum.IsDefined(value.Interpolation)
                    || ((value.Value < minimum || value.Value > maximum) && curve.TargetSettings.Overflow == MappingOverflow.Fail))
                    throw new ArgumentOutOfRangeException(nameof(pointIds));
                length = Math.Max(length, checked(value.Tick + 1));
            }, rejectCollisions: true);
        if (!plan.HasChanges && length == instrument.TemplateLengthTicks)
            return Prepared(false, EventInstrumentChange(instrumentId), static _ => { }, static _ => { });
        SubVoice result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
        ValueCurve replacement = result.Curves.Single(value => value.Id == curve.Id);
        replacement.Points.Clear();
        replacement.Points.AdoptEditedSnapshot(project, source, plan.Changes, plan.Appended, scope.Token);
        return plan.Own(new BoundedTemplateRootEdit(ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project,
            instrument, voice, result, EventInstrumentChange(instrument.Id)), instrument, length));
    }

    private static IPreparedProjectEdit PrepareBoundedValueCurveClipboard(MidoraProject project,
        EventInstrument instrument, SubVoice voice, ValueCurve curve,
        IReadOnlyList<CurvePointClipboardSnapshot> snapshots, long tickOffset)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        CurvePointQuerySnapshot source = curve.Points.CreateQuerySnapshot();
        long firstId = project.NextStableId, nextId = firstId;
        using var ordered = BoundedEditSort.Sort(snapshots.Select(value => new CurvePointSnapshotValue(
            default, checked(tickOffset + value.Tick), value.Value, value.Interpolation)),
            Comparer<CurvePointSnapshotValue>.Create((a, b) => a.Tick.CompareTo(b.Tick)), scope.Resources, scope.Token);
        long previousTick = -1, length = instrument.TemplateLengthTicks;
        foreach (CurvePointSnapshotValue value in ordered)
        {
            scope.Token.ThrowIfCancellationRequested();
            ValidateValueCurvePoint(curve, default, value.Tick, value.Value, value.Interpolation);
            if (value.Tick == previousTick)
                throw new InvalidOperationException("Pasted Value Curve points would create duplicate point ticks.");
            previousTick = value.Tick;
            length = Math.Max(length, checked(value.Tick + 1));
        }
        using BoundedCurvePointPlan plan = BoundedCurvePointPlan.Append(source, ordered,
            () => new MidoraId(checked(nextId++)), static _ => { });
        SubVoice result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
        ValueCurve replacement = result.Curves.Single(value => value.Id == curve.Id);
        replacement.Points.Clear();
        replacement.Points.AdoptEditedSnapshot(project, source, plan.Changes, plan.Appended, scope.Token);
        IPreparedProjectEdit prepared = plan.Own(new BoundedTemplateRootEdit(ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project,
            instrument, voice, result, EventInstrumentChange(instrument.Id), firstId, nextId), instrument, length));
        return PublishBoundedNoteSelection(prepared, [], plan.ResultSelection,
            static values => values, scope);
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalLaneClipboard(MidoraProject project,
        SegmentLocation target, LogicalParameterDefinition definition,
        IReadOnlyList<CurvePointClipboardSnapshot> snapshots, long tickOffset)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        long firstId = project.NextStableId, nextId = firstId;
        LogicalParameterLane lane = new(project, new MidoraId(nextId++)) { ParameterId = definition.Id };
        CurvePointQuerySnapshot empty = lane.Points.CreateQuerySnapshot();
        using BoundedCurvePointPlan plan = BoundedCurvePointPlan.Append(empty,
            snapshots.Select(value => new CurvePointSnapshotValue(default, checked(tickOffset + value.Tick),
                value.Value, CurveInterpolation.Step)), () => new MidoraId(checked(nextId++)),
            value => { if (value.Tick < 0) throw new ArgumentOutOfRangeException(nameof(tickOffset));
                ValidatePointValue(definition, value.Value, CurveInterpolation.Step); });
        lane.Points.AdoptEditedSnapshot(project, empty, plan.Changes, plan.Appended, scope.Token);
        Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, target.Segment, scope.Token);
        result.ParameterLanes.Add(lane);
        return plan.Own(ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project,
            target.Track, target.Segment, result, TrackChange(target.Track.Id), firstId, nextId));
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalClipboardAppend(MidoraProject project,
        SegmentLocation target, IReadOnlyList<LogicalNoteClipboardSnapshot> snapshots, long tickOffset)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        LogicalNoteQuerySnapshot source = target.Segment.Notes.CreateQuerySnapshot();
        long firstId = project.NextStableId, nextId = firstId;
        var appended = BoundedLogicalNotePlanning.Append(Values(), static value => new(value.StartTick, value.Note),
            key => source.EnumerateExactStart(key.Tick, key.Key), scope.Resources, scope.Token, static value => value.Id);
        try
        {
            Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, target.Segment, scope.Token);
            result.Notes.Clear();
            result.Notes.AdoptEditedSnapshot(project, source, EmptyBoundedChanges<LogicalNoteSnapshotValue>(scope), appended, scope.Token);
            IPreparedProjectEdit prepared = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, target.Track, target.Segment,
                result, TrackChange(target.Track.Id), firstId, nextId);
            return PublishBoundedNoteSelection(prepared, [], appended.Select(static value => value.Id),
                static values => values, scope);
        }
        catch { appended.Dispose(); throw; }
        IEnumerable<LogicalNoteSnapshotValue> Values()
        {
            long count = 0;
            foreach (LogicalNoteClipboardSnapshot value in snapshots)
            {
                scope.Checkpoint(++count, snapshots.Count);
                long tick = checked(tickOffset + value.StartOffset);
                ValidateLogicalNote(tick, value.LengthTicks, value.Note, value.Velocity);
                MidoraId id = new(nextId);
                nextId = checked(nextId + 1);
                yield return new(id, tick, value.LengthTicks, value.Note, value.Velocity);
            }
        }
    }

}
