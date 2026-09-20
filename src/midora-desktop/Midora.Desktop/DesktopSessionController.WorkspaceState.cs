using Midora.Domain;
using Midora.Application;
using Midora.Persistence;
using System.Windows.Threading;

namespace Midora.Desktop;

public sealed partial class DesktopSessionController
{
    internal WorkspaceStateRegistry EditorStates { get; private set; } = new();
    private Action<IReadOnlyDictionary<MidoraId, MidoraId>>? _editorCloneSubscription;
    private Action<IReadOnlyList<SegmentIdentityTransfer>, bool>? _editorTransferSubscription;

    internal void ResetPianoEditorPreferences()
    {
        if (Project is not { } project) return;
        foreach (var pair in EditorStates.Freeze().Profiles)
            EditorStates.SetProfile(pair.Key, TrackEditorProfile.Default(project.TicksPerQuarterNote,
                pair.Key.Kind == EditorStateOwnerKind.SubVoice),
                EditorProfileFields.Piano | EditorProfileFields.Event | EditorProfileFields.Zoom);
    }

    private void StartEditorStateSession(ProjectContext context)
    {
        EditorStates.Dispose();
        var registry = EditorStates = new();
        registry.CapacityExceeded += () => SetStatusMessage(
            "Editor view memory limit reached. Existing remembered views are retained; additional view state cannot be remembered in this session. Music editing and saving are unaffected.");
        _editorCloneSubscription = map =>
        {
            void Apply()
            {
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(EditorStates, registry) || registry.IsDisposed) return;
                registry.CopyProfiles(map);
            }
            if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess()) _uiDispatcher.BeginInvoke((Action)Apply);
            else Apply();
        };
        context.Document.PresentationObjectsCloned += _editorCloneSubscription;
        _editorTransferSubscription = (transfers, undo) =>
        {
            void Apply()
            {
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(EditorStates, registry) || registry.IsDisposed) return;
                foreach (var transfer in undo ? transfers.Reverse() : transfers)
                {
                    var source = undo ? transfer.TargetId : transfer.SourceId;
                    var target = undo ? transfer.SourceId : transfer.TargetId;
                    foreach (var workspace in Workspaces)
                        if (workspace is TimelineWorkspaceViewModel { IsSegment: true } && workspace.ObjectId == source)
                            workspace.CaptureLaneViewState();
                    registry.TransferSegmentView(source, target);
                }
            }
            // Transfer identities before the Render-priority content refresh prunes deleted owners.
            if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess())
                _uiDispatcher.BeginInvoke(DispatcherPriority.Send, (Action)Apply);
            else Apply();
        };
        context.Document.SegmentIdentitiesTransferred += _editorTransferSubscription;
    }

    private void ReconcileEditorStates()
    {
        if (Project is not { } project) return;
        var presentation = Persistence!.Presentation;
        var onion = presentation.Current;
        if (EditorStates.EntryCount == 0 && onion.TrackOnionPresets.Count == 0 && onion.SubVoiceOnionPresets.Count == 0) return;
        // Only metadata identities are inspected, never Note/Event collections or selection.
        var tracks = project.Tracks.Select(x => x.Id).Concat(project.PureMidiTracks.Select(x => x.Id)).ToHashSet();
        var voices = project.EventInstruments.SelectMany(i => i.SubVoices.Select(v =>
            new EditorStateOwner(EditorStateOwnerKind.SubVoice, v.Id, i.Id))).ToHashSet();
        EditorStates.Prune(key => key.Kind switch
        {
            EditorStateOwnerKind.Track => tracks.Contains(key.Id),
            EditorStateOwnerKind.Segment => ProjectSegmentIndex.FindLogical(project, key.Id) is not null
                || ProjectSegmentIndex.FindMidi(project, key.Id) is not null,
            EditorStateOwnerKind.SubVoice => voices.Contains(key),
            _ => false
        });
        // Deleting a target releases its own preferences. A surviving target's
        // dormant source IDs stay intact, so Undo can restore that source.
        var trackPresets = onion.TrackOnionPresets.Where(x => tracks.Contains(x.TargetTrackId)).ToArray();
        var voicePresets = onion.SubVoiceOnionPresets.Where(x => voices.Contains(
            new(EditorStateOwnerKind.SubVoice, x.TargetSubVoiceId, x.EventInstrumentId))).ToArray();
        if (trackPresets.Length != onion.TrackOnionPresets.Count || voicePresets.Length != onion.SubVoiceOnionPresets.Count)
            presentation.Replace(onion with { TrackOnionPresets = trackPresets, SubVoiceOnionPresets = voicePresets });
    }

    private ProjectPresentationMonitoringV4 CaptureMonitoringPresentation()
    {
        return new(
            _mutedTrackIds.OrderBy(id => id).ToArray(),
            _soloTrackIds.OrderBy(id => id).ToArray(),
            _mutedSharedGroupIds.OrderBy(id => id).ToArray(),
            _soloSharedGroupIds.OrderBy(id => id).ToArray());
    }

    /// <summary>
    /// Publishes the bounded, presentation-only workspace snapshot immediately
    /// before a save. This keeps the hot registry independent from Project
    /// commands while ensuring the file contains the latest UI state.
    /// </summary>
    private void CaptureWorkspacePresentationForSave()
    {
        if (Project is null || Persistence is null || EditorStates.IsDisposed) return;
        Persistence.Presentation.ReplaceWorkspace(
            EditorStates.CapturePresentationState(CaptureMonitoringPresentation()));
    }

    private void RestoreWorkspacePresentation(ProjectContext context)
    {
        ProjectPresentationWorkspaceStateV4 state =
            context.Persistence.Presentation.Current.WorkspaceState
            ?? ProjectPresentationWorkspaceStateV4.Empty;
        _ = EditorStates.TryRestorePresentationState(state);

        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        if (Project is not { } project) return;

        HashSet<MidoraId> trackIds = project.ArrangementTracks
            .Select(track => track.TrackId).ToHashSet();
        HashSet<MidoraId> groupIds = project.EventInstrumentUsages.Select(item => item.Id)
            .Concat(project.MidiChannelRoots.Select(item => item.Id)).ToHashSet();
        ProjectPresentationMonitoringV4 monitoring = state.Monitoring;
        _mutedTrackIds.UnionWith(monitoring.MutedTrackIds.Where(trackIds.Contains));
        _soloTrackIds.UnionWith(monitoring.SoloTrackIds.Where(trackIds.Contains));
        _mutedSharedGroupIds.UnionWith(monitoring.MutedGroupIds.Where(groupIds.Contains));
        _soloSharedGroupIds.UnionWith(monitoring.SoloGroupIds.Where(groupIds.Contains));

        if (context.Playback is not { } playback) return;
        foreach (MidoraId trackId in _mutedTrackIds) playback.SetTrackMuted(trackId, true);
        foreach (MidoraId trackId in _soloTrackIds) playback.SetTrackSolo(trackId, true);
        foreach (MidoraId groupId in _mutedSharedGroupIds) playback.SetSharedGroupMuted(groupId, true);
        foreach (MidoraId groupId in _soloSharedGroupIds) playback.SetSharedGroupSolo(groupId, true);
    }
}
