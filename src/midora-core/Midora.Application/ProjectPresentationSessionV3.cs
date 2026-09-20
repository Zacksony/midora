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
        // Save/Save Copy cannot silently discard valid view state.
        return new(state.AllTracksMode, trackPresets, subVoicePresets, state.WorkspaceState);
    }
}

public sealed record ProjectPresentationSaveSnapshotV3(
    ProjectPresentationStateV3 State,
    long Revision);
