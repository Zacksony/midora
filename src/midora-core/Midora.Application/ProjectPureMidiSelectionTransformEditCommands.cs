using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand FlipDirectMidiNotesHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        ChangeBoundedDirectMidiNotes(
            "Flip Direct MIDI Notes horizontally",
            segmentId,
            noteIds,
            bounds => value => value with
            {
                StartTick = checked(bounds.MinimumTick + bounds.MaximumEndTick - checked(value.StartTick + value.LengthTicks))
            });

    public static IProjectEditCommand FlipDirectMidiNotesVertical(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        ChangeBoundedDirectMidiNotes(
            "Flip Direct MIDI Notes vertically",
            segmentId,
            noteIds,
            bounds => value => value with { Key = checked(bounds.MinimumKey + bounds.MaximumKey - value.Key) });

    public static IProjectEditCommand ScaleDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        ChangeBoundedDirectMidiNotes(
            "Scale Direct MIDI Notes",
            segmentId,
            noteIds,
            bounds =>
            {
                ValidateScaleFactor(factor);
                return value => value with
                {
                    StartTick = ScaleTick(bounds.MinimumTick, value.StartTick, factor),
                    LengthTicks = ScaleLength(value.LengthTicks, factor)
                };
            });

    public static IProjectEditCommand TransposeDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveDirectMidiNotes(segmentId, noteIds, tickDelta: 0, keyDelta: semitones);

    public static IProjectEditCommand FlipDirectMidiEventPointsHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Flip Direct MIDI Event points horizontally", project =>
        {
            if (FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count <= 4096)
                return TransformDirectMidiEventPoints("Flip Direct MIDI Event points horizontally", segmentId, eventIds,
                    values => { long left = values.Min(static x => x.Tick), right = values.Max(static x => x.Tick);
                        return values.Select(value => value with { Tick = checked(left + right - value.Tick) }).ToArray(); }).Prepare(project);
            return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, values =>
            {
                ValidateBoundedDirectEventLane(values);
                long minimum = values.Min(static value => value.Value.Tick), maximum = values.Max(static value => value.Value.Tick);
                return value => value with { Tick = checked(minimum + maximum - value.Tick) };
            }, BoundedEventCollisionMode.Overwrite);
        });

    public static IProjectEditCommand ScaleDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        double factor) =>
        Command("Scale Direct MIDI Event points", project =>
        {
            if (FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count <= 4096)
                return TransformDirectMidiEventPoints("Scale Direct MIDI Event points", segmentId, eventIds,
                    values => { ValidateScaleFactor(factor); long start = values.Min(static x => x.Tick);
                        return values.Select(value => value with { Tick = ScaleTick(start, value.Tick, factor) }).ToArray(); }).Prepare(project);
            return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, values =>
            {
                ValidateBoundedDirectEventLane(values);
                ValidateScaleFactor(factor);
                long minimum = values.Min(static value => value.Value.Tick);
                return value => value with { Tick = ScaleTick(minimum, value.Tick, factor) };
            }, BoundedEventCollisionMode.Overwrite);
        });

    public static IProjectEditCommand BatchEditDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit Direct MIDI Notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            if (noteIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.Notes.Count > 4096)
                return ChangeBoundedDirectMidiNotes("Batch edit Direct MIDI Notes", segmentId, noteIds, bounds =>
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    return value =>
                    {
                        BatchEditValues calculated = program.Evaluate(new(
                            Velocity: value.NoteOnVelocity, PointValue: 0, KeyNumber: value.Key,
                            Gate: value.LengthTicks, Tick: value.StartTick,
                            RelativeTick: checked(value.StartTick - bounds.MinimumTick)), clock, BatchExpressionTimeout);
                        long? tick = RoundTickOrDiscard(calculated.Tick);
                        double key = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                        if (tick is null || key is < 0 or > 127) return null;
                        return value with
                        {
                            StartTick = tick.Value, Key = checked((int)key),
                            LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                            NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
                        };
                    };
                }).Prepare(project);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            DirectNoteValue[] old = selected.Select(value => SnapshotDirectNote(value.Note)).ToArray();
            long relativeOrigin = old.Min(value => value.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            DirectNoteBatchResult[] replacement = old.Select(value =>
            {
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: value.NoteOnVelocity,
                        PointValue: 0,
                        KeyNumber: value.Key,
                        Gate: value.LengthTicks,
                        Tick: value.StartTick,
                        RelativeTick: checked(value.StartTick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                double roundedKey = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                bool keyOutOfRange = roundedKey is < 0 or > 127;
                DirectNoteValue result = value with
                {
                    StartTick = tick ?? 0,
                    LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                    Key = keyOutOfRange ? 0 : checked((int)roundedKey),
                    NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
                };
                bool discard = tick is null || keyOutOfRange;
                if (!discard)
                {
                    ValidateDirectMidiNote(
                        result.StartTick,
                        result.LengthTicks,
                        result.Key,
                        result.NoteOnVelocity,
                        result.NoteOffVelocity);
                }
                return new DirectNoteBatchResult(result, discard);
            }).ToArray();
            bool[] discarded = replacement.Select(value => value.Discard).ToArray();
            Dictionary<(long Tick, int Key), (long Tick, int Key)> occupied = [];
            for (int index = 0; index < replacement.Length; index++)
            {
                if (!discarded[index])
                {
                    (long Tick, int Key) source = (old[index].StartTick, old[index].Key);
                    (long Tick, int Key) target = (
                        replacement[index].Value.StartTick,
                        replacement[index].Value.Key);
                    if (occupied.TryGetValue(target, out var incumbentSource)
                        && incumbentSource != source)
                    {
                        discarded[index] = true;
                    }
                    else
                    {
                        occupied[target] = source;
                    }
                }
            }
            DirectMidiNote[] discardedNotes = selected
                .Where((_, index) => discarded[index])
                .Select(static value => value.Note)
                .ToArray();
            Action? restoreDiscarded = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                old.Where((value, index) => value != replacement[index].Value || discarded[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(
                        selected.Select(static value => value.Note).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!discarded[index]) ApplyDirectNote(selected[index].Note, replacement[index].Value);
                    }
                    if (discardedNotes.Length != 0)
                        restoreDiscarded = location.Segment.Notes.RemoveRangeWithUndo(discardedNotes);
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(
                        selected.Select(static value => value.Note).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectNote(selected[index].Note, old[index]);
                    if (discardedNotes.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Direct MIDI Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                }),
                noteTargets: replacement
                    .Where((_, index) => !discarded[index])
                    .Select(value => new DirectMidiNoteCollisionTarget(
                        location.Segment,
                        value.Value.StartTick,
                        value.Value.Key)));
        });

    public static IProjectEditCommand BatchEditDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit Direct MIDI Event points", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidatePointBatchProgram(program);
            if (eventIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count > 4096)
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, values =>
                {
                    int maximum = ValidateBoundedDirectEventLane(values);
                    var first = values.First().Value;
                    int offset = MidiEditingValueDomain.Offset(first.Kind, first.Data1);
                    ValidateDirectRange(program, BatchEditField.PointValue, offset, maximum + offset);
                    long origin = values.Min(static value => value.Value.Tick);
                    var clock = Stopwatch.StartNew();
                    return value =>
                    {
                        DirectMidiEventValue old = new(value.Tick, value.Kind, value.Data1, value.Data2, value.Order);
                        var calculated = program.Evaluate(new(0, DirectMidiEventPointValue(old) + offset, 0, 0,
                            value.Tick, checked(value.Tick - origin)), clock, BatchExpressionTimeout);
                        long? tick = RoundTickOrDiscard(calculated.Tick);
                        if (!tick.HasValue) return null;
                        var result = WithDirectMidiEventPointValue(old with { Tick = tick.Value },
                            checked((int)RoundAndClamp(calculated.PointValue, offset, maximum + offset) - offset));
                        return value with { Tick = result.Tick, Data1 = result.Data1, Data2 = result.Data2 };
                    };
                });
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            EnsureSameDirectMidiEventLane(selected);
            int maximum = selected[0].Event.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127;
            int offset = MidiEditingValueDomain.Offset(selected[0].Event.Kind, selected[0].Event.Data1);
            ValidateDirectRange(program, BatchEditField.PointValue, offset, maximum + offset);
            DirectMidiEventValue[] old = selected.Select(value => value.Original).ToArray();
            long relativeOrigin = old.Min(value => value.Tick);
            Stopwatch clock = Stopwatch.StartNew();
            DirectEventBatchResult[] replacement = old.Select(value =>
            {
                int pointValue = DirectMidiEventPointValue(value);
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: 0,
                        PointValue: pointValue + offset,
                        KeyNumber: 0,
                        Gate: 0,
                        Tick: value.Tick,
                        RelativeTick: checked(value.Tick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                int scalar = checked((int)RoundAndClamp(calculated.PointValue, offset, maximum + offset) - offset);
                DirectMidiEventValue result = WithDirectMidiEventPointValue(
                    value with { Tick = tick ?? 0 },
                    scalar);
                bool discard = tick is null;
                if (!discard)
                    ValidateDirectMidiEvent(result.Tick, result.Kind, result.Data1, result.Data2);
                return new DirectEventBatchResult(result, discard);
            }).ToArray();
            DirectMidiChannelEvent[] discardedEvents = selected
                .Where((_, index) => replacement[index].Discard)
                .Select(static value => value.Event)
                .ToArray();
            Action? restoreDiscarded = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                old.Where((value, index) => value != replacement[index].Value || replacement[index].Discard).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!replacement[index].Discard)
                            ApplyDirectEvent(selected[index].Event, replacement[index].Value);
                    }
                    if (discardedEvents.Length != 0)
                        restoreDiscarded = location.Segment.ChannelEvents.RemoveRangeWithUndo(discardedEvents);
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, old[index]);
                    if (discardedEvents.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Direct MIDI Events do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                }),
                eventTargets: replacement
                    .Where(value => !value.Discard)
                    .Select(value => new DirectMidiEventCollisionTarget(
                        location.Segment,
                        value.Value.Tick,
                        value.Value.Kind,
                        value.Value.Data1)));
        });

    public static IProjectEditCommand FlipMidiSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope) =>
        TransformMidiSegmentSelection(
            "Flip MIDI Segments horizontally",
            segmentIds,
            scope,
            MidiSegmentContentTransformKind.FlipHorizontal,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand FlipMidiSegmentsVertical(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        TransformMidiSegmentSelection(
            "Flip exposed MIDI Segment Notes vertically",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            MidiSegmentContentTransformKind.FlipVertical,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand ScaleMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        double factor,
        SegmentSelectionTransformScope scope) =>
        TransformMidiSegmentSelection(
            "Scale MIDI Segments",
            segmentIds,
            scope,
            MidiSegmentContentTransformKind.Scale,
            factor,
            semitones: 0);

    public static IProjectEditCommand TransposeMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        int semitones) =>
        TransformMidiSegmentSelection(
            "Transpose exposed MIDI Segment Notes",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            MidiSegmentContentTransformKind.Transpose,
            factor: 1,
            semitones);

    public static IProjectEditCommand BatchEditMidiSegmentExposedNotes(
        IReadOnlyCollection<MidoraId> segmentIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit exposed MIDI Segment Notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            MidiSegmentSelection[] segments = SelectMidiSegments(project, segmentIds);
            if (RequiresBoundedMidiContent(segments))
                return PrepareBoundedMidiSegmentTransform(project, "Batch edit exposed MIDI Segment Notes", segments,
                    SegmentSelectionTransformScope.ExposedContentOnly, MidiSegmentContentTransformKind.FlipVertical,
                    1, 0, program);
            List<MidiSegmentDirectNoteSelection> selected = [];
            foreach (MidiSegmentSelection segment in segments)
            {
                long contentLeft = segment.Segment.ContentOffsetTick;
                long contentRight = segment.Segment.ContentEndTick;
                MidoraId[] exposedIds = segment.Segment.Notes
                    .CreateQuerySnapshot()
                    .QueryValues(contentLeft, contentRight, 0, 127)
                    .Select(static value => value.Id)
                    .ToArray();
                selected.AddRange(segment.Segment.Notes
                    .ResolveByIds(exposedIds)
                    .OrderBy(static value => value.Index)
                    .Select(value => new MidiSegmentDirectNoteSelection(
                        segment.Segment,
                        value.Value,
                        value.Index,
                        SnapshotDirectNote(value.Value))));
            }
            if (selected.Count == 0)
                throw new InvalidOperationException("The selected MIDI Segments expose no Direct MIDI Notes to batch edit.");

            long relativeOrigin = selected.Min(value => value.Old.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            MidiSegmentDirectNoteTransform[] notes = selected.Select(value =>
            {
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: value.Old.NoteOnVelocity,
                        PointValue: 0,
                        KeyNumber: value.Old.Key,
                        Gate: value.Old.LengthTicks,
                        Tick: value.Old.StartTick,
                        RelativeTick: checked(value.Old.StartTick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                double roundedKey = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                bool keyOutOfRange = roundedKey is < 0 or > 127;
                DirectNoteValue replacement = value.Old with
                {
                    StartTick = tick ?? 0,
                    LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                    Key = keyOutOfRange ? 0 : checked((int)roundedKey),
                    NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
                };
                bool discard = tick is null || keyOutOfRange;
                if (!discard)
                {
                    ValidateDirectMidiNote(
                        replacement.StartTick,
                        replacement.LengthTicks,
                        replacement.Key,
                        replacement.NoteOnVelocity,
                        replacement.NoteOffVelocity);
                }
                return new MidiSegmentDirectNoteTransform(
                    value.Segment,
                    value.Note,
                    value.Old,
                    replacement,
                    discard);
            }).ToArray();
            return PrepareMidiSegmentContentEdit(segments, [], notes, [], []);
        });

    private static IProjectEditCommand TransformMidiSegmentSelection(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope,
        MidiSegmentContentTransformKind kind,
        double factor,
        int semitones) =>
        Command(name, project =>
        {
            if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (kind == MidiSegmentContentTransformKind.Scale) ValidateScaleFactor(factor);
            MidiSegmentSelection[] segments = SelectMidiSegments(project, segmentIds);
            if (RequiresBoundedMidiContent(segments))
                return PrepareBoundedMidiSegmentTransform(project, name, segments, scope, kind, factor, semitones);
            long selectionLeft = segments.Min(value => value.Segment.ProjectStartTick);
            long selectionRight = segments.Max(value => value.Segment.ProjectRange.EndTick);
            List<MidiSegmentWindowTransform> windows = [];
            List<MidiSegmentDirectNoteTransform> notes = [];
            List<MidiSegmentDirectEventTransform> events = [];
            List<MidiSegmentOpaqueEventTransform> opaque = [];

            foreach (MidiSegmentSelection entry in segments)
            {
                MidiSegment segment = entry.Segment;
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
                        MidiSegmentContentTransformKind.FlipHorizontal => oldWindow with
                        {
                            ProjectStartTick = checked(selectionLeft + selectionRight
                                - checked(oldWindow.ProjectStartTick + oldWindow.LengthTicks))
                        },
                        MidiSegmentContentTransformKind.Scale => oldWindow with
                        {
                            ProjectStartTick = ScaleTick(selectionLeft, oldWindow.ProjectStartTick, factor),
                            LengthTicks = ScaleLength(oldWindow.LengthTicks, factor)
                        },
                        _ => oldWindow
                    };
                }
                windows.Add(new(entry, oldWindow, newWindow));

                MidoraId[] exposedNoteIds = segment.Notes
                    .CreateQuerySnapshot()
                    .QueryValues(contentLeft, contentRight, 0, 127)
                    .Select(static value => value.Id)
                    .ToArray();
                foreach (DirectMidiNoteMatch match in segment.Notes
                    .ResolveByIds(exposedNoteIds)
                    .OrderBy(static value => value.Index))
                {
                    DirectMidiNote note = match.Value;
                    DirectNoteValue old = SnapshotDirectNote(note);
                    DirectNoteValue replacement = kind switch
                    {
                        MidiSegmentContentTransformKind.FlipHorizontal => old with
                        {
                            StartTick = checked(contentLeft + contentRight
                                - checked(old.StartTick + old.LengthTicks))
                        },
                        MidiSegmentContentTransformKind.FlipVertical => old with { Key = 127 - old.Key },
                        MidiSegmentContentTransformKind.Scale => old with
                        {
                            StartTick = ScaleTick(contentLeft, old.StartTick, factor),
                            LengthTicks = ScaleLength(old.LengthTicks, factor)
                        },
                        MidiSegmentContentTransformKind.Transpose => old with
                        {
                            Key = checked(old.Key + semitones)
                        },
                        _ => old
                    };
                    bool discard = replacement.Key is < 0 or > 127;
                    if (!discard)
                    {
                        ValidateDirectMidiNote(
                            replacement.StartTick,
                            replacement.LengthTicks,
                            replacement.Key,
                            replacement.NoteOnVelocity,
                            replacement.NoteOffVelocity);
                    }
                    notes.Add(new(segment, note, old, replacement, discard));
                }

                if (kind is MidiSegmentContentTransformKind.FlipHorizontal
                    or MidiSegmentContentTransformKind.Scale)
                {
                    MidoraId[] exposedEventIds = segment.ChannelEvents
                        .CreateQuerySnapshot()
                        .QueryValues(contentLeft, contentRight)
                        .Select(static value => value.Id)
                        .ToArray();
                    foreach (DirectMidiChannelEventMatch match in segment.ChannelEvents
                        .ResolveByIds(exposedEventIds)
                        .OrderBy(static value => value.Index))
                    {
                        DirectMidiChannelEvent value = match.Value;
                        DirectMidiEventValue old = SnapshotDirectEvent(value);
                        DirectMidiEventValue replacement = old with
                        {
                            Tick = kind == MidiSegmentContentTransformKind.FlipHorizontal
                                ? checked(contentLeft + checked(contentRight - 1) - old.Tick)
                                : ScaleTick(contentLeft, old.Tick, factor)
                        };
                        ValidateDirectMidiEvent(
                            replacement.Tick,
                            replacement.Kind,
                            replacement.Data1,
                            replacement.Data2);
                        events.Add(new(segment, value, old, replacement));
                    }
                    MidoraId[] exposedOpaqueIds = segment.OpaqueEvents
                        .CreateQuerySnapshot()
                        .QueryValues(contentLeft, contentRight)
                        .Select(static value => value.Id)
                        .ToArray();
                    foreach (OpaqueMidiEventMatch match in segment.OpaqueEvents
                        .ResolveByIds(exposedOpaqueIds)
                        .OrderBy(static value => value.Index))
                    {
                        OpaqueMidiEvent value = match.Value;
                        long replacement = kind == MidiSegmentContentTransformKind.FlipHorizontal
                            ? checked(contentLeft + checked(contentRight - 1) - value.Tick)
                            : ScaleTick(contentLeft, value.Tick, factor);
                        if (replacement < 0) throw new ArgumentOutOfRangeException(nameof(segmentIds));
                        opaque.Add(new(segment, value, value.Tick, replacement));
                    }
                }
            }

            ValidateMidiSegmentTransformWindows(windows);
            return PrepareMidiSegmentContentEdit(segments, windows, notes, events, opaque);
        });

    private static IPreparedProjectEdit PrepareMidiSegmentContentEdit(
        IReadOnlyCollection<MidiSegmentSelection> segments,
        IReadOnlyCollection<MidiSegmentWindowTransform> windows,
        IReadOnlyCollection<MidiSegmentDirectNoteTransform> notes,
        IReadOnlyCollection<MidiSegmentDirectEventTransform> events,
        IReadOnlyCollection<MidiSegmentOpaqueEventTransform> opaque)
    {
        Dictionary<MidiSegment, Action>? restoreDiscarded = null;
        IPreparedProjectEdit prepared = Prepared(
            windows.Any(value => value.Old != value.Replacement)
                || notes.Any(value => value.Discard || value.Old != value.Replacement)
                || events.Any(value => value.Old != value.Replacement)
                || opaque.Any(value => value.OldTick != value.ReplacementTick),
            PureMidiTrackChange(segments.Select(value => value.Track.Id).Distinct().ToArray()),
            _ =>
            {
                foreach (IGrouping<MidiSegment, MidiSegmentDirectEventTransform> group in
                    events.GroupBy(static value => value.Segment))
                {
                    DirectMidiChannelEvent[] selected = group.Select(static value => value.Event).ToArray();
                    using IDisposable batch = group.Key.ChannelEvents.BeginBatchChange(selected);
                    foreach (MidiSegmentDirectEventTransform value in group)
                        ApplyDirectEvent(value.Event, value.Replacement);
                }
                foreach (IGrouping<MidiSegment, MidiSegmentOpaqueEventTransform> group in
                    opaque.GroupBy(static value => value.Segment))
                {
                    OpaqueMidiEvent[] selected = group.Select(static value => value.Event).ToArray();
                    using IDisposable batch = group.Key.OpaqueEvents.BeginBatchChange(selected);
                    foreach (MidiSegmentOpaqueEventTransform value in group)
                        value.Event.Tick = value.ReplacementTick;
                }
                foreach (IGrouping<MidiSegment, MidiSegmentDirectNoteTransform> group in
                    notes.Where(static value => !value.Discard).GroupBy(static value => value.Segment))
                {
                    DirectMidiNote[] selected = group.Select(static value => value.Note).ToArray();
                    using IDisposable batch = group.Key.Notes.BeginBatchChange(selected);
                    foreach (MidiSegmentDirectNoteTransform value in group)
                        ApplyDirectNote(value.Note, value.Replacement);
                }
                restoreDiscarded = [];
                foreach (IGrouping<MidiSegment, MidiSegmentDirectNoteTransform> group in
                    notes.Where(static value => value.Discard).GroupBy(static value => value.Segment))
                {
                    restoreDiscarded.Add(
                        group.Key,
                        group.Key.Notes.RemoveRangeForExactCollision(
                            group.Select(static value => value.Note).ToArray()));
                }
                foreach (MidiSegmentWindowTransform value in windows)
                    SetMidiSegmentWindow(value.Entry.Segment, value.Replacement);
                SortTransformedMidiSegments(windows);
            },
            _ =>
            {
                foreach (MidiSegmentWindowTransform value in windows)
                    SetMidiSegmentWindow(value.Entry.Segment, value.Old);
                SortTransformedMidiSegments(windows);
                foreach (IGrouping<MidiSegment, MidiSegmentDirectNoteTransform> group in
                    notes.Where(static value => !value.Discard).GroupBy(static value => value.Segment))
                {
                    DirectMidiNote[] selected = group.Select(static value => value.Note).ToArray();
                    using IDisposable batch = group.Key.Notes.BeginBatchChange(selected);
                    foreach (MidiSegmentDirectNoteTransform value in group)
                        ApplyDirectNote(value.Note, value.Old);
                }
                if (restoreDiscarded is not null)
                {
                    foreach (Action restore in restoreDiscarded.Values) restore();
                    restoreDiscarded = null;
                }
                foreach (IGrouping<MidiSegment, MidiSegmentOpaqueEventTransform> group in
                    opaque.GroupBy(static value => value.Segment))
                {
                    OpaqueMidiEvent[] selected = group.Select(static value => value.Event).ToArray();
                    using IDisposable batch = group.Key.OpaqueEvents.BeginBatchChange(selected);
                    foreach (MidiSegmentOpaqueEventTransform value in group)
                        value.Event.Tick = value.OldTick;
                }
                foreach (IGrouping<MidiSegment, MidiSegmentDirectEventTransform> group in
                    events.GroupBy(static value => value.Segment))
                {
                    DirectMidiChannelEvent[] selected = group.Select(static value => value.Event).ToArray();
                    using IDisposable batch = group.Key.ChannelEvents.BeginBatchChange(selected);
                    foreach (MidiSegmentDirectEventTransform value in group)
                        ApplyDirectEvent(value.Event, value.Old);
                }
            });
        return ResolveTargetedExactDirectMidiCollisions(
            prepared,
            noteTargets: notes
                .Where(value => !value.Discard
                    && (value.Old.StartTick != value.Replacement.StartTick
                        || value.Old.Key != value.Replacement.Key))
                .Select(value => new DirectMidiNoteCollisionTarget(
                    value.Segment,
                    value.Replacement.StartTick,
                    value.Replacement.Key)),
            eventTargets: events
                .Where(value => value.Old.Tick != value.Replacement.Tick
                    || value.Old.Kind != value.Replacement.Kind
                    || DirectMidiEventUsesData1Selector(value.Old.Kind)
                        && value.Old.Data1 != value.Replacement.Data1)
                .Select(value => new DirectMidiEventCollisionTarget(
                    value.Segment,
                    value.Replacement.Tick,
                    value.Replacement.Kind,
                    value.Replacement.Data1)));
    }

    private static void ValidateMidiSegmentTransformWindows(
        IReadOnlyCollection<MidiSegmentWindowTransform> windows)
    {
        HashSet<MidiSegment> selected = windows.Select(value => value.Entry.Segment).ToHashSet();
        foreach (IGrouping<PureMidiTrack, MidiSegmentWindowTransform> group in windows
            .GroupBy(value => value.Entry.Track))
        {
            MidiSegmentWindowTransform[] ordered = group
                .OrderBy(value => value.Replacement.ProjectStartTick)
                .ThenBy(value => value.Entry.Segment.Id)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                SegmentWindow value = ordered[index].Replacement;
                ValidateSegmentRange(value.ProjectStartTick, value.LengthTicks, value.ContentOffsetTick);
                TickRange range = new(
                    value.ProjectStartTick,
                    checked(value.ProjectStartTick + value.LengthTicks));
                if (group.Key.Segments.Any(segment =>
                    !selected.Contains(segment) && range.Intersects(segment.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The transformation would overlap an existing MIDI Segment.");
                }
                if (index > 0)
                {
                    SegmentWindow previous = ordered[index - 1].Replacement;
                    if (checked(previous.ProjectStartTick + previous.LengthTicks) > value.ProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "The transformation would overlap selected MIDI Segments.");
                    }
                }
            }
        }
    }

    private static void SortTransformedMidiSegments(
        IReadOnlyCollection<MidiSegmentWindowTransform> windows)
    {
        foreach (PureMidiTrack track in windows.Select(value => value.Entry.Track).Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    private static IProjectEditCommand TransformDirectMidiEventPoints(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        Func<IReadOnlyList<DirectMidiEventValue>, DirectMidiEventValue[]> transform) =>
        Command(name, project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            EnsureSameDirectMidiEventLane(selected);
            DirectMidiEventValue[] old = selected.Select(value => value.Original).ToArray();
            DirectMidiEventValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
                throw new InvalidOperationException("A Direct MIDI Event transform returned the wrong result count.");
            foreach (DirectMidiEventValue value in replacement)
                ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, replacement[index]);
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, old[index]);
                }),
                eventTargets: replacement.Select(value => new DirectMidiEventCollisionTarget(
                    location.Segment,
                    value.Tick,
                    value.Kind,
                    value.Data1)));
        });

    private static void EnsureSameDirectMidiEventLane(IReadOnlyList<DirectEventSelection> selected)
    {
        DirectMidiChannelEvent first = selected[0].Event;
        if (selected.Skip(1).Any(value => !SameDirectMidiEventLane(first, value.Event)))
        {
            throw new ArgumentException(
                "Direct MIDI Event point transforms require one event lane.",
                nameof(selected));
        }
    }

    private static bool SameDirectMidiEventLane(
        DirectMidiChannelEvent left,
        DirectMidiChannelEvent right) =>
        left.Kind == right.Kind
        && (!DirectMidiEventUsesData1Selector(left.Kind) || left.Data1 == right.Data1);

    private static bool DirectMidiEventUsesData1Selector(DirectMidiChannelEventKind kind) =>
        kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff;

    private static int DirectMidiEventPointValue(DirectMidiEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PitchBend => (value.Data2 << 7) | value.Data1,
        DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => value.Data1,
        _ => value.Data2
    };

    private static DirectMidiEventValue WithDirectMidiEventPointValue(
        DirectMidiEventValue value,
        int pointValue) => value.Kind switch
        {
            DirectMidiChannelEventKind.PitchBend => value with
            {
                Data1 = pointValue & 0x7f,
                Data2 = (pointValue >> 7) & 0x7f
            },
            DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure =>
                value with { Data1 = pointValue },
            _ => value with { Data2 = pointValue }
        };

    private readonly record struct DirectNoteBatchResult(DirectNoteValue Value, bool Discard);
    private readonly record struct DirectEventBatchResult(DirectMidiEventValue Value, bool Discard);
    private enum MidiSegmentContentTransformKind
    {
        FlipHorizontal,
        FlipVertical,
        Scale,
        Transpose
    }
    private readonly record struct MidiSegmentDirectNoteSelection(
        MidiSegment Segment,
        DirectMidiNote Note,
        int Index,
        DirectNoteValue Old);
    private readonly record struct MidiSegmentWindowTransform(
        MidiSegmentSelection Entry,
        SegmentWindow Old,
        SegmentWindow Replacement);
    private readonly record struct MidiSegmentDirectNoteTransform(
        MidiSegment Segment,
        DirectMidiNote Note,
        DirectNoteValue Old,
        DirectNoteValue Replacement,
        bool Discard);
    private readonly record struct MidiSegmentDirectEventTransform(
        MidiSegment Segment,
        DirectMidiChannelEvent Event,
        DirectMidiEventValue Old,
        DirectMidiEventValue Replacement);
    private readonly record struct MidiSegmentOpaqueEventTransform(
        MidiSegment Segment,
        OpaqueMidiEvent Event,
        long OldTick,
        long ReplacementTick);
}
