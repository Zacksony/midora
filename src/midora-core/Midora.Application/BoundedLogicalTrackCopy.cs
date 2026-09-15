using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedLogicalTrackCopy(MidoraProject project, LogicalTrack source,
        string name, bool shareState, int insertionIndex)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        using var metadata = scope.Resources.ReserveWorking(checked((project.Tracks.Count + project.EventInstrumentUsages.Count
            + project.ArrangementTracks.Count + (long)source.Segments.Count
            + source.Segments.Sum(segment => (long)segment.ParameterLanes.Count)) * 256L));
        LogicalTrack[] oldTracks = project.Tracks.ToArray();
        EventInstrumentUsage[] oldUsages = project.EventInstrumentUsages.ToArray();
        ArrangementTrackReference[] oldOrder = project.ArrangementTracks.ToArray();
        Segment[] sourceSegments = source.Segments.ToArray();
        var stamps = sourceSegments.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray();
        long firstId = project.NextStableId, nextId = firstId;
        var progress = new LogicalStructureProgress(scope, sourceSegments.Sum(LogicalSegmentRecordCount));
        LogicalTrack copy = new(project, Allocate()) { Name = name, EventInstrumentUsageId = source.EventInstrumentUsageId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName, ColorOverride = source.ColorOverride };
        foreach (Segment segment in sourceSegments)
        {
            scope.Token.ThrowIfCancellationRequested();
            copy.Segments.Add(CloneBoundedLogicalSegment(project, segment, Allocate, scope, progress));
        }
        EventInstrumentUsage? newUsage = null;
        if (!shareState && source.EventInstrumentUsageId is { } usageId)
        {
            EventInstrumentUsage usage = project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                ?? throw new InvalidOperationException("The Logical Track Event Instrument Usage no longer exists.");
            newUsage = new(project, Allocate()) { EventInstrumentId = usage.EventInstrumentId };
            copy.EventInstrumentUsageId = newUsage.Id;
        }
        var nextOrder = oldOrder.ToList(); nextOrder.Insert(insertionIndex, new(ArrangementTrackKind.LogicalTrack, copy.Id));
        progress.Complete();
        return new BoundedLogicalTrackCopyRoots(project, source, sourceSegments, stamps,
            oldTracks, [..oldTracks, copy], oldUsages, newUsage is null ? oldUsages : [..oldUsages, newUsage],
            oldOrder, nextOrder.ToArray(), firstId, nextId);
        MidoraId Allocate() { var id = new MidoraId(nextId); nextId = checked(nextId + 1); return id; }
    }

    private sealed class BoundedLogicalTrackCopyRoots(MidoraProject owner, LogicalTrack source, Segment[] sourceSegments,
        ProjectTimelineOwnerSourceStamp[] sourceStamps, LogicalTrack[] oldTracks, LogicalTrack[] nextTracks,
        EventInstrumentUsage[] oldUsages, EventInstrumentUsage[] nextUsages,
        ArrangementTrackReference[] oldOrder, ArrangementTrackReference[] nextOrder,
        long firstId, long nextId) : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private bool _published, _applied;
        private readonly List<LogicalTrack> _oldTrackRoot = owner.Tracks;
        private readonly List<LogicalTrack> _newTrackRoot = nextTracks.ToList();
        private readonly List<EventInstrumentUsage> _oldUsageRoot = owner.EventInstrumentUsages;
        private readonly List<EventInstrumentUsage> _newUsageRoot = nextUsages.ToList();
        private readonly List<ArrangementTrackReference> _oldOrderRoot = owner.ArrangementTracks;
        private readonly List<ArrangementTrackReference> _newOrderRoot = nextOrder.ToList();
        public bool HasChanges => true;
        public ProjectChangeSet Changes { get; } = EverythingChange();
        public void ValidateForPublication(MidoraProject project) => Validate(project, false);
        private void Validate(MidoraProject project, bool after)
        {
            if (!ReferenceEquals(project, owner)) throw new InvalidOperationException("This Track copy belongs to another project.");
            if (!project.Tracks.SequenceEqual(after ? nextTracks : oldTracks)
                || !project.EventInstrumentUsages.SequenceEqual(after ? nextUsages : oldUsages)
                || !project.ArrangementTracks.SequenceEqual(after ? nextOrder : oldOrder))
                throw new InvalidOperationException("The Track catalog changed before publication.");
            if (_published) return;
            if (project.NextStableId != firstId || !source.Segments.SequenceEqual(sourceSegments))
                throw new InvalidOperationException("The Track source changed before publication.");
            for (int i = 0; i < sourceSegments.Length; i++)
                if (!sourceStamps[i].Matches(sourceSegments[i])) throw new InvalidOperationException("The Track content changed before publication.");
        }
        public void Apply(MidoraProject project)
        {
            if (_applied) throw new InvalidOperationException("The Track copy is already applied.");
            Validate(project, false);
            if (!_published) project.AdvanceNextStableId(nextId);
            project.Tracks = _newTrackRoot;
            project.EventInstrumentUsages = _newUsageRoot;
            project.ArrangementTracks = _newOrderRoot;
            _published = _applied = true;
        }
        public void Undo(MidoraProject project)
        {
            if (!_applied) return;
            Validate(project, true);
            project.Tracks = _oldTrackRoot;
            project.EventInstrumentUsages = _oldUsageRoot;
            project.ArrangementTracks = _oldOrderRoot;
            _applied = false;
        }
    }
}
