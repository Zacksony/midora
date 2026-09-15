using Midora.Domain;

namespace Midora.Application;

public enum ProjectObjectClipboardKind
{
    EventInstrument,
    LogicalTrack,
    PureMidiTrack,
    Segments,
    MidiSegments,
    ArrangementSegments,
    LogicalNotes,
    DirectMidiNotes,
    DirectMidiEvents,
    OpaqueMidiEvents,
    LogicalParameterLane,
    LogicalParameterLaneContent,
    SubVoiceTimelineEvents,
    SubVoice,
    ValueCurveContent,
    MappingChain,
    LogicalParameterDefinition,
    LogicalParameterMapping,
    MappingStep,
    EnvelopePreset,
    MappingFunction,
    ConductorEvents,
    InstrumentChanges
}

public sealed class ProjectObjectClipboardPayload : IDisposable
{
    internal ProjectObjectClipboardPayload(
        object sourceSessionIdentity,
        ProjectObjectClipboardKind kind,
        int objectCount,
        string plainTextSummary,
        ProjectObjectClipboardData data)
    {
        SourceSessionIdentity = sourceSessionIdentity;
        Kind = kind;
        ObjectCount = objectCount;
        PlainTextSummary = plainTextSummary;
        Data = data;
        _storage = ClipboardCaptureScope.TakeStorage();
    }

