using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

/// <summary>
/// Owns Project presentation state independently from Project source history. Presentation
/// edits are deliberately not Project commands and never affect compilation or Project Modified.
/// </summary>
public sealed class ProjectPresentationSessionV3
{
    private readonly object _sync = new();
    private ProjectPresentationStateV3 _state;
    private long _revision;
    private long _savedRevision;
    private bool _recoveryDirty;

    public ProjectPresentationSessionV3(
        ProjectPresentationStateV3? initialState = null,
        bool recoveryDirty = false)
    {
        _state = initialState ?? ProjectPresentationStateV3.Empty;
        _recoveryDirty = recoveryDirty;
    }

    public ProjectPresentationStateV3 Current
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsModified
    {
        get
        {
            lock (_sync)
            {
                return _recoveryDirty || _revision != _savedRevision;
            }
        }
    }

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public event EventHandler? Changed;

    public void CopyForDuplicate(IReadOnlyDictionary<MidoraId, MidoraId> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var state = Current;
        MidoraId Remap(MidoraId id) => map.TryGetValue(id, out var replacement) ? replacement : id;
        var tracks = state.TrackOnionPresets.Where(p => map.ContainsKey(p.TargetTrackId))
            .Select(p => p with { TargetTrackId = Remap(p.TargetTrackId), SourceTrackIds = p.SourceTrackIds.Select(Remap).Where(id => id != Remap(p.TargetTrackId)).ToArray() }).ToArray();
        var voices = state.SubVoiceOnionPresets.Where(p => map.ContainsKey(p.TargetSubVoiceId))
            .Select(p => p with { EventInstrumentId = Remap(p.EventInstrumentId), TargetSubVoiceId = Remap(p.TargetSubVoiceId),
                SourceSubVoiceIds = p.SourceSubVoiceIds.Select(Remap).Where(id => id != Remap(p.TargetSubVoiceId)).ToArray() }).ToArray();
        if (tracks.Length == 0 && voices.Length == 0) return;
        Replace(state with { TrackOnionPresets = state.TrackOnionPresets.Concat(tracks).ToArray(),
            SubVoiceOnionPresets = state.SubVoiceOnionPresets.Concat(voices).ToArray() });
    }

    public void Replace(ProjectPresentationStateV3 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed;
        lock (_sync)
        {
            changed = !Equals(_state, state);
            if (!changed)
            {
                return;
            }
            if (_revision == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "The Project presentation revision counter is exhausted.");
            }
            _state = state;
            _revision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceWorkspace(ProjectPresentationWorkspaceStateV4 workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Replace(Current with { WorkspaceState = workspace });
    }

    public ProjectPresentationSaveSnapshotV3 CreateSaveSnapshot(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_sync)
        {
            ProjectPresentationStateV3 filtered = FilterDormantReferences(_state, project);
            return new(filtered, _revision);
        }
    }

