using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyPureMidiTrack(
        ProjectDocumentSession document,
        MidoraId trackId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        PureMidiTrack track = document.Project.PureMidiTracks.SingleOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentOutOfRangeException(nameof(trackId));
        MidiChannelRoot root = document.Project.MidiChannelRoots.SingleOrDefault(
            value => value.Id == track.MidiChannelRootId)
            ?? throw new InvalidOperationException(
                "The copied Pure MIDI Track references a missing MIDI Channel Root.");
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.PureMidiTrack,
            1,
            "1 MIDI Track",
            new PureMidiTrackClipboardData(
                SnapshotPureMidiTrack(track),
                SnapshotMidiRoute(root)));
    }

    public static ProjectObjectClipboardPayload CopyMidiSegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0) throw new ArgumentException("At least one MIDI Segment must be copied.", nameof(segmentIds));
        ClipboardCaptureScope.ReserveMetadata(segmentIds.Count, 2048);
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(segmentIds, nameof(segmentIds));
        List<(PureMidiTrack Track, MidiSegment Segment, int TrackIndex)> selected = [];
        foreach (MidoraId id in requested)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            selected.Add(FindMidiSegmentForClipboard(document.Project, id));
        }
        var primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
        if (primary.Segment is null)
            throw new ArgumentException("The primary MIDI Segment must belong to the selection.", nameof(primarySegmentId));
        long earliest = selected.Min(value => value.Segment.ProjectStartTick);
        MidiSegmentClipboardSnapshot[] snapshots = selected
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Segment.ProjectStartTick)
            .ThenBy(value => value.Segment.Id)
            .Select(value => SnapshotMidiSegment(
                value.Segment,
                checked(value.TrackIndex - primary.TrackIndex),
                checked(value.Segment.ProjectStartTick - earliest)))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MidiSegments,
            snapshots.Length,
            snapshots.Length == 1 ? "1 MIDI Segment" : $"{snapshots.Length} MIDI Segments",
            new MidiSegmentClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyDirectMidiNotes(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(noteIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(noteIds, nameof(noteIds));
        if (requested.Count == 0 || requested.Count != noteIds.Count)
            throw new ArgumentException("Direct MIDI Note selections require distinct stable IDs.", nameof(noteIds));
        long earliest = long.MaxValue;
        IReadOnlyList<DirectMidiNoteClipboardSnapshot> absolute = ClipboardCaptureScope.Capture(
            EnumerateClipboardSelection(document.Project, source.Notes.CreateObjectSource(), requested).Select(value =>
            {
                earliest = Math.Min(earliest, value.StartTick);
                return new DirectMidiNoteClipboardSnapshot(
                value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity,
                value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder, PreserveOrders: true);
            }), requested.Count, reportReadProgress: false);
        if (absolute.Count != requested.Count)
            throw new ArgumentException("Every copied Direct MIDI Note must belong to the source Segment.", nameof(noteIds));
        IReadOnlyList<DirectMidiNoteClipboardSnapshot> snapshots = new ProjectedClipboardList<DirectMidiNoteClipboardSnapshot, DirectMidiNoteClipboardSnapshot>(
            absolute, value => value with { StartOffset = checked(value.StartOffset - earliest) });
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.DirectMidiNotes,
            snapshots.Count,
            snapshots.Count == 1 ? "1 MIDI Note" : $"{snapshots.Count} MIDI Notes",
            new DirectMidiNoteClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyDirectMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(eventIds, nameof(eventIds));
        if (requested.Count == 0 || requested.Count != eventIds.Count)
            throw new ArgumentException("Direct MIDI Event selections require distinct stable IDs.", nameof(eventIds));
        long earliest = long.MaxValue;
        IReadOnlyList<DirectMidiEventClipboardSnapshot> absolute = ClipboardCaptureScope.Capture(
            EnumerateClipboardSelection(document.Project, source.ChannelEvents.CreateObjectSource(), requested).Select(value =>
            {
                earliest = Math.Min(earliest, value.Tick);
                return new DirectMidiEventClipboardSnapshot(value.Tick, value.Kind, value.Data1, value.Data2, value.Order);
            }), requested.Count, reportReadProgress: false);
        if (absolute.Count != requested.Count)
            throw new ArgumentException("Every copied Direct MIDI Event must belong to the source Segment.", nameof(eventIds));
        IReadOnlyList<DirectMidiEventClipboardSnapshot> snapshots = new ProjectedClipboardList<DirectMidiEventClipboardSnapshot, DirectMidiEventClipboardSnapshot>(
            absolute, value => value with { Tick = checked(value.Tick - earliest) });
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.DirectMidiEvents,
            snapshots.Count,
            snapshots.Count == 1 ? "1 MIDI Event" : $"{snapshots.Count} MIDI Events",
            new DirectMidiEventClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyOpaqueMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(eventIds, nameof(eventIds));
        if (requested.Count == 0 || requested.Count != eventIds.Count)
            throw new ArgumentException("Imported MIDI event selections require distinct stable IDs.", nameof(eventIds));
        OpaqueClipboardList absolute = OpaqueClipboardList.Capture(
            EnumerateClipboardSelection(document.Project, source.OpaqueEvents.CreateObjectSource(), requested), reportReadProgress: false);
        if (absolute.Count != requested.Count)
            throw new ArgumentException("Every copied imported MIDI event must belong to the source Segment.", nameof(eventIds));
        long earliest = absolute.MinimumTick;
        IReadOnlyList<OpaqueMidiEventClipboardSnapshot> snapshots = new ProjectedClipboardList<OpaqueMidiEventClipboardSnapshot, OpaqueMidiEventClipboardSnapshot>(
            absolute, value => value with { Tick = checked(value.Tick - earliest) });
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.OpaqueMidiEvents,
            snapshots.Count,
            snapshots.Count == 1 ? "1 imported MIDI Event" : $"{snapshots.Count} imported MIDI Events",
            new OpaqueMidiEventClipboardData(snapshots));
    }

    public static IProjectEditCommand CreatePastePureMidiTrackCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId rootId,
        int insertionIndex)
    {
        PureMidiTrackClipboardData data = RequirePayload<PureMidiTrackClipboardData>(
            document, payload, ProjectObjectClipboardKind.PureMidiTrack);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PastePureMidiTrackClipboard(
            data.Track,
            data.Route,
            rootId,
            insertionIndex));
    }

    public static IProjectEditCommand CreatePastePureMidiTrackIndependentCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        int insertionIndex)
    {
        PureMidiTrackClipboardData data = RequirePayload<PureMidiTrackClipboardData>(
            document,
            payload,
            ProjectObjectClipboardKind.PureMidiTrack);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PastePureMidiTrackClipboard(
            data.Track,
            data.Route,
            targetRootId: null,
            insertionIndex));
    }

    public static IProjectEditCommand CreatePasteMidiSegmentsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetTrackId,
        long editCursorTick)
    {
        MidiSegmentClipboardData data = RequirePayload<MidiSegmentClipboardData>(
            document, payload, ProjectObjectClipboardKind.MidiSegments);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteMidiSegmentClipboard(
            data.Segments, targetTrackId, editCursorTick),
            new(payload.Kind, targetTrackId, TargetIsDirectMidi: true));
    }

    public static IProjectEditCommand CreatePasteNotesCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick,
        bool targetIsDirectMidi)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(payload);
        if (!ReferenceEquals(document.ClipboardSessionIdentity, payload.SourceSessionIdentity))
            throw new InvalidOperationException("Project object clipboard payloads are only valid in their source Project session.");
        if (targetIsDirectMidi)
        {
            IReadOnlyList<DirectMidiNoteClipboardSnapshot> values = payload.Kind switch
            {
                ProjectObjectClipboardKind.DirectMidiNotes when payload.Data is DirectMidiNoteClipboardData direct => direct.Notes,
                ProjectObjectClipboardKind.LogicalNotes when payload.Data is LogicalNoteClipboardData logical =>
                    new ProjectedClipboardList<LogicalNoteClipboardSnapshot, DirectMidiNoteClipboardSnapshot>(logical.Notes, value => new DirectMidiNoteClipboardSnapshot(
                        value.StartOffset,
                        value.LengthTicks,
                        value.Note,
                        value.Velocity,
                        0,
                        0,
                        0,
                        PreserveOrders: false)),
                _ => throw new ArgumentException("The clipboard payload does not contain MIDI-compatible Notes.", nameof(payload))
            };
            return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteDirectMidiNoteClipboard(
                values, targetSegmentId, editCursorTick),
                new(payload.Kind, targetSegmentId, TargetIsDirectMidi: true), independentlyPreparedContent: true);
        }
        IReadOnlyList<LogicalNoteClipboardSnapshot> logicalValues = payload.Kind switch
        {
            ProjectObjectClipboardKind.LogicalNotes when payload.Data is LogicalNoteClipboardData logical => logical.Notes,
            ProjectObjectClipboardKind.DirectMidiNotes when payload.Data is DirectMidiNoteClipboardData direct =>
                new ProjectedClipboardList<DirectMidiNoteClipboardSnapshot, LogicalNoteClipboardSnapshot>(direct.Notes, value => new LogicalNoteClipboardSnapshot(
                    value.StartOffset, value.LengthTicks, value.Key, value.NoteOnVelocity)),
            _ => throw new ArgumentException("The clipboard payload does not contain MIDI-compatible Notes.", nameof(payload))
        };
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalNoteClipboard(
            logicalValues, targetSegmentId, editCursorTick), new(payload.Kind, targetSegmentId), independentlyPreparedContent: true);
    }

    public static IProjectEditCommand CreatePasteDirectMidiEventsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        DirectMidiEventClipboardData data = RequirePayload<DirectMidiEventClipboardData>(
            document, payload, ProjectObjectClipboardKind.DirectMidiEvents);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteDirectMidiEventClipboard(
            data.Events, targetSegmentId, editCursorTick),
            new(payload.Kind, targetSegmentId, TargetIsDirectMidi: true));
    }

    public static IProjectEditCommand CreatePasteOpaqueMidiEventsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        OpaqueMidiEventClipboardData data = RequirePayload<OpaqueMidiEventClipboardData>(
            document, payload, ProjectObjectClipboardKind.OpaqueMidiEvents);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteOpaqueMidiEventClipboard(
            data.Events, targetSegmentId, editCursorTick),
            new(payload.Kind, targetSegmentId, TargetIsDirectMidi: true));
    }

    private static PureMidiTrackClipboardSnapshot SnapshotPureMidiTrack(PureMidiTrack track)
    {
        ClipboardCaptureScope.ReserveMetadata(track.Segments.Count, 2048);
        return new(
        track.Name,
        track.Color,
        track.Segments.Select(segment => SnapshotMidiSegment(
            segment, 0, segment.ProjectStartTick)).ToArray());
    }

    private static MidiSegmentClipboardSnapshot SnapshotMidiSegment(
        MidiSegment segment,
        int trackOffset,
        long startOffset) => new(
            trackOffset,
            startOffset,
            segment.LengthTicks,
            segment.ContentOffsetTick,
            ClipboardCaptureScope.Capture(segment.Notes.CreateQuerySnapshot().QueryValues(0, long.MaxValue).Select(note => new DirectMidiNoteClipboardSnapshot(
                note.StartTick, note.LengthTicks, note.Key, note.NoteOnVelocity,
                note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder,
                PreserveOrders: true)), segment.Notes.Count),
            ClipboardCaptureScope.Capture(EnumerateClipboardSource(segment.ChannelEvents.CreateObjectSource()).Select(value => new DirectMidiEventClipboardSnapshot(
                value.Tick, value.Kind, value.Data1, value.Data2, value.Order)), segment.ChannelEvents.Count),
            OpaqueClipboardList.Capture(EnumerateClipboardSource(segment.OpaqueEvents.CreateObjectSource())))
            {
                InstrumentChanges = ClipboardCaptureScope.Capture(InstrumentChangeCopies.Capture(
                    segment.InstrumentChanges, segment.ChannelEvents.CreateObjectSource()), segment.InstrumentChanges.Count)
            };

    private static MidiRouteClipboardSnapshot SnapshotMidiRoute(MidiChannelRoot root) => new(
        root.Name,
        root.RoutingMode,
        root.FixedZeroBasedPort,
        root.FixedZeroBasedChannel,
        root.ChannelMode);

    private static (PureMidiTrack Track, MidiSegment Segment, int TrackIndex) FindMidiSegmentForClipboard(
        MidoraProject project,
        MidoraId segmentId)
    {
        (PureMidiTrack Track, MidiSegment Segment, int TrackIndex)? result = null;
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            int trackIndex = ProjectDomainEditCommands.FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                track.Id);
            foreach (MidiSegment segment in track.Segments)
            {
                if (segment.Id != segmentId) continue;
                if (result.HasValue) throw new InvalidOperationException("The MIDI Segment stable ID is duplicated.");
                result = (track, segment, trackIndex);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }
}

