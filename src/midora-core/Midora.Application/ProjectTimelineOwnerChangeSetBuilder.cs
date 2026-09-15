using System.Collections.Immutable;
using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Builds Stage 4 timeline receipts from the already planned object set.  It
/// resolves only candidate Stable IDs through the old/new source indexes; it
/// never enumerates the complete owner merely to discover an invalidation
/// footprint.
/// </summary>
internal static class ProjectTimelineOwnerChangeSetBuilder
{
    public static bool TryAddLogicalNoteValueEdits(ProjectChangeSet changes, Segment previous, Segment current,
        IImmutableTimelineValueSource<TimelineValueEdit<LogicalNoteSnapshotValue>> edits) => TryAddValueOnlyEdits(
            changes, previous.Id, ProjectTimelineOwnerKind.LogicalSegment, ProjectTimelineSourceKind.LogicalNotes,
            previous.Notes.Generation, current.Notes.Generation, previous.Notes.CreateQuerySnapshot(), edits,
            static (a, b) => a.Id == b.Id && a.StartTick == b.StartTick && a.LengthTicks == b.LengthTicks && a.Note == b.Note,
            static value => NoteRange(value.StartTick, value.LengthTicks, value.Note));

    public static bool TryAddTemplateNoteValueEdits(ProjectChangeSet changes, SubVoice previous, SubVoice current,
        IImmutableTimelineValueSource<TimelineValueEdit<TemplateEventSnapshotValue>> edits) => TryAddValueOnlyEdits(
            changes, previous.Id, ProjectTimelineOwnerKind.SubVoice, ProjectTimelineSourceKind.SubVoiceEvents,
            previous.Events.Generation, current.Events.Generation, previous.Events.CreateQuerySnapshot(), edits,
            static (a, b) => a.Kind == TemplateEventKind.Note && b.Kind == TemplateEventKind.Note && a.Id == b.Id
                && a.Tick == b.Tick && a.LengthTicks == b.LengthTicks && a.Number == b.Number,
            static value => NoteRange(value.Tick, value.LengthTicks, value.Number));

