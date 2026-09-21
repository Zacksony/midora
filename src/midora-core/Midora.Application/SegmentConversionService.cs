using Midora.Domain;

namespace Midora.Application;

/// <summary>Losses across the complete content, including the cropped-out region.</summary>
public readonly record struct SegmentConversionLossSummary(
    long LogicalParameterPoints = 0,
    long MidiChannelEvents = 0,
    long OpaqueEvents = 0,
    long NonZeroNoteOffVelocities = 0,
    long DuplicateNotes = 0,
    long LogicalParameterLanes = 0)
{
    public bool HasLoss => LogicalParameterPoints != 0 || MidiChannelEvents != 0
        || OpaqueEvents != 0 || NonZeroNoteOffVelocities != 0 || DuplicateNotes != 0 || LogicalParameterLanes != 0;
}

/// <summary>
/// A frozen, reviewed clipboard/transfer proposal. Taking the command transfers
/// its page leases; abandoning a proposal never edits the Project.
/// </summary>
public sealed class SegmentConversionPlan : IDisposable
{
    private IProjectEditCommand? _command;
    internal SegmentConversionPlan(IProjectEditCommand command, SegmentConversionLossSummary losses)
    { _command = command; Losses = losses; }
    public SegmentConversionLossSummary Losses { get; }
    public bool RequiresConfirmation => Losses.HasLoss;
    public IProjectEditCommand CreateCommand(bool confirmDataLoss = false)
    {
        if (RequiresConfirmation && !confirmDataLoss)
            throw new InvalidOperationException("The Segment conversion requires explicit data-loss confirmation.");
        return Interlocked.Exchange(ref _command, null)
            ?? throw new ObjectDisposedException(nameof(SegmentConversionPlan));
    }
    public void Dispose() => (Interlocked.Exchange(ref _command, null) as IDisposable)?.Dispose();
}

public static class SegmentConversionService
{
    /// <summary>Run on the cancellable preparation worker, not on the UI thread.</summary>
    public static SegmentConversionPlan AnalyzePaste(ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload, MidoraId targetTrackId, long editCursorTick,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null) =>
        ProjectObjectClipboard.AnalyzeArrangementSegmentPaste(document, payload, targetTrackId,
            editCursorTick, [], cancellationToken, progress);

    /// <summary>The destination tick is the primary Segment's new Project start.</summary>
    public static SegmentConversionPlan AnalyzeTransfer(ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds, MidoraId primarySegmentId,
        MidoraId targetTrackId, long newPrimaryStartTick, bool copy,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
    {
        using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: document.Project);
        using ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyArrangementSegments(
            document, segmentIds, primarySegmentId);
        var data = (ArrangementSegmentClipboardData)payload.Data;
        ArrangementSegmentClipboardSnapshot primary = data.Segments.Single(value => value.SourceId == primarySegmentId);
        long targetEarliestTick = checked(newPrimaryStartTick - primary.StartOffset);
        return ProjectObjectClipboard.AnalyzeArrangementSegmentPaste(document, payload, targetTrackId,
            targetEarliestTick, copy ? [] : segmentIds.ToArray(), cancellationToken, progress);
    }
}

internal sealed record ArrangementSegmentClipboardData(ArrangementSegmentClipboardSnapshot[] Segments)
    : ProjectObjectClipboardData;
