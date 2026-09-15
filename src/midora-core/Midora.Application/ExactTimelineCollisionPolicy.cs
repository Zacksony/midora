using System.Collections.ObjectModel;
using Midora.Domain;

namespace Midora.Application;

internal readonly record struct DirectMidiNoteCollisionTarget(
    MidiSegment Segment,
    long Tick,
    int Key);

internal readonly record struct LogicalNoteCollisionTarget(
    Segment Segment,
    long Tick,
    int Key);

internal readonly record struct TemplateNoteCollisionTarget(
    SubVoice SubVoice,
    long Tick,
    int Key);

internal readonly record struct DirectMidiEventCollisionTarget(
    MidiSegment Segment,
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1);

internal readonly record struct LogicalParameterPointCollisionTarget(
    LogicalParameterLane Lane,
    long Tick);

internal readonly record struct TemplateEventPointCollisionTarget(
    SubVoice SubVoice,
    long Tick,
    int Detail);

internal readonly record struct ValueCurvePointCollisionTarget(
    ValueCurve Curve,
    long Tick);

/// <summary>
/// Resolves exact timeline-key collisions at the Project edit transaction boundary.
/// Logical/Template/Direct MIDI Notes keep the existing occupant at an exact
/// start-tick/key. Logical Parameter, Template MIDI, and Direct MIDI event points
/// instead keep the last newcomer from the current edit, so a moved or newly drawn
/// point replaces the former value.
/// The policy deliberately does not treat duration overlap at different start ticks
/// as a collision and does not clean unrelated pre-existing damage.
/// </summary>
internal static class ExactTimelineCollisionPolicy
{
    public static IPreparedProjectEdit Scope(
        IPreparedProjectEdit source,
        IEnumerable<Segment>? logicalNoteSegments = null,
        IEnumerable<LogicalParameterLane>? logicalParameterLanes = null,
        IEnumerable<SubVoice>? subVoices = null,
        IEnumerable<ValueCurve>? valueCurves = null,
        IEnumerable<MidiSegment>? directMidiSegments = null,
        IEnumerable<LogicalNoteCollisionTarget>? logicalNoteTargets = null,
        IEnumerable<TemplateNoteCollisionTarget>? templateNoteTargets = null,
        IEnumerable<DirectMidiNoteCollisionTarget>? directMidiNoteTargets = null,
        IEnumerable<DirectMidiEventCollisionTarget>? directMidiEventTargets = null,
        IEnumerable<LogicalParameterPointCollisionTarget>? logicalParameterPointTargets = null,
        IEnumerable<TemplateEventPointCollisionTarget>? templateEventPointTargets = null,
        IEnumerable<ValueCurvePointCollisionTarget>? valueCurvePointTargets = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        CollisionScopeSet additions = new(
            (logicalNoteSegments ?? []).Distinct().ToArray(),
            (logicalParameterLanes ?? []).Distinct().ToArray(),
            (subVoices ?? []).Distinct().ToArray(),
            (valueCurves ?? []).Distinct().ToArray(),
            (directMidiSegments ?? []).Distinct().ToArray(),
            (logicalNoteTargets ?? []).Distinct().ToArray(),
            (templateNoteTargets ?? []).Distinct().ToArray(),
            (directMidiNoteTargets ?? []).Distinct().ToArray(),
            (directMidiEventTargets ?? []).Distinct().ToArray(),
            (logicalParameterPointTargets ?? []).Distinct().ToArray(),
            (templateEventPointTargets ?? []).Distinct().ToArray(),
            (valueCurvePointTargets ?? []).Distinct().ToArray());
        if (additions.IsEmpty) return source;
        if (source is CollisionScopedPreparedEdit existing)
        {
            return new CollisionScopedPreparedEdit(
                existing.Source,
                CollisionScopeSet.Merge(existing.Scopes, additions));
        }
        return new CollisionScopedPreparedEdit(source, additions);
    }

    public static IPreparedProjectEdit Wrap(
        MidoraProject project,
        IPreparedProjectEdit source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasChanges || source is not CollisionScopedPreparedEdit scoped)
        {
            return source;
        }

        if (scoped.Scopes.HasOnlyTargets)
        {
            return new TargetedCollisionPreparedEdit(
                scoped.Source,
                CaptureTargetedLogicalNoteBaseline(scoped.Scopes.LogicalNoteTargets),
                CaptureTargetedTemplateNoteBaseline(scoped.Scopes.TemplateNoteTargets),
                CaptureTargetedDirectMidiNoteBaseline(scoped.Scopes.DirectMidiNoteTargets),
                CaptureTargetedLogicalParameterPointBaseline(scoped.Scopes.LogicalParameterPointTargets),
                CaptureTargetedTemplateEventPointBaseline(scoped.Scopes.TemplateEventPointTargets),
                CaptureTargetedDirectMidiEventBaseline(scoped.Scopes.DirectMidiEventTargets),
                CaptureTargetedValueCurvePointBaseline(scoped.Scopes.ValueCurvePointTargets));
        }