internal sealed record PureMidiTrackClipboardData(
    PureMidiTrackClipboardSnapshot Track,
    MidiRouteClipboardSnapshot Route) : ProjectObjectClipboardData;
internal sealed record MidiRouteClipboardSnapshot(
    string Name,
    MidiChannelRootRoutingMode RoutingMode,
    byte FixedZeroBasedPort,
    byte FixedZeroBasedChannel,
    MidiChannelMode ChannelMode);
internal sealed record PureMidiTrackClipboardSnapshot(
    string Name,
    MidoraColor? Color,
    MidiSegmentClipboardSnapshot[] Segments);
internal sealed record MidiSegmentClipboardData(MidiSegmentClipboardSnapshot[] Segments) : ProjectObjectClipboardData;
internal sealed record MidiSegmentClipboardSnapshot(
    int TrackOffset,
    long StartOffset,
    long LengthTicks,
    long ContentOffsetTick,
    IReadOnlyList<DirectMidiNoteClipboardSnapshot> Notes,
    IReadOnlyList<DirectMidiEventClipboardSnapshot> Events,
    IReadOnlyList<OpaqueMidiEventClipboardSnapshot> OpaqueEvents)
{
    public IReadOnlyList<InstrumentChangeClipboardRecord> InstrumentChanges { get; init; } = [];
}
internal sealed record DirectMidiNoteClipboardData(IReadOnlyList<DirectMidiNoteClipboardSnapshot> Notes) : ProjectObjectClipboardData;
internal sealed record DirectMidiEventClipboardData(IReadOnlyList<DirectMidiEventClipboardSnapshot> Events) : ProjectObjectClipboardData;
internal sealed record OpaqueMidiEventClipboardData(IReadOnlyList<OpaqueMidiEventClipboardSnapshot> Events) : ProjectObjectClipboardData;
internal readonly record struct DirectMidiNoteClipboardSnapshot(
    long StartOffset,
    long LengthTicks,
    int Key,
    int NoteOnVelocity,
    int NoteOffVelocity,
    long NoteOnOrder,
    long NoteOffOrder,
    bool PreserveOrders);
