using Midora.Domain;

namespace Midora.Application;

public sealed record DirectMidiEventPointEdit(long Tick, int Data1, int Data2);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand SetArrangementSegmentValues(
        IReadOnlyCollection<MidoraId> segmentIds,
        long? projectStartTick = null,
        long? lengthTicks = null,
        long? contentOffsetTick = null) =>
        PrepareArrangementDirectoryEdit("Update Segment properties", project =>
            SetArrangementSegmentValuesCore(segmentIds, projectStartTick, lengthTicks, contentOffsetTick));

    private static IProjectEditCommand SetArrangementSegmentValuesCore(
        IReadOnlyCollection<MidoraId> segmentIds, long? projectStartTick,
        long? lengthTicks, long? contentOffsetTick) =>
        Command("Update Segment properties", project =>
        {
            ArgumentNullException.ThrowIfNull(segmentIds);
            using IDisposable metadata = ReserveArrangementEditDirectory(project, segmentIds.Count);
            HashSet<MidoraId> requested = segmentIds.ToHashSet();
            if (requested.Count == 0
                || requested.Count != segmentIds.Count
                || requested.Contains(default))
            {
                throw new ArgumentException(
                    "Segment IDs must be distinct and valid.",
                    nameof(segmentIds));
            }

            var logical = project.Tracks
                .SelectMany(track => track.Segments.Select(segment => (
                    Track: track,
                    Segment: segment,
                    Old: new SegmentWindow(
                        segment.ProjectStartTick,
                        segment.LengthTicks,
                        segment.ContentOffsetTick))))
                .Where(value => IsRequestedArrangementSegment(requested, value.Segment.Id))
                .Select(value => (
                    value.Track,
                    value.Segment,
                    value.Old,
                    Replacement: new SegmentWindow(
                        projectStartTick ?? value.Old.ProjectStartTick,
                        lengthTicks ?? value.Old.LengthTicks,
                        contentOffsetTick ?? value.Old.ContentOffsetTick)))
                .ToArray();
            var midi = project.PureMidiTracks
                .SelectMany(track => track.Segments.Select(segment => (
                    Track: track,
                    Segment: segment,
                    Old: new SegmentWindow(
                        segment.ProjectStartTick,
                        segment.LengthTicks,
                        segment.ContentOffsetTick))))
                .Where(value => IsRequestedArrangementSegment(requested, value.Segment.Id))
                .Select(value => (
                    value.Track,
                    value.Segment,
                    value.Old,
                    Replacement: new SegmentWindow(
                        projectStartTick ?? value.Old.ProjectStartTick,
                        lengthTicks ?? value.Old.LengthTicks,
                        contentOffsetTick ?? value.Old.ContentOffsetTick)))
                .ToArray();
            if (requested.Count != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentIds));
            }

            foreach (var value in logical)
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                ValidateSegmentRange(
                    value.Replacement.ProjectStartTick,
                    value.Replacement.LengthTicks,
                    value.Replacement.ContentOffsetTick);
            }
            foreach (var value in midi)
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                ValidateSegmentRange(
                    value.Replacement.ProjectStartTick,
                    value.Replacement.LengthTicks,
                    value.Replacement.ContentOffsetTick);
            }

            HashSet<Segment> logicalSelection = logical.Select(value => value.Segment).ToHashSet();
            foreach (var group in logical.GroupBy(value => value.Track))
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                TickRange[] final = group
                    .Select(value => new TickRange(
                        value.Replacement.ProjectStartTick,
                        checked(value.Replacement.ProjectStartTick + value.Replacement.LengthTicks)))
                    .Concat(group.Key.Segments
                        .Where(segment => !logicalSelection.Contains(segment))
                        .Select(segment => segment.ProjectRange))
                    .OrderBy(range => range.StartTick)
                    .ToArray();
                if (final.Zip(final.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick))
                {
                    throw new InvalidOperationException(
                        "The Segment property edit would create an overlap.");
                }
            }

            HashSet<MidiSegment> midiSelection = midi.Select(value => value.Segment).ToHashSet();
            foreach (var group in midi.GroupBy(value => value.Track))
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                TickRange[] final = group
                    .Select(value => new TickRange(
                        value.Replacement.ProjectStartTick,
                        checked(value.Replacement.ProjectStartTick + value.Replacement.LengthTicks)))
                    .Concat(group.Key.Segments
                        .Where(segment => !midiSelection.Contains(segment))
                        .Select(segment => segment.ProjectRange))
                    .OrderBy(range => range.StartTick)
                    .ToArray();
                if (final.Zip(final.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick))
                {
                    throw new InvalidOperationException(
                        "The MIDI Segment property edit would create an overlap.");
                }
            }

            ProjectChangeSet changes = new();
            changes.TrackIds.UnionWith(logical.Select(value => value.Track.Id));
            changes.PureMidiTrackIds.UnionWith(midi.Select(value => value.Track.Id));
            return Prepared(
                logical.Any(value => value.Old != value.Replacement)
                    || midi.Any(value => value.Old != value.Replacement),
                changes,
                _ =>
                {
                    foreach (var value in logical) SetWindow(value.Segment, value.Replacement);
                    foreach (var value in midi) SetMidiSegmentWindow(value.Segment, value.Replacement);
                    SortSegmentOwners(logical.Select(value => value.Track));
                    SortMidiSegmentOwners(midi.Select(value => value.Track));
                },
                _ =>
                {
                    foreach (var value in logical) SetWindow(value.Segment, value.Old);
                    foreach (var value in midi) SetMidiSegmentWindow(value.Segment, value.Old);
                    SortSegmentOwners(logical.Select(value => value.Track));
                    SortMidiSegmentOwners(midi.Select(value => value.Track));
                });
        });

    private static void SortSegmentOwners(IEnumerable<LogicalTrack> tracks)
    {
        foreach (LogicalTrack track in tracks.Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    private static void SortMidiSegmentOwners(IEnumerable<PureMidiTrack> tracks)
    {
        foreach (PureMidiTrack track in tracks.Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    public static IProjectEditCommand DeleteArrangementSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        PrepareArrangementDirectoryEdit("Delete Arrangement Segments", _ => DeleteArrangementSegmentsCore(segmentIds));

    private static IProjectEditCommand DeleteArrangementSegmentsCore(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete Arrangement Segments", project =>
        {
            ArgumentNullException.ThrowIfNull(segmentIds);
            using IDisposable metadata = ReserveArrangementEditDirectory(project, segmentIds.Count);
            HashSet<MidoraId> requested = segmentIds.ToHashSet();
            if (requested.Count != segmentIds.Count || requested.Contains(default))
                throw new ArgumentException("Arrangement Segment IDs must be distinct and valid.", nameof(segmentIds));
            (LogicalTrack Track, Segment Segment, int Index)[] logical = project.Tracks
                .SelectMany(track => track.Segments.Select((segment, index) => (track, segment, index)))
                .Where(value => IsRequestedArrangementSegment(requested, value.segment.Id))
                .Select(value => (value.track, value.segment, value.index))
                .ToArray();
            (PureMidiTrack Track, MidiSegment Segment, int Index)[] midi = project.PureMidiTracks
                .SelectMany(track => track.Segments.Select((segment, index) => (track, segment, index)))
                .Where(value => IsRequestedArrangementSegment(requested, value.segment.Id))
                .Select(value => (value.track, value.segment, value.index))
                .ToArray();
            if (requested.Count != 0 || logical.Length + midi.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(segmentIds));
            ProjectChangeSet changes = new();
            changes.TrackIds.UnionWith(logical.Select(value => value.Track.Id));
            changes.PureMidiTrackIds.UnionWith(midi.Select(value => value.Track.Id));
            HashSet<MidoraId> deleted = logical.Select(value => value.Segment.Id)
                .Concat(midi.Select(value => value.Segment.Id)).ToHashSet();
            return Prepared(
                true,
                changes,
                _ =>
                {
                    foreach (LogicalTrack track in logical.Select(value => value.Track).Distinct())
                        track.Segments.RemoveAll(segment => deleted.Contains(segment.Id));
                    foreach (PureMidiTrack track in midi.Select(value => value.Track).Distinct())
                        track.Segments.RemoveAll(segment => deleted.Contains(segment.Id));
                },
                _ =>
                {
                    foreach (var value in logical.OrderBy(value => value.Index))
                        InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                    foreach (var value in midi.OrderBy(value => value.Index))
                        InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                });
        });

    public static IProjectEditCommand DeleteMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete MIDI Segments", project =>
        {
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            return Prepared(
                true,
                PureMidiTrackChange(selected.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (MidiSegmentSelection value in selected)
                        RemoveRequired(value.Track.Segments, value.Segment, "MIDI Segment");
                },
                _ =>
                {
                    foreach (IGrouping<PureMidiTrack, MidiSegmentSelection> group in selected.GroupBy(value => value.Track))
                        foreach (MidiSegmentSelection value in group.OrderBy(value => value.Index))
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                });
        });

    public static IProjectEditCommand MoveMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick) =>
        TransformMidiSegments(
            "Move MIDI Segments",
            segmentIds,
            primarySegmentId,
            targetTrackId,
            newPrimaryStartTick,
            duplicate: false);

    public static IProjectEditCommand DuplicateMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick) =>
        TransformMidiSegments(
            "Duplicate MIDI Segments",
            segmentIds,
            primarySegmentId,
            targetTrackId,
            newPrimaryStartTick,
            duplicate: true);

    private static IProjectEditCommand TransformMidiSegments(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick,
        bool duplicate,
        bool detachedPreparation = false) =>
        Command(name, project =>
        {
            if (newPrimaryStartTick < 0) throw new ArgumentOutOfRangeException(nameof(newPrimaryStartTick));
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            MidiSegmentSelection primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
            if (primary.Segment is null)
                throw new ArgumentException("The primary MIDI Segment must be selected.", nameof(primarySegmentId));
            int primaryTrackIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                primary.Track.Id);
            PureMidiTrack targetPrimaryTrack = FindPureMidiTrack(project, targetTrackId);
            int targetPrimaryTrackIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                targetPrimaryTrack.Id);
            long delta = checked(newPrimaryStartTick - primary.Segment.ProjectStartTick);
            MidiSegmentBatchPlacement[] placements = selected.Select(value =>
            {
                int sourceTrackIndex = FindArrangementTrackIndex(
                    project,
                    ArrangementTrackKind.PureMidiTrack,
                    value.Track.Id);
                int targetTrackIndex = checked(
                    targetPrimaryTrackIndex + sourceTrackIndex - primaryTrackIndex);
                if ((uint)targetTrackIndex >= (uint)project.ArrangementTracks.Count
                    || project.ArrangementTracks[targetTrackIndex] is not
                    { Kind: ArrangementTrackKind.PureMidiTrack } targetReference)
                {
                    throw new InvalidOperationException(
                        "The MIDI Segment batch cannot preserve its relative Arrangement lane offsets at the target.");
                }
                long start = checked(value.Segment.ProjectStartTick + delta);
                if (start < 0)
                    throw new InvalidOperationException("The MIDI Segment move would cross tick 0.");
                return new MidiSegmentBatchPlacement(
                    value,
                    FindPureMidiTrack(project, targetReference.TrackId),
                    start);
            }).ToArray();
            HashSet<MidiSegment> moving = duplicate
                ? []
                : selected.Select(value => value.Segment).ToHashSet();
            ValidateMidiSegmentPlacements(placements, moving);
            if (duplicate && !detachedPreparation && RequiresBoundedMidiContent(selected))
                return new SequentialProjectEditCommand(name,
                    [draft => TransformMidiSegments(name, segmentIds, primarySegmentId,
                        targetTrackId, newPrimaryStartTick, duplicate: true, detachedPreparation: true)]).Prepare(project);
            MidiSegment[]? copies = null;
            return Prepared(
                true,
                PureMidiTrackChange(placements
                    .SelectMany(value => new[] { value.Source.Track.Id, value.TargetTrack.Id })
                    .Distinct()
                    .ToArray()),
                owner =>
                {
                    if (duplicate)
                    {
                        if (copies is null)
                        {
                            var copyProgress = new DirectContentCopyProgress(placements.Sum(value => DirectContentRecordCount(value.Source.Segment)));
                            copies = placements.Select(value =>
                                CloneMidiSegment(owner, value.Source.Segment, value.Start, copyProgress)).ToArray();
                        }
                        for (int index = 0; index < copies.Length; index++)
                            InsertMidiSegmentByTime(placements[index].TargetTrack.Segments, copies[index]);
                        return;
                    }
                    foreach (MidiSegmentSelection value in selected)
                        RemoveRequired(value.Track.Segments, value.Segment, "MIDI Segment");
                    foreach (MidiSegmentBatchPlacement placement in placements)
                    {
                        placement.Source.Segment.ProjectStartTick = placement.Start;
                        InsertMidiSegmentByTime(placement.TargetTrack.Segments, placement.Source.Segment);
                    }
                },
                _ =>
                {
                    if (duplicate)
                    {
                        for (int index = 0; index < (copies?.Length ?? 0); index++)
                            RemoveRequired(
                                placements[index].TargetTrack.Segments,
                                copies![index],
                                "MIDI Segment copy");
                        return;
                    }
                    foreach (MidiSegmentBatchPlacement placement in placements)
                        RemoveRequired(
                            placement.TargetTrack.Segments,
                            placement.Source.Segment,
                            "MIDI Segment");
                    foreach (MidiSegmentSelection value in selected)
                        value.Segment.ProjectStartTick = value.OriginalStartTick;
                    foreach (IGrouping<PureMidiTrack, MidiSegmentSelection> group in selected.GroupBy(value => value.Track))
                    {
                        foreach (MidiSegmentSelection value in group.OrderBy(value => value.Index))
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                    }
                });
        });

    public static IProjectEditCommand AdjustMidiSegmentEdges(
        IReadOnlyCollection<MidoraId> segmentIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        Command("Adjust MIDI Segment edges", project =>
        {
            if (minimumLengthTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
            if (startDelta != 0 && endDelta != 0)
                throw new ArgumentException("Exactly one MIDI Segment edge may change.");
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, -selected.Min(value => value.Segment.ProjectStartTick))
                : startDelta;
            MidiSegmentEdgeEdit[] edits = selected.Select(value =>
            {
                SegmentWindow old = new(
                    value.Segment.ProjectStartTick,
                    value.Segment.LengthTicks,
                    value.Segment.ContentOffsetTick);
                SegmentEdgeAdjustment adjustment = AdjustSegmentEdgesSaturated(
                    old,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks);
                return new MidiSegmentEdgeEdit(value, old, adjustment.Window, adjustment.ContentShift);
            }).ToArray();
            ValidateMidiSegmentEdgeEdits(edits);
            if (edits.Any(static edit => edit.ContentShift != 0) && RequiresBoundedMidiContent(selected))
                return PrepareBoundedMidiSegmentEdgeShifts(project, edits);
            return Prepared(
                edits.Any(value => value.Old != value.Replacement),
                PureMidiTrackChange(edits.Select(value => value.Selection.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (MidiSegmentEdgeEdit edit in edits)
                    {
                        ShiftMidiSegmentContent(edit.Selection.Segment, edit.ContentShift);
                        SetMidiSegmentWindow(edit.Selection.Segment, edit.Replacement);
                    }
                },
                _ =>
                {
                    foreach (MidiSegmentEdgeEdit edit in edits)
                    {
                        SetMidiSegmentWindow(edit.Selection.Segment, edit.Old);
                        ShiftMidiSegmentContent(edit.Selection.Segment, -edit.ContentShift);
                    }
                });
        });

    public static IProjectEditCommand SetMidiSegmentWindow(
        MidoraId segmentId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick) =>
        Command("Change MIDI Segment window", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            SegmentWindow old = new(
                location.Segment.ProjectStartTick,
                location.Segment.LengthTicks,
                location.Segment.ContentOffsetTick);
            SegmentWindow replacement = new(
                projectStartTick,
                lengthTicks,
                contentOffsetTick);
            ValidateMidiSegmentEdgeEdits(
            [
                new MidiSegmentEdgeEdit(
                    new MidiSegmentSelection(
                        location.Track,
                        location.Segment,
                        location.Index,
                        location.Segment.ProjectStartTick),
                    old,
                    replacement,
                    ContentShift: 0)
            ]);
            return Prepared(
                old != replacement,
                PureMidiTrackChange(location.Track.Id),
                _ => SetMidiSegmentWindow(location.Segment, replacement),
                _ => SetMidiSegmentWindow(location.Segment, old));
        });

    public static IProjectEditCommand DeleteDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        ChangeBoundedDirectMidiNotes("Delete Direct MIDI Notes", segmentId, noteIds, _ => _ => null);

    public static IProjectEditCommand MoveDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int keyDelta) =>
        ChangeDirectMidiNotes(
            "Move Direct MIDI Notes",
            segmentId,
            noteIds,
            value => value with
            {
                StartTick = checked(value.StartTick + tickDelta),
                Key = checked(value.Key + keyDelta)
            });

    public static IProjectEditCommand SetDirectMidiNoteValues(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long? startTick = null,
        long? lengthTicks = null,
        int? key = null,
        int? noteOnVelocity = null,
        int? noteOffVelocity = null) =>
        ChangeDirectMidiNotes(
            "Update Direct MIDI Note properties",
            segmentId,
            noteIds,
            value => value with
            {
                StartTick = startTick ?? value.StartTick,
                LengthTicks = lengthTicks ?? value.LengthTicks,
                Key = key ?? value.Key,
                NoteOnVelocity = noteOnVelocity ?? value.NoteOnVelocity,
                NoteOffVelocity = noteOffVelocity ?? value.NoteOffVelocity
            });

    public static IProjectEditCommand AdjustDirectMidiNoteEdges(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        ChangeDirectMidiNotes(
            "Adjust Direct MIDI Note edges",
            segmentId,
            noteIds,
            value =>
            {
                if (minimumLengthTicks < 1)
                    throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
                long oldEnd = checked(value.StartTick + value.LengthTicks);
                long effectiveMinimumLengthTicks = Math.Min(value.LengthTicks, minimumLengthTicks);
                long start = startDelta == 0
                    ? value.StartTick
                    : Math.Clamp(
                        checked(value.StartTick + startDelta),
                        0,
                        Math.Max(0, checked(oldEnd - effectiveMinimumLengthTicks)));
                long end = endDelta == 0
                    ? oldEnd
                    : Math.Max(
                        checked(start + effectiveMinimumLengthTicks),
                        checked(oldEnd + endDelta));
                return value with { StartTick = start, LengthTicks = checked(end - start) };
            });

    public static IProjectEditCommand PaintDirectMidiNoteVelocities(
        MidoraId segmentId,
        IReadOnlyDictionary<MidoraId, int> velocities) =>
        Command("Paint Direct MIDI Note velocities", project =>
        {
            if (velocities.Count > 4096 || FindMidiSegment(project, segmentId).Segment.Notes.Count > 4096)
                return ChangeBoundedDirectMidiNotes("Paint Direct MIDI Note velocities", segmentId,
                    velocities.Keys as IReadOnlyCollection<MidoraId> ?? velocities.Keys.ToArray(),
                    _ => value => value with { NoteOnVelocity = velocities[value.Id] }).Prepare(project);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, velocities.Keys.ToArray());
            int[] old = selected.Select(value => value.Note.NoteOnVelocity).ToArray();
            int[] replacement = selected.Select(value => velocities[value.Note.Id]).ToArray();
            if (replacement.Any(value => value is < 1 or > 127))
                throw new ArgumentOutOfRangeException(nameof(velocities));
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(
                        selected.Select(static value => value.Note).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        selected[index].Note.NoteOnVelocity = replacement[index];
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(
                        selected.Select(static value => value.Note).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                        selected[index].Note.NoteOnVelocity = old[index];
                });
        });

    public static IProjectEditCommand DuplicateDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int keyDelta) =>
        Command("Duplicate Direct MIDI Notes", project =>
        {
            if (noteIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.Notes.Count > 4096)
                return PrepareBoundedDirectMidiNoteAppend(project, segmentId, firstId => ReadCopies(firstId), noteIds);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            DirectNoteValue[] values = selected
                .Where(value => (long)value.Note.Key + keyDelta is >= 0 and <= 127)
                .Select(value => SnapshotDirectNote(value.Note) with
            {
                StartTick = checked(value.Note.StartTick + tickDelta),
                Key = checked(value.Note.Key + keyDelta)
            }).ToArray();
            foreach (DirectNoteValue value in values)
                ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            DirectMidiNote[]? copies = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                values.Length != 0,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    copies ??= values.Select(value => CreateDirectNote(owner, value)).ToArray();
                    location.Segment.Notes.AddRange(copies);
                },
                _ =>
                {
                    location.Segment.Notes.RemoveRange(copies ?? []);
                }),
                noteTargets: values.Select(value => new DirectMidiNoteCollisionTarget(
                    location.Segment,
                    value.StartTick,
                    value.Key)));

            IEnumerable<DirectMidiNoteValue> ReadCopies(long nextId)
            {
                var notes = BoundedDirectMidiNoteSource.Capture(FindMidiSegment(project, segmentId).Segment.Notes);
                using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default);
                using var ids = BoundedEditSort.Sort(notes.ResolveIds(noteIds, scope.Token),
                    Comparer<BoundedDirectNoteDelta>.Create(static (a, b) => a.Value.Id.CompareTo(b.Value.Id)), scope.Resources, scope.Token);
                MidoraId previous = default;
                foreach (var item in ids)
                {
                    var value = item.Value;
                    if (value.Id == previous)
                        throw new ArgumentOutOfRangeException(nameof(noteIds));
                    previous = value.Id;
                    scope.Token.ThrowIfCancellationRequested();
                    if ((long)value.Key + keyDelta is < 0 or > 127) continue;
                    yield return value with { Id = new MidoraId(nextId++), StartTick = checked(value.StartTick + tickDelta),
                        Key = checked(value.Key + keyDelta) };
                }
            }
        });

    private static long ArrangementSegmentDirectoryCount(MidoraProject project)
    {
        long count = 0;
        foreach (LogicalTrack track in project.Tracks)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            count = checked(count + track.Segments.Count);
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            count = checked(count + track.Segments.Count);
        }
        return count;
    }

    private static IProjectEditCommand PrepareArrangementDirectoryEdit(string name,
        Func<MidoraProject, IProjectEditCommand> factory) => Command(name, project =>
        ArrangementSegmentDirectoryCount(project) > 4096
            ? new SequentialProjectEditCommand(name, [factory]).Prepare(project)
            : factory(project).Prepare(project));

    private static IDisposable ReserveArrangementEditDirectory(MidoraProject project, int selectedCount)
    {
        BulkEditPreparationContext? scope = BulkEditPreparationContext.Current;
        BoundedEditResources resources = scope?.Resources ?? new();
        scope?.Token.ThrowIfCancellationRequested();
        // Selection sets, old/new windows, grouping and the largest overlap-sort
        // directory are admitted before materializing any per-Segment collection.
        return resources.ReserveWorking(checked(4096L + selectedCount * 1024L
            + ArrangementSegmentDirectoryCount(project) * 128L));
    }

    private static bool IsRequestedArrangementSegment(HashSet<MidoraId> requested, MidoraId id)
    {
        BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
        return requested.Remove(id);
    }

    private static IProjectEditCommand ChangeDirectMidiNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<DirectNoteValue, DirectNoteValue> transform) =>
        ChangeBoundedDirectMidiNotes(name, segmentId, noteIds, _ => value =>
        {
            DirectNoteValue result = transform(new(value.StartTick, value.LengthTicks, value.Key,
                value.NoteOnVelocity, value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder));
            if (result.Key is < 0 or > 127) return null;
            return new(value.Id, result.StartTick, result.LengthTicks, result.Key,
                result.NoteOnVelocity, result.NoteOffVelocity, result.NoteOnOrder, result.NoteOffOrder);
        });

    private static IProjectEditCommand ChangeDirectMidiNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<DirectNoteValue>, DirectNoteValue[]> transform) =>
        Command(name, project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectMidiNote[] selectedNotes = SelectDirectNotes(location.Segment, noteIds)
                .Select(static value => value.Note)
                .ToArray();
            DirectNoteValue[] old = selectedNotes.Select(SnapshotDirectNote).ToArray();
            DirectNoteValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
                throw new InvalidOperationException("A Direct MIDI Note transform returned the wrong result count.");
            for (int index = 0; index < replacement.Length; index++)
            {
                DirectNoteValue value = replacement[index];
                if (value.Key is < 0 or > 127)
                {
                    continue;
                }
                ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            }
            DirectMidiNote[] discardedNotes = selectedNotes
                .Where((_, index) => replacement[index].Key is < 0 or > 127)
                .ToArray();
            Action? restoreDiscarded = null;
            IPreparedProjectEdit prepared = Prepared(
                old.Where((value, index) => value != replacement[index]
                    || replacement[index].Key is < 0 or > 127).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(selectedNotes);
                    for (int index = 0; index < selectedNotes.Length; index++)
                    {
                        if (replacement[index].Key is >= 0 and <= 127)
                            ApplyDirectNote(selectedNotes[index], replacement[index]);
                    }
                    if (discardedNotes.Length != 0)
                        restoreDiscarded = location.Segment.Notes.RemoveRangeWithUndo(discardedNotes);
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.Notes.BeginBatchChange(selectedNotes);
                    for (int index = 0; index < selectedNotes.Length; index++)
                        ApplyDirectNote(selectedNotes[index], old[index]);
                    if (discardedNotes.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Direct MIDI Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                });
            DirectMidiNoteCollisionTarget[] collisionTargets = replacement
                    .Where((value, index) => value.Key is >= 0 and <= 127
                        && (value.StartTick != old[index].StartTick
                            || value.Key != old[index].Key))
                    .Select(value => new DirectMidiNoteCollisionTarget(
                        location.Segment,
                        value.StartTick,
                        value.Key))
                    .ToArray();
            return collisionTargets.Length == 0
                ? prepared
                : ResolveTargetedExactDirectMidiCollisions(
                    prepared,
                    noteTargets: collisionTargets);
        });

    public static IProjectEditCommand UpsertDirectMidiEventPoints(
        MidoraId segmentId,
        DirectMidiChannelEventKind kind,
        int laneData1,
        IReadOnlyCollection<DirectMidiEventPointEdit> points) =>
        Command("Draw Direct MIDI Event points", project =>
        {
            if (points.Count > 4096 || FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count > 4096)
                return PrepareBoundedDirectMidiEventLine(project, segmentId, kind, laneData1, points);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectMidiEventPointEdit[] edits = points.OrderBy(value => value.Tick).ToArray();
            if (edits.Length == 0 || edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
                throw new ArgumentException("Direct MIDI Event point ticks must be distinct.", nameof(points));
            HashSet<DirectMidiEventStartKey> editedKeys = edits.Select(edit => new DirectMidiEventStartKey(
                    edit.Tick,
                    kind,
                    DirectMidiEventUsesData1Selector(kind) ? laneData1 : 0))
                .ToHashSet();
            Dictionary<long, DirectMidiChannelEvent> existingByTick = [];
            foreach (DirectMidiChannelEvent value in location.Segment.ChannelEvents.QueryStartKeys(editedKeys))
                existingByTick.TryAdd(value.Tick, value);
            DirectMidiChannelEvent[] existing = edits
                .Where(edit => existingByTick.ContainsKey(edit.Tick))
                .Select(edit => existingByTick[edit.Tick])
                .ToArray();
            DirectMidiEventValue[] old = existing.Select(SnapshotDirectEvent).ToArray();
            DirectMidiChannelEvent[]? created = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(existing);
                    foreach (DirectMidiEventPointEdit edit in edits)
                    {
                        ValidateDirectMidiEvent(edit.Tick, kind, edit.Data1, edit.Data2);
                        if (existingByTick.TryGetValue(edit.Tick, out DirectMidiChannelEvent? target))
                        {
                            target.Data1 = edit.Data1;
                            target.Data2 = edit.Data2;
                        }
                    }
                    created ??= edits
                        .Where(edit => !existingByTick.ContainsKey(edit.Tick))
                        .Select(edit => new DirectMidiChannelEvent(owner)
                        {
                            Tick = edit.Tick,
                            Kind = kind,
                            Data1 = edit.Data1,
                            Data2 = edit.Data2,
                            Order = owner.NextStableId
                        })
                        .ToArray();
                    // BeginBatchChange already owns publication for the complete
                    // line gesture. AddRange opens its own collection batch, so
                    // calling it here would incorrectly attempt to nest batches.
                    // Add each prevalidated point to the active batch instead.
                    foreach (DirectMidiChannelEvent value in created)
                        location.Segment.ChannelEvents.Add(value);
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(existing);
                    location.Segment.ChannelEvents.RemoveRange(created ?? []);
                    for (int index = 0; index < existing.Length; index++) ApplyDirectEvent(existing[index], old[index]);
                }),
                eventTargets: edits.Select(value => new DirectMidiEventCollisionTarget(
                    location.Segment,
                    value.Tick,
                    kind,
                    value.Data1)));
        });

    public static IProjectEditCommand AdjustDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        long tickDelta,
        int data1Delta,
        int data2Delta,
        bool duplicate) => AdjustDirectMidiEventPointsCore(segmentId, eventIds, tickDelta,
            data1Delta, data2Delta, duplicate, scalarValueDelta: null);

    public static IProjectEditCommand AdjustDirectMidiEventPointValues(
        MidoraId segmentId, IReadOnlyCollection<MidoraId> eventIds, long tickDelta,
        int valueDelta, bool duplicate) => AdjustDirectMidiEventPointsCore(segmentId, eventIds,
            tickDelta, 0, 0, duplicate, valueDelta);

    private static IProjectEditCommand AdjustDirectMidiEventPointsCore(
        MidoraId segmentId, IReadOnlyCollection<MidoraId> eventIds, long tickDelta,
        int data1Delta, int data2Delta, bool duplicate, int? scalarValueDelta) =>
        Command(duplicate ? "Duplicate Direct MIDI Event points" : "Move Direct MIDI Event points", project =>
        {
            if (eventIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count > 4096)
            {
                if (!duplicate)
                    return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, _ => value => ShiftRecord(value));
                return PrepareBoundedDirectMidiEventAppend(project, segmentId, firstId => ReadCopies(firstId), eventIds);
            }
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            DirectMidiEventValue[] values = selected.Select(value => Shift(SnapshotDirectEvent(value.Event))).ToArray();
            foreach (DirectMidiEventValue value in values)
                ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            DirectMidiChannelEvent[]? copies = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    if (duplicate)
                    {
                        copies ??= values.Select(value => CreateDirectEvent(owner, value)).ToArray();
                        location.Segment.ChannelEvents.AddRange(copies);
                    }
                    else
                    {
                        using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                            selected.Select(static value => value.Event).ToArray());
                        for (int index = 0; index < selected.Length; index++)
                            ApplyDirectEvent(selected[index].Event, values[index]);
                    }
                },
                _ =>
                {
                    if (duplicate)
                    {
                        location.Segment.ChannelEvents.RemoveRange(copies ?? []);
                    }
                    else
                    {
                        using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                            selected.Select(static value => value.Event).ToArray());
                        for (int index = 0; index < selected.Length; index++) ApplyDirectEvent(selected[index].Event, selected[index].Original);
                    }
                }),
                eventTargets: values.Select(value => new DirectMidiEventCollisionTarget(
                    location.Segment,
                    value.Tick,
                    value.Kind,
                    value.Data1)));
            IEnumerable<DirectMidiChannelEventValue> ReadCopies(long nextId)
            {
                var source = BoundedDirectMidiEventSource.Capture(FindMidiSegment(project, segmentId).Segment.ChannelEvents);
                using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
                using var ordered = BoundedEditSort.Sort(source.ResolveIds(eventIds, scope.Token),
                    Comparer<BoundedDirectEventDelta>.Create(static (a, b) => a.Value.Id.CompareTo(b.Value.Id)), scope.Resources, scope.Token);
                MidoraId previous = default;
                foreach (var item in ordered)
                {
                    var value = item.Value;
                    if (value.Id == previous)
                        throw new ArgumentOutOfRangeException(nameof(eventIds));
                    previous = value.Id;
                    yield return ShiftRecord(value) with { Id = new(checked(nextId++)) };
                }
            }

            DirectMidiEventValue Shift(DirectMidiEventValue value)
            {
                var moved = value with { Tick = checked(value.Tick + tickDelta) };
                if (scalarValueDelta is not int delta)
                    return moved with { Data1 = checked(value.Data1 + data1Delta), Data2 = checked(value.Data2 + data2Delta) };
                int scalar = checked(DirectMidiEventPointValue(value) + delta);
                int maximum = value.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127;
                if (scalar < 0 || scalar > maximum)
                    throw new ArgumentOutOfRangeException(nameof(scalarValueDelta));
                return WithDirectMidiEventPointValue(moved, scalar);
            }

            DirectMidiChannelEventValue ShiftRecord(DirectMidiChannelEventValue value)
            {
                var moved = Shift(new(value.Tick, value.Kind, value.Data1, value.Data2, value.Order));
                return value with { Tick = moved.Tick, Data1 = moved.Data1, Data2 = moved.Data2 };
            }
        });

    public static IProjectEditCommand SetDirectMidiEventValues(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        long? tick = null,
        DirectMidiChannelEventKind? kind = null,
        int? data1 = null,
        int? data2 = null,
        int? controllerDisplayValue = null) =>
        Command("Update Direct MIDI Event properties", project =>
        {
            int EncodeControllerValue(DirectMidiChannelEventKind targetKind, int controller, int display)
            {
                if (targetKind != DirectMidiChannelEventKind.ControlChange || data2.HasValue)
                    throw new ArgumentException("A display value requires a CC target and cannot be combined with a raw value.", nameof(controllerDisplayValue));
                return MidiEditingValueDomain.ControllerRaw(controller, display);
            }
            if (eventIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count > 4096)
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, _ => value => value with
                { Tick = tick ?? value.Tick, Kind = kind ?? value.Kind, Data1 = data1 ?? value.Data1,
                    Data2 = controllerDisplayValue is int display
                        ? EncodeControllerValue(kind ?? value.Kind, data1 ?? value.Data1, display) : data2 ?? value.Data2 });
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            DirectMidiEventValue[] replacement = selected.Select(value => value.Original with
            {
                Tick = tick ?? value.Original.Tick,
                Kind = kind ?? value.Original.Kind,
                Data1 = data1 ?? value.Original.Data1,
                Data2 = controllerDisplayValue is int display
                    ? EncodeControllerValue(kind ?? value.Original.Kind, data1 ?? value.Original.Data1, display) : data2 ?? value.Original.Data2
            }).ToArray();
            foreach (DirectMidiEventValue value in replacement)
            {
                ValidateDirectMidiEvent(
                    value.Tick,
                    value.Kind,
                    value.Data1,
                    value.Data2);
            }
            return ResolveTargetedExactDirectMidiCollisions(Prepared(
                selected.Where((value, index) => value.Original != replacement[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                    {
                        ApplyDirectEvent(selected[index].Event, replacement[index]);
                    }
                },
                _ =>
                {
                    using IDisposable batch = location.Segment.ChannelEvents.BeginBatchChange(
                        selected.Select(static value => value.Event).ToArray());
                    for (int index = 0; index < selected.Length; index++)
                    {
                        ApplyDirectEvent(selected[index].Event, selected[index].Original);
                    }
                }),
                eventTargets: replacement.Select(value => new DirectMidiEventCollisionTarget(
                    location.Segment,
                    value.Tick,
                    value.Kind,
                    value.Data1)));
        });

    public static IProjectEditCommand DeleteDirectMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Delete Direct MIDI Events", project =>
        {
            if (eventIds.Count > 4096 || FindMidiSegment(project, segmentId).Segment.ChannelEvents.Count > 4096)
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, _ => _ => null,
                    BoundedEventCollisionMode.None);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            DirectMidiChannelEvent[] values = selected.Select(static value => value.Event).ToArray();
            Action? restore = null;
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                _ => restore = location.Segment.ChannelEvents.RemoveRangeWithUndo(values),
                _ =>
                {
                    (restore ?? throw new InvalidOperationException(
                        "Direct MIDI Events do not have a pending removal to restore."))();
                    restore = null;
                });
        });

    public static IProjectEditCommand AdjustOpaqueMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        long tickDelta,
        bool duplicate) =>
        Command(duplicate ? "Duplicate imported MIDI events" : "Move imported MIDI events", project =>
            PrepareBoundedOpaqueTransform(project, segmentId, eventIds, tick => checked(tick + tickDelta), duplicate));

    public static IProjectEditCommand DeleteOpaqueMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Delete imported MIDI events", project =>
            PrepareBoundedOpaqueTransform(project, segmentId, eventIds, _ => null));

    private static MidiSegmentSelection[] SelectMidiSegments(
        MidoraProject project,
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one MIDI Segment is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        return ids.Select(id =>
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("MIDI Segment IDs must be distinct.", nameof(ids));
            MidiSegmentLocation value = FindMidiSegment(project, id);
            return new MidiSegmentSelection(value.Track, value.Segment, value.Index, value.Segment.ProjectStartTick);
        }).ToArray();
    }

    private static DirectNoteSelection[] SelectDirectNotes(MidiSegment segment, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one Direct MIDI Note is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        foreach (MidoraId id in ids)
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("Direct MIDI Note IDs must be distinct.", nameof(ids));
        }
        DirectMidiNote[] values = segment.Notes.ResolveValuesByIds(distinct).ToArray();
        if (values.Length != ids.Count) throw new ArgumentOutOfRangeException(nameof(ids));
        return values.Select(static value => new DirectNoteSelection(value)).ToArray();
    }

    private static DirectEventSelection[] SelectDirectEvents(MidiSegment segment, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one Direct MIDI Event is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        foreach (MidoraId id in ids)
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("Direct MIDI Event IDs must be distinct.", nameof(ids));
        }
        DirectMidiChannelEvent[] values = segment.ChannelEvents.ResolveValuesByIds(distinct).ToArray();
        if (values.Length != ids.Count) throw new ArgumentOutOfRangeException(nameof(ids));
        return values.Select(value => new DirectEventSelection(
            value,
            SnapshotDirectEvent(value))).ToArray();
    }

    private static OpaqueEventSelection[] SelectOpaqueEvents(
        MidiSegment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one imported MIDI event is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        foreach (MidoraId id in ids)
        {
            if (id == default || !distinct.Add(id))
                throw new ArgumentException("Imported MIDI event IDs must be distinct.", nameof(ids));
        }
        OpaqueMidiEvent[] values = segment.OpaqueEvents.ResolveValuesByIds(distinct).ToArray();
        if (values.Length != ids.Count) throw new ArgumentOutOfRangeException(nameof(ids));
        return values.Select(value => new OpaqueEventSelection(
            value,
            SnapshotOpaqueEvent(value))).ToArray();
    }

    private static void ValidateMidiSegmentPlacements(
        IReadOnlyCollection<MidiSegmentBatchPlacement> placements,
        HashSet<MidiSegment> moving)
    {
        foreach (IGrouping<PureMidiTrack, MidiSegmentBatchPlacement> group in placements.GroupBy(value => value.TargetTrack))
        {
            TickRange[] ranges = group.Select(value => new TickRange(
                value.Start,
                checked(value.Start + value.Source.Segment.LengthTicks)))
                .OrderBy(value => value.StartTick)
                .ToArray();
            if (ranges.Zip(ranges.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick)
                || group.Key.Segments
                    .Where(value => !moving.Contains(value))
                    .Any(existing => ranges.Any(range => range.Intersects(existing.ProjectRange))))
            {
                throw new InvalidOperationException("The MIDI Segment edit would create an overlap.");
            }
        }
    }

    private static void ValidateMidiSegmentEdgeEdits(MidiSegmentEdgeEdit[] edits)
    {
        HashSet<MidiSegment> selected = edits.Select(value => value.Selection.Segment).ToHashSet();
        foreach (IGrouping<PureMidiTrack, MidiSegmentEdgeEdit> group in edits.GroupBy(value => value.Selection.Track))
        {
            TickRange[] ranges = group.Select(value => new TickRange(
                value.Replacement.ProjectStartTick,
                checked(value.Replacement.ProjectStartTick + value.Replacement.LengthTicks)))
                .OrderBy(value => value.StartTick).ToArray();
            if (ranges.Zip(ranges.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick)
                || group.Key.Segments.Where(value => !selected.Contains(value)).Any(existing => ranges.Any(range => range.Intersects(existing.ProjectRange))))
                throw new InvalidOperationException("The MIDI Segment resize would create an overlap.");
        }
    }

    private static MidiSegment CloneMidiSegment(MidoraProject project, MidiSegment source, long start,
        DirectContentCopyProgress? progress = null)
    {
        MidiSegment result = new(project)
        {
            ProjectStartTick = start,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };
        var notes = source.Notes.CreateObjectSource();
        var events = source.ChannelEvents.CreateObjectSource();
        var opaque = source.OpaqueEvents.CreateObjectSource();
        progress ??= new(DirectContentRecordCount(source));
        AdoptBoundedDirectMidiNotes(project, result, progress.Read(notes)
            .Select(value => value with { Id = project.AllocateStableId() }));
        long firstEventId = project.NextStableId;
        AdoptBoundedDirectMidiEvents(project, result, progress.Read(events)
            .Select(value => value with { Id = project.AllocateStableId() }));
        result.InstrumentChanges = InstrumentChangeCopies.Restore(project,
            InstrumentChangeCopies.Capture(source.InstrumentChanges, events), firstEventId, true,
            group => InstrumentChangeResolver.TryRead(result, group, out _));
        AdoptBoundedOpaqueMidiEvents(project, result, progress.Read(opaque)
            .Select(value => value with { Id = project.AllocateStableId() }));
        return result;
    }

    private static void ShiftMidiSegmentContent(MidiSegment segment, long delta)
    {
        if (delta == 0) return;
        using (segment.Notes.BeginBatchChangeAll())
        {
            foreach (DirectMidiNote note in segment.Notes)
                note.StartTick = checked(note.StartTick + delta);
        }
        using (segment.ChannelEvents.BeginBatchChangeAll())
        {
            foreach (DirectMidiChannelEvent value in segment.ChannelEvents)
                value.Tick = checked(value.Tick + delta);
        }
        using (segment.OpaqueEvents.BeginBatchChangeAll())
        {
            foreach (OpaqueMidiEvent value in segment.OpaqueEvents)
                value.Tick = checked(value.Tick + delta);
        }
    }

    private static void SetMidiSegmentWindow(MidiSegment segment, SegmentWindow value)
    {
        segment.ProjectStartTick = value.ProjectStartTick;
        segment.LengthTicks = value.LengthTicks;
        segment.ContentOffsetTick = value.ContentOffsetTick;
    }

    private static DirectNoteValue SnapshotDirectNote(DirectMidiNote value) => new(
        value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity,
        value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder);

    private static DirectMidiNote CreateDirectNote(MidoraProject project, DirectNoteValue value)
    {
        DirectMidiNote result = new(project);
        ApplyDirectNote(result, value);
        return result;
    }

    private static void ApplyDirectNote(DirectMidiNote target, DirectNoteValue value)
    {
        target.SetValues(
            value.StartTick,
            value.LengthTicks,
            value.Key,
            value.NoteOnVelocity,
            value.NoteOffVelocity,
            value.NoteOnOrder,
            value.NoteOffOrder);
    }

    private static DirectMidiEventValue SnapshotDirectEvent(DirectMidiChannelEvent value) => new(
        value.Tick, value.Kind, value.Data1, value.Data2, value.Order);
    private static DirectMidiChannelEvent CreateDirectEvent(MidoraProject project, DirectMidiEventValue value)
    {
        DirectMidiChannelEvent result = new(project);
        ApplyDirectEvent(result, value);
        return result;
    }
    private static void ApplyDirectEvent(DirectMidiChannelEvent target, DirectMidiEventValue value)
        => target.SetValues(
            value.Tick,
            value.Kind,
            value.Data1,
            value.Data2,
            value.Order);
    private static OpaqueMidiEventValue SnapshotOpaqueEvent(OpaqueMidiEvent value) => new(
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload.ToArray(),
        value.Order);
    private static OpaqueMidiEvent CreateOpaqueEvent(MidoraProject project, OpaqueMidiEventValue value) => new(project)
    {
        Tick = value.Tick,
        Kind = value.Kind,
        MetaType = value.MetaType,
        Payload = value.Payload.ToArray(),
        Order = value.Order
    };
    private static DirectMidiChannelEvent? FindDirectEventAtTick(
        MidiSegment segment,
        DirectMidiChannelEventKind kind,
        int laneData1,
        long tick) => segment.ChannelEvents.FirstOrDefault(value =>
            value.Tick == tick
            && value.Kind == kind
            && (kind is DirectMidiChannelEventKind.ControlChange
                    or DirectMidiChannelEventKind.PolyphonicKeyPressure
                    or DirectMidiChannelEventKind.NoteOn
                    or DirectMidiChannelEventKind.NoteOff
                ? value.Data1 == laneData1
                : true));

    private readonly record struct MidiSegmentSelection(
        PureMidiTrack Track,
        MidiSegment Segment,
        int Index,
        long OriginalStartTick);
    private readonly record struct MidiSegmentBatchPlacement(
        MidiSegmentSelection Source,
        PureMidiTrack TargetTrack,
        long Start);
    private readonly record struct MidiSegmentEdgeEdit(
        MidiSegmentSelection Selection,
        SegmentWindow Old,
        SegmentWindow Replacement,
        long ContentShift);
    private readonly record struct DirectNoteSelection(DirectMidiNote Note);
    private readonly record struct DirectEventSelection(
        DirectMidiChannelEvent Event,
        DirectMidiEventValue Original);
    private readonly record struct OpaqueEventSelection(
        OpaqueMidiEvent Event,
        OpaqueMidiEventValue Original);
    private readonly record struct DirectNoteValue(
        long StartTick,
        long LengthTicks,
        int Key,
        int NoteOnVelocity,
        int NoteOffVelocity,
        long NoteOnOrder,
        long NoteOffOrder);
    private readonly record struct DirectMidiEventValue(
        long Tick,
        DirectMidiChannelEventKind Kind,
        int Data1,
        int Data2,
        long Order);
    private readonly record struct OpaqueMidiEventValue(
        long Tick,
        OpaqueMidiEventKind Kind,
        byte MetaType,
        byte[] Payload,
        long Order);
}