        CollisionBaseline baseline = CaptureBaseline(scoped.Scopes);
        return new CollisionResolvingPreparedEdit(scoped.Source, baseline, scoped.Scopes);
    }

    private static TargetedLogicalNoteBaseline[] CaptureTargetedLogicalNoteBaseline(
        IReadOnlyCollection<LogicalNoteCollisionTarget> targets) => targets
        .GroupBy(static target => target.Segment)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<TargetedNoteKey> keys = group
                .Select(static target => new TargetedNoteKey(target.Tick, target.Key))
                .ToHashSet();
            Dictionary<TargetedNoteKey, MidoraId[]> occupants = QueryTargetedLogicalNotes(group.Key, keys)
                .GroupBy(static note => new TargetedNoteKey(note.StartTick, note.Note))
                .ToDictionary(static group => group.Key, static group => group.Select(static note => note.Id).ToArray());
            return new TargetedLogicalNoteBaseline(group.Key, keys, occupants);
        })
        .ToArray();

    private static TargetedTemplateNoteBaseline[] CaptureTargetedTemplateNoteBaseline(
        IReadOnlyCollection<TemplateNoteCollisionTarget> targets) => targets
        .GroupBy(static target => target.SubVoice)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<TargetedNoteKey> keys = group
                .Select(static target => new TargetedNoteKey(target.Tick, target.Key))
                .ToHashSet();
            Dictionary<TargetedNoteKey, MidoraId[]> occupants = QueryTargetedTemplateNotes(group.Key, keys)
                .GroupBy(static note => new TargetedNoteKey(note.Tick, note.Number))
                .ToDictionary(static group => group.Key, static group => group.Select(static note => note.Id).ToArray());
            return new TargetedTemplateNoteBaseline(group.Key, keys, occupants);
        })
        .ToArray();

    private static TargetedDirectMidiNoteBaseline[] CaptureTargetedDirectMidiNoteBaseline(
        IReadOnlyCollection<DirectMidiNoteCollisionTarget> targets) => targets
        .GroupBy(static target => target.Segment)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<DirectMidiNoteStartKey> keys = group
                .Select(static target => new DirectMidiNoteStartKey(target.Tick, target.Key))
                .ToHashSet();
            Dictionary<DirectMidiNoteStartKey, MidoraId[]> occupants = group.Key.Notes.QueryStartKeys(keys)
                .GroupBy(static note => new DirectMidiNoteStartKey(note.StartTick, note.Key))
                .ToDictionary(static group => group.Key, static group => group.Select(static note => note.Id).ToArray());
            return new TargetedDirectMidiNoteBaseline(group.Key, keys, occupants);
        })
        .ToArray();

    private static TargetedLogicalParameterPointBaseline[] CaptureTargetedLogicalParameterPointBaseline(
        IReadOnlyCollection<LogicalParameterPointCollisionTarget> targets) => targets
        .GroupBy(static target => target.Lane)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<long> ticks = group.Select(static target => target.Tick).ToHashSet();
            Dictionary<long, MidoraId[]> occupants = QueryTargetedCurvePoints(group.Key.Points, ticks)
                .GroupBy(static point => point.Tick)
                .ToDictionary(static values => values.Key, static values => values.Select(static point => point.Id).ToArray());
            return new TargetedLogicalParameterPointBaseline(group.Key, ticks, occupants);
        })
        .ToArray();

    private static TargetedTemplateEventPointBaseline[] CaptureTargetedTemplateEventPointBaseline(
        IReadOnlyCollection<TemplateEventPointCollisionTarget> targets) => targets
        .GroupBy(static target => target.SubVoice)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<TargetedPointKey> keys = group
                .Select(static target => new TargetedPointKey(target.Tick, target.Detail))
                .ToHashSet();
            Dictionary<TargetedPointKey, MidoraId[]> occupants = QueryTargetedTemplateEvents(group.Key, keys)
                .SelectMany(static value => TemplateEventExactCollision.GetNonNoteDetails(
                        value.Kind,
                        value.Number,
                        value.HasBankMsb,
                        value.HasBankLsb)
                    .Select(detail => (Key: new TargetedPointKey(value.Tick, detail), Value: value)))
                .Where(value => keys.Contains(value.Key))
                .GroupBy(static value => value.Key, static value => value.Value)
                .ToDictionary(static values => values.Key, static values => values.Select(static value => value.Id).ToArray());
            return new TargetedTemplateEventPointBaseline(group.Key, keys, occupants);
        })
        .ToArray();

    private static TargetedDirectMidiEventBaseline[] CaptureTargetedDirectMidiEventBaseline(
        IReadOnlyCollection<DirectMidiEventCollisionTarget> targets) => targets
        .GroupBy(static target => target.Segment)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<DirectMidiEventStartKey> keys = group
                .Select(static target => new DirectMidiEventStartKey(
                    target.Tick,
                    target.Kind,
                    DirectMidiEventUsesData1Selector(target.Kind) ? target.Data1 : 0))
                .ToHashSet();
            Dictionary<DirectMidiEventStartKey, MidoraId[]> occupants = group.Key.ChannelEvents
                .QueryStartKeys(keys)
                .GroupBy(static value => new DirectMidiEventStartKey(
                    value.Tick,
                    value.Kind,
                    DirectMidiEventUsesData1Selector(value.Kind) ? value.Data1 : 0))
                .ToDictionary(static values => values.Key, static values => values.Select(static value => value.Id).ToArray());
            return new TargetedDirectMidiEventBaseline(group.Key, keys, occupants);
        })
        .ToArray();

    private static TargetedValueCurvePointBaseline[] CaptureTargetedValueCurvePointBaseline(
        IReadOnlyCollection<ValueCurvePointCollisionTarget> targets) => targets
        .GroupBy(static target => target.Curve)
        .OrderBy(static group => group.Key.Id)
        .Select(group =>
        {
            HashSet<long> ticks = group.Select(static target => target.Tick).ToHashSet();
            Dictionary<long, MidoraId[]> occupants = QueryTargetedCurvePoints(group.Key.Points, ticks)
                .GroupBy(static point => point.Tick)
                .ToDictionary(static values => values.Key, static values => values.Select(static point => point.Id).ToArray());
            return new TargetedValueCurvePointBaseline(group.Key, ticks, occupants);
        })
        .ToArray();

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedLogicalParameterPoints(
        IReadOnlyList<TargetedLogicalParameterPointBaseline> baselines)
    {
        List<TargetedCurvePointCandidate> discarded = [];
        long order = 0;
        foreach (TargetedLogicalParameterPointBaseline baseline in baselines)
        {
            foreach (IGrouping<long, CurvePoint> group in QueryTargetedCurvePoints(
                    baseline.Lane.Points,
                    baseline.Ticks)
                .GroupBy(static point => point.Tick))
            {
                baseline.Occupants.TryGetValue(group.Key, out MidoraId[]? occupants);
                CurvePoint[] current = group.ToArray();
                CurvePoint[] newcomers = current
                    .Where(point => !ContainsId(occupants, point.Id))
                    .ToArray();
                if (newcomers.Length == 0)
                {
                    order += current.Length;
                    continue;
                }
                CurvePoint winner = newcomers[^1];
                foreach (CurvePoint point in current)
                {
                    if (!ReferenceEquals(point, winner))
                        discarded.Add(new(baseline.Lane.Points, point, order));
                    order++;
                }
            }
        }
        return RemoveTargetedCurvePoints(discarded);
    }

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedTemplateEventPoints(
        IReadOnlyList<TargetedTemplateEventPointBaseline> baselines)
    {
        List<TargetedTemplatePointCandidate> current = [];
        long order = 0;
        foreach (TargetedTemplateEventPointBaseline baseline in baselines)
        {
            foreach (TemplateEvent value in QueryTargetedTemplateEvents(baseline.SubVoice, baseline.Keys))
            {
                current.Add(new(baseline, value, order++));
            }
        }

        HashSet<TemplateEvent> discarded = [];
        foreach (IGrouping<(TargetedTemplateEventPointBaseline Baseline, TargetedPointKey Key), TargetedTemplatePointCandidate> group in
            current.SelectMany(candidate => TemplateEventExactCollision.GetNonNoteDetails(
                    candidate.Value.Kind,
                    candidate.Value.Number,
                    candidate.Value.HasBankMsb,
                    candidate.Value.HasBankLsb)
                .Select(detail => (Key: new TargetedPointKey(candidate.Value.Tick, detail), Candidate: candidate)))
                .Where(value => value.Candidate.Baseline.Keys.Contains(value.Key))
                .GroupBy(
                    static value => (value.Candidate.Baseline, value.Key),
                    static value => value.Candidate))
        {
            group.Key.Baseline.Occupants.TryGetValue(group.Key.Key, out MidoraId[]? occupants);
            TargetedTemplatePointCandidate[] candidates = group
                .Where(candidate => !discarded.Contains(candidate.Value))
                .OrderBy(static value => value.Order)
                .ToArray();
            if (candidates.Length < 2) continue;
            TargetedTemplatePointCandidate[] newcomers = candidates
                .Where(candidate => !ContainsId(occupants, candidate.Value.Id))
                .ToArray();
            if (newcomers.Length == 0) continue;
            TemplateEvent winner = newcomers[^1].Value;
            foreach (TargetedTemplatePointCandidate candidate in candidates)
            {
                if (!ReferenceEquals(candidate.Value, winner)) discarded.Add(candidate.Value);
            }
        }

        return current
            .Where(candidate => discarded.Contains(candidate.Value))
            .GroupBy(static candidate => candidate.Baseline.SubVoice.Events)
            .OrderBy(static group => group.Min(static candidate => candidate.Order))
            .Select(static group => new CollisionRemoval(group.Key.RemoveRangeForExactCollision(
                group.OrderBy(static candidate => candidate.Order)
                    .Select(static candidate => candidate.Value)
                    .Distinct()
                    .ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedDirectMidiEvents(
        IReadOnlyList<TargetedDirectMidiEventBaseline> baselines)
    {
        List<TargetedDirectMidiEventCandidate> discarded = [];
        long order = 0;
        foreach (TargetedDirectMidiEventBaseline baseline in baselines)
        {
            foreach (IGrouping<DirectMidiEventStartKey, DirectMidiChannelEvent> group in baseline.Segment.ChannelEvents
                .QueryStartKeys(baseline.Keys)
                .GroupBy(static value => new DirectMidiEventStartKey(
                    value.Tick,
                    value.Kind,
                    DirectMidiEventUsesData1Selector(value.Kind) ? value.Data1 : 0)))
            {
                baseline.Occupants.TryGetValue(group.Key, out MidoraId[]? occupants);
                DirectMidiChannelEvent[] current = group.ToArray();
                DirectMidiChannelEvent[] newcomers = current
                    .Where(value => !ContainsId(occupants, value.Id))
                    .ToArray();
                if (newcomers.Length == 0)
                {
                    order += current.Length;
                    continue;
                }
                DirectMidiChannelEvent winner = newcomers[^1];
                foreach (DirectMidiChannelEvent value in current)
                {
                    if (!ReferenceEquals(value, winner))
                        discarded.Add(new(baseline.Segment.ChannelEvents, value, order));
                    order++;
                }
            }
        }
        return discarded
            .GroupBy(static candidate => candidate.Collection)
            .OrderBy(static group => group.Min(static candidate => candidate.Order))
            .Select(static group => new CollisionRemoval(group.Key.RemoveRangeWithUndo(
                group.OrderBy(static candidate => candidate.Order)
                    .Select(static candidate => candidate.Value)
                    .ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedValueCurvePoints(
        IReadOnlyList<TargetedValueCurvePointBaseline> baselines)
    {
        List<TargetedCurvePointCandidate> discarded = [];
        long order = 0;
        foreach (TargetedValueCurvePointBaseline baseline in baselines)
        {
            foreach (IGrouping<long, CurvePoint> group in QueryTargetedCurvePoints(
                    baseline.Curve.Points,
                    baseline.Ticks)
                .GroupBy(static point => point.Tick))
            {
                baseline.Occupants.TryGetValue(group.Key, out MidoraId[]? occupants);
                CurvePoint[] current = group.ToArray();
                CurvePoint? incumbent = current.FirstOrDefault(point => ContainsId(occupants, point.Id));
                bool hasNewcomer = current.Any(point => !ContainsId(occupants, point.Id));
                if (!hasNewcomer)
                {
                    order += current.Length;
                    continue;
                }
                CurvePoint winner = incumbent ?? current[0];
                foreach (CurvePoint point in current)
                {
                    if (!ReferenceEquals(point, winner))
                        discarded.Add(new(baseline.Curve.Points, point, order));
                    order++;
                }
            }
        }
        return RemoveTargetedCurvePoints(discarded);
    }

    private static IReadOnlyList<CollisionRemoval> RemoveTargetedCurvePoints(
        IReadOnlyCollection<TargetedCurvePointCandidate> discarded) => discarded
        .GroupBy(static candidate => candidate.Collection)
        .OrderBy(static group => group.Min(static candidate => candidate.Order))
        .Select(static group => new CollisionRemoval(group.Key.RemoveRangeWithUndo(
            group.OrderBy(static candidate => candidate.Order)
                .Select(static candidate => candidate.Value)
                .ToArray())))
        .ToArray();

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedDirectMidiNotes(
        IReadOnlyList<TargetedDirectMidiNoteBaseline> baselines)
    {
        List<TargetedDirectMidiNoteCandidate> discarded = [];
        long order = 0;
        foreach (TargetedDirectMidiNoteBaseline baseline in baselines)
        {
            Dictionary<DirectMidiNoteStartKey, TargetedDirectMidiNoteCandidate> firstByKey = [];
            Dictionary<DirectMidiNoteStartKey, List<TargetedDirectMidiNoteCandidate>> duplicatesByKey = [];
            foreach (DirectMidiNote note in baseline.Segment.Notes.QueryEditedStartKeys(baseline.Keys))
            {
                DirectMidiNoteStartKey key = new(note.StartTick, note.Key);
                TargetedDirectMidiNoteCandidate candidate = new(baseline.Segment.Notes, note, order++);
                if (firstByKey.TryAdd(key, candidate)) continue;
                if (!duplicatesByKey.TryGetValue(key, out List<TargetedDirectMidiNoteCandidate>? values))
                {
                    values = [firstByKey[key]];
                    duplicatesByKey.Add(key, values);
                }
                values.Add(candidate);
            }

            foreach ((DirectMidiNoteStartKey key, TargetedDirectMidiNoteCandidate first) in firstByKey)
            {
                baseline.Occupants.TryGetValue(key, out MidoraId[]? incumbents);
                duplicatesByKey.TryGetValue(key, out List<TargetedDirectMidiNoteCandidate>? duplicateValues);
                bool firstIsIncumbent = ContainsId(incumbents, first.Note.Id);
                bool hasNewcomer = !firstIsIncumbent
                    || duplicateValues?.Any(value => !ContainsId(incumbents, value.Note.Id)) == true;
                if (!hasNewcomer) continue;
                bool hasLiveIncumbent = incumbents?.Any(id =>
                    baseline.Segment.Notes.IsUneditedSourceNotePresent(id)
                    || first.Note.Id == id
                    || duplicateValues?.Any(value => value.Note.Id == id) == true) == true;
                if (hasLiveIncumbent)
                {
                    if (!firstIsIncumbent) discarded.Add(first);
                    if (duplicateValues is not null)
                        discarded.AddRange(duplicateValues.Skip(1).Where(value => !ContainsId(incumbents, value.Note.Id)));
                }
                else if (duplicateValues is not null)
                {
                    discarded.AddRange(duplicateValues.Skip(1));
                }
            }
        }

        return discarded
            .GroupBy(static value => value.Collection)
            .OrderBy(static group => group.Min(static value => value.Order))
            .Select(static group => new CollisionRemoval(
                group.Key.RemoveRangeForExactCollision(group
                    .OrderBy(static value => value.Order)
                    .Select(static value => value.Note)
                    .ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedLogicalNotes(
        IReadOnlyList<TargetedLogicalNoteBaseline> baselines)
    {
        List<TargetedLogicalNoteCandidate> discarded = [];
        long order = 0;
        foreach (TargetedLogicalNoteBaseline baseline in baselines)
        {
            Dictionary<TargetedNoteKey, TargetedLogicalNoteCandidate> firstByKey = [];
            Dictionary<TargetedNoteKey, List<TargetedLogicalNoteCandidate>> duplicatesByKey = [];
            foreach (LogicalNote note in QueryTargetedLogicalNotes(baseline.Segment, baseline.Keys))
            {
                TargetedNoteKey key = new(note.StartTick, note.Note);
                TargetedLogicalNoteCandidate candidate = new(baseline.Segment.Notes, note, order++);
                if (firstByKey.TryAdd(key, candidate)) continue;
                if (!duplicatesByKey.TryGetValue(key, out List<TargetedLogicalNoteCandidate>? values))
                {
                    values = [firstByKey[key]];
                    duplicatesByKey.Add(key, values);
                }
                values.Add(candidate);
            }
            foreach ((TargetedNoteKey key, TargetedLogicalNoteCandidate first) in firstByKey)
            {
                baseline.Occupants.TryGetValue(key, out MidoraId[]? incumbents);
                duplicatesByKey.TryGetValue(key, out List<TargetedLogicalNoteCandidate>? duplicateValues);
                bool firstIsIncumbent = ContainsId(incumbents, first.Note.Id);
                bool hasLiveIncumbent = firstIsIncumbent
                    || duplicateValues?.Skip(1).Any(value => ContainsId(incumbents, value.Note.Id)) == true;
                if (hasLiveIncumbent)
                {
                    if (!firstIsIncumbent) discarded.Add(first);
                    if (duplicateValues is not null)
                        discarded.AddRange(duplicateValues.Skip(1).Where(value => !ContainsId(incumbents, value.Note.Id)));
                }
                else if (duplicateValues is not null)
                {
                    discarded.AddRange(duplicateValues.Skip(1));
                }
            }
        }
        return discarded
            .GroupBy(static value => value.Collection)
            .OrderBy(static group => group.Min(static value => value.Order))
            .Select(static group => new CollisionRemoval(
                group.Key.RemoveRangeForExactCollision(group
                    .OrderBy(static value => value.Order)
                    .Select(static value => value.Note)
                    .ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<CollisionRemoval> ResolveTargetedTemplateNotes(
        IReadOnlyList<TargetedTemplateNoteBaseline> baselines)
    {
        List<TargetedTemplateNoteCandidate> discarded = [];
        long order = 0;
        foreach (TargetedTemplateNoteBaseline baseline in baselines)
        {
            Dictionary<TargetedNoteKey, TargetedTemplateNoteCandidate> firstByKey = [];
            Dictionary<TargetedNoteKey, List<TargetedTemplateNoteCandidate>> duplicatesByKey = [];
            foreach (TemplateEvent note in QueryTargetedTemplateNotes(baseline.SubVoice, baseline.Keys))
            {
                TargetedNoteKey key = new(note.Tick, note.Number);
                TargetedTemplateNoteCandidate candidate = new(baseline.SubVoice.Events, note, order++);
                if (firstByKey.TryAdd(key, candidate)) continue;
                if (!duplicatesByKey.TryGetValue(key, out List<TargetedTemplateNoteCandidate>? values))
                {
                    values = [firstByKey[key]];
                    duplicatesByKey.Add(key, values);
                }
                values.Add(candidate);
            }
            foreach ((TargetedNoteKey key, TargetedTemplateNoteCandidate first) in firstByKey)
            {
                baseline.Occupants.TryGetValue(key, out MidoraId[]? incumbents);
                duplicatesByKey.TryGetValue(key, out List<TargetedTemplateNoteCandidate>? duplicateValues);
                bool firstIsIncumbent = ContainsId(incumbents, first.Note.Id);
                bool hasLiveIncumbent = firstIsIncumbent
                    || duplicateValues?.Skip(1).Any(value => ContainsId(incumbents, value.Note.Id)) == true;
                if (hasLiveIncumbent)
                {
                    if (!firstIsIncumbent) discarded.Add(first);
                    if (duplicateValues is not null)
                        discarded.AddRange(duplicateValues.Skip(1).Where(value => !ContainsId(incumbents, value.Note.Id)));
                }
                else if (duplicateValues is not null)
                {
                    discarded.AddRange(duplicateValues.Skip(1));
                }
            }
        }
        return discarded
            .GroupBy(static value => value.Collection)
            .OrderBy(static group => group.Min(static value => value.Order))
            .Select(static group => new CollisionRemoval(
                group.Key.RemoveRangeForExactCollision(group
                    .OrderBy(static value => value.Order)
                    .Select(static value => value.Note)
                    .ToArray())))
            .ToArray();
    }

    private static IEnumerable<LogicalNote> QueryTargetedLogicalNotes(
        Segment segment,
        IReadOnlySet<TargetedNoteKey> keys)
    {
        if (keys.Count == 0) yield break;
        LogicalNoteQuerySnapshot snapshot = segment.Notes.CreateQuerySnapshot();
        HashSet<TimelineStartLaneKey> queryKeys = keys
            .Select(static value => new TimelineStartLaneKey(value.Tick, value.Key))
            .ToHashSet();
        HashSet<MidoraId> matchingIds = snapshot.QueryStartKeys(queryKeys)
            .Select(static value => value.Id)
            .ToHashSet();
        foreach (LogicalNote note in segment.Notes.ResolveByIdsInCollectionOrder(matchingIds))
        {
            yield return note;
        }
    }

    private static IEnumerable<TemplateEvent> QueryTargetedTemplateNotes(
        SubVoice voice,
        IReadOnlySet<TargetedNoteKey> keys)
    {
        if (keys.Count == 0) yield break;
        TemplateEventQuerySnapshot snapshot = voice.Events.CreateQuerySnapshot();
        HashSet<TimelineStartLaneKey> queryKeys = keys
            .Select(static value => new TimelineStartLaneKey(value.Tick, value.Key))
            .ToHashSet();
        HashSet<MidoraId> matchingIds = snapshot.QueryNoteStartKeys(queryKeys)
            .Select(static value => value.Id)
            .ToHashSet();
        foreach (TemplateEvent note in voice.Events.ResolveByIdsInCollectionOrder(matchingIds))
        {
            yield return note;
        }
    }

    private static IEnumerable<CurvePoint> QueryTargetedCurvePoints(
        CurvePointCollection points,
        IReadOnlySet<long> ticks)
    {
        if (ticks.Count == 0) yield break;
        HashSet<MidoraId> matchingIds = points.CreateQuerySnapshot()
            .QueryTicks(ticks)
            .Select(static value => value.Id)
            .ToHashSet();
        foreach (CurvePoint point in points.ResolveByIdsInCollectionOrder(matchingIds))
        {
            yield return point;
        }
    }

    private static IEnumerable<TemplateEvent> QueryTargetedTemplateEvents(
        SubVoice voice,
        IReadOnlySet<TargetedPointKey> keys)
    {
        if (keys.Count == 0) yield break;
        HashSet<long> ticks = keys.Select(static value => value.Tick).ToHashSet();
        HashSet<MidoraId> matchingIds = voice.Events.CreateQuerySnapshot()
            .QueryEventTicks(ticks)
            .Where(value => TemplateEventExactCollision.GetNonNoteDetails(
                    value.Kind,
                    value.Number,
                    value.HasBankMsb,
                    value.HasBankLsb)
                .Any(detail => keys.Contains(new(value.Tick, detail))))
            .Select(static value => value.Id)
            .ToHashSet();
        foreach (TemplateEvent value in voice.Events.ResolveByIdsInCollectionOrder(matchingIds))
        {
            yield return value;
        }
    }

    public static IPreparedProjectEdit CombineScopes(
        IPreparedProjectEdit source,
        IEnumerable<IPreparedProjectEdit> children)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(children);
        CollisionScopeSet? combined = null;
        foreach (IPreparedProjectEdit child in children)
        {
            if (child is not CollisionScopedPreparedEdit scoped) continue;
            combined = combined is null
                ? scoped.Scopes
                : CollisionScopeSet.Merge(combined, scoped.Scopes);
        }
        return combined is null ? source : new CollisionScopedPreparedEdit(source, combined);
    }

    private static CollisionBaseline CaptureBaseline(CollisionScopeSet scopes)
    {
        Dictionary<MidoraId, HashSet<CollisionKey>> keysById = [];
        foreach (CollisionCandidate candidate in EnumerateCandidates(scopes))
        {
            if (!keysById.TryGetValue(candidate.Id, out HashSet<CollisionKey>? keys))
            {
                keys = [];
                keysById.Add(candidate.Id, keys);
            }
            keys.UnionWith(candidate.Keys);
        }
        return new(keysById);
    }

    private static IReadOnlyList<CollisionRemoval> Resolve(
        CollisionScopeSet scopes,
        CollisionBaseline baseline)
    {
        CollisionCandidate[] candidates = EnumerateCandidates(scopes).ToArray();
        HashSet<CollisionCandidate> discarded = [];
        foreach (IGrouping<CollisionKey, CollisionCandidate> group in candidates
            .SelectMany(candidate => candidate.Keys.Select(key => (key, candidate)))
            .GroupBy(value => value.key, value => value.candidate))
        {
            CollisionCandidate[] occupants = group
                .Where(candidate => !discarded.Contains(candidate))
                .OrderBy(candidate => candidate.Order)
                .ToArray();
            if (occupants.Length < 2)
            {
                continue;
            }

            HashSet<CollisionCandidate> incumbents = occupants
                .Where(candidate => baseline.Contains(candidate.Id, group.Key))
                .ToHashSet();
            if (UsesLaterPointEditWins(group.Key.Scope))
            {
                CollisionCandidate[] newcomers = occupants
                    .Where(candidate => !baseline.Contains(candidate.Id, group.Key))
                    .ToArray();
                if (newcomers.Length == 0)
                {
                    // Do not turn an unrelated pre-existing collision into an
                    // implicit cleanup edit.
                    continue;
                }

                CollisionCandidate pointWinner = newcomers[^1];
                foreach (CollisionCandidate candidate in occupants)
                {
                    if (!ReferenceEquals(candidate, pointWinner))
                    {
                        discarded.Add(candidate);
                    }
                }
                continue;
            }

            CollisionCandidate winner = incumbents.Count != 0
                ? occupants.First(incumbents.Contains)
                : occupants[0];
            foreach (CollisionCandidate candidate in occupants)
            {
                if (!ReferenceEquals(candidate, winner)
                    && !incumbents.Contains(candidate))
                {
                    discarded.Add(candidate);
                }
            }
        }

        CollisionRemoval[] removals = discarded
            .OrderByDescending(candidate => candidate.Order)
            .Select(candidate => candidate.Remove())
            .ToArray();
        return removals;
    }

    private static bool UsesLaterPointEditWins(CollisionScope scope) =>
        scope is CollisionScope.LogicalParameterPoint
            or CollisionScope.TemplateEventPoint
            or CollisionScope.DirectMidiEventPoint;

    private static IEnumerable<CollisionCandidate> EnumerateCandidates(CollisionScopeSet scopes)
    {
        long order = 0;
        foreach (Segment segment in scopes.LogicalNoteSegments.OrderBy(static value => value.Id))
        {
            foreach (LogicalNote note in segment.Notes)
            {
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.LogicalNote, segment.Id, note.StartTick, note.Note)],
                    order++,
                    segment.Notes,
                    note,
                    "Logical Note");
            }
        }

        foreach (LogicalParameterLane lane in scopes.LogicalParameterLanes
            .OrderBy(static value => value.Id))
        {
            foreach (CurvePoint point in lane.Points)
            {
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.LogicalParameterPoint, lane.Id, point.Tick, 0)],
                    order++,
                    lane.Points,
                    point,
                    "Logical Parameter point");
            }
        }

        foreach (SubVoice voice in scopes.SubVoices.OrderBy(static value => value.Id))
        {
            foreach (TemplateEvent value in voice.Events)
            {
                CollisionKey[] keys = value.Kind == TemplateEventKind.Note
                    ? [new(CollisionScope.TemplateNote, voice.Id, value.Tick, value.Number)]
                    : TemplateEventExactCollision.GetNonNoteDetails(
                            value.Kind,
                            value.Number,
                            value.HasBankMsb,
                            value.HasBankLsb)
                        .Select(detail => new CollisionKey(
                            CollisionScope.TemplateEventPoint,
                            voice.Id,
                            value.Tick,
                            detail))
                        .ToArray();
                yield return CollisionCandidate.ForCollection(
                    value.Id,
                    keys,
                    order++,
                    voice.Events,
                    value,
                    "Template Event");
            }
        }

        HashSet<Segment> fullLogicalNoteSegments = scopes.LogicalNoteSegments.ToHashSet();
        foreach (IGrouping<Segment, LogicalNoteCollisionTarget> group in
            scopes.LogicalNoteTargets
                .Where(target => !fullLogicalNoteSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<TargetedNoteKey> keys = group
                .Select(static target => new TargetedNoteKey(target.Tick, target.Key))
                .ToHashSet();
            foreach (LogicalNote note in QueryTargetedLogicalNotes(group.Key, keys))
            {
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.LogicalNote, group.Key.Id, note.StartTick, note.Note)],
                    order++,
                    group.Key.Notes,
                    note,
                    "Logical Note");
            }
        }

        HashSet<SubVoice> fullSubVoices = scopes.SubVoices.ToHashSet();
        foreach (IGrouping<SubVoice, TemplateNoteCollisionTarget> group in
            scopes.TemplateNoteTargets
                .Where(target => !fullSubVoices.Contains(target.SubVoice))
                .GroupBy(static target => target.SubVoice)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<TargetedNoteKey> keys = group
                .Select(static target => new TargetedNoteKey(target.Tick, target.Key))
                .ToHashSet();
            foreach (TemplateEvent value in QueryTargetedTemplateNotes(group.Key, keys))
            {
                yield return CollisionCandidate.ForCollection(
                    value.Id,
                    [new(CollisionScope.TemplateNote, group.Key.Id, value.Tick, value.Number)],
                    order++,
                    group.Key.Events,
                    value,
                    "Template Note");
            }
        }

        HashSet<LogicalParameterLane> fullLogicalParameterLanes = scopes.LogicalParameterLanes.ToHashSet();
        foreach (IGrouping<LogicalParameterLane, LogicalParameterPointCollisionTarget> group in
            scopes.LogicalParameterPointTargets
                .Where(target => !fullLogicalParameterLanes.Contains(target.Lane))
                .GroupBy(static target => target.Lane)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<long> ticks = group.Select(static target => target.Tick).ToHashSet();
            foreach (CurvePoint point in QueryTargetedCurvePoints(group.Key.Points, ticks))
            {
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.LogicalParameterPoint, group.Key.Id, point.Tick, 0)],
                    order++,
                    group.Key.Points,
                    point,
                    "Logical Parameter point");
            }
        }

        foreach (IGrouping<SubVoice, TemplateEventPointCollisionTarget> group in
            scopes.TemplateEventPointTargets
                .Where(target => !fullSubVoices.Contains(target.SubVoice))
                .GroupBy(static target => target.SubVoice)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<TargetedPointKey> keys = group
                .Select(static target => new TargetedPointKey(target.Tick, target.Detail))
                .ToHashSet();
            foreach (TemplateEvent value in QueryTargetedTemplateEvents(group.Key, keys))
            {
                CollisionKey[] collisionKeys = TemplateEventExactCollision.GetNonNoteDetails(
                        value.Kind,
                        value.Number,
                        value.HasBankMsb,
                        value.HasBankLsb)
                    .Select(detail => new CollisionKey(
                        CollisionScope.TemplateEventPoint,
                        group.Key.Id,
                        value.Tick,
                        detail))
                    .Where(key => keys.Contains(new(key.Tick, key.Detail)))
                    .ToArray();
                yield return CollisionCandidate.ForCollection(
                    value.Id,
                    collisionKeys,
                    order++,
                    group.Key.Events,
                    value,
                    "Template Event");
            }
        }

        foreach (ValueCurve curve in scopes.ValueCurves.OrderBy(static value => value.Id))
        {
            foreach (CurvePoint point in curve.Points)
            {
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.ValueCurvePoint, curve.Id, point.Tick, 0)],
                    order++,
                    curve.Points,
                    point,
                    "Value Curve point");
            }
        }

        HashSet<ValueCurve> fullValueCurves = scopes.ValueCurves.ToHashSet();
        foreach (IGrouping<ValueCurve, ValueCurvePointCollisionTarget> group in
            scopes.ValueCurvePointTargets
                .Where(target => !fullValueCurves.Contains(target.Curve))
                .GroupBy(static target => target.Curve)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<long> ticks = group.Select(static target => target.Tick).ToHashSet();
            foreach (CurvePoint point in QueryTargetedCurvePoints(group.Key.Points, ticks))
            {
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.ValueCurvePoint, group.Key.Id, point.Tick, 0)],
                    order++,
                    group.Key.Points,
                    point,
                    "Value Curve point");
            }
        }

        foreach (MidiSegment segment in scopes.DirectMidiSegments.OrderBy(static value => value.Id))
        {
            foreach (DirectMidiNote note in segment.Notes)
            {
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.DirectMidiNote, segment.Id, note.StartTick, note.Key)],
                    order++,
                    segment.Notes,
                    note,
                    "Direct MIDI Note");
            }
            foreach (DirectMidiChannelEvent value in segment.ChannelEvents)
            {
                int selector = DirectMidiEventUsesData1Selector(value.Kind)
                    ? value.Data1 + 1
                    : 0;
                int detail = ((int)value.Kind << 16) | selector;
                yield return CollisionCandidate.ForList(
                    value.Id,
                    [new(CollisionScope.DirectMidiEventPoint, segment.Id, value.Tick, detail)],
                    order++,
                    segment.ChannelEvents,
                    value,
                    "Direct MIDI Event");
            }
        }

        HashSet<MidiSegment> fullDirectMidiSegments = scopes.DirectMidiSegments.ToHashSet();
        foreach (IGrouping<MidiSegment, DirectMidiNoteCollisionTarget> group in
            scopes.DirectMidiNoteTargets
                .Where(target => !fullDirectMidiSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<DirectMidiNoteStartKey> keys = group
                .Select(static target => new DirectMidiNoteStartKey(target.Tick, target.Key))
                .ToHashSet();
            foreach (DirectMidiNote note in group.Key.Notes.QueryStartKeys(keys))
            {
                yield return CollisionCandidate.ForDirectMidiNote(
                    note.Id,
                    [new(CollisionScope.DirectMidiNote, group.Key.Id, note.StartTick, note.Key)],
                    order++,
                    group.Key.Notes,
                    note);
            }
        }

        foreach (IGrouping<MidiSegment, DirectMidiEventCollisionTarget> group in
            scopes.DirectMidiEventTargets
                .Where(target => !fullDirectMidiSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<DirectMidiEventStartKey> keys = group.Select(target => new DirectMidiEventStartKey(
                    target.Tick,
                    target.Kind,
                    DirectMidiEventUsesData1Selector(target.Kind) ? target.Data1 : 0))
                .ToHashSet();
            foreach (DirectMidiChannelEvent value in group.Key.ChannelEvents.QueryStartKeys(keys))
            {
                int selector = DirectMidiEventUsesData1Selector(value.Kind)
                    ? value.Data1 + 1
                    : 0;
                int detail = ((int)value.Kind << 16) | selector;
                yield return CollisionCandidate.ForDirectMidiEvent(
                    value.Id,
                    [new(CollisionScope.DirectMidiEventPoint, group.Key.Id, value.Tick, detail)],
                    order++,
                    group.Key.ChannelEvents,
                    value);
            }
        }
    }

    private static bool DirectMidiEventUsesData1Selector(
        DirectMidiChannelEventKind kind) =>
        kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff;

    private static bool ContainsId(MidoraId[]? values, MidoraId id) =>
        values is not null && Array.IndexOf(values, id) >= 0;

    private sealed class CollisionResolvingPreparedEdit(
        IPreparedProjectEdit source,
        CollisionBaseline baseline,
        CollisionScopeSet scopes) : IPreparedTimelineSelectionEdit, IDisposable
    {
        private IReadOnlyList<CollisionRemoval>? _lastRemovals;
        private bool _applyStarted;

        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public bool HasPreparedSelection => source is IPreparedTimelineSelectionEdit { HasPreparedSelection: true };
        public PreparedTimelineSelection PreparedSelection =>
            GetPreparedSelection(source);

        public void Apply(MidoraProject project)
        {
            _applyStarted = true;
            _lastRemovals = [];
            source.Apply(project);
            _lastRemovals = Resolve(scopes, baseline);
        }

        public void Undo(MidoraProject project)
        {
            if (!_applyStarted || _lastRemovals is null)
            {
                throw new InvalidOperationException(
                    "An exact-collision edit cannot be undone before Apply.");
            }
            foreach (CollisionRemoval removal in _lastRemovals.Reverse())
            {
                removal.Restore();
            }
            source.Undo(project);
            _lastRemovals = null;
            _applyStarted = false;
        }

        public void Dispose()
        {
            if (source is IDisposable disposable) disposable.Dispose();
        }
    }

    private sealed class TargetedCollisionPreparedEdit(
        IPreparedProjectEdit source,
        IReadOnlyList<TargetedLogicalNoteBaseline> logicalBaselines,
        IReadOnlyList<TargetedTemplateNoteBaseline> templateBaselines,
        IReadOnlyList<TargetedDirectMidiNoteBaseline> directBaselines,
        IReadOnlyList<TargetedLogicalParameterPointBaseline> logicalParameterBaselines,
        IReadOnlyList<TargetedTemplateEventPointBaseline> templateEventBaselines,
        IReadOnlyList<TargetedDirectMidiEventBaseline> directEventBaselines,
        IReadOnlyList<TargetedValueCurvePointBaseline> valueCurveBaselines)
        : IPreparedTimelineSelectionEdit, IDisposable
    {
        private IReadOnlyList<CollisionRemoval>? _lastRemovals;
        private bool _applyStarted;

        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public bool HasPreparedSelection => source is IPreparedTimelineSelectionEdit { HasPreparedSelection: true };
        public PreparedTimelineSelection PreparedSelection =>
            GetPreparedSelection(source);

        public void Apply(MidoraProject project)
        {
            _applyStarted = true;
            _lastRemovals = [];
            source.Apply(project);
            _lastRemovals = ResolveTargetedLogicalNotes(logicalBaselines)
                .Concat(ResolveTargetedTemplateNotes(templateBaselines))
                .Concat(ResolveTargetedDirectMidiNotes(directBaselines))
                .Concat(ResolveTargetedLogicalParameterPoints(logicalParameterBaselines))
                .Concat(ResolveTargetedTemplateEventPoints(templateEventBaselines))
                .Concat(ResolveTargetedDirectMidiEvents(directEventBaselines))
                .Concat(ResolveTargetedValueCurvePoints(valueCurveBaselines))
                .ToArray();
        }

        public void Undo(MidoraProject project)
        {
            if (!_applyStarted || _lastRemovals is null)
            {
                throw new InvalidOperationException(
                    "An exact-collision edit cannot be undone before Apply.");
            }
            foreach (CollisionRemoval removal in _lastRemovals.Reverse())
                removal.Restore();
            source.Undo(project);
            _lastRemovals = null;
            _applyStarted = false;
        }

        public void Dispose()
        {
            if (source is IDisposable disposable) disposable.Dispose();
        }
    }

    private sealed record CollisionScopedPreparedEdit(
        IPreparedProjectEdit Source,
        CollisionScopeSet Scopes) : IPreparedTimelineSelectionEdit, IDisposable
    {
        public bool HasChanges => Source.HasChanges;
        public ProjectChangeSet Changes => Source.Changes;
        public bool HasPreparedSelection => Source is IPreparedTimelineSelectionEdit { HasPreparedSelection: true };
        public PreparedTimelineSelection PreparedSelection =>
            GetPreparedSelection(Source);
        public void Apply(MidoraProject project) => Source.Apply(project);
        public void Undo(MidoraProject project) => Source.Undo(project);
        public void Dispose()
        {
            if (Source is IDisposable disposable) disposable.Dispose();
        }
    }

    private static PreparedTimelineSelection GetPreparedSelection(
        IPreparedProjectEdit source) =>
        source is IPreparedTimelineSelectionEdit selectionEdit
            ? selectionEdit.PreparedSelection
            : throw new InvalidOperationException(
                "A collision-scoped Timeline selection edit did not expose its frozen selection result.");

    private sealed record CollisionScopeSet(
        IReadOnlyCollection<Segment> LogicalNoteSegments,
        IReadOnlyCollection<LogicalParameterLane> LogicalParameterLanes,
        IReadOnlyCollection<SubVoice> SubVoices,
        IReadOnlyCollection<ValueCurve> ValueCurves,
        IReadOnlyCollection<MidiSegment> DirectMidiSegments,
        IReadOnlyCollection<LogicalNoteCollisionTarget> LogicalNoteTargets,
        IReadOnlyCollection<TemplateNoteCollisionTarget> TemplateNoteTargets,
        IReadOnlyCollection<DirectMidiNoteCollisionTarget> DirectMidiNoteTargets,
        IReadOnlyCollection<DirectMidiEventCollisionTarget> DirectMidiEventTargets,
        IReadOnlyCollection<LogicalParameterPointCollisionTarget> LogicalParameterPointTargets,
        IReadOnlyCollection<TemplateEventPointCollisionTarget> TemplateEventPointTargets,
        IReadOnlyCollection<ValueCurvePointCollisionTarget> ValueCurvePointTargets)
    {
        public bool IsEmpty => LogicalNoteSegments.Count == 0
            && LogicalParameterLanes.Count == 0
            && SubVoices.Count == 0
            && ValueCurves.Count == 0
            && DirectMidiSegments.Count == 0
            && LogicalNoteTargets.Count == 0
            && TemplateNoteTargets.Count == 0
            && DirectMidiNoteTargets.Count == 0
            && DirectMidiEventTargets.Count == 0
            && LogicalParameterPointTargets.Count == 0
            && TemplateEventPointTargets.Count == 0
            && ValueCurvePointTargets.Count == 0;

        public bool HasOnlyTargets =>
            LogicalNoteTargets.Count
                + TemplateNoteTargets.Count
                + DirectMidiNoteTargets.Count
                + DirectMidiEventTargets.Count
                + LogicalParameterPointTargets.Count
                + TemplateEventPointTargets.Count
                + ValueCurvePointTargets.Count != 0
            && LogicalNoteSegments.Count == 0
            && LogicalParameterLanes.Count == 0
            && SubVoices.Count == 0
            && ValueCurves.Count == 0
            && DirectMidiSegments.Count == 0;

        public static CollisionScopeSet Merge(CollisionScopeSet left, CollisionScopeSet right) =>
            new(
                left.LogicalNoteSegments.Concat(right.LogicalNoteSegments).Distinct().ToArray(),
                left.LogicalParameterLanes.Concat(right.LogicalParameterLanes).Distinct().ToArray(),
                left.SubVoices.Concat(right.SubVoices).Distinct().ToArray(),
                left.ValueCurves.Concat(right.ValueCurves).Distinct().ToArray(),
                left.DirectMidiSegments.Concat(right.DirectMidiSegments).Distinct().ToArray(),
                left.LogicalNoteTargets.Concat(right.LogicalNoteTargets).Distinct().ToArray(),
                left.TemplateNoteTargets.Concat(right.TemplateNoteTargets).Distinct().ToArray(),
                left.DirectMidiNoteTargets.Concat(right.DirectMidiNoteTargets).Distinct().ToArray(),
                left.DirectMidiEventTargets.Concat(right.DirectMidiEventTargets).Distinct().ToArray(),
                left.LogicalParameterPointTargets.Concat(right.LogicalParameterPointTargets).Distinct().ToArray(),
                left.TemplateEventPointTargets.Concat(right.TemplateEventPointTargets).Distinct().ToArray(),
                left.ValueCurvePointTargets.Concat(right.ValueCurvePointTargets).Distinct().ToArray());
    }

    private sealed record CollisionBaseline(
        IReadOnlyDictionary<MidoraId, HashSet<CollisionKey>> KeysById)
    {
        public bool Contains(MidoraId id, CollisionKey key) =>
            KeysById.TryGetValue(id, out HashSet<CollisionKey>? keys)
            && keys.Contains(key);
    }

    private sealed class CollisionCandidate(
        MidoraId id,
        IReadOnlyList<CollisionKey> keys,
        long order,
        Func<CollisionRemoval> remove)
    {
        public MidoraId Id { get; } = id;
        public IReadOnlyList<CollisionKey> Keys { get; } = keys;
        public long Order { get; } = order;
        public CollisionRemoval Remove() => remove();

        public static CollisionCandidate ForList<T>(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            IList<T> values,
            T value,
            string name)
            where T : class =>
            new(id, keys, order, () =>
            {
                int index = values.IndexOf(value);
                if (index < 0)
                {
                    throw new InvalidOperationException($"The conflicting {name} is no longer present.");
                }
                values.RemoveAt(index);
                return new(
                    () =>
                    {
                        if (values.Contains(value))
                        {
                            throw new InvalidOperationException($"The conflicting {name} is already restored.");
                        }
                        values.Insert(Math.Clamp(index, 0, values.Count), value);
                    });
            });

        public static CollisionCandidate ForCollection<T>(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            Collection<T> values,
            T value,
            string name)
            where T : class =>
            new(id, keys, order, () =>
            {
                int index = values.IndexOf(value);
                if (index < 0)
                {
                    throw new InvalidOperationException($"The conflicting {name} is no longer present.");
                }
                values.RemoveAt(index);
                return new(
                    () =>
                    {
                        if (values.Contains(value))
                        {
                            throw new InvalidOperationException($"The conflicting {name} is already restored.");
                        }
                        values.Insert(Math.Clamp(index, 0, values.Count), value);
                    });
            });

        public static CollisionCandidate ForDirectMidiNote(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            DirectMidiNoteCollection values,
            DirectMidiNote value) =>
            new(id, keys, order, () => new(values.RemoveForExactCollision(value)));

        public static CollisionCandidate ForDirectMidiEvent(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            DirectMidiChannelEventCollection values,
            DirectMidiChannelEvent value) =>
            new(id, keys, order, () => new(values.RemoveForExactCollision(value)));
    }

    private sealed record CollisionRemoval(Action Restore);

    private sealed record TargetedDirectMidiNoteBaseline(
        MidiSegment Segment,
        IReadOnlySet<DirectMidiNoteStartKey> Keys,
        IReadOnlyDictionary<DirectMidiNoteStartKey, MidoraId[]> Occupants);

    private sealed record TargetedLogicalNoteBaseline(
        Segment Segment,
        IReadOnlySet<TargetedNoteKey> Keys,
        IReadOnlyDictionary<TargetedNoteKey, MidoraId[]> Occupants);

    private sealed record TargetedTemplateNoteBaseline(
        SubVoice SubVoice,
        IReadOnlySet<TargetedNoteKey> Keys,
        IReadOnlyDictionary<TargetedNoteKey, MidoraId[]> Occupants);

    private sealed record TargetedLogicalParameterPointBaseline(
        LogicalParameterLane Lane,
        IReadOnlySet<long> Ticks,
        IReadOnlyDictionary<long, MidoraId[]> Occupants);

    private sealed record TargetedTemplateEventPointBaseline(
        SubVoice SubVoice,
        IReadOnlySet<TargetedPointKey> Keys,
        IReadOnlyDictionary<TargetedPointKey, MidoraId[]> Occupants);

    private sealed record TargetedDirectMidiEventBaseline(
        MidiSegment Segment,
        IReadOnlySet<DirectMidiEventStartKey> Keys,
        IReadOnlyDictionary<DirectMidiEventStartKey, MidoraId[]> Occupants);

    private sealed record TargetedValueCurvePointBaseline(
        ValueCurve Curve,
        IReadOnlySet<long> Ticks,
        IReadOnlyDictionary<long, MidoraId[]> Occupants);

    private readonly record struct TargetedDirectMidiNoteCandidate(
        DirectMidiNoteCollection Collection,
        DirectMidiNote Note,
        long Order);

    private readonly record struct TargetedLogicalNoteCandidate(
        LogicalNoteCollection Collection,
        LogicalNote Note,
        long Order);

    private readonly record struct TargetedTemplateNoteCandidate(
        TemplateEventCollection Collection,
        TemplateEvent Note,
        long Order);

    private readonly record struct TargetedCurvePointCandidate(
        CurvePointCollection Collection,
        CurvePoint Value,
        long Order);

    private readonly record struct TargetedTemplatePointCandidate(
        TargetedTemplateEventPointBaseline Baseline,
        TemplateEvent Value,
        long Order);

    private readonly record struct TargetedDirectMidiEventCandidate(
        DirectMidiChannelEventCollection Collection,
        DirectMidiChannelEvent Value,
        long Order);

    private readonly record struct TargetedNoteKey(long Tick, int Key);
    private readonly record struct TargetedPointKey(long Tick, int Detail);

    private readonly record struct CollisionKey(
        CollisionScope Scope,
        MidoraId OwnerId,
        long Tick,
        int Detail);

    private enum CollisionScope
    {
        LogicalNote,
        LogicalParameterPoint,
        TemplateNote,
        TemplateEventPoint,
        ValueCurvePoint,
        DirectMidiNote,
        DirectMidiEventPoint
    }

}