    private static bool TryAddValueOnlyEdits<T>(ProjectChangeSet changes, MidoraId ownerId,
        ProjectTimelineOwnerKind ownerKind, ProjectTimelineSourceKind sourceKind, long previousRevision,
        long currentRevision, ITimelineObjectSource<T> snapshot, IImmutableTimelineValueSource<TimelineValueEdit<T>> edits,
        Func<T, T, bool> sameGeometry, Func<T, ProjectTimelineContentChangeRange> range) where T : unmanaged
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default);
        using var footprints = new BoundedEditRecordStore<ProjectTimelineContentChangeRange>(scope.Resources);
        var ordinals = new BoundedEditRecordStore<TimelineOrdinalRange>(scope.Resources);
        bool ordinalsTransferred = false;
        try
        {
            int first = -1, end = -1;
            foreach (var edit in edits)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (edit.IsDeleted) return false;
                T original = snapshot.GetByOrdinal(edit.Ordinal);
                if (!sameGeometry(original, edit.Replacement)) return false;
                if (EqualityComparer<T>.Default.Equals(original, edit.Replacement)) continue;
                if (edit.Ordinal < end) throw new InvalidOperationException("Value-only receipt edits must be unique and sorted.");
                if (edit.Ordinal != end)
                {
                    if (first >= 0) ordinals.Add(new(first, end - first), scope.Token);
                    first = edit.Ordinal;
                }
                end = checked(edit.Ordinal + 1);
                footprints.Add(range(original), scope.Token);
            }
            if (first < 0) return true;
            ordinals.Add(new(first, end - first), scope.Token);
            footprints.Seal(); ordinals.Seal();
            var ranges = MergeRanges(footprints);
            var ordinalRanges = FreezeResult(ordinals, scope.Token);
            ordinalsTransferred = true;
            // Geometry and ordinal identity are unchanged: old, new and dirty
            // footprints are exactly equal and may share one immutable result.
            Append(changes, ownerId, ownerKind, new ProjectTimelineSourceChange(sourceKind, null,
                previousRevision, currentRevision, ordinalRanges, [], ranges)
            {
                PreviousContentRanges = ranges, CurrentContentRanges = ranges,
                CurrentOrdinalRanges = ordinalRanges, CurrentPageIndices = []
            });
            return true;
        }
        finally { if (!ordinalsTransferred) ordinals.Dispose(); }
    }

    public static void AddLogicalNotes(
        ProjectChangeSet changes,
        Segment previous,
        Segment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalNotes,
            laneOrCurveId: null,
            previous.Notes.Generation,
            current.Notes.Generation,
            candidateIds,
            ids => ResolveLogicalNotes(previous.Notes, ids),
            ids => ResolveLogicalNotes(current.Notes, ids));

    public static void AddDirectNotes(
        ProjectChangeSet changes,
        MidiSegment previous,
        MidiSegment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiNotes,
            laneOrCurveId: null,
            previous.Notes.Generation,
            current.Notes.Generation,
            candidateIds,
            ids => ResolveDirectNotes(previous.Notes, ids),
            ids => ResolveDirectNotes(current.Notes, ids));

    public static void AddSubVoiceEvents(
        ProjectChangeSet changes,
        SubVoice previous,
        SubVoice current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.SubVoice,
            ProjectTimelineSourceKind.SubVoiceEvents,
            laneOrCurveId: null,
            previous.Events.Generation,
            current.Events.Generation,
            ReconcileTemplateInstrumentChanges(previous, current, candidateIds),
            ids => ResolveTemplateEvents(previous.Events, ids),
            ids => ResolveTemplateEvents(current.Events, ids));

    public static void AddLogicalParameterPoints(
        ProjectChangeSet changes,
        MidoraId ownerId,
        LogicalParameterLane previous,
        LogicalParameterLane current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            ownerId,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalParameterPoints,
            previous.Id,
            previous.Points.Generation,
            current.Points.Generation,
            candidateIds,
            ids => ResolveCurvePoints(previous.Points, ids),
            ids => ResolveCurvePoints(current.Points, ids));

    public static void AddDirectEvents(
        ProjectChangeSet changes,
        MidiSegment previous,
        MidiSegment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiChannelEvents,
            laneOrCurveId: null,
            previous.ChannelEvents.Generation,
            current.ChannelEvents.Generation,
            ReconcileDirectInstrumentChanges(previous, current, candidateIds),
            ids => ResolveDirectEvents(previous.ChannelEvents, ids),
            ids => ResolveDirectEvents(current.ChannelEvents, ids));

    private static IEnumerable<MidoraId> ReconcileDirectInstrumentChanges(MidiSegment previous,
        MidiSegment current, IEnumerable<MidoraId> affected)
    {
        current.InstrumentChanges = previous.InstrumentChanges;
        if (previous.InstrumentChanges.Count == 0)
        {
            foreach (var id in affected) yield return id;
            current.InstrumentChanges = current.InstrumentChanges.ValidatedAt(current.ChannelEvents.Generation);
            yield break;
        }
        var source = current.ChannelEvents.CreateQuerySnapshot();
        using var recentBudget = BulkEditPreparationContext.Current!.Resources.ReserveWorking(131072);
        var recent = new HashSet<MidoraId>(4096);
        using var invalid = new BoundedEditRecordStore<MidoraId>(BulkEditPreparationContext.Current!.Resources);
        foreach (var id in affected)
        {
            if (current.InstrumentChanges.TryGetByMember(id, out var group) && recent.Add(group.Id))
            {
                if (!InstrumentChangeResolver.TryRead(source, group, out _)) invalid.Add(group.Id, BulkEditPreparationContext.Current.Token);
                if (recent.Count == 4096) recent.Clear();
            }
            yield return id;
        }
        invalid.Seal();
        if (invalid.Count != 0) current.InstrumentChanges = BoundedInstrumentChangeStorage.RemoveAffected(current.InstrumentChanges, invalid);
        current.InstrumentChanges = current.InstrumentChanges.ValidatedAt(current.ChannelEvents.Generation);
    }

    private static IEnumerable<MidoraId> ReconcileTemplateInstrumentChanges(SubVoice previous,
        SubVoice current, IEnumerable<MidoraId> affected)
    {
        current.InstrumentChanges = previous.InstrumentChanges;
        if (previous.InstrumentChanges.Count == 0)
        {
            foreach (var id in affected) yield return id;
            current.InstrumentChanges = current.InstrumentChanges.ValidatedAt(current.Events.Generation);
            yield break;
        }
        var source = current.Events.CreateQuerySnapshot();
        InstrumentChangeSelectionQuery.PrepareSource(source);
        using var recentBudget = BulkEditPreparationContext.Current!.Resources.ReserveWorking(131072);
        var recent = new HashSet<MidoraId>(4096);
        using var invalid = new BoundedEditRecordStore<MidoraId>(BulkEditPreparationContext.Current!.Resources);
        foreach (var id in affected)
        {
            if (current.InstrumentChanges.TryGetByMember(id, out var group) && recent.Add(group.Id))
            {
                if (!InstrumentChangeResolver.TryRead(source, group, out _)) invalid.Add(group.Id, BulkEditPreparationContext.Current.Token);
                if (recent.Count == 4096) recent.Clear();
            }
            yield return id;
        }
        invalid.Seal();
        if (invalid.Count != 0) current.InstrumentChanges = BoundedInstrumentChangeStorage.RemoveAffected(current.InstrumentChanges, invalid);
        current.InstrumentChanges = current.InstrumentChanges.ValidatedAt(current.Events.Generation);
    }

    private static IReadOnlyList<ResolvedValue<LogicalNoteSnapshotValue>> ResolveLogicalNotes(
        LogicalNoteCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        LogicalNoteQuerySnapshot snapshot = source.CreateQuerySnapshot();
        snapshot.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, BulkEditPreparationContext.Current!.Token);
        List<ResolvedValue<LogicalNoteSnapshotValue>> result = [];
        foreach (MidoraId id in candidateIds)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            if (!snapshot.TryFindOrdinalById(id, out int ordinal)) continue;
            LogicalNoteSnapshotValue value = snapshot.GetByOrdinal(ordinal);
            result.Add(new(id, ordinal, value, NoteRange(value.StartTick, value.LengthTicks, value.Note)));
        }
        return result;
    }

    private static IReadOnlyList<ResolvedValue<DirectMidiNoteValue>> ResolveDirectNotes(
        DirectMidiNoteCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        DirectMidiNoteObjectSource snapshot = source.CreateObjectSource();
        List<ResolvedValue<DirectMidiNoteValue>> result = [];
        IReadOnlySet<MidoraId> ids = candidateIds as IReadOnlySet<MidoraId> ?? candidateIds.ToHashSet();
        foreach (var match in snapshot.QueryByIds(ids))
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            DirectMidiNoteValue value = match.Value;
            result.Add(new(value.Id, match.Index, value, NoteRange(value.StartTick, value.LengthTicks, value.Key)));
        }
        return result;
    }

    private static IReadOnlyList<ResolvedValue<TemplateEventSnapshotValue>> ResolveTemplateEvents(
        TemplateEventCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        TemplateEventQuerySnapshot snapshot = source.CreateQuerySnapshot();
        snapshot.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, BulkEditPreparationContext.Current!.Token);
        List<ResolvedValue<TemplateEventSnapshotValue>> result = [];
        foreach (MidoraId id in candidateIds)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            if (!snapshot.TryFindOrdinalById(id, out int ordinal)) continue;
            TemplateEventSnapshotValue value = snapshot.GetByOrdinal(ordinal);
            result.Add(new(id, ordinal, value, value.Kind == TemplateEventKind.Note
                ? NoteRange(value.Tick, value.LengthTicks, value.Number) : PointRange(value.Tick, TemplateEventLane(value))));
        }
        return result;
    }

    private static IReadOnlyList<ResolvedValue<CurvePointSnapshotValue>> ResolveCurvePoints(
        CurvePointCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        CurvePointQuerySnapshot snapshot = source.CreateQuerySnapshot();
        snapshot.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, BulkEditPreparationContext.Current!.Token);
        List<ResolvedValue<CurvePointSnapshotValue>> result = [];
        foreach (MidoraId id in candidateIds)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            if (!snapshot.TryFindOrdinalById(id, out int ordinal)) continue;
            CurvePointSnapshotValue value = snapshot.GetByOrdinal(ordinal);
            result.Add(new(id, ordinal, value, PointRange(value.Tick, 0)));
        }
        return result;
    }

    private static IReadOnlyList<ResolvedValue<DirectMidiChannelEventValue>> ResolveDirectEvents(
        DirectMidiChannelEventCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        DirectMidiChannelEventObjectSource snapshot = source.CreateObjectSource();
        List<ResolvedValue<DirectMidiChannelEventValue>> result = [];
        IReadOnlySet<MidoraId> ids = candidateIds as IReadOnlySet<MidoraId> ?? candidateIds.ToHashSet();
        foreach (var match in snapshot.QueryByIds(ids))
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            DirectMidiChannelEventValue value = match.Value;
            result.Add(new(value.Id, match.Index, value, PointRange(value.Tick, DirectEventLane(value))));
        }
        return result;
    }

    private static int DirectEventLane(DirectMidiChannelEventValue value)
    {
        int selector = value.Kind is DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.ControlChange
                ? value.Data1
                : 0;
        return checked(((int)value.Kind * 256) + selector);
    }

    private static int TemplateEventLane(TemplateEventSnapshotValue value)
    {
        int selector = value.Kind is TemplateEventKind.ControlChange
            or TemplateEventKind.RegisteredParameter
            or TemplateEventKind.NonRegisteredParameter
                ? value.Number
                : 0;
        return checked(((int)value.Kind * 256) + selector);
    }

    private static TemplateEventSnapshotValue Snapshot(TemplateEvent value) => new(
        value.Id,
        value.Kind,
        value.Tick,
        value.LengthTicks,
        value.Number,
        value.Value,
        value.SecondaryValue,
        value.HasBankMsb,
        value.HasBankLsb,
        value.FollowPitchDelta);

    private static void Add<TValue>(
        ProjectChangeSet changes,
        MidoraId ownerId,
        ProjectTimelineOwnerKind ownerKind,
        ProjectTimelineSourceKind sourceKind,
        MidoraId? laneOrCurveId,
        long previousRevision,
        long currentRevision,
        IEnumerable<MidoraId> candidateIds,
        Func<IReadOnlyCollection<MidoraId>, IReadOnlyList<ResolvedValue<TValue>>> resolvePrevious,
        Func<IReadOnlyCollection<MidoraId>, IReadOnlyList<ResolvedValue<TValue>>> resolveCurrent)
        where TValue : struct
    {
        using BulkEditPreparationContext scope = BulkEditPreparationContext.Enter(
            BulkEditPreparationContext.Current?.Token ?? default);
        BoundedEditResources resources = scope.Resources;
        using var ids = BoundedEditSort.Sort(candidateIds.Where(static id => id != default),
            Comparer<MidoraId>.Default, resources, scope.Token);
        using var oldOrdinals = new BoundedEditRecordStore<int>(resources);
        using var newOrdinals = new BoundedEditRecordStore<int>(resources);
        using var oldRanges = new BoundedEditRecordStore<ProjectTimelineContentChangeRange>(resources);
        using var newRanges = new BoundedEditRecordStore<ProjectTimelineContentChangeRange>(resources);
        List<MidoraId> page = new(resources.Budget.PageRecordCount);
        MidoraId last = default;
        foreach (MidoraId id in ids.ReadValues(scope.Token))
        {
            if (id == last) continue;
            last = id;
            page.Add(id);
            if (page.Count == resources.Budget.PageRecordCount) ComparePage();
        }
        if (page.Count != 0) ComparePage();
        oldOrdinals.Seal();
        newOrdinals.Seal();
        oldRanges.Seal();
        newRanges.Seal();
        if (oldOrdinals.Count == 0 && newOrdinals.Count == 0) return;

        IReadOnlyList<TimelineOrdinalRange> previousOrdinals = CompressOrdinals(oldOrdinals);
        IReadOnlyList<TimelineOrdinalRange> currentOrdinals = CompressOrdinals(newOrdinals);
        IReadOnlyList<ProjectTimelineContentChangeRange> previousRanges = MergeRanges(oldRanges);
        IReadOnlyList<ProjectTimelineContentChangeRange> currentRanges = MergeRanges(newRanges);
        IReadOnlyList<ProjectTimelineContentChangeRange> invalidationRanges = MergeRanges(
            previousRanges.Concat(currentRanges));

        ProjectTimelineSourceChange source = new(
            sourceKind,
            laneOrCurveId,
            previousRevision,
            currentRevision,
            previousOrdinals,
            PageIndices: [],
            invalidationRanges)
        {
            PreviousContentRanges = previousRanges,
            CurrentContentRanges = currentRanges,
            CurrentOrdinalRanges = currentOrdinals,
            CurrentPageIndices = []
        };
        Append(changes, ownerId, ownerKind, source);

        void ComparePage()
        {
            scope.Token.ThrowIfCancellationRequested();
            Dictionary<MidoraId, ResolvedValue<TValue>> previous =
                resolvePrevious(page).ToDictionary(static value => value.Id);
            Dictionary<MidoraId, ResolvedValue<TValue>> current =
                resolveCurrent(page).ToDictionary(static value => value.Id);
            foreach (MidoraId id in page)
            {
                bool had = previous.TryGetValue(id, out ResolvedValue<TValue> before);
                bool has = current.TryGetValue(id, out ResolvedValue<TValue> after);
                if (had && has && EqualityComparer<TValue>.Default.Equals(before.Value, after.Value)) continue;
                if (had) { oldOrdinals.Add(before.Ordinal, scope.Token); oldRanges.Add(before.Range, scope.Token); }
                if (has) { newOrdinals.Add(after.Ordinal, scope.Token); newRanges.Add(after.Range, scope.Token); }
            }
            page.Clear();
        }
    }

    private static void Append(
        ProjectChangeSet changes,
        MidoraId ownerId,
        ProjectTimelineOwnerKind ownerKind,
        ProjectTimelineSourceChange source)
    {
        for (int index = 0; index < changes.TimelineOwnerChanges.Count; index++)
        {
            ProjectTimelineOwnerChangeSet existing = changes.TimelineOwnerChanges[index];
            if (existing.OwnerId != ownerId || existing.OwnerKind != ownerKind) continue;
            changes.TimelineOwnerChanges[index] = existing with
            {
                Sources = existing.Sources.Add(source)
            };
            return;
        }
        changes.TimelineOwnerChanges.Add(new(ownerId, ownerKind, [source]));
    }

    private static MidoraId[] FreezeIds(IEnumerable<MidoraId> ids) => ids
        .Where(static id => id != default)
        .Distinct()
        .ToArray();

    private static IReadOnlyList<TimelineOrdinalRange> CompressOrdinals(
        IEnumerable<int> ordinals)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        using var ordered = BoundedEditSort.Sort(ordinals, Comparer<int>.Default, scope.Resources, scope.Token);
        if (ordered.Count == 0) return [];
        var result = new BoundedEditRecordStore<TimelineOrdinalRange>(scope.Resources);
        try
        {
            int first = -1, end = -1;
            foreach (int ordinal in ordered.ReadValues(scope.Token))
            {
                if (first == -1) { first = ordinal; end = checked(ordinal + 1); continue; }
                if (ordinal < end) continue;
                if (ordinal == end) { end++; continue; }
                result.Add(new(first, end - first), scope.Token);
                first = ordinal;
                end = checked(ordinal + 1);
            }
            result.Add(new(first, end - first));
            result.Seal();
            return FreezeResult(result, scope.Token);
        }
        catch { result.Dispose(); throw; }
    }

    private static IReadOnlyList<ProjectTimelineContentChangeRange> MergeRanges(
        IEnumerable<ProjectTimelineContentChangeRange> ranges)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        using var ordered = BoundedEditSort.Sort(ranges, Comparer<ProjectTimelineContentChangeRange>.Create((a, b) =>
        {
            int value = a.MinimumLane.CompareTo(b.MinimumLane);
            if (value == 0) value = a.MaximumLane.CompareTo(b.MaximumLane);
            if (value == 0) value = a.StartTick.CompareTo(b.StartTick);
            return value == 0 ? a.EndTick.CompareTo(b.EndTick) : value;
        }), scope.Resources, scope.Token);
        if (ordered.Count == 0) return [];
        var result = new BoundedEditRecordStore<ProjectTimelineContentChangeRange>(scope.Resources);
        try
        {
            ProjectTimelineContentChangeRange? current = null;
            foreach (ProjectTimelineContentChangeRange next in ordered.ReadValues(scope.Token))
            {
                if (current is not { } value) { current = next; continue; }
                if (next.MinimumLane == value.MinimumLane && next.MaximumLane == value.MaximumLane
                    && next.StartTick <= value.EndTick)
                {
                    current = new(value.StartTick, Math.Max(value.EndTick, next.EndTick), value.MinimumLane, value.MaximumLane);
                    continue;
                }
                result.Add(value, scope.Token);
                current = next;
            }
            if (current is { } lastRange) result.Add(lastRange, scope.Token);
            result.Seal();
            return FreezeResult(result, scope.Token);
        }
        catch { result.Dispose(); throw; }
    }

    private static IReadOnlyList<T> FreezeResult<T>(BoundedEditRecordStore<T> result,
        CancellationToken cancellationToken) where T : unmanaged
    {
        if (result.Count <= result.PageCapacity)
        {
            T[] values = result.ReadValues(cancellationToken).ToArray();
            result.Dispose();
            return values;
        }
        result.SpillResidentPages(cancellationToken);
        return new BoundedImmutableValueSource<T>(result);
    }

    private static ProjectTimelineContentChangeRange NoteRange(
        long startTick,
        long lengthTicks,
        int lane) => new(
            startTick,
            SaturatingEnd(startTick, Math.Max(1, lengthTicks)),
            lane,
            lane);

    private static ProjectTimelineContentChangeRange PointRange(long tick, int lane)
    {
        if (tick == long.MaxValue)
            throw new InvalidOperationException("A timeline point at Int64.MaxValue has no representable half-open footprint.");
        return new(tick, tick + 1, lane, lane);
    }

    private static long SaturatingEnd(long startTick, long lengthTicks) =>
        lengthTicks > 0 && startTick <= long.MaxValue - lengthTicks
            ? startTick + lengthTicks
            : long.MaxValue;

    private readonly record struct ResolvedValue<TValue>(
        MidoraId Id,
        int Ordinal,
        TValue Value,
        ProjectTimelineContentChangeRange Range)
        where TValue : struct;
}
