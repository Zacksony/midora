using System.Collections;
using Midora.Domain;

namespace Midora.Application;

/// <summary>One editable timeline container, not a track or a display lane.</summary>
public readonly record struct TimelineObjectOwner(
    ProjectTimelineOwnerKind Kind, MidoraId OwnerId, MidoraId? EventInstrumentId = null);

public enum TimelineObjectSelectionKind { Note, Event, Opaque }

/// <summary>Scalar classification only; opaque payloads never enter staging.</summary>
public readonly record struct TimelineObjectSelectionMember(
    MidoraId Id, TimelineObjectSelectionKind Kind, MidoraId? LaneId = null,
    TemplateEventKind? TemplateKind = null, int Number = 0,
    DirectMidiChannelEventKind? DirectKind = null, bool HasBankMsb = false, bool HasBankLsb = false);

/// <summary>Background-only bounded classification shared by menus and deletion.</summary>
public static class ProjectTimelineObjectSelection
{
    public static IEnumerable<TimelineObjectSelectionMember> ReadMembers(
        MidoraProject project, TimelineObjectOwner owner, IReadOnlyCollection<MidoraId> ids,
        CancellationToken token = default, IProgress<TimelineEditPreparationProgress>? progress = null,
        bool requireAll = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ids);
        token.ThrowIfCancellationRequested();
        // Clearing a selection has no source membership to resolve. In
        // particular, do not prepare cold ordinal indexes for unrelated pages.
        if (ids.Count == 0) yield break;
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        int found = 0;
        switch (owner.Kind)
        {
            case ProjectTimelineOwnerKind.LogicalSegment:
            {
                Segment segment = project.Tracks.SelectMany(static t => t.Segments)
                    .FirstOrDefault(s => s.Id == owner.OwnerId) ?? throw UnknownOwner();
                using var laneMetadata = scope.Resources.ReserveWorking(checked(segment.ParameterLanes.Count * 128L));
                var notes = segment.Notes.CreateQuerySnapshot();
                var lanes = segment.ParameterLanes.Select(l => (l.Id, Source: l.Points.CreateQuerySnapshot())).ToArray();
                foreach (var note in Read(notes, 0, lanes.Length + 1)) { found++; yield return new(note.Id, TimelineObjectSelectionKind.Note); }
                for (int index = 0; index < lanes.Length; index++)
                {
                    var lane = lanes[index];
                    foreach (var point in Read(lane.Source, index + 1, lanes.Length + 1))
                    { found++; yield return new(point.Id, TimelineObjectSelectionKind.Event, lane.Id); }
                }
                break;
            }
            case ProjectTimelineOwnerKind.DirectMidiSegment:
            {
                MidiSegment segment = project.PureMidiTracks.SelectMany(static t => t.Segments)
                    .FirstOrDefault(s => s.Id == owner.OwnerId) ?? throw UnknownOwner();
                var notes = segment.Notes.CreateObjectSource();
                var events = segment.ChannelEvents.CreateObjectSource();
                var opaque = segment.OpaqueEvents.CreateObjectSource();
                foreach (var note in Read(notes, 0, 3)) { found++; yield return new(note.Id, TimelineObjectSelectionKind.Note); }
                foreach (var point in Read(events, 1, 3))
                { found++; yield return new(point.Id, TimelineObjectSelectionKind.Event, DirectKind: point.Kind, Number: point.Data1); }
                foreach (var point in ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(project, opaque, ids,
                    scope.Token, scope.ProgressInRange(2d / 3, 1d / 3), requireAll: false))
                { found++; yield return new(point.Id, TimelineObjectSelectionKind.Opaque); }
                break;
            }
            case ProjectTimelineOwnerKind.SubVoice:
            {
                EventInstrument instrument = project.EventInstruments.FirstOrDefault(i => i.Id == owner.EventInstrumentId)
                    ?? throw UnknownOwner();
                SubVoice voice = instrument.SubVoices.FirstOrDefault(v => v.Id == owner.OwnerId) ?? throw UnknownOwner();
                var source = voice.Events.CreateQuerySnapshot();
                foreach (var value in Read(source, 0, 1))
                {
                    found++;
                    yield return new(value.Id, value.Kind == TemplateEventKind.Note ? TimelineObjectSelectionKind.Note
                        : TimelineObjectSelectionKind.Event, TemplateKind: value.Kind, Number: value.Number,
                        HasBankMsb: value.HasBankMsb, HasBankLsb: value.HasBankLsb);
                }
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(owner));
        }
        scope.Token.ThrowIfCancellationRequested();
        if (requireAll && found != ids.Count)
            throw new ArgumentException("Every selected timeline object must belong to the current owner.", nameof(ids));
        IEnumerable<T> Read<T>(ITimelineObjectSource<T> source, int component, int components) where T : unmanaged =>
            ProjectTimelineReadPreparation.ReadSelectedValues(project, source, ids, scope.Token,
                scope.ProgressInRange((double)component / components, 1d / components),
                requireAll: false, preserveFormalOrder: false);
        static ArgumentException UnknownOwner() => new("The timeline owner is no longer available.", nameof(owner));
    }
}