internal readonly record struct DirectMidiEventClipboardSnapshot(
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1,
    int Data2,
    long Order);
internal sealed record OpaqueMidiEventClipboardSnapshot(
    long Tick,
    OpaqueMidiEventKind Kind,
    byte MetaType,
    byte[] Payload,
    long Order);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PastePureMidiTrackClipboard(
        PureMidiTrackClipboardSnapshot snapshot,
        MidiRouteClipboardSnapshot route,
        MidoraId? targetRootId,
        int insertionIndex) =>
        Command("Paste Pure MIDI Track", project =>
        {
            MidiChannelRoot? targetRoot = targetRootId is MidoraId rootId
                ? FindMidiChannelRoot(project, rootId)
                : route.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    ? project.MidiChannelRoots.SingleOrDefault(value =>
                        value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                        && value.FixedZeroBasedPort == route.FixedZeroBasedPort
                        && value.FixedZeroBasedChannel == route.FixedZeroBasedChannel)
                    : null;
            if (targetRoot is not null
                && targetRoot.RoutingMode == MidiChannelRootRoutingMode.Fixed
                && targetRoot.ChannelMode != route.ChannelMode
                && targetRootId is null)
            {
                throw new InvalidOperationException(
                    "The copied Fixed route conflicts with the existing Channel Mode.");
            }
            ValidateInsertionIndex(
                insertionIndex,
                project.ArrangementTracks.Count,
                nameof(insertionIndex));
            int trackIndex = insertionIndex;
            if (targetRoot is { RoutingMode: MidiChannelRootRoutingMode.Auto })
            {
                int first = project.ArrangementTracks.FindIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == targetRoot.Id);
                int last = project.ArrangementTracks.FindLastIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == targetRoot.Id);
                if (first < 0)
                {
                    throw new InvalidOperationException(
                        "The target Auto MIDI Channel has no Arrangement member.");
                }
                if (trackIndex < first || trackIndex > last + 1)
                {
                    trackIndex = last + 1;
                }
            }
            PureMidiTrack? copy = null;
            MidiChannelRoot? createdRoot = null;
            return Prepared(
                true,
                EverythingChange(),
                owner =>
                {
                    if (targetRoot is null && createdRoot is null)
                    {
                        createdRoot = new(owner)
                        {
                            Name = ProjectTextRules.NormalizeShortText(
                                route.Name,
                                allowEmpty: false,
                                nameof(route)),
                            RoutingMode = route.RoutingMode,
                            FixedZeroBasedPort = route.FixedZeroBasedPort,
                            FixedZeroBasedChannel = route.FixedZeroBasedChannel,
                            ChannelMode = route.ChannelMode
                        };
                    }
                    MidiChannelRoot root = targetRoot ?? createdRoot!;
                    if (createdRoot is not null && !owner.MidiChannelRoots.Contains(createdRoot))
                    {
                        EnsureMidiChannelRootIdAvailable(owner, createdRoot.Id);
                        owner.MidiChannelRoots.Add(createdRoot);
                    }
                    copy ??= CreatePureMidiTrackFromClipboard(owner, snapshot, root.Id);
                    EnsurePureMidiTrackIdAvailable(owner, copy.Id);
                    copy.MidiChannelRootId = root.Id;
                    owner.PureMidiTracks.Add(copy);
                    InsertAt(
                        owner.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.PureMidiTrack,
                            copy.Id),
                        "pasted Arrangement Track reference");
                },
                owner =>
                {
                    PureMidiTrack value = copy ?? throw new InvalidOperationException(
                        "The pasted Pure MIDI Track does not exist.");
                    RemoveRequired(
                        owner.ArrangementTracks,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.PureMidiTrack,
                            value.Id),
                        "pasted Arrangement Track reference");
                    RemoveRequired(owner.PureMidiTracks, value, "pasted Pure MIDI Track");
                    if (createdRoot is not null)
                    {
                        RemoveRequired(owner.MidiChannelRoots, createdRoot, "MIDI Channel Root");
                    }
                });
        });

    internal static IProjectEditCommand PasteMidiSegmentClipboard(
        IReadOnlyList<MidiSegmentClipboardSnapshot> snapshots,
        MidoraId targetTrackId,
        long editCursorTick) =>
        Command("Paste MIDI Segments", project =>
        {
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            PureMidiTrack primary = FindPureMidiTrack(project, targetTrackId);
            int primaryIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                primary.Id);
            var placements = snapshots.Select(snapshot =>
            {
                int index = checked(primaryIndex + snapshot.TrackOffset);
                if ((uint)index >= (uint)project.ArrangementTracks.Count
                    || project.ArrangementTracks[index] is not
                    { Kind: ArrangementTrackKind.PureMidiTrack } targetReference)
                {
                    throw new InvalidOperationException(
                        "The MIDI Segment clipboard cannot preserve its relative Arrangement lane offsets.");
                }
                PureMidiTrack track = FindPureMidiTrack(project, targetReference.TrackId);
                long start = checked(editCursorTick + snapshot.StartOffset);
                ValidateSegmentRange(start, snapshot.LengthTicks, snapshot.ContentOffsetTick);
                return (Snapshot: snapshot, Track: track, Start: start);
            }).ToArray();
            foreach (IGrouping<PureMidiTrack, (MidiSegmentClipboardSnapshot Snapshot, PureMidiTrack Track, long Start)> group in placements.GroupBy(value => value.Track))
            {
                ValidateClipboardSegmentRanges(group.Select(value => new TickRange(value.Start,
                    checked(value.Start + value.Snapshot.LengthTicks))),
                    group.Key.Segments.Select(static value => value.ProjectRange));
            }
            MidiSegment[]? copies = null;
            return WithCreatedClipboardSelection(Prepared(true, EverythingChange(), owner =>
            {
                copies ??= placements.Select(value => CreateMidiSegmentFromClipboard(owner, value.Snapshot, value.Start)).ToArray();
                foreach (var group in placements.Select((value, index) => (value.Track, Copy: copies[index]))
                    .GroupBy(static value => value.Track))
                    MergeClipboardSegments(group.Key.Segments, group.Select(static value => value.Copy),
                        static value => value.ProjectStartTick, static value => value.Id);
            }, _ =>
            {
                if (copies is null) throw new InvalidOperationException("Pasted MIDI Segments do not exist.");
                for (int i = 0; i < copies.Length; i++) RemoveRequired(placements[i].Track.Segments, copies[i], "pasted MIDI Segment");
            }), () => copies!.Select(static value => value.Id).ToArray());
        });

    internal static IProjectEditCommand PasteDirectMidiNoteClipboard(
        IReadOnlyList<DirectMidiNoteClipboardSnapshot> snapshots,
        MidoraId segmentId,
        long editCursorTick) =>
        Command("Paste Direct MIDI Notes", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            return PrepareBoundedDirectMidiNoteAppend(project, segmentId, firstId => Values(firstId));
            IEnumerable<DirectMidiNoteValue> Values(long firstId)
            {
                long nextId = firstId;
                long count = 0;
                foreach (DirectMidiNoteClipboardSnapshot value in snapshots)
                {
                    BulkEditPreparationContext.Current?.Checkpoint(++count, snapshots.Count);
                    long tick = checked(editCursorTick + value.StartOffset);
                    ValidateDirectMidiNote(tick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
                    MidoraId id = new(nextId);
                    nextId = checked(nextId + 1);
                    long onOrder = value.PreserveOrders ? value.NoteOnOrder : checked(id.Value * 2);
                    long offOrder = value.PreserveOrders ? value.NoteOffOrder : checked(onOrder + 1);
                    yield return new(id, tick, value.LengthTicks, value.Key, value.NoteOnVelocity,
                        value.NoteOffVelocity, onOrder, offOrder);
                }
            }
        });

    internal static IProjectEditCommand PasteDirectMidiEventClipboard(
        IReadOnlyList<DirectMidiEventClipboardSnapshot> snapshots,
        MidoraId segmentId,
        long editCursorTick) =>
        Command("Paste Direct MIDI Events", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            return PrepareBoundedDirectMidiEventAppend(project, segmentId, firstId => Values(firstId));
            IEnumerable<DirectMidiChannelEventValue> Values(long nextId)
            {
                foreach (DirectMidiEventClipboardSnapshot value in snapshots)
                {
                    BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                    long tick = checked(editCursorTick + value.Tick);
                    ValidateDirectMidiEvent(tick, value.Kind, value.Data1, value.Data2);
                    yield return new(new MidoraId(checked(nextId++)), tick, value.Kind, value.Data1, value.Data2, value.Order);
                }
            }
        });

    internal static IProjectEditCommand PasteOpaqueMidiEventClipboard(
        IReadOnlyList<OpaqueMidiEventClipboardSnapshot> snapshots,
        MidoraId targetSegmentId,
        long editCursorTick) =>
        Command("Paste imported MIDI events", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            MidiSegmentLocation location = FindMidiSegment(project, targetSegmentId);
            if (snapshots.Count == 0) throw new ArgumentException("The imported MIDI event clipboard is empty.", nameof(snapshots));
            return PrepareBoundedOpaqueMidiAppend(project, targetSegmentId, firstId => Values(firstId));
            IEnumerable<Midora.Domain.OpaqueMidiEventValue> Values(long nextId)
            {
                foreach (OpaqueMidiEventClipboardSnapshot value in snapshots)
                {
                    BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                    long tick = checked(editCursorTick + value.Tick);
                    if (tick < 0) throw new InvalidOperationException("Imported MIDI events cannot be pasted before tick 0.");
                    if (!Enum.IsDefined(value.Kind))
                        throw new InvalidOperationException("The imported MIDI event clipboard contains an invalid event kind.");
                    yield return new(new MidoraId(checked(nextId++)), tick, value.Kind,
                        value.MetaType, value.Payload, value.Order);
                }
            }
        });

    private static PureMidiTrack CreatePureMidiTrackFromClipboard(
        MidoraProject project,
        PureMidiTrackClipboardSnapshot snapshot,
        MidoraId rootId)
    {
        PureMidiTrack track = new(project)
        {
            Name = ProjectTextRules.NormalizeShortText(snapshot.Name, true, nameof(snapshot)),
            MidiChannelRootId = rootId,
            Color = snapshot.Color
        };
        MergeClipboardSegments(track.Segments, snapshot.Segments.Select(segment =>
            CreateMidiSegmentFromClipboard(project, segment, segment.StartOffset)),
            static value => value.ProjectStartTick, static value => value.Id);
        return track;
    }

    private static MidiSegment CreateMidiSegmentFromClipboard(
        MidoraProject project,
        MidiSegmentClipboardSnapshot snapshot,
        long start)
    {
        MidiSegment segment = new(project)
        {
            ProjectStartTick = start,
            LengthTicks = snapshot.LengthTicks,
            ContentOffsetTick = snapshot.ContentOffsetTick
        };
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        AdoptBoundedDirectMidiNotes(project, segment, FirstClipboardValues(snapshot.Notes,
            static note => note.StartOffset, static note => note.Key).Select(note =>
        {
            MidoraId id = project.AllocateStableId();
            long onOrder = note.PreserveOrders ? note.NoteOnOrder : checked(id.Value * 2);
            long offOrder = note.PreserveOrders ? note.NoteOffOrder : checked(onOrder + 1);
            return new DirectMidiNoteValue(id, note.StartOffset, note.LengthTicks, note.Key,
                note.NoteOnVelocity, note.NoteOffVelocity, onOrder, offOrder);
        }));
        long firstEventId = project.NextStableId;
        AdoptBoundedDirectMidiEvents(project, segment, snapshot.Events.Select(value =>
            new DirectMidiChannelEventValue(project.AllocateStableId(), value.Tick, value.Kind, value.Data1, value.Data2, value.Order)));
        segment.InstrumentChanges = InstrumentChangeCopies.Restore(project, snapshot.InstrumentChanges, firstEventId, true,
            group => InstrumentChangeResolver.TryRead(segment, group, out _));
        AdoptBoundedOpaqueMidiEvents(project, segment, snapshot.OpaqueEvents.Select(value =>
            new Midora.Domain.OpaqueMidiEventValue(project.AllocateStableId(), value.Tick,
                value.Kind, value.MetaType, value.Payload, value.Order)));
        return segment;
    }
}
