using Midora.Domain;

namespace Midora.Application;

public enum SegmentSelectionTransformScope
{
    ExposedContentOnly,
    ExposedContentAndSegments
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand FlipLogicalNotesHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformLogicalNotes(
            "Flip logical notes horizontally",
            segmentId,
            noteIds,
            values =>
            {
                long left = values.Min(value => value.StartTick);
                long right = values.Max(value => checked(value.StartTick + value.LengthTicks));
                return values.Select(value => value with
                {
                    StartTick = checked(left + (right
                        - checked(value.StartTick + value.LengthTicks)))
                }).ToArray();
            }, boundedTransform: values =>
            {
                long left = values.Min(v => v.Value.StartTick);
                long right = values.Max(v => checked(v.Value.StartTick + v.Value.LengthTicks));
                return value => value with { StartTick = checked(left + (right - checked(value.StartTick + value.LengthTicks))) };
            });

    public static IProjectEditCommand FlipLogicalNotesVertical(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformLogicalNotes(
            "Flip logical notes vertically",
            segmentId,
            noteIds,
            values =>
            {
                int minimum = values.Min(value => value.Note);
                int maximum = values.Max(value => value.Note);
                return values.Select(value => value with
                {
                    Note = checked(minimum + maximum - value.Note)
                }).ToArray();
            }, boundedTransform: values =>
            {
                int minimum = values.Min(v => v.Value.Note), maximum = values.Max(v => v.Value.Note);
                return value => value with { Note = checked(minimum + maximum - value.Note) };
            });

    public static IProjectEditCommand ScaleLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        TransformLogicalNotes(
            "Scale logical notes",
            segmentId,
            noteIds,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.StartTick);
                return values.Select(value => value with
                {
                    StartTick = ScaleTick(origin, value.StartTick, factor),
                    LengthTicks = ScaleLength(value.LengthTicks, factor)
                }).ToArray();
            }, boundedTransform: values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(v => v.Value.StartTick);
                return value => value with { StartTick = ScaleTick(origin, value.StartTick, factor), LengthTicks = ScaleLength(value.LengthTicks, factor) };
            });

    public static IProjectEditCommand TransposeLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveLogicalNotes(segmentId, noteIds, tickDelta: 0, pitchDelta: semitones);

    public static IProjectEditCommand FlipTemplateNotesHorizontal(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformTemplateNotes(
            "Flip template notes horizontally",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                long left = values.Min(value => value.Tick);
                long right = values.Max(value => checked(value.Tick + value.LengthTicks));
                return values.Select(value => value with
                {
                    Tick = checked(left + (right
                        - checked(value.Tick + value.LengthTicks)))
                }).ToArray();
            }, boundedTransform: values =>
            {
                long left = values.Min(v => v.Value.Tick);
                long right = values.Max(v => checked(v.Value.Tick + v.Value.LengthTicks));
                return value => value with { Tick = checked(left + (right - checked(value.Tick + value.LengthTicks))) };
            });

    public static IProjectEditCommand FlipTemplateNotesVertical(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformTemplateNotes(
            "Flip template notes vertically",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                int minimum = values.Min(value => value.Number);
                int maximum = values.Max(value => value.Number);
                return values.Select(value => value with
                {
                    Number = checked(minimum + maximum - value.Number)
                }).ToArray();
            }, boundedTransform: values =>
            {
                int minimum = values.Min(v => v.Value.Number), maximum = values.Max(v => v.Value.Number);
                return value => value with { Number = checked(minimum + maximum - value.Number) };
            });

    public static IProjectEditCommand ScaleTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        TransformTemplateNotes(
            "Scale template notes",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.Tick);
                return values.Select(value => value with
                {
                    Tick = ScaleTick(origin, value.Tick, factor),
                    LengthTicks = ScaleLength(value.LengthTicks, factor)
                }).ToArray();
            }, boundedTransform: values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(v => v.Value.Tick);
                return value => value with { Tick = ScaleTick(origin, value.Tick, factor), LengthTicks = ScaleLength(value.LengthTicks, factor) };
            });

    public static IProjectEditCommand TransposeTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveTemplateNotes(
            eventInstrumentId,
            subVoiceId,
            noteIds,
            tickDelta: 0,
            pitchDelta: semitones);

    public static IProjectEditCommand FlipLogicalParameterPointsHorizontal(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        ChooseBoundedPointCommand(project => pointIds.Count >= BoundedPointThreshold || FindLogicalParameterLane(FindSegment(project, segmentId).Segment, laneId).Points.Count >= BoundedPointThreshold,
        BoundedLogicalPoints("Flip logical parameter points horizontally", segmentId, laneId, pointIds, BoundedPointOperation.Flip),
        TransformLogicalParameterPoints(
            "Flip logical parameter points horizontally",
            segmentId,
            laneId,
            pointIds,
            points =>
            {
                long left = points.Min(value => value.Tick);
                long right = points.Max(value => value.Tick);
                return points.Select(value => checked(left + (right - value.Tick))).ToArray();
            }));

    public static IProjectEditCommand ScaleLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        double factor) =>
        ChooseBoundedPointCommand(project => pointIds.Count >= BoundedPointThreshold || FindLogicalParameterLane(FindSegment(project, segmentId).Segment, laneId).Points.Count >= BoundedPointThreshold,
        BoundedLogicalPoints("Scale logical parameter points", segmentId, laneId, pointIds, BoundedPointOperation.Scale, factor: factor),
        TransformLogicalParameterPoints(
            "Scale logical parameter points",
            segmentId,
            laneId,
            pointIds,
            points =>
            {
                ValidateScaleFactor(factor);
                long origin = points.Min(value => value.Tick);
                return points.Select(value => ScaleTick(origin, value.Tick, factor)).ToArray();
            }));

    public static IProjectEditCommand FlipSubVoiceEventPointsHorizontal(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target) =>
        ChooseBoundedPointCommand(project => eventIds.Count >= BoundedPointThreshold || FindSubVoice(FindEventInstrument(project, eventInstrumentId), subVoiceId).Events.Count >= BoundedPointThreshold,
        BoundedTemplatePoints("Flip SubVoice event points horizontally", eventInstrumentId, subVoiceId,
            eventIds, BoundedPointOperation.Flip, target),
        TransformSubVoiceEventPoints(
            "Flip SubVoice event points horizontally",
            eventInstrumentId,
            subVoiceId,
            eventIds,
            target,
            values =>
            {
                long left = values.Min(value => value.Tick);
                long right = values.Max(value => value.Tick);
                return values.Select(value => checked(left + (right - value.Tick))).ToArray();
            }));

    public static IProjectEditCommand ScaleSubVoiceEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target,
        double factor) =>
        ChooseBoundedPointCommand(project => eventIds.Count >= BoundedPointThreshold || FindSubVoice(FindEventInstrument(project, eventInstrumentId), subVoiceId).Events.Count >= BoundedPointThreshold,
        BoundedTemplatePoints("Scale SubVoice event points", eventInstrumentId, subVoiceId,
            eventIds, BoundedPointOperation.Scale, target, factor: factor),
        TransformSubVoiceEventPoints(
            "Scale SubVoice event points",
            eventInstrumentId,
            subVoiceId,
            eventIds,
            target,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.Tick);
                return values.Select(value => ScaleTick(origin, value.Tick, factor)).ToArray();
            }));

    public static IProjectEditCommand FlipSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope) =>
        TransformSegments(
            "Flip segments horizontally",
            segmentIds,
            scope,
            SegmentContentTransformKind.FlipHorizontal,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand FlipSegmentsVertical(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        TransformSegments(
            "Flip exposed Segment notes vertically",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            SegmentContentTransformKind.FlipVertical,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand ScaleSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        double factor,
        SegmentSelectionTransformScope scope) =>
        TransformSegments(
            "Scale segments",
            segmentIds,
            scope,
            SegmentContentTransformKind.Scale,
            factor,
            semitones: 0);

    public static IProjectEditCommand TransposeSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        int semitones) =>
        TransformSegments(
            "Transpose exposed Segment notes",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            SegmentContentTransformKind.Transpose,
            factor: 1,
            semitones);

    private static IProjectEditCommand TransformLogicalNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<LogicalNoteValue>, LogicalNoteValue[]> transform,
        Func<IReadOnlyList<BoundedLogicalNotePlanning.Selected<LogicalNoteSnapshotValue>>, Func<LogicalNoteSnapshotValue, LogicalNoteSnapshotValue?>>? boundedTransform = null) =>
        Command(name, project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            if ((noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold) && boundedTransform is not null)
                return PrepareBoundedLogicalNotes(project, location, noteIds, boundedTransform);
            SelectedLogicalNote[] selected = SelectLogicalNotes(location.Segment, noteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
            {
                throw new InvalidOperationException("A Logical Note transform returned the wrong result count.");
            }
            ValidateLogicalNoteBatch(replacement);
            return ResolveTargetedExactLogicalNoteCollisions(
                PrepareLogicalNoteBatch(
                    location.Track.Id,
                    location.Segment.Notes,
                    selected,
                    old,
                    replacement),
                replacement.Select(value => new LogicalNoteCollisionTarget(
                    location.Segment,
                    value.StartTick,
                    value.Note)));
        });

    private static IProjectEditCommand TransformTemplateNotes(
        string name,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<TemplateEventValue>, TemplateEventValue[]> transform,
        Func<IReadOnlyList<BoundedLogicalNotePlanning.Selected<TemplateEventSnapshotValue>>, Func<TemplateEventSnapshotValue, TemplateEventSnapshotValue?>>? boundedTransform = null) =>
        Command(name, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if ((noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold) && boundedTransform is not null)
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, boundedTransform);
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
            TemplateEventValue[] old = selected.Select(value => value.Old).ToArray();
            TemplateEventValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
            {
                throw new InvalidOperationException("A Template Note transform returned the wrong result count.");
            }
            for (int index = 0; index < replacement.Length; index++)
            {
                ValidateTemplateEventEdit(selected[index].Event, replacement[index]);
            }
            long oldLength = instrument.TemplateLengthTicks;
            long newLength = Math.Max(
                oldLength,
                replacement.Max(value => checked(value.Tick + value.LengthTicks)));
            return ResolveTargetedExactTemplateNoteCollisions(Prepared(
                old.Where((value, index) => value != replacement[index]).Any()
                    || oldLength != newLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, replacement[index]);
                    }
                    instrument.TemplateLengthTicks = newLength;
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, old[index]);
                    }
                    instrument.TemplateLengthTicks = oldLength;
                }), replacement.Select(value => new TemplateNoteCollisionTarget(
                    voice,
                    value.Tick,
                    value.Number)));
        });

    private static IProjectEditCommand TransformLogicalParameterPoints(
        string name,
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        Func<IReadOnlyList<CurvePoint>, long[]> transform) =>
        Command(name, project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            long[] ticks = transform(selected.Select(value => value.Point).ToArray());
            if (ticks.Length != selected.Length || ticks.Any(value => value < 0 || value == long.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(pointIds));
            }
            CurvePoint[] replacement = selected.Select((value, index) => new CurvePoint(
                project,
                value.Point.Id,
                ticks[index],
                value.Point.Value,
                CurveInterpolation.Step)).ToArray();
            return ResolveTargetedExactLogicalParameterPointCollisions(
                PrepareCurvePointReplacementBatch(
                    location.Track.Id,
                    lane.Points,
                    selected,
                    replacement),
                lane,
                replacement.Select(static value => value.Tick));
        });

    private static IProjectEditCommand TransformSubVoiceEventPoints(
        string name,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target,
        Func<IReadOnlyList<TemplateEventValue>, long[]> transform) =>
        Command(name, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
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
            long[] ticks = transform(selected.Select(value => value.Old).ToArray());
            if (ticks.Length != selected.Length)
            {
                throw new InvalidOperationException("A SubVoice event transform returned the wrong result count.");
            }
            TemplateEventValue[] replacement = selected.Select((value, index) =>
                value.Old with { Tick = ticks[index] }).ToArray();
            for (int index = 0; index < replacement.Length; index++)
            {
                ValidateTemplateEventEdit(selected[index].Event, replacement[index]);
            }
            long oldLength = instrument.TemplateLengthTicks;
            long newLength = Math.Max(oldLength, checked(replacement.Max(value => value.Tick) + 1));
            return ResolveTargetedExactTemplateEventPointCollisions(Prepared(
                selected.Where((value, index) => value.Old != replacement[index]).Any()
                    || oldLength != newLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, replacement[index]);
                    }
                    instrument.TemplateLengthTicks = newLength;
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    foreach (TemplateEventTransformEntry value in selected)
                    {
                        SetTemplateEvent(value.Event, value.Old);
                    }
                    instrument.TemplateLengthTicks = oldLength;
                }), replacement.SelectMany(value =>
                    CreateTemplateEventPointCollisionTargets(voice, value)));
        });

    private static IProjectEditCommand TransformSegments(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope,
        SegmentContentTransformKind kind,
        double factor,
        int semitones) =>
        Command(name, project =>
        {
            if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (kind == SegmentContentTransformKind.Scale) ValidateScaleFactor(factor);
            HashSet<MidoraId> requested = ValidateBatchIds(segmentIds, nameof(segmentIds), "Segment");
            SegmentTransformEntry[] segments = project.Tracks
                .SelectMany(track => track.Segments.Select((segment, index) =>
                    new SegmentTransformEntry(track, segment, index)))
                .Where(value => requested.Contains(value.Segment.Id))
                .ToArray();
            if (segments.Length != requested.Count)
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Segment.",
                    nameof(segmentIds));
            }

            if (RequiresBoundedLogicalSegmentContent(segments))
                return PrepareBoundedLogicalSegmentTransform(project, segments, kind, scope, factor, semitones);

            long selectionLeft = segments.Min(value => value.Segment.ProjectStartTick);
            long selectionRight = segments.Max(value => value.Segment.ProjectRange.EndTick);
            List<SegmentWindowTransform> windows = [];
            List<LogicalNoteTransform> notes = [];
            List<CurvePointTransform> points = [];
            foreach (SegmentTransformEntry entry in segments)
            {
                Segment segment = entry.Segment;
                long contentLeft = segment.ContentOffsetTick;
                long contentRight = segment.ContentEndTick;
                SegmentWindow oldWindow = new(
                    segment.ProjectStartTick,
                    segment.LengthTicks,
                    segment.ContentOffsetTick);
                SegmentWindow newWindow = oldWindow;
                if (scope == SegmentSelectionTransformScope.ExposedContentAndSegments)
                {
                    newWindow = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => oldWindow with
                        {
                            ProjectStartTick = checked(selectionLeft + (selectionRight
                                - checked(oldWindow.ProjectStartTick + oldWindow.LengthTicks)))
                        },
                        SegmentContentTransformKind.Scale => oldWindow with
                        {
                            ProjectStartTick = ScaleTick(
                                selectionLeft,
                                oldWindow.ProjectStartTick,
                                factor),
                            LengthTicks = ScaleLength(oldWindow.LengthTicks, factor)
                        },
                        _ => oldWindow
                    };
                }
                windows.Add(new(entry, oldWindow, newWindow));

                LogicalNoteQuerySnapshot noteSnapshot = segment.Notes.CreateQuerySnapshot();
                foreach (LogicalNoteSnapshotValue noteValue in noteSnapshot.QueryValues(
                    contentLeft,
                    contentRight))
                {
                    if (!segment.Notes.TryGetById(noteValue.Id, out LogicalNote? note)
                        || note is null)
                    {
                        continue;
                    }
                    long noteEnd = checked(note.StartTick + note.LengthTicks);
                    if (note.StartTick >= contentRight || noteEnd <= contentLeft) continue;
                    LogicalNoteValue old = Snapshot(note);
                    LogicalNoteValue replacement = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => old with
                        {
                            StartTick = checked(contentLeft + (contentRight
                                - checked(old.StartTick + old.LengthTicks)))
                        },
                        SegmentContentTransformKind.FlipVertical => old with
                        {
                            Note = 127 - old.Note
                        },
                        SegmentContentTransformKind.Scale => old with
                        {
                            StartTick = ScaleTick(contentLeft, old.StartTick, factor),
                            LengthTicks = ScaleLength(old.LengthTicks, factor)
                        },
                        SegmentContentTransformKind.Transpose => old with
                        {
                            Note = checked(old.Note + semitones)
                        },
                        _ => old
                    };
                    bool discard = replacement.Note is < 0 or > 127;
                    if (!discard) ValidateLogicalNote(
                        replacement.StartTick,
                        replacement.LengthTicks,
                        replacement.Note,
                        replacement.Velocity);
                    notes.Add(new(segment, note, old, replacement, discard));
                }
                if (kind is SegmentContentTransformKind.FlipHorizontal
                    or SegmentContentTransformKind.Scale)
                {
                    foreach (LogicalParameterLane lane in segment.ParameterLanes)
                    {
                        foreach (CurvePointSnapshotValue pointValue in lane.Points
                            .CreateQuerySnapshot()
                            .QueryValues(contentLeft, contentRight))
                        {
                            if (!lane.Points.TryGetById(pointValue.Id, out CurvePoint? point)
                                || point is null)
                            {
                                continue;
                            }
                            long tick = kind == SegmentContentTransformKind.FlipHorizontal
                                ? checked(contentLeft + (checked(contentRight - 1) - point.Tick))
                                : ScaleTick(contentLeft, point.Tick, factor);
                            CurvePoint replacement = new(
                                project,
                                point.Id,
                                tick,
                                point.Value,
                                CurveInterpolation.Step);
                            points.Add(new(lane, point, replacement));
                        }
                    }
                }
            }

            ValidateSegmentTransformWindows(project, windows);
            bool changed = windows.Any(value => value.Old != value.Replacement)
                || notes.Any(value => value.Discard || value.Old != value.Replacement)
                || points.Any(value => value.Old != value.Replacement);
            var noteGroups = notes
                .GroupBy(static value => value.Segment)
                .Select(static group => (Segment: group.Key, Values: group.ToArray()))
                .ToArray();
            var pointGroups = points
                .GroupBy(static value => value.Lane)
                .Select(static group => (Lane: group.Key, Values: group.ToArray()))
                .ToArray();
            Dictionary<Segment, Action> restoreDiscardedBySegment = [];
            IPreparedProjectEdit prepared = ResolveTargetedExactLogicalNoteCollisions(Prepared(
                changed,
                TrackChange(segments.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (var group in pointGroups)
                    {
                        ReplaceCurvePointBatch(
                            group.Lane.Points,
                            group.Values.Select(static value => value.Old).ToArray(),
                            group.Values.Select(static value => value.Replacement).ToArray());
                    }
                    foreach (var group in noteGroups)
                    {
                        using IDisposable batch = group.Segment.Notes.BeginBatchChange();
                        foreach (LogicalNoteTransform note in group.Values)
                        {
                            if (!note.Discard) SetLogicalNote(note.Note, note.Replacement);
                        }
                        LogicalNote[] discarded = group.Values
                            .Where(static value => value.Discard)
                            .Select(static value => value.Note)
                            .ToArray();
                        if (discarded.Length != 0)
                        {
                            restoreDiscardedBySegment[group.Segment] =
                                group.Segment.Notes.RemoveRangeWithUndo(discarded);
                        }
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
                    foreach (var group in noteGroups)
                    {
                        using IDisposable batch = group.Segment.Notes.BeginBatchChange();
                        foreach (LogicalNoteTransform note in group.Values)
                        {
                            SetLogicalNote(note.Note, note.Old);
                        }
                        if (restoreDiscardedBySegment.TryGetValue(group.Segment, out Action? restore))
                        {
                            restore();
                            restoreDiscardedBySegment.Remove(group.Segment);
                        }
                    }
                    foreach (var group in pointGroups)
                    {
                        ReplaceCurvePointBatch(
                            group.Lane.Points,
                            group.Values.Select(static value => value.Replacement).ToArray(),
                            group.Values.Select(static value => value.Old).ToArray());
                    }
                }), notes
                    .Where(static value => !value.Discard)
                    .Select(static value => new LogicalNoteCollisionTarget(
                        value.Segment,
                        value.Replacement.StartTick,
                        value.Replacement.Note)));
            return points
                .GroupBy(static value => value.Lane)
                .Aggregate(
                    prepared,
                    static (current, group) => ResolveTargetedExactLogicalParameterPointCollisions(
                        current,
                        group.Key,
                        group.Select(static value => value.Replacement.Tick)));
        });

    private static void ValidateSegmentTransformWindows(
        MidoraProject project,
        IReadOnlyCollection<SegmentWindowTransform> windows)
    {
        HashSet<Segment> selected = windows.Select(value => value.Entry.Segment).ToHashSet();
        foreach (IGrouping<LogicalTrack, SegmentWindowTransform> group in windows
            .GroupBy(value => value.Entry.Track))
        {
            SegmentWindowTransform[] ordered = group
                .OrderBy(value => value.Replacement.ProjectStartTick)
                .ThenBy(value => value.Entry.Segment.Id)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                SegmentWindow value = ordered[index].Replacement;
                ValidateSegmentRange(
                    value.ProjectStartTick,
                    value.LengthTicks,
                    value.ContentOffsetTick);
                TickRange range = new(
                    value.ProjectStartTick,
                    checked(value.ProjectStartTick + value.LengthTicks));
                if (group.Key.Segments.Any(segment =>
                    !selected.Contains(segment) && range.Intersects(segment.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The transformation would overlap an existing Segment.");
                }
                if (index != 0)
                {
                    SegmentWindow previous = ordered[index - 1].Replacement;
                    if (checked(previous.ProjectStartTick + previous.LengthTicks)
                        > value.ProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "The transformation would overlap selected Segments.");
                    }
                }
            }
        }
    }

    private static void SortTransformedSegments(
        IReadOnlyCollection<SegmentWindowTransform> windows)
    {
        foreach (LogicalTrack track in windows.Select(value => value.Entry.Track).Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    private static void ValidateScaleFactor(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor),
                "The scale factor must be a finite positive number.");
        }
    }

    private static long ScaleTick(long origin, long tick, double factor)
    {
        long delta = checked(tick - origin);
        double scaled = delta * factor;
        if (!double.IsFinite(scaled)
            || scaled < long.MinValue
            || scaled > long.MaxValue)
        {
            throw new OverflowException("The scaled tick is outside the Int64 range.");
        }
        return checked(origin + (long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private static long ScaleLength(long length, double factor)
    {
        double scaled = length * factor;
        if (!double.IsFinite(scaled) || scaled > long.MaxValue)
        {
            throw new OverflowException("The scaled length is outside the Int64 range.");
        }
        return Math.Max(1, (long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private enum SegmentContentTransformKind
    {
        FlipHorizontal,
        FlipVertical,
        Scale,
        Transpose
    }

    private readonly record struct TemplateEventTransformEntry(
        TemplateEvent Event,
        int Index,
        TemplateEventValue Old);
    private readonly record struct SegmentTransformEntry(
        LogicalTrack Track,
        Segment Segment,
        int Index);
    private readonly record struct SegmentWindowTransform(
        SegmentTransformEntry Entry,
        SegmentWindow Old,
        SegmentWindow Replacement);
    private readonly record struct LogicalNoteTransform(
        Segment Segment,
        LogicalNote Note,
        LogicalNoteValue Old,
        LogicalNoteValue Replacement,
        bool Discard);
    private readonly record struct CurvePointTransform(
        LogicalParameterLane Lane,
        CurvePoint Old,
        CurvePoint Replacement);
}
