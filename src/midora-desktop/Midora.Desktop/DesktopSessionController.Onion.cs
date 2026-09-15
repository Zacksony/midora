using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

public sealed partial class DesktopSessionController
{
    private WorkspaceViewModel[] _onionWorkspaceRoster = [];
    private void PublishOnionWorkspaceRoster()
        => Volatile.Write(ref _onionWorkspaceRoster, Workspaces.ToArray());

    internal sealed record OnionControls(MidoraId TargetId, MidoraId? InstrumentId, bool Enabled, double Opacity,
        IReadOnlyList<MidoraId> Sources, OnionSourceMode SourceMode, MidoraId? Previous, MidoraId? Next);

    internal OnionControls? GetOnionControls(WorkspaceViewModel workspace)
    {
        if (Project is not { } project || Persistence is not { } persistence || !Workspaces.Contains(workspace)) return null;
        var state = persistence.Presentation.Current;
        if (workspace is TimelineWorkspaceViewModel { IsSegment: true }
            && OnionPresentation.Target(project, workspace.ObjectId) is { } target)
        {
            var order = project.TracksInArrangementOrder().Select(t => t.TrackId).ToList();
            int index = order.IndexOf(target.Track);
            var preset = state.TrackOnionPresets.FirstOrDefault(p => p.TargetTrackId == target.Track);
            return new(target.Track, null, preset?.Enabled ?? false, preset?.Opacity ?? .3, preset?.SourceTrackIds ?? [],
                preset?.SourceMode ?? OnionSourceMode.Custom,
                index > 0 ? order[index - 1] : null, index >= 0 && index + 1 < order.Count ? order[index + 1] : null);
        }
        if (workspace is InstrumentWorkspaceViewModel { ActiveSubVoiceId: { } voiceId }
            && project.EventInstruments.FirstOrDefault(i => i.Id == workspace.ObjectId) is { } instrument)
        {
            int index = instrument.SubVoices.FindIndex(v => v.Id == voiceId);
            if (index < 0) return null;
            var preset = state.SubVoiceOnionPresets.FirstOrDefault(p => p.EventInstrumentId == instrument.Id && p.TargetSubVoiceId == voiceId);
            return new(voiceId, instrument.Id, preset?.Enabled ?? false, preset?.Opacity ?? .3, preset?.SourceSubVoiceIds ?? [],
                preset?.SourceMode ?? OnionSourceMode.Custom,
                index > 0 ? instrument.SubVoices[index - 1].Id : null,
                index + 1 < instrument.SubVoices.Count ? instrument.SubVoices[index + 1].Id : null);
        }
        return null;
    }

    internal void SetOnionEnabled(WorkspaceViewModel workspace, bool enabled)
    {
        if (GetOnionControls(workspace) is { } state) ApplyOnionControls(state with { Enabled = enabled });
    }

    internal void ShowAdjacentOnionSource(WorkspaceViewModel workspace, bool previous)
    {
        if (GetOnionControls(workspace) is not { } state
            || (previous ? state.Previous : state.Next) is null) return;
        ApplyOnionControls(state with
        { SourceMode = previous ? OnionSourceMode.Previous : OnionSourceMode.Next, Enabled = true });
    }

    internal void SetOnionOpacity(WorkspaceViewModel workspace, double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (GetOnionControls(workspace) is { } state) ApplyOnionControls(state with { Opacity = opacity });
    }

    internal void SetCustomOnionSources(WorkspaceViewModel workspace, IReadOnlyList<MidoraId> sources)
    {
        if (GetOnionControls(workspace) is { } state)
            ApplyOnionControls(state with { Sources = sources.ToArray(), SourceMode = OnionSourceMode.Custom, Enabled = true });
    }

    private void ApplyOnionControls(OnionControls state)
    {
        if (state.InstrumentId is { } instrumentId)
            SetSubVoiceOnion(new(instrumentId, state.TargetId, state.Enabled, state.Opacity, state.Sources, state.SourceMode));
        else SetTrackOnion(new(state.TargetId, state.Enabled, state.Opacity, state.Sources, state.SourceMode));
    }

    private static IReadOnlyList<MidoraId> ResolveOnionSources(OnionSourceMode mode, MidoraId target,
        IReadOnlyList<MidoraId> customSources, IEnumerable<MidoraId> formalOrder)
    {
        if (mode == OnionSourceMode.Custom) return customSources;
        MidoraId? previous = null;
        bool foundTarget = false;
        foreach (MidoraId id in formalOrder)
        {
            if (foundTarget) return [id];
            if (id == target)
            {
                if (mode == OnionSourceMode.Previous) return previous is { } source ? [source] : [];
                foundTarget = true;
            }
            previous = id;
        }
        return [];
    }

    private string _onionIdentity = Guid.NewGuid().ToString("N");
    public AllTracksWorkspaceViewModel OpenAllTracks()
    {
        if (Project is null || Persistence is null) throw new InvalidOperationException("Open a Project first.");
        var workspace = (AllTracksWorkspaceViewModel)GetOrCreate(WorkspaceKey.ForType(WorkspaceKind.AllTracks),
            () => new AllTracksWorkspaceViewModel(mode =>
            {
                if (Persistence is { } persistence)
                    persistence.Presentation.Replace(persistence.Presentation.Current with { AllTracksMode = mode });
            }));
        RefreshOnionPresentations();
        ActiveWorkspace = workspace;
        return workspace;
    }

