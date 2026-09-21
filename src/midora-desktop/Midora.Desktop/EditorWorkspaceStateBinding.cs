using System.ComponentModel;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>Disposable UI adapter. Only this side of the boundary may reference a VM.</summary>
internal sealed class EditorWorkspaceStateBinding : IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly WorkspaceStateRegistry _registry;
    private TimelineEditorSettings? _piano, _events;
    private EditorStateOwner? _profileOwner, _localOwner;
    private int _applying;
    private bool _disposed;
    private IDisposable? _subscription;
    internal EditorWorkspaceStateBinding(WorkspaceViewModel workspace, WorkspaceStateRegistry registry)
    {
        _workspace = workspace; _registry = registry;
        workspace.PropertyChanged += OnWorkspaceChanged;
        workspace.ObjectList.PropertyChanged += OnListChanged;
    }
    internal EditorStateOwner? LocalOwner => _localOwner;
    internal EditorStateOwner? ProfileOwner => _profileOwner;

    internal IDisposable BeginRebuild()
    {
        _workspace.CaptureLaneViewState();
        ++_applying;
        return new RebuildScope(this);
    }
    private sealed class RebuildScope(EditorWorkspaceStateBinding binding) : IDisposable
    {
        public void Dispose() { --binding._applying; }
    }

    internal void PrepareSegment(MidoraProject project, MidoraId trackId)
    {
        if (_workspace.ObjectId is not { } segmentId) return;
        Bind(new(EditorStateOwnerKind.Track, trackId), new(EditorStateOwnerKind.Segment, segmentId), project.TicksPerQuarterNote);
    }
    internal void PrepareSubVoice(MidoraProject project, MidoraId? voiceId)
    {
        if (voiceId is null)
        {
            _workspace.CaptureLaneViewState();
            Suspend();
            _profileOwner = _localOwner = null; WireSettings(); return;
        }
        var key = new EditorStateOwner(EditorStateOwnerKind.SubVoice, voiceId.Value, _workspace.ObjectId!.Value);
        Bind(key, key, project.TicksPerQuarterNote);
    }
    private void Bind(EditorStateOwner profile, EditorStateOwner local, int tpqn)
    {
        WireSettings();
        bool changedOwner = _localOwner != local;
        bool changedProfile = _profileOwner != profile;
        if (!changedOwner && !changedProfile) { ApplyLatestProfile(); return; }
        _workspace.CaptureLaneViewState();
        if (changedProfile) Suspend();
        _profileOwner = profile; _localOwner = local;
        if (!_registry.TryGetProfile(profile, out var value))
        {
            var defaults = TrackEditorProfile.Default(tpqn, profile.Kind == EditorStateOwnerKind.SubVoice);
            _registry.SetProfile(profile, defaults);
            value = new(0, defaults);
        }
        ApplyProfile(value.Value, EditorProfileFields.All);
        Listen();
        if (changedOwner)
        {
            if (_workspace is TimelineWorkspaceViewModel timeline)
                timeline.RestoreExplicitMidiLaneTargets(_registry.GetLanes(local).ExplicitMidiTargets);
            if (!_registry.TryGetView(local, out var position))
                position = new(0, local.Kind == EditorStateOwnerKind.SubVoice ? PianoEditorDefaults.SubVoiceFirstLane : PianoEditorDefaults.SegmentFirstLane, 0);
            ApplyLocal(position);
            _registry.SetView(local, position);
        }
    }

    private void WireSettings()
    {
        var piano = _workspace is TimelineWorkspaceViewModel t ? t.EditorSettings : ((InstrumentWorkspaceViewModel)_workspace).EditorSettings;
        var events = _workspace is TimelineWorkspaceViewModel timeline ? timeline.LaneEditorSettings : ((InstrumentWorkspaceViewModel)_workspace).EventLaneEditorSettings;
        if (!ReferenceEquals(_piano, piano))
        {
            if (_piano is not null) _piano.PropertyChanged -= OnPianoChanged;
            _piano = piano; _piano.PropertyChanged += OnPianoChanged;
        }
        if (!ReferenceEquals(_events, events))
        {
            if (_events is not null) _events.PropertyChanged -= OnEventChanged;
            _events = events; _events.PropertyChanged += OnEventChanged;
        }
    }
    private static bool IsPreference(string? name) => name is nameof(TimelineEditorSettings.OperationSubdivision)
        or nameof(TimelineEditorSettings.SnapEnabled) or nameof(TimelineEditorSettings.GridVisible)
        or nameof(TimelineEditorSettings.DefaultLengthTicks) or nameof(TimelineEditorSettings.DefaultVelocity);
    private void OnPianoChanged(object? sender, PropertyChangedEventArgs e)
    { if (IsPreference(e.PropertyName)) PublishProfile(EditorProfileFields.Piano); }
    private void OnEventChanged(object? sender, PropertyChangedEventArgs e)
    { if (IsPreference(e.PropertyName)) PublishProfile(EditorProfileFields.Event); }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying != 0 || _disposed || _workspace.IsDisposed) return;
        var fields = e.PropertyName switch
        {
            nameof(TimelineWorkspaceViewModel.TickSpan) or nameof(TimelineWorkspaceViewModel.LaneHeight)
                or nameof(InstrumentWorkspaceViewModel.TimelineTickSpan) or nameof(InstrumentWorkspaceViewModel.TimelineLaneHeight) => EditorProfileFields.Zoom,
            nameof(TimelineWorkspaceViewModel.ToolMode) => EditorProfileFields.Tool,
            nameof(WorkspaceViewModel.ValueTraceShape) => EditorProfileFields.Shape,
            nameof(TimelineWorkspaceViewModel.IsLowerEditorVisible) or nameof(TimelineWorkspaceViewModel.BottomEditorRowHeight) => EditorProfileFields.Lanes,
            _ => EditorProfileFields.None
        };
        if (fields != EditorProfileFields.None) PublishProfile(fields);
        if (e.PropertyName is nameof(TimelineWorkspaceViewModel.StartTick) or nameof(TimelineWorkspaceViewModel.FirstLane)
            or nameof(InstrumentWorkspaceViewModel.TimelineStartTick) or nameof(InstrumentWorkspaceViewModel.TimelineFirstLane)) CaptureLocal();
    }
    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineObjectListState.IsVisible) or nameof(TimelineObjectListState.LayoutWidth)) PublishProfile(EditorProfileFields.List);
        if (e.PropertyName == nameof(TimelineObjectListState.FirstRow)) CaptureLocal();
    }
    private TrackEditorProfile CaptureProfile()
    {
        var piano = EditorSettingsValues.Capture(_piano!);
        var events = EditorSettingsValues.Capture(_events!);
        var list = _workspace.ObjectList;
        if (_workspace is TimelineWorkspaceViewModel t)
            return new(piano, events, t.TickSpan, t.LaneHeight, t.ToolMode, t.ValueTraceShape,
                t.IsLowerEditorVisible, t.LastLowerEditorHeight, list.IsVisible, list.LayoutWidth);
        var v = (InstrumentWorkspaceViewModel)_workspace;
        return new(piano, events, v.TimelineTickSpan, v.TimelineLaneHeight, v.ToolMode, v.ValueTraceShape,
            v.IsLowerEditorVisible, v.LastLowerEditorHeight, list.IsVisible, list.LayoutWidth);
    }
    private void PublishProfile(EditorProfileFields fields)
    {
        if (_applying != 0 || _disposed || _workspace.IsDisposed || _profileOwner is not { } owner) return;
        ++_applying;
        try { _registry.SetProfile(owner, CaptureProfile(), fields); }
        finally { --_applying; }
    }
    private void OnProfileChanged(EditorProfileFields fields)
    {
        if (_applying != 0 || _disposed || _workspace.IsDisposed || _workspace.IsPresentationSuspended || _profileOwner is not { } owner) return;
        if (_registry.TryGetProfile(owner, out var state)) ApplyProfile(state.Value, fields);
    }
    internal void Suspend() { _subscription?.Dispose(); _subscription = null; }
    private void Listen()
    {
        if (!_disposed && !_registry.IsDisposed && !_workspace.IsPresentationSuspended && _subscription is null && _profileOwner is { } owner)
            _subscription = _registry.Listen(owner, OnProfileChanged);
    }
    internal void ApplyLatestProfile()
    {
        if (_disposed || _registry.IsDisposed || _profileOwner is not { } owner) return;
        Listen();
        if (_registry.TryGetProfile(owner, out var state)) ApplyProfile(state.Value, EditorProfileFields.All);
    }
    private void ApplyProfile(TrackEditorProfile state, EditorProfileFields fields)
    {
        ++_applying;
        try
        {
            if ((fields & EditorProfileFields.Piano) != 0) state.Piano.Apply(_piano!);
            if ((fields & EditorProfileFields.Event) != 0) state.Event.Apply(_events!);
            if ((fields & EditorProfileFields.Shape) != 0) _workspace.ValueTraceShape = state.Shape;
            if (_workspace is TimelineWorkspaceViewModel t)
            {
                if ((fields & EditorProfileFields.Zoom) != 0) { t.TickSpan = state.TickSpan; t.LaneHeight = state.KeyHeight; }
                if ((fields & EditorProfileFields.Tool) != 0) t.ToolMode = state.Tool;
                if ((fields & EditorProfileFields.Lanes) != 0) { t.IsLowerEditorVisible = state.LanesVisible; t.RestoreLowerEditorHeight(state.LanesHeight); }
            }
            else if (_workspace is InstrumentWorkspaceViewModel v)
            {
                if ((fields & EditorProfileFields.Zoom) != 0) { v.TimelineTickSpan = state.TickSpan; v.TimelineLaneHeight = state.KeyHeight; }
                if ((fields & EditorProfileFields.Tool) != 0) v.ToolMode = state.Tool;
                if ((fields & EditorProfileFields.Lanes) != 0) { v.IsLowerEditorVisible = state.LanesVisible; v.RestoreLowerEditorHeight(state.LanesHeight); }
            }
            if ((fields & EditorProfileFields.List) != 0)
            { _workspace.ObjectList.LayoutWidth = state.ListWidth; _workspace.ObjectList.IsVisible = state.ListVisible; }
        }
        finally { --_applying; }
    }
    private void ApplyLocal(EditorLocalView state)
    {
        ++_applying;
        try
        {
            if (_workspace is TimelineWorkspaceViewModel t) { t.StartTick = state.StartTick; t.FirstLane = state.FirstLane; }
            else if (_workspace is InstrumentWorkspaceViewModel v) { v.TimelineStartTick = state.StartTick; v.TimelineFirstLane = state.FirstLane; }
            _workspace.ObjectList.FirstRow = state.FirstRow;
        }
        finally { --_applying; }
    }
    private void CaptureLocal()
    {
        if (_applying != 0 || _disposed || _workspace.IsDisposed || _localOwner is not { } owner) return;
        var state = _workspace is TimelineWorkspaceViewModel t
            ? new EditorLocalView(t.StartTick, t.FirstLane, _workspace.ObjectList.FirstRow)
            : new(((InstrumentWorkspaceViewModel)_workspace).TimelineStartTick,
                ((InstrumentWorkspaceViewModel)_workspace).TimelineFirstLane, _workspace.ObjectList.FirstRow);
        _registry.SetView(owner, state);
    }
    internal LaneTabMemory GetLaneMemory() => _localOwner is { } owner ? _registry.GetLanes(owner) : LaneTabMemory.Empty;
    internal void SaveLaneMemory(EditorStateOwner owner, LaneTabMemory memory)
    {
        if (!_disposed && _localOwner == owner)
            _registry.SetLanes(owner, memory with { ExplicitMidiTargets = _registry.GetLanes(owner).ExplicitMidiTargets });
    }
    internal void RememberExplicitMidiTargets(IEnumerable<DirectMidiEventLaneTarget> targets)
    {
        if (_disposed || _localOwner is not { } owner) return;
        _registry.SetLanes(owner, _registry.GetLanes(owner) with
        { ExplicitMidiTargets = [.. targets.OrderBy(x => x.Kind).ThenBy(x => x.Data1)] });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _workspace.CaptureLaneViewState(); CaptureLocal(); _disposed = true;
        _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace.ObjectList.PropertyChanged -= OnListChanged;
        Suspend();
        if (_piano is not null) _piano.PropertyChanged -= OnPianoChanged;
        if (_events is not null) _events.PropertyChanged -= OnEventChanged;
    }
}