internal sealed record ArrangementSegmentClipboardSnapshot(
    MidoraId SourceId, SegmentClipboardSnapshot? Logical, MidiSegmentClipboardSnapshot? Direct)
{
    public int TrackOffset => Logical?.TrackOffset ?? Direct!.TrackOffset;
    public long StartOffset => Logical?.StartOffset ?? Direct!.StartOffset;
    public long LengthTicks => Logical?.LengthTicks ?? Direct!.LengthTicks;
    public long ContentOffsetTick => Logical?.ContentOffsetTick ?? Direct!.ContentOffsetTick;
}
internal sealed record SegmentConversionPlacement(
    ArrangementSegmentClipboardSnapshot Source, ArrangementTrackReference Target, long StartTick);

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyArrangementSegments(ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds, MidoraId primarySegmentId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0) throw new ArgumentException("At least one Segment must be copied.", nameof(segmentIds));
        ClipboardCaptureScope.ReserveMetadata(segmentIds.Count, 2048);
        IReadOnlySet<MidoraId> ids = ValidateDistinctIds(segmentIds, nameof(segmentIds));
        var rows = new List<(MidoraId Id, int Track, long Start, Segment? Logical, MidiSegment? Direct)>();
        for (int index = 0; index < document.Project.ArrangementTracks.Count; index++)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            ArrangementTrackReference reference = document.Project.ArrangementTracks[index];
            if (reference.Kind == ArrangementTrackKind.LogicalTrack)
                foreach (Segment segment in document.Project.Tracks.Single(value => value.Id == reference.TrackId).Segments)
                    if (ids.Contains(segment.Id)) rows.Add((segment.Id, index, segment.ProjectStartTick, segment, null));
            if (reference.Kind == ArrangementTrackKind.PureMidiTrack)
                foreach (MidiSegment segment in document.Project.PureMidiTracks.Single(value => value.Id == reference.TrackId).Segments)
                    if (ids.Contains(segment.Id)) rows.Add((segment.Id, index, segment.ProjectStartTick, null, segment));
        }
        if (rows.Count != ids.Count || !ids.Contains(primarySegmentId))
            throw new ArgumentException("Every selected Segment, including the primary, must exist exactly once.");
        int primaryTrack = rows.Single(value => value.Id == primarySegmentId).Track;
        long earliest = rows.Min(static value => value.Start);
        ArrangementSegmentClipboardSnapshot[] snapshots = rows.OrderBy(static value => value.Track)
            .ThenBy(static value => value.Start).ThenBy(static value => value.Id)
            .Select(value => new ArrangementSegmentClipboardSnapshot(value.Id,
                value.Logical is null ? null : SnapshotSegment(value.Logical, value.Track - primaryTrack, value.Start - earliest),
                value.Direct is null ? null : SnapshotMidiSegment(value.Direct, value.Track - primaryTrack, value.Start - earliest)))
            .ToArray();
        return new(document.ClipboardSessionIdentity, ProjectObjectClipboardKind.ArrangementSegments,
            snapshots.Length, $"{snapshots.Length} Segment(s)", new ArrangementSegmentClipboardData(snapshots));
    }

    public static ProjectObjectClipboardCutPreparation PrepareCutArrangementSegments(ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds, MidoraId primarySegmentId)
    {
        ProjectObjectClipboardPayload payload = CopyArrangementSegments(document, segmentIds, primarySegmentId);
        var data = (ArrangementSegmentClipboardData)payload.Data;
        List<Func<MidoraProject, IProjectEditCommand>> edits = [];
        MidoraId[] logical = data.Segments.Where(static row => row.Logical is not null).Select(static row => row.SourceId).ToArray();
        MidoraId[] direct = data.Segments.Where(static row => row.Direct is not null).Select(static row => row.SourceId).ToArray();
        if (logical.Length != 0) edits.Add(_ => ProjectDomainEditCommands.DeleteSegments(logical));
        if (direct.Length != 0) edits.Add(_ => ProjectDomainEditCommands.DeleteMidiSegments(direct));
        return PrepareCut(document, payload, new SequentialProjectEditCommand("Cut Segments", edits));
    }

    internal static SegmentConversionPlan AnalyzeArrangementSegmentPaste(ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload, MidoraId targetTrackId, long tick, MidoraId[] moveIds,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(payload);
        using IDisposable lease = payload.AcquireStorageLease();
        if (!ReferenceEquals(payload.SourceSessionIdentity, document.ClipboardSessionIdentity))
            throw new InvalidOperationException("The clipboard belongs to another Project session.");
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: document.Project);
        long revision = document.Compilation.SourceRevision;
        ArrangementSegmentClipboardSnapshot[] snapshots = payload.Data switch
        {
            ArrangementSegmentClipboardData mixed => mixed.Segments,
            SegmentClipboardData logical => logical.Segments.Select(static value => new ArrangementSegmentClipboardSnapshot(default, value, null)).ToArray(),
            MidiSegmentClipboardData direct => direct.Segments.Select(static value => new ArrangementSegmentClipboardSnapshot(default, null, value)).ToArray(),
            _ => throw new ArgumentException("The clipboard does not contain Segments.", nameof(payload))
        };
        using IDisposable metadata = scope.Resources.ReserveWorking(checked(snapshots.Length * 2048L));
        SegmentConversionPlacement[] placements = ResolveConversionPlacements(document.Project, snapshots, targetTrackId, tick);
        ProjectDomainEditCommands.ValidateSegmentConversionPlacements(document.Project, placements, moveIds);
        long parameters = 0, channelEvents = 0, opaque = 0, off = 0, duplicates = 0, lanes = 0;
        for (int index = 0; index < placements.Length; index++)
        {
            scope.Checkpoint(index, placements.Length, TimelineEditPreparationPhase.ReadingSelection);
            var placement = placements[index];
            if (placement.Source.Logical is { } logical && placement.Target.Kind == ArrangementTrackKind.PureMidiTrack)
            {
                lanes = checked(lanes + logical.ParameterLanes.Length);
                parameters = checked(parameters + logical.ParameterLanes.Sum(static lane => (long)lane.Points.Count));
                duplicates = checked(duplicates + CountConversionDuplicates(logical.Notes.Select(static note =>
                    new ConversionNoteKey(note.StartOffset, note.Note))));
            }
            else if (placement.Source.Direct is { } direct && placement.Target.Kind == ArrangementTrackKind.LogicalTrack)
            {
                channelEvents = checked(channelEvents + direct.Events.Count);
                opaque = checked(opaque + direct.OpaqueEvents.Count);
                foreach (var note in direct.Notes)
                { token.ThrowIfCancellationRequested(); if (note.NoteOffVelocity != 0) off++; }
                duplicates = checked(duplicates + CountConversionDuplicates(direct.Notes.Select(static note =>
                    new ConversionNoteKey(note.StartOffset, note.Key))));
            }
        }
        token.ThrowIfCancellationRequested();
        if (document.Compilation.SourceRevision != revision)
            throw new InvalidOperationException("The Project changed while the Segment conversion was reviewed.");
        IProjectEditCommand inner = ProjectDomainEditCommands.ConvertArrangementSegments(
            placements, moveIds, () =>
            {
                if (document.Compilation.SourceRevision != revision)
                    throw new InvalidOperationException("The Project changed after the Segment conversion was reviewed. Review it again.");
            });
        return new(KeepClipboardAlive(payload, inner, new(ProjectObjectClipboardKind.ArrangementSegments, targetTrackId)),
            new(parameters, channelEvents, opaque, off, duplicates, lanes));
    }

    private readonly record struct ConversionNoteKey(long Tick, int Key);
    private static long CountConversionDuplicates(IEnumerable<ConversionNoteKey> values)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        using var sorted = BoundedEditSort.Sort(values, Comparer<ConversionNoteKey>.Create(static (a, b) =>
        { int c = a.Tick.CompareTo(b.Tick); return c == 0 ? a.Key.CompareTo(b.Key) : c; }), scope.Resources, scope.Token);
        long count = 0; bool any = false; ConversionNoteKey previous = default;
        foreach (var value in sorted.ReadValues(scope.Token))
        { if (any && value == previous) count++; previous = value; any = true; }
        return count;
    }

    private static SegmentConversionPlacement[] ResolveConversionPlacements(MidoraProject project,
        ArrangementSegmentClipboardSnapshot[] snapshots, MidoraId targetTrackId, long tick)
    {
        if (snapshots.Length == 0 || tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        int targetIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == targetTrackId);
        if (targetIndex < 0) throw new ArgumentOutOfRangeException(nameof(targetTrackId));
        return snapshots.Select(source =>
        {
            int index = checked(targetIndex + source.TrackOffset);
            if ((uint)index >= (uint)project.ArrangementTracks.Count)
                throw new InvalidOperationException("The Segment selection cannot preserve its relative Track offsets at the target.");
            return new SegmentConversionPlacement(source, project.ArrangementTracks[index], checked(tick + source.StartOffset));
        }).ToArray();
    }
}

