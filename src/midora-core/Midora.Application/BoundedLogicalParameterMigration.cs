using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static BoundedTimelineEditValueSource<CurvePointSnapshotValue> PrepareBoundedParameterPointValues(
        CurvePointQuerySnapshot source, LogicalParameterTarget target, LogicalParameterLaneRebindMode mode,
        BulkEditPreparationContext scope, LogicalStructureProgress progress)
    {
        var records = new BoundedEditRecordStore<TimelineValueEdit<CurvePointSnapshotValue>>(scope.Resources);
        try
        {
            int ordinal = 0;
            foreach (var value in progress.Read(source.EnumerateAll()))
            {
                double? converted = ConvertLogicalParameterValue(value.Value, target, mode);
                if (converted is not double number) records.Add(new(ordinal, true, value), scope.Token);
                else if (number != value.Value || value.Interpolation != CurveInterpolation.Step)
                    records.Add(new(ordinal, false, value with { Value = number, Interpolation = CurveInterpolation.Step }), scope.Token);
                ordinal++;
            }
            records.Seal(); records.SpillResidentPages(scope.Token);
            return new(records);
        }
        catch { records.Dispose(); throw; }
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalParameterLaneRebind(MidoraProject project, SegmentLocation location,
        LogicalParameterLane lane, MidoraId parameterId, LogicalParameterTarget target, LogicalParameterLaneRebindMode mode)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        using var metadata = scope.Resources.ReserveWorking(checked((long)location.Segment.ParameterLanes.Count * 256));
        var snapshot = lane.Points.CreateQuerySnapshot();
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        var progress = new LogicalStructureProgress(scope, snapshot.Count);
        var plan = PrepareBoundedParameterPointValues(snapshot, target, mode, scope, progress);
        if (plan.Count == 0 && lane.ParameterId == parameterId)
        {
            plan.Dispose(); progress.Complete();
            return ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(project, location.Track,
                location.Segment, TrackChange(location.Track.Id), stamp);
        }
        Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, scope.Token);
        var resultLane = result.ParameterLanes.Single(value => value.Id == lane.Id);
        resultLane.ParameterId = parameterId;
        if (plan.Count != 0)
        {
            resultLane.Points.Clear(); resultLane.Points.AdoptEditedSnapshot(project, snapshot, plan, cancellationToken: scope.Token);
        }
        else plan.Dispose();
        progress.Complete();
        return ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track, location.Segment, result,
            TrackChange(location.Track.Id), expectedSourceStamp: stamp);
    }

    private static IPreparedProjectEdit PrepareBoundedLogicalParameterDefinitionMigration(MidoraProject project,
        EventInstrument instrument, LogicalParameterDefinition parameter, ValidatedLogicalParameterDefinitionEdit target,
        LogicalParameterLaneRebindMode mode, string name)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var sourceTracks = project.Tracks.Where(track => track.Segments.Any(segment => segment.ParameterLanes.Any(lane => lane.ParameterId == parameter.Id)));
        long directoryCount = sourceTracks.Sum(track => (long)track.Segments.Count
            + track.Segments.Sum(segment => (long)segment.ParameterLanes.Count));
        using var metadata = scope.Resources.ReserveWorking(checked((directoryCount + parameter.EnumItems.Count + (long)target.Items.Count) * 384));
        long firstId = project.NextStableId, nextId = firstId;
        var definitionStamp = CaptureDefinition(parameter);
        string sourceName = parameter.Name;
        var itemStamps = parameter.EnumItems.Select(item => new LogicalParameterEnumItemSnapshot(item, item.Name, item.Value)).ToArray();
        int parameterIndex = instrument.LogicalParameters.IndexOf(parameter);
        var tracks = sourceTracks.ToArray();
        var originals = tracks.Select(track => (Track: track, Old: track.Segments.ToArray(),
            Stamps: track.Segments.Select(ProjectTimelineOwnerSourceStamp.Capture).ToArray())).ToArray();
        var progress = new LogicalStructureProgress(scope, tracks.SelectMany(track => track.Segments).SelectMany(segment => segment.ParameterLanes)
            .Where(lane => lane.ParameterId == parameter.Id).Sum(lane => (long)lane.Points.Count));
        Dictionary<Segment, Segment> replacements = [];
        foreach (LogicalTrack track in tracks)
            foreach (Segment segment in track.Segments)
                foreach (LogicalParameterLane lane in segment.ParameterLanes)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    if (lane.ParameterId != parameter.Id) continue;
                    var snapshot = lane.Points.CreateQuerySnapshot();
                    var plan = PrepareBoundedParameterPointValues(snapshot, target.Target, mode, scope, progress);
                    if (plan.Count == 0) { plan.Dispose(); continue; }
                    if (!replacements.TryGetValue(segment, out var result))
                    {
                        result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, segment, scope.Token);
                        replacements.Add(segment, result);
                    }
                    var resultLane = result.ParameterLanes.Single(value => value.Id == lane.Id);
                    resultLane.Points.Clear(); resultLane.Points.AdoptEditedSnapshot(project, snapshot, plan, cancellationToken: scope.Token);
                }
        LogicalParameterDefinition replacement = new(project, parameter.Id) { Name = name, Type = target.Type,
            Minimum = target.Minimum, Maximum = target.Maximum, DisplayMinimum = target.DisplayMinimum,
            DisplayMaximum = target.DisplayMaximum, DefaultValue = target.DefaultValue, UsesExplicitEnumValues = target.UsesExplicitEnumValues };
        foreach (var item in target.Items)
        {
            scope.Token.ThrowIfCancellationRequested();
            MidoraId id = item.ExistingItem?.Id ?? Allocate();
            replacement.EnumItems.Add(new LogicalParameterEnumItem(project, id) { Name = item.Name, Value = item.Value });
        }
        var changes = EventInstrumentChange(instrument.Id); changes.TrackIds.UnionWith(tracks.Select(track => track.Id));
        bool hasChanges = replacements.Count != 0 || !string.Equals(parameter.Name, name, StringComparison.Ordinal)
            || DefinitionMigrationHasChanges(parameter, target, []);
        IPreparedProjectEdit segmentRoots = new BoundedLogicalTrackSegmentRoots(project, replacements, changes, originals, preserveOrder: true);
        progress.Complete();
        return new BoundedParameterDefinitionRoots(project, instrument, parameter, replacement, segmentRoots, changes, hasChanges, firstId, nextId,
            definitionStamp, sourceName, itemStamps, parameterIndex);
        MidoraId Allocate() { var id = new MidoraId(nextId); nextId = checked(nextId + 1); return id; }
    }

    private sealed class BoundedParameterDefinitionRoots : IPreparedProjectEdit, IPreparedProjectEditPublicationGate
    {
        private readonly MidoraProject _owner;
        private readonly EventInstrument _instrument;
        private readonly LogicalParameterDefinition _old, _next;
        private readonly LogicalParameterDefinitionSnapshot _oldState;
        private readonly string _oldName;
        private readonly LogicalParameterEnumItemSnapshot[] _oldItems;
        private readonly IPreparedProjectEdit _segments;
        private readonly int _index;
        private readonly long _firstId, _nextId;
        private bool _applied, _published;
        public BoundedParameterDefinitionRoots(MidoraProject owner, EventInstrument instrument, LogicalParameterDefinition old,
            LogicalParameterDefinition next, IPreparedProjectEdit segments, ProjectChangeSet changes, bool changed, long firstId, long nextId,
            LogicalParameterDefinitionSnapshot oldState, string oldName, LogicalParameterEnumItemSnapshot[] oldItems, int index)
        {
            _owner = owner; _instrument = instrument; _old = old; _next = next; _segments = segments;
            _oldState = oldState; _oldName = oldName; _oldItems = oldItems;
            _index = index; _firstId = firstId; _nextId = nextId;
            Changes = changes; HasChanges = changed;
        }
        public bool HasChanges { get; }
        public ProjectChangeSet Changes { get; }
        public void ValidateForPublication(MidoraProject project)
        {
            ValidateDefinition(project, false);
            ((IPreparedProjectEditPublicationGate)_segments).ValidateForPublication(project);
        }
        private void ValidateDefinition(MidoraProject project, bool after)
        {
            if (!ReferenceEquals(project, _owner) || !project.EventInstruments.Contains(_instrument)
                || _index < 0 || _index >= _instrument.LogicalParameters.Count
                || !ReferenceEquals(_instrument.LogicalParameters[_index], after ? _next : _old))
                throw new InvalidOperationException("The Logical Parameter definition changed before publication.");
            if (!_published && (project.NextStableId != _firstId || CaptureDefinition(_old) != _oldState || _old.Name != _oldName
                || _old.EnumItems.Count != _oldItems.Length
                || _old.EnumItems.Where((item, index) => !ReferenceEquals(item, _oldItems[index].Item)
                    || item.Name != _oldItems[index].Name || item.Value != _oldItems[index].Value).Any()))
                throw new InvalidOperationException("The Logical Parameter source changed before publication.");
        }
        public void Apply(MidoraProject project)
        {
            if (_applied) throw new InvalidOperationException("The migration is already applied.");
            ValidateForPublication(project);
            if (!HasChanges) return;
            _segments.Apply(project);
            if (!_published) project.AdvanceNextStableId(_nextId);
            _instrument.LogicalParameters[_index] = _next;
            _published = _applied = true;
        }
        public void Undo(MidoraProject project)
        {
            if (!_applied) return;
            ValidateDefinition(project, true);
            _segments.Undo(project);
            _instrument.LogicalParameters[_index] = _old;
            _applied = false;
        }
    }
}
