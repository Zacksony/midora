using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static readonly TimeSpan BatchExpressionTimeout = TimeSpan.FromSeconds(10);

    public static IProjectEditCommand BatchEditLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit logical notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            SegmentLocation location = FindSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, location, noteIds, values =>
                {
                    long origin = values.Min(v => v.Value.StartTick);
                    Stopwatch timer = Stopwatch.StartNew();
                    return value =>
                    {
                        var result = EvaluateLogicalNote(new(value.StartTick, value.LengthTicks, value.Note, value.Velocity), origin, program, timer);
                        var changed = result.Replacement;
                        return result.Discard ? null : value with { StartTick = changed.StartTick, LengthTicks = changed.LengthTicks,
                            Note = changed.Note, Velocity = changed.Velocity };
                    };
                }, expandWindow: true);
            SelectedLogicalNote[] selected = SelectLogicalNotes(location.Segment, noteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            long relativeOrigin = old.Min(value => value.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            BatchLogicalNoteResult[] results = old.Select(value => EvaluateLogicalNote(
                value,
                relativeOrigin,
                program,
                clock)).ToArray();
            SegmentTransformEntry entry = new(
                location.Track,
                location.Segment,
                location.Track.Segments.IndexOf(location.Segment));
            SegmentWindowTransform window = PlanLogicalNoteBatchWindow(entry, results);
            ValidateSegmentTransformWindows(project, [window]);
            return ResolveTargetedExactLogicalNoteCollisions(
                PrepareBatchLogicalNotes(
                    location.Track.Id,
                    location.Segment,
                    selected,
                    old,
                    results,
                    window),
                results
                    .Where(static value => !value.Discard)
                    .Select(value => new LogicalNoteCollisionTarget(
                        location.Segment,
                        value.Replacement.StartTick,
                        value.Replacement.Note)));
        });

    public static IProjectEditCommand BatchEditTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit template notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, values =>
                {
                    long origin = values.Min(v => v.Value.Tick);
                    Stopwatch timer = Stopwatch.StartNew();
                    return value =>
                    {
                        var result = EvaluateTemplateNote(new(value.Kind, value.Tick, value.LengthTicks,
                            value.Number, value.Value, value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta), origin, program, timer);
                        var changed = result.Replacement;
                        return result.Discard ? null : value with { Tick = changed.Tick, LengthTicks = changed.LengthTicks,
                            Number = changed.Number, Value = changed.Value };
                    };
                });
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEventTransformEntry[] selected = voice.Events
                .ResolveByIdsWithIndicesInCollectionOrder(requested)
                .Select(static value => new TemplateEventTransformEntry(
                    value.Value,
                    value.Index,
                    CaptureTemplateEvent(value.Value)))
                .ToArray();
            if (selected.Length != requested.Count
                || selected.Any(value => value.Event.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }
            long relativeOrigin = selected.Min(value => value.Old.Tick);
            Stopwatch clock = Stopwatch.StartNew();
            BatchTemplateNoteResult[] results = selected.Select(value => EvaluateTemplateNote(
                value.Old,
                relativeOrigin,
                program,
                clock)).ToArray();
            return ResolveTargetedExactTemplateNoteCollisions(
                PrepareBatchTemplateNotes(
                    eventInstrumentId,
                    instrument,
                    voice,
                    selected,
                    results),
                results
                    .Where(static value => !value.Discard)
                    .Select(value => new TemplateNoteCollisionTarget(
                        voice,
                        value.Replacement.Tick,
                        value.Replacement.Number)));
        });

    public static IProjectEditCommand BatchEditSegmentExposedNotes(
        IReadOnlyCollection<MidoraId> segmentIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit exposed Segment notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            HashSet<MidoraId> requested = ValidateBatchIds(segmentIds, nameof(segmentIds), "Segment");
            SegmentTransformEntry[] segments = project.Tracks
                .SelectMany(track => track.Segments.Select((segment, index) =>
                    new SegmentTransformEntry(track, segment, index)))
                .Where(value => requested.Contains(value.Segment.Id))
                .ToArray();
            if (segments.Length != requested.Count)
            {
                throw new ArgumentException("Every selected ID must identify a Segment.", nameof(segmentIds));
            }
            if (RequiresBoundedLogicalSegmentContent(segments))
                return PrepareBoundedLogicalSegmentTransform(project, segments, null,
                    SegmentSelectionTransformScope.ExposedContentOnly, program: program);
            List<(SegmentTransformEntry Segment, SelectedLogicalNote Note)> exposed = [];
            foreach (SegmentTransformEntry segment in segments)
            {
                long left = segment.Segment.ContentOffsetTick;
                long right = segment.Segment.ContentEndTick;
                MidoraId[] exposedIds = segment.Segment.Notes
                    .CreateQuerySnapshot()
                    .QueryValues(left, right)
                    .Select(static value => value.Id)
                    .ToArray();
                exposed.AddRange(segment.Segment.Notes
                    .ResolveByIdsWithIndicesInCollectionOrder(exposedIds)
                    .Select(value => (
                        segment,
                        new SelectedLogicalNote(value.Value, value.Index))));
            }
            if (exposed.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected Segments expose no Logical Notes to batch edit.");
            }
            long relativeOrigin = exposed.Min(value => value.Note.Note.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            BatchLogicalNoteResult[] results = exposed.Select(value => EvaluateLogicalNote(
                Snapshot(value.Note.Note),
                relativeOrigin,
                program,
                clock)).ToArray();
            Dictionary<Segment, List<int>> indicesBySegment = segments
                .ToDictionary(static value => value.Segment, static _ => new List<int>());
            for (int index = 0; index < exposed.Count; index++)
            {
                indicesBySegment[exposed[index].Segment.Segment].Add(index);
            }
            SegmentWindowTransform[] windows = segments.Select(segment =>
                PlanLogicalNoteBatchWindow(
                    segment,
                    results,
                    indicesBySegment[segment.Segment])).ToArray();
            ValidateSegmentTransformWindows(project, windows);
            return ResolveTargetedExactLogicalNoteCollisions(
                PrepareBatchLogicalNotesAcrossSegments(
                    segments,
                    exposed,
                    results,
                    windows),
                exposed.Select((value, index) => (value.Segment.Segment, Result: results[index]))
                    .Where(static value => !value.Result.Discard)
                    .Select(static value => new LogicalNoteCollisionTarget(
                        value.Segment,
                        value.Result.Replacement.StartTick,
                        value.Result.Replacement.Note)));
        });

    public static IProjectEditCommand BatchEditLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        BatchEditExpressionProgram program) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedLogicalPoints("Batch edit logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Batch, program: program)
        :
        Command("Batch edit logical parameter points", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidatePointBatchProgram(program);
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            if (lane.Points.Count >= BoundedPointThreshold)
                return BoundedLogicalPoints("Batch edit logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Batch, program: program).Prepare(project);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                location.Track,
                lane.ParameterId);
            ValidateDirectRange(program, BatchEditField.PointValue, definition.Minimum, definition.Maximum);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            long relativeOrigin = selected.Min(value => value.Point.Tick);
            Stopwatch clock = Stopwatch.StartNew();
            BatchCurvePointResult[] results = selected.Select(value =>
            {
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: 0,
                        PointValue: value.Point.Value,
                        KeyNumber: 0,
                        Gate: 0,
                        Tick: value.Point.Tick,
                        RelativeTick: checked(value.Point.Tick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                double pointValue = NormalizeLogicalParameterBatchValue(
                    definition,
                    calculated.PointValue);
                CurvePoint? replacement = tick is long resolvedTick
                    ? new CurvePoint(
                        project,
                        value.Point.Id,
                        resolvedTick,
                        pointValue,
                        CurveInterpolation.Step)
                    : null;
                return new BatchCurvePointResult(value, replacement);
            }).ToArray();
            SegmentTransformEntry segmentEntry = new(
                location.Track,
                location.Segment,
                location.Track.Segments.IndexOf(location.Segment));
            SegmentWindowTransform window = PlanCurvePointBatchWindow(segmentEntry, results);
            ValidateSegmentTransformWindows(project, [window]);
            return ResolveTargetedExactLogicalParameterPointCollisions(
                PrepareBatchCurvePoints(
                    TrackChange(location.Track.Id),
                    lane.Points,
                    results,
                    "Logical Parameter point",
                    window),
                lane,
                results
                    .Where(static value => value.Replacement is not null)
                    .Select(static value => value.Replacement!.Tick));
        });

    public static IProjectEditCommand BatchEditSubVoiceEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target,
        BatchEditExpressionProgram program) =>
        eventIds.Count >= BoundedPointThreshold
        ? BoundedTemplatePoints("Batch edit SubVoice event points", eventInstrumentId, subVoiceId,
            eventIds, BoundedPointOperation.Batch, target, program: program)
        :
        Command("Batch edit SubVoice event points", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidatePointBatchProgram(program);
            (double minimum, double maximum) = MidiEventTargetRange(target);
            ValidateDirectRange(program, BatchEditField.PointValue, minimum, maximum);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return BoundedTemplatePoints("Batch edit SubVoice event points", eventInstrumentId, subVoiceId, eventIds, BoundedPointOperation.Batch, target, program: program).Prepare(project);
            HashSet<MidoraId> requested = ValidateBatchIds(eventIds, nameof(eventIds), "Template Event");
            TemplateEventTransformEntry[] selected = voice.Events
                .ResolveByIdsWithIndicesInCollectionOrder(requested)
                .Select(static value => new TemplateEventTransformEntry(
                    value.Value,
                    value.Index,
                    CaptureTemplateEvent(value.Value)))
                .ToArray();
            if (selected.Length != requested.Count
                || selected.Any(value => value.Event.Kind == TemplateEventKind.Note
                    || !TemplateEventMidiTargets.Enumerate(value.Event).Contains(target)))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a point in the requested SubVoice event lane.",
                    nameof(eventIds));
            }
            long relativeOrigin = selected.Min(value => value.Old.Tick);
            Stopwatch clock = Stopwatch.StartNew();
            BatchTemplateEventResult[] results = selected.Select(value =>
            {
                int oldPointValue = TemplateEventMidiTargets.GetValue(value.Event, target);
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: 0,
                        PointValue: oldPointValue,
                        KeyNumber: 0,
                        Gate: 0,
                        Tick: value.Old.Tick,
                        RelativeTick: checked(value.Old.Tick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                int pointValue = checked((int)RoundAndClamp(calculated.PointValue, minimum, maximum));
                TemplateEventValue? replacement = tick is long resolvedTick
                    ? SetTemplateEventPointValue(
                        value.Old with { Tick = resolvedTick },
                        target,
                        pointValue)
                    : null;
                if (replacement is not null)
                {
                    ValidateTemplateEventEdit(value.Event, replacement);
                }
                return new BatchTemplateEventResult(value, replacement);
            }).ToArray();
            return ResolveTargetedExactTemplateEventPointCollisions(
                PrepareBatchTemplateEventPoints(
                    eventInstrumentId,
                    instrument,
                    voice,
                    selected,
                    results),
                results
                    .Where(static value => value.Replacement is not null)
                    .SelectMany(value => CreateTemplateEventPointCollisionTargets(
                        voice,
                        value.Replacement!)));
        });

    private static void ValidateNoteBatchProgram(BatchEditExpressionProgram program)
    {
        BatchEditField[] expected =
        [
            BatchEditField.Velocity,
            BatchEditField.KeyNumber,
            BatchEditField.Gate,
            BatchEditField.Tick
        ];
        if (!program.Fields.Order().SequenceEqual(expected.Order()))
        {
            throw new ArgumentException(
                "A note batch program must contain Velocity, Key Number, Gate, and Tick fields.",
                nameof(program));
        }
        ValidateDirectRange(program, BatchEditField.Velocity, 1, 127);
        ValidateDirectRange(program, BatchEditField.KeyNumber, 0, 127);
        ValidateDirectRange(program, BatchEditField.Gate, 1, long.MaxValue);
        ValidateDirectRange(program, BatchEditField.Tick, 0, long.MaxValue - 1d);
    }

    private static void ValidatePointBatchProgram(BatchEditExpressionProgram program)
    {
        BatchEditField[] expected = [BatchEditField.PointValue, BatchEditField.Tick];
        if (!program.Fields.Order().SequenceEqual(expected.Order()))
        {
            throw new ArgumentException(
                "An event-point batch program must contain Point Value and Tick fields.",
                nameof(program));
        }
        ValidateDirectRange(program, BatchEditField.Tick, 0, long.MaxValue - 1d);
    }

    private static void ValidateDirectRange(
        BatchEditExpressionProgram program,
        BatchEditField field,
        double minimum,
        double maximum)
    {
        if (program.GetFormulaKind(field) == BatchEditFormulaKind.DirectValue
            && program.GetConstant(field) is double value
            && (value < minimum || value > maximum))
        {
            throw new ArgumentOutOfRangeException(
                nameof(program),
                $"The direct {field} value must be between {minimum} and {maximum}.");
        }
    }

    private static BatchLogicalNoteResult EvaluateLogicalNote(
        LogicalNoteValue old,
        long relativeOrigin,
        BatchEditExpressionProgram program,
        Stopwatch clock)
    {
        BatchEditValues calculated = program.Evaluate(
            new(
                Velocity: old.Velocity,
                PointValue: 0,
                KeyNumber: old.Note,
                Gate: old.LengthTicks,
                Tick: old.StartTick,
                RelativeTick: checked(old.StartTick - relativeOrigin)),
            clock,
            BatchExpressionTimeout);
        long? tick = RoundTickOrDiscard(calculated.Tick);
        double roundedNote = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
        bool noteOutOfRange = roundedNote is < 0 or > 127;
        int note = noteOutOfRange ? 0 : checked((int)roundedNote);
        bool discard = tick is null || noteOutOfRange;
        long length = RoundAndClamp(calculated.Gate, 1, long.MaxValue);
        int velocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127));
        LogicalNoteValue replacement = old with
        {
            StartTick = tick ?? 0,
            LengthTicks = length,
            Note = Math.Clamp(note, 0, 127),
            Velocity = velocity
        };
        if (!discard) ValidateLogicalNote(
            replacement.StartTick,
            replacement.LengthTicks,
            replacement.Note,
            replacement.Velocity);
        return new(replacement, discard);
    }

    private static BatchTemplateNoteResult EvaluateTemplateNote(
        TemplateEventValue old,
        long relativeOrigin,
        BatchEditExpressionProgram program,
        Stopwatch clock)
    {
        BatchEditValues calculated = program.Evaluate(
            new(
                Velocity: old.Value,
                PointValue: 0,
                KeyNumber: old.Number,
                Gate: old.LengthTicks,
                Tick: old.Tick,
                RelativeTick: checked(old.Tick - relativeOrigin)),
            clock,
            BatchExpressionTimeout);
        long? tick = RoundTickOrDiscard(calculated.Tick);
        double roundedNote = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
        bool noteOutOfRange = roundedNote is < 0 or > 127;
        int note = noteOutOfRange ? 0 : checked((int)roundedNote);
        bool discard = tick is null || noteOutOfRange;
        TemplateEventValue replacement = old with
        {
            Tick = tick ?? 0,
            LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
            Number = Math.Clamp(note, 0, 127),
            Value = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
        };
        return new(replacement, discard);
    }

    private static IPreparedProjectEdit PrepareBatchLogicalNotes(
        MidoraId trackId,
        Segment segment,
        SelectedLogicalNote[] selected,
        LogicalNoteValue[] old,
        BatchLogicalNoteResult[] results,
        SegmentWindowTransform window)
    {
        bool changed = old.Where((value, index) =>
            results[index].Discard || value != results[index].Replacement).Any()
            || window.Old != window.Replacement;
        LogicalNote[] discarded = selected
            .Where((_, index) => results[index].Discard)
            .Select(static value => value.Note)
            .ToArray();
        Action? restoreDiscarded = null;
        return Prepared(
            changed,
            TrackChange(trackId),
            _ =>
            {
                using (segment.Notes.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!results[index].Discard)
                            SetLogicalNote(selected[index].Note, results[index].Replacement);
                    }
                    if (discarded.Length != 0)
                        restoreDiscarded = segment.Notes.RemoveRangeWithUndo(discarded);
                }
                SetWindow(segment, window.Replacement);
                SortTransformedSegments([window]);
            },
            _ =>
            {
                SetWindow(segment, window.Old);
                SortTransformedSegments([window]);
                using (segment.Notes.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                        SetLogicalNote(selected[index].Note, old[index]);
                    if (discarded.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Logical Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                }
            });
    }

    private static IPreparedProjectEdit PrepareBatchLogicalNotesAcrossSegments(
        IReadOnlyCollection<SegmentTransformEntry> segments,
        IReadOnlyList<(SegmentTransformEntry Segment, SelectedLogicalNote Note)> selected,
        IReadOnlyList<BatchLogicalNoteResult> results,
        IReadOnlyCollection<SegmentWindowTransform> windows)
    {
        LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note.Note)).ToArray();
        bool changed = old.Where((value, index) =>
            results[index].Discard || value != results[index].Replacement).Any()
            || windows.Any(value => value.Old != value.Replacement);
        var groups = selected
            .Select((value, index) => (Index: index, Entry: value))
            .GroupBy(static value => value.Entry.Segment.Segment)
            .Select(static group => (Segment: group.Key, Values: group.ToArray()))
            .ToArray();
        Dictionary<Segment, Action> restoreDiscardedBySegment = [];
        return Prepared(
            changed,
            TrackChange(segments.Select(value => value.Track.Id).Distinct().ToArray()),
            _ =>
            {
                foreach (var group in groups)
                {
                    using IDisposable batch = group.Segment.Notes.BeginBatchChange();
                    foreach (var value in group.Values)
                    {
                        if (!results[value.Index].Discard)
                            SetLogicalNote(value.Entry.Note.Note, results[value.Index].Replacement);
                    }
                    LogicalNote[] discarded = group.Values
                        .Where(value => results[value.Index].Discard)
                        .Select(static value => value.Entry.Note.Note)
                        .ToArray();
                    if (discarded.Length != 0)
                        restoreDiscardedBySegment[group.Segment] =
                            group.Segment.Notes.RemoveRangeWithUndo(discarded);
                }
                foreach (SegmentWindowTransform window in windows)
                {
                    SetWindow(window.Entry.Segment, window.Replacement);
                }
                SortTransformedSegments(windows);
            },
            _ =>
            {
                foreach (SegmentWindowTransform window in windows)
                {
                    SetWindow(window.Entry.Segment, window.Old);
                }
                SortTransformedSegments(windows);
                foreach (var group in groups)
                {
                    using IDisposable batch = group.Segment.Notes.BeginBatchChange();
                    foreach (var value in group.Values)
                        SetLogicalNote(value.Entry.Note.Note, old[value.Index]);
                    if (restoreDiscardedBySegment.TryGetValue(group.Segment, out Action? restore))
                    {
                        restore();
                        restoreDiscardedBySegment.Remove(group.Segment);
                    }
                }
            });
    }

    private static IPreparedProjectEdit PrepareBatchTemplateNotes(
        MidoraId eventInstrumentId,
        EventInstrument instrument,
        SubVoice voice,
        TemplateEventTransformEntry[] selected,
        BatchTemplateNoteResult[] results)
    {
        long oldLength = instrument.TemplateLengthTicks;
        long newLength = Math.Max(
            oldLength,
            results.Where(value => !value.Discard)
                .Select(value => checked(value.Replacement.Tick + value.Replacement.LengthTicks))
                .DefaultIfEmpty(oldLength)
                .Max());
        bool changed = selected.Where((value, index) =>
            results[index].Discard || value.Old != results[index].Replacement).Any()
            || oldLength != newLength;
        TemplateEvent[] discarded = selected
            .Where((_, index) => results[index].Discard)
            .Select(static value => value.Event)
            .ToArray();
        Action? restoreDiscarded = null;
        return Prepared(
            changed,
            EventInstrumentChange(eventInstrumentId),
            _ =>
            {
                using (voice.Events.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!results[index].Discard)
                            SetTemplateEvent(selected[index].Event, results[index].Replacement);
                    }
                    if (discarded.Length != 0)
                        restoreDiscarded = voice.Events.RemoveRangeWithUndo(discarded);
                }
                instrument.TemplateLengthTicks = newLength;
            },
            _ =>
            {
                using (voice.Events.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                        SetTemplateEvent(selected[index].Event, selected[index].Old);
                    if (discarded.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Template Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                }
                instrument.TemplateLengthTicks = oldLength;
            });
    }

    private static IPreparedProjectEdit PrepareBatchCurvePoints(
        ProjectChangeSet changes,
        CurvePointCollection points,
        BatchCurvePointResult[] results,
        string objectName,
        SegmentWindowTransform window)
    {
        CurvePoint[] retainedOld = results
            .Where(static value => value.Replacement is not null)
            .Select(static value => value.Selected.Point)
            .ToArray();
        CurvePoint[] retainedReplacement = results
            .Where(static value => value.Replacement is not null)
            .Select(static value => value.Replacement!)
            .ToArray();
        CurvePoint[] discarded = results
            .Where(static value => value.Replacement is null)
            .Select(static value => value.Selected.Point)
            .ToArray();
        Action? restoreDiscarded = null;
        return Prepared(
            results.Any(value => value.Replacement is null
                || value.Selected.Point != value.Replacement)
                || window.Old != window.Replacement,
            changes,
            _ =>
            {
                points.ReplaceRange(retainedOld, retainedReplacement);
                if (discarded.Length != 0)
                    restoreDiscarded = points.RemoveRangeWithUndo(discarded);
                SetWindow(window.Entry.Segment, window.Replacement);
                SortTransformedSegments([window]);
            },
            _ =>
            {
                SetWindow(window.Entry.Segment, window.Old);
                SortTransformedSegments([window]);
                points.ReplaceRange(retainedReplacement, retainedOld);
                if (discarded.Length != 0)
                {
                    (restoreDiscarded ?? throw new InvalidOperationException(
                        $"Discarded {objectName} values do not have a pending removal to restore."))();
                    restoreDiscarded = null;
                }
            });
    }

    private static SegmentWindowTransform PlanLogicalNoteBatchWindow(
        SegmentTransformEntry entry,
        BatchLogicalNoteResult[] results,
        IReadOnlyCollection<int>? indices = null)
    {
        int[] affected = indices?.ToArray() ?? Enumerable.Range(0, results.Length).ToArray();
        foreach (int index in affected)
        {
            if (!results[index].Discard
                && FallsBeforeProjectStart(entry.Segment, results[index].Replacement.StartTick))
            {
                results[index] = results[index] with { Discard = true };
            }
        }
        return PlanBatchWindow(
            entry,
            affected.Where(index => !results[index].Discard)
                .Select(index => results[index].Replacement.StartTick));
    }

    private static SegmentWindowTransform PlanCurvePointBatchWindow(
        SegmentTransformEntry entry,
        BatchCurvePointResult[] results)
    {
        for (int index = 0; index < results.Length; index++)
        {
            CurvePoint? replacement = results[index].Replacement;
            if (replacement is not null
                && FallsBeforeProjectStart(entry.Segment, replacement.Tick))
            {
                results[index] = results[index] with { Replacement = null };
            }
        }
        return PlanBatchWindow(
            entry,
            results.Where(value => value.Replacement is not null)
                .Select(value => value.Replacement!.Tick));
    }

    private static SegmentWindowTransform PlanBatchWindow(
        SegmentTransformEntry entry,
        IEnumerable<long> retainedTicks)
    {
        Segment segment = entry.Segment;
        SegmentWindow old = new(
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick);
        long minimum = retainedTicks.DefaultIfEmpty(old.ContentOffsetTick).Min();
        long replacementOffset = Math.Min(old.ContentOffsetTick, minimum);
        long expansion = checked(old.ContentOffsetTick - replacementOffset);
        SegmentWindow replacement = old with
        {
            ProjectStartTick = checked(old.ProjectStartTick - expansion),
            LengthTicks = checked(old.LengthTicks + expansion),
            ContentOffsetTick = replacementOffset
        };
        return new(entry, old, replacement);
    }

    private static bool FallsBeforeProjectStart(Segment segment, long sourceTick) =>
        sourceTick < segment.ContentOffsetTick
        && checked(segment.ContentOffsetTick - sourceTick) > segment.ProjectStartTick;

    private static IPreparedProjectEdit PrepareBatchTemplateEventPoints(
        MidoraId eventInstrumentId,
        EventInstrument instrument,
        SubVoice voice,
        TemplateEventTransformEntry[] selected,
        BatchTemplateEventResult[] results)
    {
        long oldLength = instrument.TemplateLengthTicks;
        long newLength = Math.Max(
            oldLength,
            results.Where(value => value.Replacement is not null)
                .Select(value => checked(value.Replacement!.Tick + 1))
                .DefaultIfEmpty(oldLength)
                .Max());
        TemplateEvent[] discarded = selected
            .Where((_, index) => results[index].Replacement is null)
            .Select(static value => value.Event)
            .ToArray();
        Action? restoreDiscarded = null;
        return Prepared(
            selected.Where((value, index) =>
                results[index].Replacement is null
                || value.Old != results[index].Replacement).Any()
                || oldLength != newLength,
            EventInstrumentChange(eventInstrumentId),
            _ =>
            {
                using (voice.Events.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (results[index].Replacement is not null)
                            SetTemplateEvent(selected[index].Event, results[index].Replacement!);
                    }
                    if (discarded.Length != 0)
                        restoreDiscarded = voice.Events.RemoveRangeWithUndo(discarded);
                }
                instrument.TemplateLengthTicks = newLength;
            },
            _ =>
            {
                using (voice.Events.BeginBatchChange())
                {
                    for (int index = 0; index < selected.Length; index++)
                        SetTemplateEvent(selected[index].Event, selected[index].Old);
                    if (discarded.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Template Events do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                }
                instrument.TemplateLengthTicks = oldLength;
            });
    }

    private static double NormalizeLogicalParameterBatchValue(
        LogicalParameterDefinition definition,
        double value)
    {
        double clamped = Math.Clamp(value, definition.Minimum, definition.Maximum);
        return definition.Type switch
        {
            LogicalParameterType.Double => clamped,
            LogicalParameterType.Integer => Math.Round(clamped, MidpointRounding.AwayFromZero),
            LogicalParameterType.Enum when definition.UsesExplicitEnumValues
                && definition.EnumItems.Count != 0 => definition.EnumItems
                    .Select(item => (double)item.Value)
                    .OrderBy(candidate => Math.Abs(candidate - clamped))
                    .ThenBy(candidate => candidate)
                    .First(),
            LogicalParameterType.Enum => Math.Round(clamped, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };
    }

    private static TemplateEventValue SetTemplateEventPointValue(
        TemplateEventValue value,
        MidiValueTarget target,
        int pointValue) =>
        target.Kind is MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents
            ? value with { SecondaryValue = pointValue }
            : value with { Value = pointValue };

    private static (double Minimum, double Maximum) MidiEventTargetRange(
        MidiValueTarget target) => target.Kind switch
        {
            MidiValueKind.ControlChange when target.Number is >= 0 and <= 119
                && target.Number is not 91 and not 93 => (0, 127),
            MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program => (0, 127),
            MidiValueKind.PitchBend => (-8192, 8191),
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter
                when target.Number is >= 0 and <= 16_383 => (0, 16_383),
            MidiValueKind.PitchBendRangeSemitones => (0, 127),
            MidiValueKind.PitchBendRangeCents => (0, 99),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    private static long? RoundTickOrDiscard(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException("The Tick expression returned a non-finite value.");
        }
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < 0) return null;
        if (rounded >= long.MaxValue)
        {
            throw new OverflowException("The calculated Tick exceeds Int64.");
        }
        return checked((long)rounded);
    }

    private static long RoundAndClamp(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException("A batch expression returned a non-finite value.");
        }
        double clamped = Math.Clamp(value, minimum, maximum);
        double rounded = Math.Round(clamped, MidpointRounding.AwayFromZero);
        if (rounded >= long.MaxValue) return long.MaxValue;
        if (rounded <= long.MinValue) return long.MinValue;
        return checked((long)rounded);
    }

    private readonly record struct BatchLogicalNoteResult(
        LogicalNoteValue Replacement,
        bool Discard);
    private readonly record struct BatchTemplateNoteResult(
        TemplateEventValue Replacement,
        bool Discard);
    private readonly record struct BatchCurvePointResult(
        SelectedCurvePoint Selected,
        CurvePoint? Replacement);
    private readonly record struct BatchTemplateEventResult(
        TemplateEventTransformEntry Selected,
        TemplateEventValue? Replacement);
}