public static partial class ProjectDomainEditCommands
{
    internal static void ValidateSegmentConversionPlacements(MidoraProject project,
        SegmentConversionPlacement[] placements, IReadOnlyCollection<MidoraId> moveIds)
    {
        var removing = new HashSet<MidoraId>(moveIds);
        foreach (var group in placements.GroupBy(static value => value.Target))
        {
            foreach (var row in group) ValidateSegmentRange(row.StartTick, row.Source.LengthTicks, row.Source.ContentOffsetTick);
            IEnumerable<TickRange> existing;
            if (group.Key.Kind == ArrangementTrackKind.LogicalTrack)
            {
                LogicalTrack track = FindTrack(project, group.Key.TrackId);
                EnsureLogicalTrackCanContainContent(project, track);
                existing = track.Segments.Where(value => !removing.Contains(value.Id)).Select(static value => value.ProjectRange);
            }
            else if (group.Key.Kind == ArrangementTrackKind.PureMidiTrack)
                existing = FindPureMidiTrack(project, group.Key.TrackId).Segments
                    .Where(value => !removing.Contains(value.Id)).Select(static value => value.ProjectRange);
            else throw new InvalidOperationException("The target cannot contain Segments.");
            ValidateClipboardSegmentRanges(group.Select(static value =>
                new TickRange(value.StartTick, checked(value.StartTick + value.Source.LengthTicks))), existing);
        }
    }