    public ProjectObjectClipboardKind Kind { get; }
    public int ObjectCount { get; }
    public string PlainTextSummary { get; }
    internal object SourceSessionIdentity { get; }
    internal ProjectObjectClipboardData Data { get; }
    private ClipboardStorageOwner? _storage;
    internal ClipboardStorageOwner StorageOwner => _storage ?? throw new ObjectDisposedException(nameof(ProjectObjectClipboardPayload));
    internal IDisposable AcquireStorageLease() => StorageOwner.Acquire();
    public void Dispose()
    {
        Interlocked.Exchange(ref _storage, null)?.Release();
        GC.SuppressFinalize(this);
    }
    ~ProjectObjectClipboardPayload()
    {
        try { Interlocked.Exchange(ref _storage, null)?.Release(); }
        catch { /* Abandoned clipboard cleanup must never crash the process. */ }
    }
}

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyLogicalTrack(
        ProjectDocumentSession document,
        MidoraId logicalTrackId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        LogicalTrack track = document.Project.Tracks.SingleOrDefault(value => value.Id == logicalTrackId)
            ?? throw new ArgumentOutOfRangeException(nameof(logicalTrackId));
        EventInstrumentUsage? usage = document.Project.FindEventInstrumentUsage(track);
        ClipboardCaptureScope.ReserveMetadata(track.Segments.Count, 2048);
        LogicalTrackClipboardSnapshot snapshot = new(
            track.Name,
            usage?.EventInstrumentId,
            track.LastBoundEventInstrumentName,
            track.ColorOverride,
            track.Segments.Select(segment => SnapshotSegment(
                segment,
                trackOffset: 0,
                startOffset: segment.ProjectStartTick)).ToArray());
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalTrack,
            1,
            "1 Logical Track",
            new LogicalTrackClipboardData(snapshot));
    }

    public static ProjectObjectClipboardPayload CopySegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Segment must be copied.",
                nameof(segmentIds));
        }
        ClipboardCaptureScope.ReserveMetadata(segmentIds.Count, 2048);
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(segmentIds, nameof(segmentIds));
        List<(LogicalTrack Track, Segment Segment, int TrackIndex)> selected = [];
        foreach (MidoraId id in requested)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            selected.Add(FindSegment(document.Project, id));
        }
        var primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
        if (primary.Segment is null)
        {
            throw new ArgumentException(
                "The primary Segment must belong to the clipboard selection.",
                nameof(primarySegmentId));
        }
        long earliest = selected.Min(value => value.Segment.ProjectStartTick);
        SegmentClipboardSnapshot[] snapshots = selected
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Segment.ProjectStartTick)
            .ThenBy(value => value.Segment.Id)
            .Select(value => SnapshotSegment(
                value.Segment,
                checked(value.TrackIndex - primary.TrackIndex),
                checked(value.Segment.ProjectStartTick - earliest)))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.Segments,
            snapshots.Length,
            snapshots.Length == 1 ? "1 Segment" : $"{snapshots.Length} Segments",
            new SegmentClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyLogicalNotes(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(logicalNoteIds);
        Segment source = FindSegment(document.Project, sourceSegmentId).Segment;
        if (logicalNoteIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Logical Note must be copied.",
                nameof(logicalNoteIds));
        }
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(logicalNoteIds, nameof(logicalNoteIds));
        LogicalNoteQuerySnapshot snapshot = source.Notes.CreateQuerySnapshot();
        long earliest = long.MaxValue;
        IReadOnlyList<LogicalNoteClipboardSnapshot> absolute = ClipboardCaptureScope.Capture(
            EnumerateClipboardSelection(document.Project, snapshot, requested)
                .Select(value =>
                {
                    earliest = Math.Min(earliest, value.StartTick);
                    return new LogicalNoteClipboardSnapshot(value.StartTick, value.LengthTicks, value.Note, value.Velocity);
                }), requested.Count, reportReadProgress: false);
        if (absolute.Count != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Logical Note must belong to the source Segment.",
                nameof(logicalNoteIds));
        }
        IReadOnlyList<LogicalNoteClipboardSnapshot> snapshots = new ProjectedClipboardList<LogicalNoteClipboardSnapshot, LogicalNoteClipboardSnapshot>(
            absolute, value => value with { StartOffset = checked(value.StartOffset - earliest) });
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalNotes,
            snapshots.Count,
            snapshots.Count == 1 ? "1 Logical Note" : $"{snapshots.Count} Logical Notes",
            new LogicalNoteClipboardData(snapshots));
    }

    public static IProjectEditCommand CreatePasteSegmentsCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId activeTargetTrackId,
        long editCursorTick)
    {
        SegmentClipboardData data = RequirePayload<SegmentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.Segments);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteSegmentClipboard(
            data.Segments,
            activeTargetTrackId,
            editCursorTick), new(payload.Kind, activeTargetTrackId));
    }

    public static IProjectEditCommand CreatePasteLogicalTrackCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        int insertionIndex)
    {
        LogicalTrackClipboardData data = RequirePayload<LogicalTrackClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.LogicalTrack);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalTrackClipboard(
            data.Track,
            targetEventInstrumentId,
            targetUsageId: null,
            insertionIndex));
    }

    public static IProjectEditCommand CreatePasteLogicalTrackIntoUsageCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetUsageId,
        int insertionIndex)
    {
        LogicalTrackClipboardData data = RequirePayload<LogicalTrackClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.LogicalTrack);
        EventInstrumentUsage usage = targetDocument.Project.EventInstrumentUsages
            .SingleOrDefault(value => value.Id == targetUsageId)
            ?? throw new ArgumentOutOfRangeException(nameof(targetUsageId));
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalTrackClipboard(
            data.Track,
            usage.EventInstrumentId,
            usage.Id,
            insertionIndex));
    }

    public static IProjectEditCommand CreatePasteLogicalTrackIndependentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        int insertionIndex)
    {
        LogicalTrackClipboardData data = RequirePayload<LogicalTrackClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.LogicalTrack);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalTrackClipboard(
            data.Track,
            data.Track.EventInstrumentId,
            targetUsageId: null,
            insertionIndex));
    }

    public static IProjectEditCommand CreatePasteLogicalNotesCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        LogicalNoteClipboardData data = RequirePayload<LogicalNoteClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.LogicalNotes);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalNoteClipboard(
            data.Notes,
            targetSegmentId,
            editCursorTick), new(payload.Kind, targetSegmentId), independentlyPreparedContent: true);
    }

    private static T RequirePayload<T>(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        ProjectObjectClipboardKind expectedKind)
        where T : ProjectObjectClipboardData
    {
        ArgumentNullException.ThrowIfNull(targetDocument);
        ArgumentNullException.ThrowIfNull(payload);
        if (!ReferenceEquals(
            targetDocument.ClipboardSessionIdentity,
            payload.SourceSessionIdentity))
        {
            throw new InvalidOperationException(
                "Project object clipboard payloads are only valid in their source Project session.");
        }
        if (payload.Kind != expectedKind || payload.Data is not T data)
        {
            throw new ArgumentException(
                $"The clipboard payload does not contain {expectedKind}.",
                nameof(payload));
        }
        return data;
    }

    private static (
        LogicalTrack Track,
        Segment Segment,
        int TrackIndex) FindSegment(MidoraProject project, MidoraId segmentId)
    {
        (LogicalTrack Track, Segment Segment, int TrackIndex)? result = null;
        foreach (LogicalTrack track in project.Tracks)
        {
            int trackIndex = ProjectDomainEditCommands.FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.LogicalTrack,
                track.Id);
            foreach (Segment segment in track.Segments)
            {
                if (segment.Id != segmentId)
                {
                    continue;
                }
                if (result.HasValue)
                {
                    throw new InvalidOperationException("The Segment stable ID is duplicated.");
                }
                result = (track, segment, trackIndex);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }

    private static SegmentClipboardSnapshot SnapshotSegment(
        Segment segment,
        int trackOffset,
        long startOffset)
    {
        ClipboardCaptureScope.ReserveMetadata(segment.ParameterLanes.Count, 1024);
        return new(
            trackOffset,
            startOffset,
            segment.LengthTicks,
            segment.ContentOffsetTick,
            ClipboardCaptureScope.Capture(segment.Notes.CreateQuerySnapshot().EnumerateAll().Select(value => new LogicalNoteClipboardSnapshot(
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity)), segment.Notes.Count),
            segment.ParameterLanes.Select(value => new LogicalParameterLaneClipboardSnapshot(
                value.ParameterId,
                ClipboardCaptureScope.Capture(value.Points.CreateQuerySnapshot().EnumerateAll().Select(point => new CurvePointClipboardSnapshot(
                    point.Tick,
                    point.Value,
                    CurveInterpolation.Step)), value.Points.Count))).ToArray());
    }
}