    public void SetTrackOnion(TrackOnionPresetV3 preset)
    {
        if (Persistence is not { } persistence) return;
        var state = persistence.Presentation.Current;
        persistence.Presentation.Replace(state with
        { TrackOnionPresets = state.TrackOnionPresets.Where(p => p.TargetTrackId != preset.TargetTrackId).Append(preset).ToArray() });
    }
    public void SetSubVoiceOnion(SubVoiceOnionPresetV3 preset)
    {
        if (Persistence is not { } persistence) return;
        var state = persistence.Presentation.Current;
        persistence.Presentation.Replace(state with
        { SubVoiceOnionPresets = state.SubVoiceOnionPresets.Where(p => p.TargetSubVoiceId != preset.TargetSubVoiceId
            || p.EventInstrumentId != preset.EventInstrumentId).Append(preset).ToArray() });
    }
    private void OnPresentationChanged(object? sender, EventArgs e)
    {
        if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess())
            _uiDispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(sender, Persistence?.Presentation)) RefreshOnionPresentations(); }));
        else RefreshOnionPresentations();
    }
    private void RefreshOnionPresentations()
    {
        // A background compile may already be notifying while Close detaches
        // _context. Hold one coherent context, not several nullable re-reads.
        var context = _context;
        if (context is null) return;
        var project = context.Compilation.Project;
        var presentation = context.Persistence.Presentation;
        var state = presentation.Current;
        foreach (var workspace in Volatile.Read(ref _onionWorkspaceRoster))
        {
            if (!ReferenceEquals(context, _context)) return;
            if (!Workspaces.Contains(workspace)) continue;
            if (workspace.IsDisposed || workspace.IsPresentationSuspended)
            {
                workspace.LastOnionRevision = (-1, -1, null);
                continue;
            }
            var stamp = (context.Document.PublicationRevision, presentation.Revision,
                workspace is InstrumentWorkspaceViewModel owner ? owner.ActiveSubVoiceId : null);
            bool changed = workspace.LastOnionRevision != stamp;
            workspace.LastOnionRevision = stamp;
            if (workspace is AllTracksWorkspaceViewModel all)
            {
                if (changed) all.Rebuild(project, _revision);
                all.UpdatePlaybackCursor(project, CurrentTick);
                all.SetMode(state.AllTracksMode);
                all.UpdateCompilation(context.Compilation.LastSuccessfulResult, context.Compilation.IsCompilationCurrent);
            }
            else if (changed && workspace is TimelineWorkspaceViewModel { IsSegment: true }
                && OnionPresentation.Target(project, workspace.ObjectId) is { } target)
            {
                var preset = state.TrackOnionPresets.FirstOrDefault(p => p.TargetTrackId == target.Track);
                workspace.CanConfigureOnion = true;
                workspace.IsOnionEnabled = preset?.Enabled == true;
                if (workspace.IsDisposed || workspace.IsPresentationSuspended || workspace.LastOnionRevision != stamp
                    || !ReferenceEquals(context, _context)) continue;
                var sources = preset is { Enabled: true, Opacity: > 0 }
                    ? ResolveOnionSources(preset.SourceMode, target.Track, preset.SourceTrackIds,
                        project.TracksInArrangementOrder().Select(t => t.TrackId))
                        .Where(id => id != target.Track).ToHashSet() : [];
                var tracks = sources.Count > 0 ? OnionPresentation.CaptureTracks(project, sources, target.Offset) : [];
                if (workspace.IsDisposed || workspace.IsPresentationSuspended || workspace.LastOnionRevision != stamp
                    || !ReferenceEquals(context, _context)) continue;
                workspace.OnionSnapshot = tracks.Count > 0
                    ? new(_onionIdentity + ":" + workspace.Key, tracks, preset!.Opacity) : null;
            }
            else if (changed && workspace is InstrumentWorkspaceViewModel instrument
                && project.EventInstruments.FirstOrDefault(i => i.Id == instrument.ObjectId) is { } definition)
            {
                var preset = state.SubVoiceOnionPresets.FirstOrDefault(p => p.EventInstrumentId == definition.Id
                    && p.TargetSubVoiceId == instrument.ActiveSubVoiceId);
                workspace.CanConfigureOnion = instrument.ActiveSubVoiceId is { } active && definition.SubVoices.Any(v => v.Id == active);
                workspace.IsOnionEnabled = preset?.Enabled == true;
                if (workspace.IsDisposed || workspace.IsPresentationSuspended || workspace.LastOnionRevision != stamp
                    || !ReferenceEquals(context, _context)) continue;
                var sources = preset is { Enabled: true, Opacity: > 0 }
                    ? ResolveOnionSources(preset.SourceMode, preset.TargetSubVoiceId, preset.SourceSubVoiceIds,
                        definition.SubVoices.Select(v => v.Id)).ToHashSet() : [];
                var tracks = sources.Count > 0
                    ? definition.SubVoices.Select((voice, order) => (voice, order))
                        .Where(p => p.voice.Id != preset!.TargetSubVoiceId && sources.Contains(p.voice.Id))
                        .Select(p => new TimelineOnionTrack(p.voice.Id, OnionPresentation.VoiceColors[p.order % OnionPresentation.VoiceColors.Length],
                            [new(new PagedTemplateNoteTimelineItemSource(p.voice, TemplateNoteTimelineProjection.Notes),
                                0, definition.TemplateLengthTicks, 0)])).ToArray() : [];
                if (workspace.IsDisposed || workspace.IsPresentationSuspended || workspace.LastOnionRevision != stamp
                    || !ReferenceEquals(context, _context)) continue;
                workspace.OnionSnapshot = tracks.Length > 0
                    ? new(_onionIdentity + ":" + workspace.Key + ":" + preset!.TargetSubVoiceId, tracks, preset.Opacity) : null;
            }
        }
    }
}