public static partial class ProjectDomainEditCommands
{
    private static readonly AsyncLocal<TimelineObjectOwner?> RequestedTimelineSelectionOwner = new();

    private static SelectionPublisher? RequestedTimelineSelectionPublisher(TimelineObjectOwner owner) =>
        RequestedTimelineSelectionOwner.Value == owner ? static ids => ids : null;

    /// <summary>
    /// Adapts legacy identity-preserving edits to exact selection publication.
    /// Existing bounded plans pass through without a second computation. A
    /// legacy small edit is evaluated on a shared-root detached mirror, using
    /// the same collision policy as ProjectDocumentSession; deleted IDs are
    /// filtered only after all properties have reached their final values.
    /// </summary>
    public static ITimelineSelectionResultEditCommand WithTimelineObjectSelection(
        IProjectEditCommand command, TimelineObjectOwner owner, IReadOnlyCollection<MidoraId> ids) =>
        ResultCommand(command?.Name ?? throw new ArgumentNullException(nameof(command)), (project, publish, token, progress) =>
        {
            ArgumentNullException.ThrowIfNull(ids);
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            IPreparedProjectEdit? prepared = PrepareOn(project);
            if (prepared is IPreparedTimelineSelectionEdit { HasPreparedSelection: true } selected)
            {
                try
                {
                    return WithSelectionPublication(prepared, selected.PreparedSelection.OriginalSelectionIds,
                        selected.PreparedSelection.ResultSelectionIds, publish);
                }
                catch { (prepared as IDisposable)?.Dispose(); throw; }
            }
            (prepared as IDisposable)?.Dispose(); prepared = null;
            using var lease = scope.Resources.BeginResourceLease();
            IDisposable? metadata = null;
            MidoraProject? draft = null;
            try
            {
                metadata = BoundedProjectDirectoryBudget.Reserve(project, scope);
                var beforeStore = new BoundedEditRecordStore<MidoraId>(scope.Resources);
                foreach (var member in ProjectTimelineObjectSelection.ReadMembers(project, owner, ids, scope.Token))
                    beforeStore.Add(member.Id);
                beforeStore.Seal();
                var before = new BoundedImmutableValueSource<MidoraId>(beforeStore);
                draft = ProjectCompilationSnapshot.Create(project, scope.Token);
                prepared = ExactTimelineCollisionPolicy.Wrap(draft, PrepareOn(draft));
                prepared.Apply(draft);
                if (draft.NextStableId != project.NextStableId)
                    throw new InvalidOperationException("An allocating edit must provide its exact newly-created selection.");
                var afterStore = new BoundedEditRecordStore<MidoraId>(scope.Resources);
                foreach (var member in ProjectTimelineObjectSelection.ReadMembers(draft, owner, ids,
                    scope.Token, requireAll: false)) afterStore.Add(member.Id);
                afterStore.Seal();
                var after = new BoundedImmutableValueSource<MidoraId>(afterStore);
                prepared = DetachedProjectRootPreparedEdit.Create(project, draft, prepared.HasChanges,
                    prepared.Changes, [prepared], metadata);
                metadata = null; draft = null;
                prepared = new MixedTimelineOwnedEdit(prepared, lease.Complete(scope.Token));
                var result = WithSelectionPublication(prepared, before, after, publish);
                scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
                scope.Token.ThrowIfCancellationRequested();
                prepared = null;
                return result;
            }
            catch { (prepared as IDisposable)?.Dispose(); draft?.Dispose(); throw; }
            finally { metadata?.Dispose(); }

            IPreparedProjectEdit PrepareOn(MidoraProject target)
            {
                var previous = RequestedTimelineSelectionOwner.Value;
                RequestedTimelineSelectionOwner.Value = owner;
                try
                {
                    return command switch
                    {
                        IProgressReportingProjectEditCommand reporting => reporting.Prepare(target, scope.Token, progress),
                        ICancellableProjectEditCommand cancellable => cancellable.Prepare(target, scope.Token),
                        _ => command.Prepare(target)
                    };
                }
                finally { RequestedTimelineSelectionOwner.Value = previous; }
            }
        });