    internal static IProjectEditCommand ConvertArrangementSegments(SegmentConversionPlacement[] placements,
        MidoraId[] moveIds, Action validateReview) => Command("Transfer Segments", project =>
    {
        validateReview();
        ValidateSegmentConversionPlacements(project, placements, moveIds);
        ConversionOriginalDirectory originalDirectory = ConversionOriginalDirectory.Create(project, moveIds);
        try
        {
            ConversionOriginal[] originals = originalDirectory.Originals;
            var changes = new ProjectChangeSet();
            foreach (var placement in placements)
                if (placement.Target.Kind == ArrangementTrackKind.LogicalTrack) changes.TrackIds.Add(placement.Target.TrackId);
                else changes.PureMidiTrackIds.Add(placement.Target.TrackId);
            foreach (var original in originals)
                if (original.Logical is not null) changes.TrackIds.Add(original.TrackId);
                else changes.PureMidiTrackIds.Add(original.TrackId);
            object[]? created = null;
            IPreparedProjectEdit edit = Prepared(true, changes, owner =>
            {
                // All targets are built and validated on the private draft before
                // removing even the first source Segment.
                created ??= placements.Select(row => BuildConversionTarget(owner, row,
                    originalDirectory.ById.GetValueOrDefault(row.Source.SourceId))).ToArray();
                int removed = 0;
                if (originals.Length != 0)
                {
                    CancellationToken token = BulkEditPreparationContext.Current!.Token;
                    long visited = 0;
                    bool IsSource(MidoraId id)
                    {
                        if ((visited++ & 255) == 0) token.ThrowIfCancellationRequested();
                        return originalDirectory.ById.ContainsKey(id);
                    }
                    foreach (MidoraId id in changes.TrackIds)
                        removed = checked(removed + FindTrack(owner, id).Segments.RemoveAll(segment => IsSource(segment.Id)));
                    foreach (MidoraId id in changes.PureMidiTrackIds)
                        removed = checked(removed + FindPureMidiTrack(owner, id).Segments.RemoveAll(segment => IsSource(segment.Id)));
                    if (removed != originals.Length)
                        throw new InvalidOperationException("The source Segment selection changed during conversion preparation.");
                }
                for (int index = 0; index < placements.Length; index++)
                    if (created[index] is Segment logical) FindTrack(owner, placements[index].Target.TrackId).Segments.Add(logical);
                    else FindPureMidiTrack(owner, placements[index].Target.TrackId).Segments.Add((MidiSegment)created[index]);
                foreach (MidoraId id in changes.TrackIds) FindTrack(owner, id).Segments.Sort(static (a, b) => a.ProjectStartTick.CompareTo(b.ProjectStartTick));
                foreach (MidoraId id in changes.PureMidiTrackIds) FindPureMidiTrack(owner, id).Segments.Sort(static (a, b) => a.ProjectStartTick.CompareTo(b.ProjectStartTick));
            }, owner =>
            {
                for (int index = 0; index < placements.Length; index++)
                    if (created![index] is Segment logical) RemoveRequired(FindTrack(owner, placements[index].Target.TrackId).Segments, logical, "converted Segment");
                    else RemoveRequired(FindPureMidiTrack(owner, placements[index].Target.TrackId).Segments, (MidiSegment)created[index], "converted MIDI Segment");
                foreach (var original in originals.OrderBy(static value => value.Index))
                    if (original.Logical is not null) FindTrack(owner, original.TrackId).Segments.Insert(original.Index, original.Logical);
                    else FindPureMidiTrack(owner, original.TrackId).Segments.Insert(original.Index, original.Direct!);
            });
            return new ConversionSelectionPrepared(edit, moveIds, () => created!.Select(static value =>
                value is Segment logical ? logical.Id : ((MidiSegment)value).Id).ToArray(), () =>
                placements.Select((row, index) => new SegmentIdentityTransfer(row.Source.SourceId,
                    created![index] is Segment logical ? logical.Id : ((MidiSegment)created[index]).Id))
                    .Where(pair => pair.SourceId != pair.TargetId && originalDirectory.ById.ContainsKey(pair.SourceId))
                    .ToArray(), originalDirectory);
        }
        catch { originalDirectory.Dispose(); throw; }
    });