internal abstract record ProjectObjectClipboardData;
internal sealed record LogicalTrackClipboardData(
    LogicalTrackClipboardSnapshot Track) : ProjectObjectClipboardData;
internal sealed record LogicalTrackClipboardSnapshot(
    string Name,
    MidoraId? EventInstrumentId,
    string? LastBoundEventInstrumentName,
    MidoraColor? ColorOverride,
    SegmentClipboardSnapshot[] Segments);
internal sealed record SegmentClipboardData(
    SegmentClipboardSnapshot[] Segments) : ProjectObjectClipboardData;
internal sealed record LogicalNoteClipboardData(
    IReadOnlyList<LogicalNoteClipboardSnapshot> Notes) : ProjectObjectClipboardData;
internal sealed record SegmentClipboardSnapshot(
    int TrackOffset,
    long StartOffset,
    long LengthTicks,
    long ContentOffsetTick,
    IReadOnlyList<LogicalNoteClipboardSnapshot> Notes,
    LogicalParameterLaneClipboardSnapshot[] ParameterLanes);
internal readonly record struct LogicalNoteClipboardSnapshot(
    long StartOffset,
    long LengthTicks,
    int Note,
    int Velocity);
internal sealed record LogicalParameterLaneClipboardSnapshot(
    MidoraId ParameterId,
    IReadOnlyList<CurvePointClipboardSnapshot> Points);
