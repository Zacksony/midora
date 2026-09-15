using Midora.Domain;

namespace Midora.Application;

public enum ProjectBatchValueEditMode
{
    ExactSet,
    RelativeAdjust
}

public enum LogicalNoteAlignment
{
    Start,
    End,
    Length
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MoveLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long tickDelta,
        int pitchDelta) =>
        Command("Move logical notes", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, _ => value =>
                {
                    int pitch = checked(value.Note + pitchDelta);
                    long tick = checked(value.StartTick + tickDelta);
                    ValidateLogicalNote(tick, value.LengthTicks, pitch is < 0 or > 127 ? 0 : pitch, value.Velocity);
                    return pitch is < 0 or > 127 ? null : value with { StartTick = tick, Note = pitch };
                }, collisions: tickDelta != 0 || pitchDelta != 0);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(value => new LogicalNoteValue(
                checked(value.StartTick + tickDelta),
                value.LengthTicks,
                checked(value.Note + pitchDelta),
                value.Velocity)).ToArray();
            bool[] discarded = replacement.Select(value => value.Note is < 0 or > 127).ToArray();
            LogicalNote[] discardedNotes = selected
                .Where((_, index) => discarded[index])
                .Select(static value => value.Note)
                .ToArray();
            Action? restoreDiscarded = null;
            for (int index = 0; index < replacement.Length; index++)
            {
                LogicalNoteValue value = replacement[index];
                ValidateLogicalNote(
                    value.StartTick,
                    value.LengthTicks,
                    discarded[index] ? 0 : value.Note,
                    value.Velocity);
            }
            IPreparedProjectEdit prepared = Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                TrackChange(segment.Track.Id),
                _ =>
                {
                    using IDisposable batch = segment.Segment.Notes.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!discarded[index])
                        {
                            SetLogicalNote(selected[index].Note, replacement[index]);
                        }
                    }
                    if (discardedNotes.Length != 0)
                        restoreDiscarded = segment.Segment.Notes.RemoveRangeWithUndo(discardedNotes);
                },
                _ =>
                {
                    SetLogicalNoteBatch(segment.Segment.Notes, selected, old);
                    if (discardedNotes.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Logical Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                });
            LogicalNoteCollisionTarget[] collisionTargets = replacement
                .Where((value, index) => !discarded[index]
                    && (value.StartTick != old[index].StartTick
                        || value.Note != old[index].Note))
                .Select(value => new LogicalNoteCollisionTarget(
                    segment.Segment,
                    value.StartTick,
                    value.Note))
                .ToArray();
            return collisionTargets.Length == 0
                ? prepared
                : ResolveTargetedExactLogicalNoteCollisions(prepared, collisionTargets);
        });

    public static IProjectEditCommand AdjustLogicalNoteEdges(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        Command("Adjust logical note edges", project =>
        {
            if (minimumLengthTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, selected =>
                {
                    long delta = startDelta < 0 ? Math.Max(startDelta, -selected.Min(v => v.Value.StartTick)) : startDelta;
                    return value =>
                    {
                        var changed = AdjustLogicalNoteEdgesSaturated(new(value.StartTick, value.LengthTicks, value.Note, value.Velocity),
                            delta, endDelta, minimumLengthTicks);
                        return value with { StartTick = changed.StartTick, LengthTicks = changed.LengthTicks };
                    };
                }, collisions: startDelta != 0);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, -old.Min(value => value.StartTick))
                : startDelta;
            LogicalNoteValue[] replacement = old
                .Select(value => AdjustLogicalNoteEdgesSaturated(
                    value,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks))
                .ToArray();
            ValidateLogicalNoteBatch(replacement);
            IPreparedProjectEdit prepared = PrepareLogicalNoteBatch(
                segment.Track.Id,
                segment.Segment.Notes,
                selected,
                old,
                replacement);
            return startDelta == 0
                ? prepared
                : ResolveTargetedExactLogicalNoteCollisions(
                    prepared,
                    replacement.Select(value => new LogicalNoteCollisionTarget(
                        segment.Segment,
                        value.StartTick,
                        value.Note)));
        });

    public static IProjectEditCommand SetLogicalNoteVelocities(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        int value,
        ProjectBatchValueEditMode mode) =>
        Command("Change logical note velocities", project =>
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, _ => item => item with
                {
                    Velocity = mode == ProjectBatchValueEditMode.ExactSet ? value : checked(item.Velocity + value)
                }, collisions: false);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(item => Snapshot(item.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(item => new LogicalNoteValue(
                item.StartTick,
                item.LengthTicks,
                item.Note,
                mode == ProjectBatchValueEditMode.ExactSet
                    ? value
                    : checked(item.Velocity + value))).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                segment.Segment.Notes,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand AdjustSegmentEdges(
        IReadOnlyCollection<MidoraId> segmentIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        Command("Adjust segment edges", project =>
        {
            if (minimumLengthTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
            ArgumentNullException.ThrowIfNull(segmentIds);
            if (segmentIds.Count == 0)
            {
                throw new ArgumentException(
                    "At least one Segment must be selected.",
                    nameof(segmentIds));
            }
            if (startDelta != 0 && endDelta != 0)
            {
                throw new ArgumentException(
                    "A batch Segment edge edit must adjust exactly one edge.",
                    nameof(startDelta));
            }

            HashSet<MidoraId> requested = [];
            (SegmentLocation Location, SegmentWindow Old)[] selected = segmentIds.Select(id =>
            {
                if (id == default || !requested.Add(id))
                {
                    throw new ArgumentException(
                        "Segment selections must contain distinct valid stable IDs.",
                        nameof(segmentIds));
                }
                SegmentLocation location = FindSegment(project, id);
                SegmentWindow old = new(
                    location.Segment.ProjectStartTick,
                    location.Segment.LengthTicks,
                    location.Segment.ContentOffsetTick);
                return (location, old);
            }).ToArray();
            long minimumBatchStartDelta = -selected.Min(value => value.Old.ProjectStartTick);
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, minimumBatchStartDelta)
                : startDelta;
            SegmentEdgeEdit[] edits = selected.Select(value =>
            {
                SegmentEdgeAdjustment adjustment = AdjustSegmentEdgesSaturated(
                    value.Old,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks);
                SegmentWindow replacement = adjustment.Window;
                ValidateSegmentRange(
                    replacement.ProjectStartTick,
                    replacement.LengthTicks,
                    replacement.ContentOffsetTick);
                return new SegmentEdgeEdit(
                    value.Location,
                    value.Old,
                    replacement,
                    adjustment.ContentShift);
            }).ToArray();

            HashSet<Segment> selectedSegments = edits
                .Select(value => value.Location.Segment)
                .ToHashSet();
            foreach (IGrouping<LogicalTrack, SegmentEdgeEdit> trackEdits in edits
                .GroupBy(value => value.Location.Track))
            {
                SegmentEdgeEdit[] ordered = trackEdits
                    .OrderBy(value => value.Replacement.ProjectStartTick)
                    .ThenBy(value => value.Location.Segment.Id)
                    .ToArray();
                for (int index = 0; index < ordered.Length; index++)
                {
                    TickRange candidate = new(
                        ordered[index].Replacement.ProjectStartTick,
                        checked(ordered[index].Replacement.ProjectStartTick
                            + ordered[index].Replacement.LengthTicks));
                    if (trackEdits.Key.Segments.Any(existing =>
                        !selectedSegments.Contains(existing)
                        && candidate.Intersects(existing.ProjectRange)))
                    {
                        throw new InvalidOperationException(
                            "The Segment batch resize would overlap an existing Segment.");
                    }
                    if (index > 0)
                    {
                        SegmentWindow previous = ordered[index - 1].Replacement;
                        long previousEnd = checked(
                            previous.ProjectStartTick + previous.LengthTicks);
                        if (previousEnd > candidate.StartTick)
                        {
                            throw new InvalidOperationException(
                                "Segments in the resized batch would overlap each other.");
                        }
                    }
                }
            }

            if (RequiresBoundedSegmentEdgeShift(edits))
                return PrepareBoundedSegmentEdgeShift(project, edits);

            foreach (SegmentEdgeEdit edit in edits.Where(value => value.ContentShift != 0))
            {
                foreach (var note in edit.Location.Segment.Notes.CreateQuerySnapshot().EnumerateAll())
                    _ = checked(note.StartTick + edit.ContentShift);
                foreach (var lane in edit.Location.Segment.ParameterLanes)
                    foreach (var point in lane.Points.CreateQuerySnapshot().EnumerateAll())
                        _ = checked(point.Tick + edit.ContentShift);
            }

            return Prepared(
                edits.Any(value => value.Old != value.Replacement),
                TrackChange(edits.Select(value => value.Location.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (SegmentEdgeEdit edit in edits)
                    {
                        ShiftSegmentContent(project, edit.Location.Segment, edit.ContentShift);
                        SetWindow(edit.Location.Segment, edit.Replacement);
                    }
                },
                _ =>
                {
                    foreach (SegmentEdgeEdit edit in edits)
                    {
                        SetWindow(edit.Location.Segment, edit.Old);
                        ShiftSegmentContent(project, edit.Location.Segment, -edit.ContentShift);
                    }
                });
        });

    public static IProjectEditCommand SetLogicalNoteValues(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long? startTick = null,
        long? lengthTicks = null,
        int? note = null,
        int? velocity = null) =>
        Command("Set logical note values", project =>
        {
            if (startTick is null
                && lengthTicks is null
                && note is null
                && velocity is null)
            {
                throw new ArgumentException(
                    "At least one Logical Note value must be provided.",
                    nameof(startTick));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, _ => item => item with
                {
                    StartTick = startTick ?? item.StartTick, LengthTicks = lengthTicks ?? item.LengthTicks,
                    Note = note ?? item.Note, Velocity = velocity ?? item.Velocity
                }, collisions: startTick is not null || note is not null);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(item => Snapshot(item.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(item => new LogicalNoteValue(
                startTick ?? item.StartTick,
                lengthTicks ?? item.LengthTicks,
                note ?? item.Note,
                velocity ?? item.Velocity)).ToArray();
            ValidateLogicalNoteBatch(replacement);
            IPreparedProjectEdit prepared = PrepareLogicalNoteBatch(
                segment.Track.Id,
                segment.Segment.Notes,
                selected,
                old,
                replacement);
            return startTick is null && note is null
                ? prepared
                : ResolveTargetedExactLogicalNoteCollisions(
                    prepared,
                    replacement.Select(value => new LogicalNoteCollisionTarget(
                        segment.Segment,
                        value.StartTick,
                        value.Note)));
        });

    public static IProjectEditCommand AlignLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        MidoraId primaryLogicalNoteId,
        LogicalNoteAlignment alignment) =>
        Command("Align logical notes", project =>
        {
            if (!Enum.IsDefined(alignment))
            {
                throw new ArgumentOutOfRangeException(nameof(alignment));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, selected =>
                {
                    var primaryValue = selected.FirstOrDefault(v => v.Value.Id == primaryLogicalNoteId).Value;
                    if (primaryValue.Id == default) throw new ArgumentException("The primary Logical Note must belong to the batch selection.", nameof(primaryLogicalNoteId));
                    long end = checked(primaryValue.StartTick + primaryValue.LengthTicks);
                    return value => alignment switch
                    {
                        LogicalNoteAlignment.Start => value with { StartTick = primaryValue.StartTick },
                        LogicalNoteAlignment.End => value with { StartTick = checked(end - value.LengthTicks) },
                        _ => value with { LengthTicks = primaryValue.LengthTicks }
                    };
                }, collisions: alignment != LogicalNoteAlignment.Length);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNote primary = selected
                .SingleOrDefault(value => value.Note.Id == primaryLogicalNoteId).Note
                ?? throw new ArgumentException(
                    "The primary Logical Note must belong to the batch selection.",
                    nameof(primaryLogicalNoteId));
            LogicalNoteValue primaryValue = Snapshot(primary);
            long primaryEnd = checked(primaryValue.StartTick + primaryValue.LengthTicks);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(value => alignment switch
            {
                LogicalNoteAlignment.Start => value with
                {
                    StartTick = primaryValue.StartTick
                },
                LogicalNoteAlignment.End => value with
                {
                    StartTick = checked(primaryEnd - value.LengthTicks)
                },
                LogicalNoteAlignment.Length => value with
                {
                    LengthTicks = primaryValue.LengthTicks
                },
                _ => throw new ArgumentOutOfRangeException(nameof(alignment))
            }).ToArray();
            ValidateLogicalNoteBatch(replacement);
            IPreparedProjectEdit prepared = PrepareLogicalNoteBatch(
                segment.Track.Id,
                segment.Segment.Notes,
                selected,
                old,
                replacement);
            return alignment == LogicalNoteAlignment.Length
                ? prepared
                : ResolveTargetedExactLogicalNoteCollisions(
                    prepared,
                    replacement.Select(value => new LogicalNoteCollisionTarget(
                        segment.Segment,
                        value.StartTick,
                        value.Note)));
        });

    public static IProjectEditCommand DeleteLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds) =>
        Command("Delete logical notes", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold || segment.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalNotes(project, segment, logicalNoteIds, _ => _ => null, collisions: false);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNote[] values = selected.Select(static value => value.Note).ToArray();
            Action? restore = null;
            return Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                _ =>
                {
                    restore = segment.Segment.Notes.RemoveRangeWithUndo(values);
                },
                _ =>
                {
                    (restore ?? throw new InvalidOperationException(
                        "Logical Notes do not have a pending removal to restore."))();
                    restore = null;
                });
        });

    public static IProjectEditCommand DuplicateLogicalNotes(
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        MidoraId targetSegmentId,
        long newEarliestStartTick) =>
        DuplicateLogicalNotes(
            sourceSegmentId,
            logicalNoteIds,
            targetSegmentId,
            newEarliestStartTick,
            pitchDelta: 0);

    public static IProjectEditCommand DuplicateLogicalNotes(
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        MidoraId targetSegmentId,
        long newEarliestStartTick,
        int pitchDelta) =>
        Command("Duplicate logical notes", project =>
        {
            if (newEarliestStartTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newEarliestStartTick));
            }
            SegmentLocation source = FindSegment(project, sourceSegmentId);
            SegmentLocation target = FindSegment(project, targetSegmentId);
            if (logicalNoteIds.Count >= BoundedNoteThreshold
                || source.Segment.Notes.Count >= BoundedNoteThreshold || target.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalDuplicate(project, source, target, logicalNoteIds, newEarliestStartTick, pitchDelta);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                source.Segment,
                logicalNoteIds);
            long earliest = selected.Min(value => value.Note.StartTick);
            long delta = checked(newEarliestStartTick - earliest);
            LogicalNoteValue[] snapshots = selected
                .Select(value => Snapshot(value.Note) with
                {
                    StartTick = checked(value.Note.StartTick + delta),
                    Note = checked(value.Note.Note + pitchDelta)
                })
                .ToArray();
            ValidateLogicalNoteBatch(snapshots);
            LogicalNote[]? copies = null;
            int insertionIndex = target.Segment.Notes.Count;
            return ResolveTargetedExactLogicalNoteCollisions(Prepared(
                hasChanges: true,
                TrackChange(source.Track.Id, target.Track.Id),
                owner =>
                {
                    if (copies is null)
                    {
                        copies = snapshots.Select(value => new LogicalNote(owner)
                        {
                            StartTick = value.StartTick,
                            LengthTicks = value.LengthTicks,
                            Note = value.Note,
                            Velocity = value.Velocity
                        }).ToArray();
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        InsertAt(
                            target.Segment.Notes,
                            insertionIndex + index,
                            copies[index],
                            "Logical Note copy");
                    }
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Note copies do not exist before the first Apply.");
                    }
                    int removed = target.Segment.Notes.RemoveRange(copies);
                    if (removed != copies.Length)
                        throw new InvalidOperationException("The Logical Note copy set is no longer present.");
                }), snapshots.Select(value => new LogicalNoteCollisionTarget(
                    target.Segment,
                    value.StartTick,
                    value.Note)));
        });

    public static IProjectEditCommand MoveSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick) =>
        Command("Move segments", project =>
        {
            SegmentBatchPlacement[] placements = PlanSegmentBatch(
                project,
                segmentIds,
                primarySegmentId,
                targetPrimaryTrackId,
                newPrimaryStartTick,
                isCopy: false);
            bool changed = placements.Any(value =>
                !ReferenceEquals(value.Source.Track, value.TargetTrack)
                || value.Source.Segment.ProjectStartTick != value.NewProjectStartTick);
            SegmentOriginalPlacement[] original = placements.Select(value =>
                new SegmentOriginalPlacement(
                    value.Source.Track,
                    value.Source.Segment,
                    value.Source.Index,
                    value.Source.Segment.ProjectStartTick)).ToArray();
            ProjectChangeSet changes = TrackChange(placements
                .SelectMany(value => new[] { value.Source.Track.Id, value.TargetTrack.Id })
                .Distinct()
                .ToArray());
            return Prepared(
                changed,
                changes,
                _ =>
                {
                    foreach (SegmentOriginalPlacement value in original)
                    {
                        RemoveRequired(value.Track.Segments, value.Segment, "Segment");
                    }
                    foreach (SegmentBatchPlacement value in placements)
                    {
                        value.Source.Segment.ProjectStartTick = value.NewProjectStartTick;
                        InsertSegmentByTime(value.TargetTrack.Segments, value.Source.Segment);
                    }
                },
                _ =>
                {
                    foreach (SegmentBatchPlacement value in placements)
                    {
                        RemoveRequired(value.TargetTrack.Segments, value.Source.Segment, "Segment");
                    }
                    foreach (SegmentOriginalPlacement value in original)
                    {
                        value.Segment.ProjectStartTick = value.ProjectStartTick;
                    }
                    foreach (IGrouping<LogicalTrack, SegmentOriginalPlacement> group in original
                        .GroupBy(value => value.Track))
                    {
                        foreach (SegmentOriginalPlacement value in group.OrderBy(value => value.Index))
                        {
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                        }
                    }
                });
        });

    public static IProjectEditCommand DuplicateSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick) =>
        Command("Duplicate segments", project =>
        {
            SegmentBatchPlacement[] placements = PlanSegmentBatch(
                project,
                segmentIds,
                primarySegmentId,
                targetPrimaryTrackId,
                newPrimaryStartTick,
                isCopy: true);
            if (RequiresBoundedSegmentCopies(placements))
                return PrepareBoundedSegmentCopies(project, placements);
            ProjectChangeSet changes = TrackChange(placements
                .Select(value => value.TargetTrack.Id)
                .Distinct()
                .ToArray());
            Segment[]? copies = null;
            return Prepared(
                hasChanges: true,
                changes,
                owner =>
                {
                    if (copies is null)
                    {
                        Segment[] created = new Segment[placements.Length];
                        for (int index = 0; index < placements.Length; index++)
                        {
                            created[index] = SegmentEditing.Duplicate(
                                owner,
                                placements[index].Source.Segment);
                            created[index].ProjectStartTick = placements[index].NewProjectStartTick;
                        }
                        copies = created;
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        InsertSegmentByTime(placements[index].TargetTrack.Segments, copies[index]);
                    }
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Segment copies do not exist before the first Apply.");
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        RemoveRequired(
                            placements[index].TargetTrack.Segments,
                            copies[index],
                            "Segment copy");
                    }
                });
        });

    public static IProjectEditCommand DeleteSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete segments", project =>
        {
            ArgumentNullException.ThrowIfNull(segmentIds);
            if (segmentIds.Count == 0)
            {
                throw new ArgumentException(
                    "At least one Segment must be selected.",
                    nameof(segmentIds));
            }
            HashSet<MidoraId> requested = [];
            SegmentOriginalPlacement[] selected = segmentIds.Select(id =>
            {
                if (id == default || !requested.Add(id))
                {
                    throw new ArgumentException(
                        "Segment selections must contain distinct valid stable IDs.",
                        nameof(segmentIds));
                }
                SegmentLocation location = FindSegment(project, id);
                return new SegmentOriginalPlacement(
                    location.Track,
                    location.Segment,
                    location.Index,
                    location.Segment.ProjectStartTick);
            }).ToArray();
            return Prepared(
                hasChanges: true,
                TrackChange(selected.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (SegmentOriginalPlacement value in selected)
                    {
                        RemoveRequired(value.Track.Segments, value.Segment, "Segment");
                    }
                },
                _ =>
                {
                    foreach (IGrouping<LogicalTrack, SegmentOriginalPlacement> group in selected
                        .GroupBy(value => value.Track))
                    {
                        foreach (SegmentOriginalPlacement value in group.OrderBy(value => value.Index))
                        {
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                        }
                    }
                });
        });

    private static SegmentBatchPlacement[] PlanSegmentBatch(
        MidoraProject project,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick,
        bool isCopy)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Segment must be selected.",
                nameof(segmentIds));
        }
        if (newPrimaryStartTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newPrimaryStartTick));
        }
        HashSet<MidoraId> requested = [];
        foreach (MidoraId id in segmentIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Segment selections must contain distinct valid stable IDs.",
                    nameof(segmentIds));
            }
        }
        SelectedSegment[] selected = requested
            .Select(id =>
            {
                SegmentLocation location = FindSegment(project, id);
                return new SelectedSegment(
                    location,
                    FindArrangementTrackIndex(
                        project,
                        ArrangementTrackKind.LogicalTrack,
                        location.Track.Id));
            })
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Location.Segment.ProjectStartTick)
            .ThenBy(value => value.Location.Segment.Id)
            .ToArray();
        SelectedSegment primary = selected
            .SingleOrDefault(value => value.Location.Segment.Id == primarySegmentId);
        if (primary.Location.Segment is null)
        {
            throw new ArgumentException(
                "The primary Segment must belong to the batch selection.",
                nameof(primarySegmentId));
        }
        LogicalTrack targetPrimaryTrack = FindTrack(project, targetPrimaryTrackId);
        int targetPrimaryTrackIndex = FindArrangementTrackIndex(
            project,
            ArrangementTrackKind.LogicalTrack,
            targetPrimaryTrack.Id);
        EnsureLogicalTrackCanContainContent(project, targetPrimaryTrack);
        long tickDelta = checked(
            newPrimaryStartTick - primary.Location.Segment.ProjectStartTick);
        SegmentBatchPlacement[] placements = selected.Select(value =>
        {
            int targetTrackIndex = checked(
                targetPrimaryTrackIndex + value.TrackIndex - primary.TrackIndex);
            if ((uint)targetTrackIndex >= (uint)project.ArrangementTracks.Count
                || project.ArrangementTracks[targetTrackIndex] is not
                { Kind: ArrangementTrackKind.LogicalTrack } targetReference)
            {
                throw new InvalidOperationException(
                    "The Segment batch cannot preserve its relative Arrangement lane offsets at the target.");
            }
            LogicalTrack targetTrack = FindTrack(project, targetReference.TrackId);
            EnsureLogicalTrackCanContainContent(project, targetTrack);
            long start = checked(value.Location.Segment.ProjectStartTick + tickDelta);
            ValidateSegmentRange(
                start,
                value.Location.Segment.LengthTicks,
                value.Location.Segment.ContentOffsetTick);
            return new SegmentBatchPlacement(
                value.Location,
                targetTrack,
                start);
        }).ToArray();

        HashSet<Segment> selectedObjects = selected
            .Select(value => value.Location.Segment)
            .ToHashSet<Segment>(ReferenceEqualityComparer.Instance);
        foreach (IGrouping<LogicalTrack, SegmentBatchPlacement> group in placements
            .GroupBy(value => value.TargetTrack))
        {
            SegmentBatchPlacement[] candidates = group
                .OrderBy(value => value.NewProjectStartTick)
                .ThenBy(value => value.Source.Segment.Id)
                .ToArray();
            for (int index = 0; index < candidates.Length; index++)
            {
                TickRange range = new(
                    candidates[index].NewProjectStartTick,
                    checked(candidates[index].NewProjectStartTick
                        + candidates[index].Source.Segment.LengthTicks));
                if (group.Key.Segments.Any(existing =>
                    (isCopy || !selectedObjects.Contains(existing))
                    && range.Intersects(existing.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The Segment batch would overlap an existing Segment.");
                }
                if (index != 0)
                {
                    SegmentBatchPlacement previous = candidates[index - 1];
                    long previousEnd = checked(previous.NewProjectStartTick
                        + previous.Source.Segment.LengthTicks);
                    if (previousEnd > candidates[index].NewProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "Segments in the batch would overlap each other.");
                    }
                }
            }
        }
        return placements;
    }

    private static IPreparedProjectEdit PrepareLogicalNoteBatch(
        MidoraId trackId,
        LogicalNoteCollection notes,
        SelectedLogicalNote[] selected,
        LogicalNoteValue[] old,
        LogicalNoteValue[] replacement) =>
        Prepared(
            old.Where((value, index) => value != replacement[index]).Any(),
            TrackChange(trackId),
            _ => SetLogicalNoteBatch(notes, selected, replacement),
            _ => SetLogicalNoteBatch(notes, selected, old));

    private static LogicalNoteValue AdjustLogicalNoteEdgesSaturated(
        LogicalNoteValue value,
        long startDelta,
        long endDelta,
        long minimumLengthTicks)
    {
        long oldEnd = checked(value.StartTick + value.LengthTicks);
        long effectiveMinimumLengthTicks = Math.Min(value.LengthTicks, minimumLengthTicks);
        long requestedStart = checked(value.StartTick + startDelta);
        long requestedEnd = checked(oldEnd + endDelta);
        long start;
        long end;
        if (startDelta != 0 && endDelta == 0)
        {
            start = Math.Clamp(
                requestedStart,
                0,
                Math.Max(0, checked(oldEnd - effectiveMinimumLengthTicks)));
            end = oldEnd;
        }
        else if (startDelta == 0)
        {
            start = value.StartTick;
            end = Math.Max(checked(start + effectiveMinimumLengthTicks), requestedEnd);
        }
        else
        {
            start = Math.Max(0, requestedStart);
            end = Math.Max(checked(start + effectiveMinimumLengthTicks), requestedEnd);
        }
        return value with
        {
            StartTick = start,
            LengthTicks = checked(end - start)
        };
    }

    private static SegmentEdgeAdjustment AdjustSegmentEdgesSaturated(
        SegmentWindow value,
        long startDelta,
        long endDelta,
        long minimumLengthTicks)
    {
        long oldEnd = checked(value.ProjectStartTick + value.LengthTicks);
        long effectiveMinimumLengthTicks = Math.Min(value.LengthTicks, minimumLengthTicks);
        if (startDelta != 0)
        {
            long requestedStart = checked(value.ProjectStartTick + startDelta);
            long start = Math.Clamp(
                requestedStart,
                0,
                Math.Max(0, checked(oldEnd - effectiveMinimumLengthTicks)));
            long appliedDelta = checked(start - value.ProjectStartTick);
            long requestedContentOffset = checked(value.ContentOffsetTick + appliedDelta);
            long contentShift = requestedContentOffset < 0
                ? checked(-requestedContentOffset)
                : 0;
            return new(
                new SegmentWindow(
                    start,
                    checked(oldEnd - start),
                    Math.Max(0, requestedContentOffset)),
                contentShift);
        }
        long requestedEnd = checked(oldEnd + endDelta);
        long end = Math.Max(
            checked(value.ProjectStartTick + effectiveMinimumLengthTicks),
            requestedEnd);
        return new(
            new SegmentWindow(
                value.ProjectStartTick,
                checked(end - value.ProjectStartTick),
                value.ContentOffsetTick),
            ContentShift: 0);
    }

    private static void ShiftSegmentContent(
        MidoraProject project,
        Segment segment,
        long tickDelta)
    {
        if (tickDelta == 0) return;
        using (segment.Notes.BeginBatchChange())
        {
            foreach (LogicalNote note in segment.Notes)
                note.StartTick = checked(note.StartTick + tickDelta);
        }
        foreach (LogicalParameterLane lane in segment.ParameterLanes)
        {
            CurvePoint[] old = lane.Points.ToArray();
            CurvePoint[] replacement = old.Select(point => new CurvePoint(
                    project,
                    point.Id,
                    checked(point.Tick + tickDelta),
                    point.Value,
                    CurveInterpolation.Step))
                .ToArray();
            lane.Points.ReplaceRange(old, replacement);
        }
    }

    private static void SetLogicalNoteBatch(
        LogicalNoteCollection notes,
        SelectedLogicalNote[] selected,
        LogicalNoteValue[] values)
    {
        using IDisposable batch = notes.BeginBatchChange();
        for (int index = 0; index < selected.Length; index++)
        {
            SetLogicalNote(selected[index].Note, values[index]);
        }
    }

    private static SelectedLogicalNote[] SelectLogicalNotes(
        Segment segment,
        IReadOnlyCollection<MidoraId> logicalNoteIds)
    {
        ArgumentNullException.ThrowIfNull(logicalNoteIds);
        if (logicalNoteIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Logical Note must be selected.",
                nameof(logicalNoteIds));
        }
        HashSet<MidoraId> requested = [];
        foreach (MidoraId id in logicalNoteIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Logical Note selections must contain distinct valid stable IDs.",
                    nameof(logicalNoteIds));
            }
        }
        SelectedLogicalNote[] selected = segment.Notes
            .ResolveByIdsWithIndicesInCollectionOrder(requested)
            .Select(static value => new SelectedLogicalNote(value.Value, value.Index))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every selected Logical Note must belong to the target Segment.",
                nameof(logicalNoteIds));
        }
        return selected;
    }

    private static LogicalNoteValue Snapshot(LogicalNote value) =>
        new(value.StartTick, value.LengthTicks, value.Note, value.Velocity);

    private static void ValidateLogicalNoteBatch(IEnumerable<LogicalNoteValue> values)
    {
        foreach (LogicalNoteValue value in values)
        {
            ValidateLogicalNote(
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity);
        }
    }

    private readonly record struct SelectedLogicalNote(LogicalNote Note, int Index);
    private readonly record struct SelectedSegment(SegmentLocation Location, int TrackIndex);
    private readonly record struct SegmentBatchPlacement(
        SegmentLocation Source,
        LogicalTrack TargetTrack,
        long NewProjectStartTick);
    private readonly record struct SegmentOriginalPlacement(
        LogicalTrack Track,
        Segment Segment,
        int Index,
        long ProjectStartTick);
    private readonly record struct SegmentEdgeEdit(
        SegmentLocation Location,
        SegmentWindow Old,
        SegmentWindow Replacement,
        long ContentShift);
    private readonly record struct SegmentEdgeAdjustment(
        SegmentWindow Window,
        long ContentShift);
}