    public void MarkSaveSucceeded(ProjectPresentationSaveSnapshotV3 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool changed;
        lock (_sync)
        {
            changed = _recoveryDirty || _savedRevision != snapshot.Revision;
            _savedRevision = snapshot.Revision;
            _recoveryDirty = false;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ProjectPresentationStateV3 FilterDormantReferences(
        ProjectPresentationStateV3 state,
        MidoraProject project)
    {
        HashSet<MidoraId> tracks = project.ArrangementTracks
            .Select(value => value.TrackId)
            .ToHashSet();
        var trackOrder = project.TracksInArrangementOrder().Select((value, index) => (value.TrackId, index))
            .ToDictionary(value => value.TrackId, value => value.index);
        TrackOnionPresetV3[] trackPresets = state.TrackOnionPresets
            .Where(value => tracks.Contains(value.TargetTrackId))
            .Select(value => value with
            {
                SourceTrackIds = value.SourceTrackIds
                    .Where(id => id != value.TargetTrackId && tracks.Contains(id))
                    .Distinct()
                    .OrderBy(id => trackOrder[id])
                    .ToArray()
            })
            .ToArray();

        Dictionary<MidoraId, HashSet<MidoraId>> subVoices = project.EventInstruments
            .ToDictionary(
                value => value.Id,
                value => value.SubVoices.Select(subVoice => subVoice.Id).ToHashSet());
        var voiceOrder = project.EventInstruments.SelectMany(i => i.SubVoices.Select((voice, index) => (voice.Id, index)))
            .ToDictionary(value => value.Id, value => value.index);
        SubVoiceOnionPresetV3[] subVoicePresets = state.SubVoiceOnionPresets
            .Where(value => subVoices.TryGetValue(value.EventInstrumentId, out HashSet<MidoraId>? ids)
                && ids.Contains(value.TargetSubVoiceId))
            .Select(value => value with
            {
                SourceSubVoiceIds = value.SourceSubVoiceIds
                    .Where(id => id != value.TargetSubVoiceId
                        && subVoices[value.EventInstrumentId].Contains(id))
                    .Distinct()
                    .OrderBy(id => voiceOrder[id])
                    .ToArray()
            })
            .ToArray();
        // Workspace state is a presentation-only snapshot. The desktop session
        // prunes its owner directories before capture; preserve it here so a
        // Save/Save Copy cannot silently discard valid view state. Navigation
        // is filtered independently as well: deleting a Segment or Event
        // Instrument must not make the otherwise valid navigation section fail
        // as a whole during save.
        return new(
            state.AllTracksMode,
            trackPresets,
            subVoicePresets,
            state.WorkspaceState,
            FilterNavigation(state.Navigation, project));
    }

    private static ProjectPresentationNavigationStateV4 FilterNavigation(
        ProjectPresentationNavigationStateV4? state,
        MidoraProject project)
    {
        ProjectPresentationNavigationStateV4 source =
            state ?? ProjectPresentationNavigationStateV4.Empty;
        HashSet<MidoraId> segmentIds = project.Tracks
            .SelectMany(track => track.Segments.Select(segment => segment.Id))
            .Concat(project.PureMidiTracks.SelectMany(track => track.Segments.Select(segment => segment.Id)))
            .ToHashSet();
        HashSet<MidoraId> instrumentIds = project.EventInstruments.Select(item => item.Id).ToHashSet();

        bool IsValidKey(ProjectPresentationNavigationKeyV4 key)
        {
            if (!Enum.IsDefined(key.Kind)) return false;
            bool objectKind = key.Kind is ProjectPresentationNavigationKindV4.SegmentEditor
                or ProjectPresentationNavigationKindV4.EventInstrumentEditor;
            if (!objectKind) return key.ObjectId is null;
            return key.ObjectId is MidoraId id && id != default && (key.Kind switch
            {
                ProjectPresentationNavigationKindV4.SegmentEditor => segmentIds.Contains(id),
                ProjectPresentationNavigationKindV4.EventInstrumentEditor => instrumentIds.Contains(id),
                _ => false
            });
        }

        ProjectPresentationNavigationKeyV4 arrangement =
            new(ProjectPresentationNavigationKindV4.Arrangement);
        List<ProjectPresentationNavigationKeyV4> tabs = [];
        foreach (ProjectPresentationNavigationKeyV4 key in source.Tabs ?? [])
        {
            if (IsValidKey(key) && !tabs.Contains(key)) tabs.Add(key);
        }
        if (!tabs.Contains(arrangement)) tabs.Insert(0, arrangement);
        else if (tabs[0] != arrangement)
        {
            tabs.Remove(arrangement);
            tabs.Insert(0, arrangement);
        }

        ProjectPresentationNavigationKeyV4 active =
            IsValidKey(source.ActiveTab) && tabs.Contains(source.ActiveTab)
                ? source.ActiveTab : arrangement;
        HashSet<ProjectPresentationNavigationKeyV4> tabSet = tabs.ToHashSet();
        List<ProjectPresentationNavigationViewV4> views = [];
        foreach (ProjectPresentationNavigationViewV4 view in source.Views ?? [])
        {
            if (!tabSet.Contains(view.Key) || views.Any(item => item.Key == view.Key)) continue;
            if (view.SecondaryId is { } secondary)
            {
                if (view.Key.Kind != ProjectPresentationNavigationKindV4.EventInstrumentEditor
                    || view.Key.ObjectId is not MidoraId instrumentId
                    || project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                        ?.SubVoices.Any(item => item.Id == secondary) != true)
                    continue;
            }
            views.Add(view);
        }
        return new(tabs, active, views);
    }
}

public sealed record ProjectPresentationSaveSnapshotV3(
    ProjectPresentationStateV3 State,
    long Revision);