    /// <summary>Mixed/cross-lane delete, one existing detached history transaction.</summary>
    public static ITimelineSelectionResultEditCommand DeleteTimelineObjects(
        TimelineObjectOwner owner, IReadOnlyCollection<MidoraId> ids) =>
        ResultCommand("Delete timeline objects", (project, publish, token, progress) =>
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) throw new ArgumentException("At least one timeline object must be selected.", nameof(ids));
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            using var lease = scope.Resources.BeginResourceLease();
            using var sortedIds = BoundedEditSort.Sort(ids, Comparer<MidoraId>.Default, scope.Resources, scope.Token);
            MidoraId previous = default;
            foreach (var id in sortedIds)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (id == default || id == previous) throw new ArgumentException("Selection IDs must be distinct and valid.", nameof(ids));
                previous = id;
            }
            using var rows = BoundedEditSort.Sort(
                ProjectTimelineObjectSelection.ReadMembers(project, owner, ids, scope.Token),
                Comparer<TimelineObjectSelectionMember>.Create(static (a, b) =>
                {
                    int order = a.Kind.CompareTo(b.Kind);
                    if (order == 0) order = Nullable.Compare(a.LaneId, b.LaneId);
                    return order != 0 ? order : a.Id.CompareTo(b.Id);
                }), scope.Resources, scope.Token, scope.ProgressInWorkRange(0, .2));
            var originalStore = new BoundedEditRecordStore<MidoraId>(scope.Resources);
            originalStore.AddRange(sortedIds, scope.Token); originalStore.Seal();
            var original = new BoundedImmutableValueSource<MidoraId>(originalStore);
            int maximumGroups = owner.Kind == ProjectTimelineOwnerKind.LogicalSegment
                ? checked(FindSegment(project, owner.OwnerId).Segment.ParameterLanes.Count + 1) : 3;
            using var factoryMetadata = scope.Resources.ReserveWorking(checked(maximumGroups * 256L));
            var factories = new List<Func<MidoraProject, IProjectEditCommand>>();
            if (owner.Kind == ProjectTimelineOwnerKind.SubVoice)
                factories.Add(_ => DeleteTemplateEvents(owner.EventInstrumentId!.Value, owner.OwnerId, original));
            else
            {
                int first = 0;
                while (first < rows.Count)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    var firstRow = rows[first];
                    int end = first + 1;
                    while (end < rows.Count && rows[end].Kind == firstRow.Kind && rows[end].LaneId == firstRow.LaneId)
                    {
                        if ((end & 255) == 0) scope.Token.ThrowIfCancellationRequested();
                        end++;
                    }
                    var group = new MixedTimelineIdSlice(rows, first, end - first);
                    factories.Add(_ => owner.Kind == ProjectTimelineOwnerKind.LogicalSegment
                        ? firstRow.Kind == TimelineObjectSelectionKind.Note
                            ? DeleteLogicalNotes(owner.OwnerId, group)
                            : DeleteMixedLogicalParameterPoints(owner.OwnerId, firstRow.LaneId!.Value, group)
                        : firstRow.Kind switch
                        {
                            TimelineObjectSelectionKind.Note => DeleteDirectMidiNotes(owner.OwnerId, group),
                            TimelineObjectSelectionKind.Event => DeleteDirectMidiEvents(owner.OwnerId, group),
                            _ => DeleteOpaqueMidiEvents(owner.OwnerId, group)
                        });
                    first = end;
                }
            }
            IPreparedProjectEdit? prepared = null;
            try
            {
                prepared = new SequentialProjectEditCommand("Delete timeline objects", factories)
                    .Prepare(project, scope.Token, scope.ProgressInWorkRange(.2, .7));
                var resources = lease.Complete(scope.Token, scope.ProgressInWorkRange(.9, .09));
                prepared = new MixedTimelineOwnedEdit(prepared, resources);
                var result = WithSelectionPublication(prepared, original, Array.Empty<MidoraId>(), publish);
                scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
                scope.Token.ThrowIfCancellationRequested();
                prepared = null;
                return result;
            }
            catch { (prepared as IDisposable)?.Dispose(); throw; }
        });

    // Deletion is also available for an orphan lane. It must not require the
    // missing Definition merely to validate a value that will be removed.
    private static ITimelineSelectionResultEditCommand DeleteMixedLogicalParameterPoints(
        MidoraId segmentId, MidoraId laneId, IReadOnlyCollection<MidoraId> ids) =>
        ResultCommand("Delete logical parameter points", (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            using var plan = BoundedCurvePointPlan.Transform(lane.Points.CreateQuerySnapshot(), ids,
                static _ => static _ => null, resolveCollisions: false, duplicate: false,
                static () => throw new InvalidOperationException("Deletion never allocates an object ID."),
                static _ => { });
            return PublishBoundedLogicalPoints(project, location, lane, stamp, plan,
                project.NextStableId, project.NextStableId, publish, false);
        });

    private sealed class MixedTimelineIdSlice(BoundedEditRecordStore<TimelineObjectSelectionMember> rows, int first, int count)
        : IReadOnlyList<MidoraId>
    {
        public int Count => count;
        public MidoraId this[int index] => index >= 0 && index < count ? rows[first + index].Id
            : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<MidoraId> GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MixedTimelineOwnedEdit(IPreparedProjectEdit source, BoundedEditPublicationResources resources)
        : IPreparedProjectEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public void ValidateForPublication(MidoraProject project)
        { if (source is IPreparedProjectEditPublicationGate gate) gate.ValidateForPublication(project); }
        public void Apply(MidoraProject project) => source.Apply(project);
        public void Undo(MidoraProject project) => source.Undo(project);
        public void Dispose() { (source as IDisposable)?.Dispose(); resources.Dispose(); }
    }
}
