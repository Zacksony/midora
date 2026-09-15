using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Publishes a sequence prepared on a private Project mirror by exchanging only
/// the affected catalog roots. Intermediate edits never touch the live Project.
/// </summary>
internal sealed class DetachedProjectRootPreparedEdit : IPreparedProjectEdit,
    IPreparedProjectEditPublicationGate, IPreparedTimelineSelectionEdit, IDisposable
{
    private readonly MidoraProject _project;
    private MidoraProject? _draft;
    private readonly List<IRootSlot> _slots;
    private List<IPreparedProjectEdit>? _intermediate;
    private readonly long _oldNextId;
    private readonly long _newNextId;
    private readonly ProjectMetadataSnapshot? _oldMetadata;
    private readonly ProjectMetadataSnapshot? _newMetadata;
    private bool _published;
    private bool _applied;
    private IDisposable? _metadataBudget;

    private DetachedProjectRootPreparedEdit(MidoraProject project, MidoraProject draft,
        bool changed, ProjectChangeSet changes, IEnumerable<IPreparedProjectEdit> intermediate, IDisposable? metadataBudget,
        bool normalizeSelection)
    {
        _project = project;
        _metadataBudget = metadataBudget;
        _draft = draft;
        HasChanges = changed;
        Changes = changes;
        _oldNextId = project.NextStableId;
        _newNextId = draft.NextStableId;
        _intermediate = [.. intermediate];
        PreparedTimelineSelection[] selections = _intermediate
            .OfType<IPreparedTimelineSelectionEdit>().Where(static e => e.HasPreparedSelection)
            .Select(static e => e.PreparedSelection).ToArray();
        if (selections.Length != 0)
            _selection = !normalizeSelection && selections.Length == 1 && _intermediate.Count == 1 ? selections[0]
                : BoundedPreparedSelection.Combine(project, draft, changes, selections, _selectionResources);
        _slots = [];
        AddCatalog(project.Tracks, draft.Tracks, changes.TrackIds.Concat(changes.PresentationTrackIds),
            static value => value.Id, () => project.Tracks, value => project.Tracks = value);
        AddCatalog(project.PureMidiTracks, draft.PureMidiTracks,
            changes.PureMidiTrackIds.Concat(changes.PresentationTrackIds),
            static value => value.Id, () => project.PureMidiTracks, value => project.PureMidiTracks = value);
        AddCatalog(project.EventInstruments, draft.EventInstruments,
            changes.EventInstrumentIds.Concat(changes.PresentationEventInstrumentIds),
            static value => value.Id, () => project.EventInstruments, value => project.EventInstruments = value);
        AddCatalog(project.EventInstrumentUsages, draft.EventInstrumentUsages, changes.EventInstrumentUsageIds,
            static value => value.Id, () => project.EventInstrumentUsages, value => project.EventInstrumentUsages = value);
        AddCatalog(project.MidiChannelRoots, draft.MidiChannelRoots, changes.MidiChannelRootIds,
            static value => value.Id, () => project.MidiChannelRoots, value => project.MidiChannelRoots = value);
        AddDamagedCatalog(project.DamagedEventInstruments, draft.DamagedEventInstruments,
            () => project.DamagedEventInstruments, value => project.DamagedEventInstruments = value);
        AddDamagedCatalog(project.DamagedEventInstrumentUsages, draft.DamagedEventInstrumentUsages,
            () => project.DamagedEventInstrumentUsages, value => project.DamagedEventInstrumentUsages = value);
        AddDamagedCatalog(project.DamagedLogicalTracks, draft.DamagedLogicalTracks,
            () => project.DamagedLogicalTracks, value => project.DamagedLogicalTracks = value);
        AddDamagedCatalog(project.DamagedMidiChannelRoots, draft.DamagedMidiChannelRoots,
            () => project.DamagedMidiChannelRoots, value => project.DamagedMidiChannelRoots = value);
        AddDamagedCatalog(project.DamagedPureMidiTracks, draft.DamagedPureMidiTracks,
            () => project.DamagedPureMidiTracks, value => project.DamagedPureMidiTracks = value);
        if (!project.ArrangementTracks.SequenceEqual(draft.ArrangementTracks))
            _slots.Add(new RootSlot<List<ArrangementTrackReference>>(project.ArrangementTracks,
                [.. draft.ArrangementTracks], () => project.ArrangementTracks, value => project.ArrangementTracks = value));
        if (changes.AffectsConductor || changes.AffectsEverything)
            _slots.Add(new RootSlot<ConductorTrack>(project.Conductor, draft.Conductor,
                () => project.Conductor, value => project.Conductor = value));
        if (changes.AffectsEverything)
        {
            _oldMetadata = project.Metadata.Snapshot();
            _newMetadata = draft.Metadata.Snapshot();
            _slots.Add(new RootSlot<MidiInitialState>(project.GlobalInitialState, draft.GlobalInitialState,
                () => project.GlobalInitialState, value => project.GlobalInitialState = value));
            _slots.Add(new RootSlot<MidiInitialState>(project.GlobalResetDefaults, draft.GlobalResetDefaults,
                () => project.GlobalResetDefaults, value => project.GlobalResetDefaults = value));
        }
        draft.ReleaseDetachedCatalogReferences();
    }

    public static IPreparedProjectEdit Create(MidoraProject project, MidoraProject draft,
        bool changed, ProjectChangeSet changes, IEnumerable<IPreparedProjectEdit> intermediate, IDisposable? metadataBudget = null,
        bool normalizeSelection = false) =>
        new DetachedProjectRootPreparedEdit(project, draft, changed, changes, intermediate, metadataBudget, normalizeSelection);

    public bool HasChanges { get; }
    public ProjectChangeSet Changes { get; }
    private readonly PreparedTimelineSelection? _selection;
    private readonly List<IDisposable> _selectionResources = [];
    public bool HasPreparedSelection => _selection is not null;
    public PreparedTimelineSelection PreparedSelection => _selection
        ?? throw new InvalidOperationException("This Project edit does not publish a Timeline selection.");

    private void AddCatalog<T>(List<T> previous, List<T> current, IEnumerable<MidoraId> affected,
        Func<T, MidoraId> id, Func<List<T>> get, Action<List<T>> set) where T : class
    {
        HashSet<MidoraId> ids = Changes.AffectsEverything ? [.. current.Select(id)] : [.. affected];
        if (ids.Count == 0 && previous.Count == current.Count
            && previous.Select(id).SequenceEqual(current.Select(id))) return;
        Dictionary<MidoraId, T> oldById = previous.ToDictionary(id);
        List<T> result = new(current.Count);
        foreach (T item in current)
            result.Add(ids.Contains(id(item)) || !oldById.TryGetValue(id(item), out T? old) ? item : old);
        _slots.Add(new RootSlot<List<T>>(previous, result, get, set));
    }

    private void AddDamagedCatalog(List<DamagedProjectObject> previous, List<DamagedProjectObject> current,
        Func<List<DamagedProjectObject>> get, Action<List<DamagedProjectObject>> set)
    {
        if (!previous.SequenceEqual(current))
            _slots.Add(new RootSlot<List<DamagedProjectObject>>(previous, current, get, set));
    }

    public void ValidateForPublication(MidoraProject project)
    {
        if (!ReferenceEquals(project, _project) || project.NextStableId != _oldNextId)
            throw new InvalidOperationException("The detached edit belongs to a stale Project revision.");
        foreach (IRootSlot slot in _slots) slot.Validate(after: false);
    }

    public void Apply(MidoraProject project)
    {
        if (!ReferenceEquals(project, _project)) throw new InvalidOperationException("The edit belongs to another Project.");
        foreach (IRootSlot slot in _slots) slot.Validate(after: false);
        if (!_published && project.NextStableId != _oldNextId)
            throw new InvalidOperationException("The detached Stable ID reservation is stale.");
        // Resource ownership is transferred before the first root is published.
        // The mirror can own spill-backed roots used by current/history snapshots.
        if (!_published && _draft is not null)
        {
            _draft.AdoptDetachedRuntimeOwner(project);
            _draft.Dispose();
            _draft = null;
            _published = true;
        }
        foreach (IRootSlot slot in _slots) slot.Set(after: true);
        if (_newMetadata is { } metadata) ApplyEditableMetadata(project.Metadata, metadata);
        project.AdvanceNextStableId(_newNextId);
        _applied = true;
        ReleaseIntermediate();
        Interlocked.Exchange(ref _metadataBudget, null)?.Dispose();
    }

    public void Undo(MidoraProject project)
    {
        if (!ReferenceEquals(project, _project)) throw new InvalidOperationException("The edit belongs to another Project.");
        if (!_applied) return;
        foreach (IRootSlot slot in _slots) slot.Validate(after: true);
        foreach (IRootSlot slot in _slots) slot.Set(after: false);
        if (_oldMetadata is { } metadata) ApplyEditableMetadata(project.Metadata, metadata);
        _applied = false;
    }

    public void Dispose()
    {
        ReleaseIntermediate();
        _draft?.Dispose();
        _draft = null;
        if (!_published) foreach (var resource in _selectionResources) resource.Dispose();
        Interlocked.Exchange(ref _metadataBudget, null)?.Dispose();
    }

    private void ReleaseIntermediate()
    {
        List<IPreparedProjectEdit>? values = Interlocked.Exchange(ref _intermediate, null);
        if (values is null) return;
        foreach (IPreparedProjectEdit value in values)
            if (value is IDisposable disposable) disposable.Dispose();
    }

    private static void ApplyEditableMetadata(ProjectMetadata target, ProjectMetadataSnapshot source)
    {
        // Session/save timestamps and accumulated working time are not edits.
        // In particular, restoring them would rewind the active session clock.
        target.ProjectName = source.ProjectName;
        target.ProjectVersion = source.ProjectVersion;
        target.AuthorOrTeam = source.AuthorOrTeam;
        target.OriginalWork = source.OriginalWork;
        target.Copyright = source.Copyright;
    }

    private interface IRootSlot
    {
        void Validate(bool after);
        void Set(bool after);
    }

    private sealed class RootSlot<T>(T before, T after, Func<T> get, Action<T> set) : IRootSlot where T : class
    {
        public void Validate(bool afterState)
        {
            if (!ReferenceEquals(get(), afterState ? after : before))
                throw new InvalidOperationException("A Project root changed while the edit was prepared.");
        }
        public void Set(bool afterState) => set(afterState ? after : before);
    }
}