internal readonly record struct CurvePointClipboardSnapshot(
    long Tick,
    double Value,
    CurveInterpolation Interpolation);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteLogicalTrackClipboard(
        LogicalTrackClipboardSnapshot snapshot,
        MidoraId? targetEventInstrumentId,
        MidoraId? targetUsageId,
        int insertionIndex) =>
        Command("Paste logical track", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            EventInstrument? target = targetEventInstrumentId is MidoraId definitionId
                ? FindEventInstrument(project, definitionId)
                : null;
            EventInstrumentUsage? sharedUsage = targetUsageId is MidoraId usageId
                ? project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                    ?? throw new ArgumentOutOfRangeException(nameof(targetUsageId))
                : null;
            if (sharedUsage is not null
                && (target is null || sharedUsage.EventInstrumentId != target.Id))
            {
                throw new InvalidOperationException(
                    "The target Event Instrument Usage does not use the selected Definition.");
            }
            if (target is null && snapshot.Segments.Length != 0)
            {
                throw new InvalidOperationException(
                    "A pasted Logical Track with content requires an Event Instrument Definition.");
            }
            ValidateInsertionIndex(insertionIndex, project.ArrangementTracks.Count, nameof(insertionIndex));
            int trackIndex = insertionIndex;
            if (sharedUsage is not null)
            {
                int first = project.ArrangementTracks.FindIndex(reference =>
                    reference.Kind == ArrangementTrackKind.LogicalTrack
                    && FindTrack(project, reference.TrackId).EventInstrumentUsageId == sharedUsage.Id);
                int last = project.ArrangementTracks.FindLastIndex(reference =>
                    reference.Kind == ArrangementTrackKind.LogicalTrack
                    && FindTrack(project, reference.TrackId).EventInstrumentUsageId == sharedUsage.Id);
                if (first < 0)
                {
                    throw new InvalidOperationException(
                        "The target Event Instrument Usage has no Arrangement member.");
                }
                if (trackIndex < first || trackIndex > last + 1)
                {
                    trackIndex = last + 1;
                }
            }
            LogicalTrack? copy = null;
            EventInstrumentUsage? createdUsage = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                owner =>
                {
                    if (copy is null)
                    {
                        if (target is not null && sharedUsage is null)
                        {
                            createdUsage = new(owner) { EventInstrumentId = target.Id };
                        }
                        copy = new LogicalTrack(owner)
                        {
                            Name = ProjectTextRules.NormalizeShortText(
                                snapshot.Name,
                                allowEmpty: true,
                                nameof(snapshot)),
                            EventInstrumentUsageId = sharedUsage?.Id ?? createdUsage?.Id,
                            LastBoundEventInstrumentName = target?.Name
                                ?? snapshot.LastBoundEventInstrumentName,
                            ColorOverride = snapshot.ColorOverride
                        };
                        MergeClipboardSegments(copy.Segments, snapshot.Segments.Select(segment =>
                            CreateSegmentFromClipboard(owner, segment, segment.StartOffset)),
                            static value => value.ProjectStartTick, static value => value.Id);
                    }
                    else
                    {
                        if (createdUsage is not null)
                        {
                            EnsureEventInstrumentUsageIdAvailable(owner, createdUsage.Id);
                        }
                        EnsureLogicalTrackIdAvailable(owner, copy.Id);
                    }
                    if (createdUsage is not null)
                    {
                        owner.EventInstrumentUsages.Add(createdUsage);
                    }
                    owner.Tracks.Add(copy);
                    InsertAt(
                        owner.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(ArrangementTrackKind.LogicalTrack, copy.Id),
                        "pasted Arrangement Track reference");
                },
                owner =>
                {
                    LogicalTrack value = copy ?? throw new InvalidOperationException(
                        "The pasted Logical Track does not exist before Undo.");
                    RemoveRequired(
                        owner.ArrangementTracks,
                        new ArrangementTrackReference(ArrangementTrackKind.LogicalTrack, value.Id),
                        "pasted Arrangement Track reference");
                    RemoveRequired(owner.Tracks, value, "pasted Logical Track");
                    if (createdUsage is not null)
                    {
                        RemoveRequired(
                            owner.EventInstrumentUsages,
                            createdUsage,
                            "Event Instrument Usage");
                    }
                });
        });

    internal static IProjectEditCommand PasteSegmentClipboard(
        IReadOnlyList<SegmentClipboardSnapshot> snapshots,
        MidoraId activeTargetTrackId,
        long editCursorTick) =>
        Command("Paste segments", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            if (snapshots.Count == 0 || editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    snapshots.Count == 0 ? nameof(snapshots) : nameof(editCursorTick));
            }
            LogicalTrack primaryTrack = FindTrack(project, activeTargetTrackId);
            EnsureLogicalTrackCanContainContent(project, primaryTrack);
            int primaryTrackIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.LogicalTrack,
                primaryTrack.Id);
            SegmentClipboardPlacement[] placements = snapshots.Select(snapshot =>
            {
                int targetIndex = checked(primaryTrackIndex + snapshot.TrackOffset);
                if ((uint)targetIndex >= (uint)project.ArrangementTracks.Count
                    || project.ArrangementTracks[targetIndex] is not
                    { Kind: ArrangementTrackKind.LogicalTrack } targetReference)
                {
                    throw new InvalidOperationException(
                        "The Segment clipboard payload cannot preserve its relative Arrangement lane offsets at the target.");
                }
                LogicalTrack targetTrack = FindTrack(project, targetReference.TrackId);
                EnsureLogicalTrackCanContainContent(project, targetTrack);
                long start = checked(editCursorTick + snapshot.StartOffset);
                ValidateSegmentRange(start, snapshot.LengthTicks, snapshot.ContentOffsetTick);
                return new SegmentClipboardPlacement(
                    snapshot,
                    targetTrack,
                    start);
            }).ToArray();
            ValidateClipboardSegmentPlacements(placements);
            Segment[]? copies = null;
            return WithCreatedClipboardSelection(Prepared(
                hasChanges: true,
                TrackChange(placements.Select(value => value.TargetTrack.Id).Distinct().ToArray()),
                owner =>
                {
                    if (copies is null)
                    {
                        Segment[] created = placements
                            .Select(value => CreateSegmentFromClipboard(
                                owner,
                                value.Snapshot,
                                value.ProjectStartTick))
                            .ToArray();
                        copies = created;
                    }
                    foreach (var group in placements.Select((value, index) => (value.TargetTrack, Copy: copies[index]))
                        .GroupBy(static value => value.TargetTrack))
                        MergeClipboardSegments(group.Key.Segments, group.Select(static value => value.Copy),
                            static value => value.ProjectStartTick, static value => value.Id);
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Segment clipboard copies do not exist before the first Apply.");
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        RemoveRequired(
                            placements[index].TargetTrack.Segments,
                            copies[index],
                            "pasted Segment");
                    }
                }), () => copies!.Select(static value => value.Id).ToArray());
        });

    internal static IProjectEditCommand PasteLogicalNoteClipboard(
        IReadOnlyList<LogicalNoteClipboardSnapshot> snapshots,
        MidoraId targetSegmentId,
        long editCursorTick) =>
        Command("Paste logical notes", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            if (snapshots.Count == 0 || editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    snapshots.Count == 0 ? nameof(snapshots) : nameof(editCursorTick));
            }
            SegmentLocation target = FindSegment(project, targetSegmentId);
            return PrepareBoundedLogicalClipboardAppend(project, target, snapshots, editCursorTick);
        });

    private static Segment CreateSegmentFromClipboard(
        MidoraProject project,
        SegmentClipboardSnapshot snapshot,
        long projectStartTick)
    {
        Segment result = new(project)
        {
            ProjectStartTick = projectStartTick,
            LengthTicks = snapshot.LengthTicks,
            ContentOffsetTick = snapshot.ContentOffsetTick
        };
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var notes = FreezeClipboardSource(FirstClipboardValues(snapshot.Notes,
            static value => value.StartOffset, static value => value.Note).Select(value =>
        {
            ValidateLogicalNote(value.StartOffset, value.LengthTicks, value.Note, value.Velocity);
            return new LogicalNoteSnapshotValue(project.AllocateStableId(), value.StartOffset,
                value.LengthTicks, value.Note, value.Velocity);
        }), static value => value.Id);
        result.Notes.AdoptSource(project, notes, scope.Token);
        foreach (LogicalParameterLaneClipboardSnapshot value in snapshot.ParameterLanes)
        {
            scope.Token.ThrowIfCancellationRequested();
            LogicalParameterLane lane = new(project) { ParameterId = value.ParameterId };
            var points = FreezeClipboardSource(FirstClipboardValues(value.Points,
                static point => point.Tick, static _ => 0).Select(point =>
                    new CurvePointSnapshotValue(project.AllocateStableId(), point.Tick, point.Value, CurveInterpolation.Step)),
                static point => point.Id);
            lane.Points.AdoptSource(project, points, scope.Token);
            result.ParameterLanes.Add(lane);
        }
        return result;
    }

    private static void ValidateClipboardSegmentPlacements(
        SegmentClipboardPlacement[] placements)
    {
        foreach (IGrouping<LogicalTrack, SegmentClipboardPlacement> group in placements
            .GroupBy(value => value.TargetTrack))
        {
            ValidateClipboardSegmentRanges(group.Select(value => new TickRange(value.ProjectStartTick,
                checked(value.ProjectStartTick + value.Snapshot.LengthTicks))),
                group.Key.Segments.Select(static value => value.ProjectRange));
        }
    }

    private readonly record struct SegmentClipboardPlacement(
        SegmentClipboardSnapshot Snapshot,
        LogicalTrack TargetTrack,
        long ProjectStartTick);
}