    private sealed record ConversionOriginal(MidoraId TrackId, int Index, Segment? Logical, MidiSegment? Direct)
    { public MidoraId Id => Logical?.Id ?? Direct!.Id; }

    private sealed class ConversionOriginalDirectory : IDisposable
    {
        private IDisposable? _budget;
        private ConversionOriginalDirectory(IDisposable budget, int count)
        { _budget = budget; ById = new(count); Originals = new ConversionOriginal[count]; }
        public Dictionary<MidoraId, ConversionOriginal?> ById { get; }
        public ConversionOriginal[] Originals { get; }
        public static ConversionOriginalDirectory Create(MidoraProject project, MidoraId[] requested)
        {
            BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
            // One bounded ID directory and a stable-order array, rather than
            // resolving every selected Segment by rescanning the whole Project.
            IDisposable budget = scope.Resources.ReserveWorking(checked(requested.Length * 192L));
            ConversionOriginalDirectory? result = null;
            try
            {
                result = new(budget, requested.Length);
                foreach (MidoraId id in requested)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    if (id == default || !result.ById.TryAdd(id, null))
                        throw new ArgumentException("Source Segment IDs must be distinct and valid.", nameof(requested));
                }
                if (requested.Length == 0) return result;
                long visited = 0;
                foreach (LogicalTrack track in project.Tracks)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    for (int index = 0; index < track.Segments.Count; index++)
                    {
                        if ((visited++ & 255) == 0) scope.Token.ThrowIfCancellationRequested();
                        Segment segment = track.Segments[index];
                        if (result.ById.TryGetValue(segment.Id, out ConversionOriginal? previous))
                        {
                            if (previous is not null) throw new InvalidOperationException("The source Segment stable ID is duplicated.");
                            result.ById[segment.Id] = new(track.Id, index, segment, null);
                        }
                    }
                }
                foreach (PureMidiTrack track in project.PureMidiTracks)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    for (int index = 0; index < track.Segments.Count; index++)
                    {
                        if ((visited++ & 255) == 0) scope.Token.ThrowIfCancellationRequested();
                        MidiSegment segment = track.Segments[index];
                        if (result.ById.TryGetValue(segment.Id, out ConversionOriginal? previous))
                        {
                            if (previous is not null) throw new InvalidOperationException("The source Segment stable ID is duplicated.");
                            result.ById[segment.Id] = new(track.Id, index, null, segment);
                        }
                    }
                }
                for (int index = 0; index < requested.Length; index++)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    result.Originals[index] = result.ById[requested[index]]
                        ?? throw new ArgumentOutOfRangeException("segmentId", "A source Segment no longer exists.");
                }
                return result;
            }
            catch
            {
                if (result is null) budget.Dispose(); else result.Dispose();
                throw;
            }
        }
        public void Dispose() => Interlocked.Exchange(ref _budget, null)?.Dispose();
    }

    private static object BuildConversionTarget(MidoraProject project, SegmentConversionPlacement row, ConversionOriginal? original)
    {
        if (row.Target.Kind == ArrangementTrackKind.LogicalTrack)
        {
            if (original?.Logical is { } old)
            {
                Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, old, BulkEditPreparationContext.Current!.Token);
                result.ProjectStartTick = row.StartTick;
                return result;
            }
            if (row.Source.Logical is { } logicalSnapshot)
                return CreateSegmentFromClipboard(project, logicalSnapshot, row.StartTick);
            BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
            // Imported notes may be stored in page/ID order. The winner must
            // instead be the first source NoteOn in the formal MIDI order.
            using var ordered = BoundedEditSort.Sort(row.Source.Direct!.Notes.Select((note, index) => new ConversionOrderedNote(note, index)),
                Comparer<ConversionOrderedNote>.Create(static (a, b) =>
                {
                    int c = a.Note.StartOffset.CompareTo(b.Note.StartOffset);
                    if (c == 0) c = a.Note.NoteOnOrder.CompareTo(b.Note.NoteOnOrder);
                    return c == 0 ? a.Ordinal.CompareTo(b.Ordinal) : c;
                }), scope.Resources, scope.Token);
            SegmentClipboardSnapshot snapshot = new(0, 0, row.Source.LengthTicks, row.Source.ContentOffsetTick,
                new ProjectedClipboardList<ConversionOrderedNote, LogicalNoteClipboardSnapshot>(ordered,
                    static value => new(value.Note.StartOffset, value.Note.LengthTicks, value.Note.Key, value.Note.NoteOnVelocity)), []);
            return CreateSegmentFromClipboard(project, snapshot, row.StartTick);
        }
        if (original?.Direct is { } oldDirect)
        {
            MidiSegment result = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(project, oldDirect, BulkEditPreparationContext.Current!.Token);
            result.ProjectStartTick = row.StartTick;
            return result;
        }
        MidiSegmentClipboardSnapshot direct = row.Source.Direct ?? new(0, 0, row.Source.LengthTicks,
            row.Source.ContentOffsetTick, new ConversionDirectNoteList(row.Source.Logical!.Notes), [], []);
        return CreateMidiSegmentFromClipboard(project, direct, row.StartTick);
    }

    private readonly record struct ConversionOrderedNote(DirectMidiNoteClipboardSnapshot Note, int Ordinal);
    private sealed class ConversionDirectNoteList(IReadOnlyList<LogicalNoteClipboardSnapshot> notes)
        : IReadOnlyList<DirectMidiNoteClipboardSnapshot>
    {
        public int Count => notes.Count;
        public DirectMidiNoteClipboardSnapshot this[int index] => Convert(notes[index], index);
        public IEnumerator<DirectMidiNoteClipboardSnapshot> GetEnumerator()
        {
            int ordinal = 0;
            foreach (var note in notes) yield return Convert(note, ordinal++);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private static DirectMidiNoteClipboardSnapshot Convert(LogicalNoteClipboardSnapshot note, int ordinal)
        {
            ValidateDirectMidiNote(note.StartOffset, note.LengthTicks, note.Note, note.Velocity, 0);
            return new(note.StartOffset, note.LengthTicks, note.Note, note.Velocity, 0, ordinal * 2L, ordinal * 2L + 1, true);
        }
    }

    private sealed class ConversionSelectionPrepared(IPreparedProjectEdit inner, IReadOnlyList<MidoraId> original,
        Func<IReadOnlyList<MidoraId>> getResult, Func<IReadOnlyList<SegmentIdentityTransfer>> getTransfers,
        IDisposable resources) : IPreparedTimelineSelectionEdit, IPreparedSegmentIdentityTransfers, IDisposable
    {
        private IDisposable? _resources = resources;
        private PreparedTimelineSelection? _selection;
        public bool HasChanges => inner.HasChanges;
        public ProjectChangeSet Changes => inner.Changes;
        public IReadOnlyList<SegmentIdentityTransfer> SegmentIdentityTransfers { get; private set; } = [];
        public PreparedTimelineSelection PreparedSelection => _selection
            ?? throw new InvalidOperationException("Conversion selection is not prepared yet.");
        public void Apply(MidoraProject project)
        {
            inner.Apply(project);
            if (_selection is not null) return;
            _selection = new(CompactMidoraIdList.Freeze(original), CompactMidoraIdList.Freeze(getResult()));
            SegmentIdentityTransfers = getTransfers();
        }
        public void Undo(MidoraProject project) => inner.Undo(project);
        public void Dispose()
        {
            IDisposable? owned = Interlocked.Exchange(ref _resources, null);
            if (owned is null) return;
            try { (inner as IDisposable)?.Dispose(); }
            finally { owned.Dispose(); }
        }
    }
}
