using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

public sealed class AllTracksWorkspaceViewModel : WorkspaceViewModel, IPlaybackTimelineWorkspace
{
    private static readonly SemaphoreSlim BuildGate = new(1);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Action<ProjectPresentationAllTracksModeV3>? _setMode;
    private readonly string _identity = Guid.NewGuid().ToString("N");
    private CancellationTokenSource _buildCancellation = new();
    private BuildProgressDispatch? _progressDispatch;
    private CompiledOnionNoteIndex? _compiled;
    private CanonicalCompiledResult? _requested;
    private CanonicalCompiledResult? _candidate;
    private string? _buildError;
    private string _buildProgress = "Preparing read-only notes…";
    private bool _isBuilding;
    private bool _current;
    private string _status = "Raw · Read-only";
    private long _generation;
    private long _startTick;
    private long _tickSpan = 3072;
    private long _extent = 3072;
    private long _rawExtent = 3072;
    private long? _playbackCursorTick;
    private int _firstLane = 48;
    private double _laneHeight = 15;
    private IReadOnlyList<TimelineOnionTrack> _tracks = [];
    private HashSet<MidoraId> _rawMidiTrackIds = [];
    private IReadOnlyList<TimelineOnionTrack>? _publishedTracks;
    private IReadOnlyList<(MidoraId Id, uint Color)> _compiledTrackStyles = [];
    private CompiledOnionNoteIndex? _publishedCompiled;
    private ProjectPresentationAllTracksModeV3? _publishedMode;
    private ProjectPresentationAllTracksModeV3 _mode;
    public AllTracksWorkspaceViewModel(Action<ProjectPresentationAllTracksModeV3> setMode)
        : base(WorkspaceKey.ForType(WorkspaceKind.AllTracks), "All Tracks") { _setMode = setMode; }
    public ProjectPresentationAllTracksModeV3[] Modes { get; } = Enum.GetValues<ProjectPresentationAllTracksModeV3>();
    public ProjectPresentationAllTracksModeV3 Mode
    {
        get => _mode;
        set { if (!IsDisposed && Set(ref _mode, value)) { _setMode?.Invoke(value); ModeChanged(); } }
    }
    internal void SetMode(ProjectPresentationAllTracksModeV3 value)
    { if (!IsDisposed && Set(ref _mode, value, nameof(Mode))) ModeChanged(); }
    private void ModeChanged()
    {
        if (Mode == ProjectPresentationAllTracksModeV3.Raw) CancelIndexBuild();
        Publish();
        if (Mode == ProjectPresentationAllTracksModeV3.Compiled) UpdateCompilation(_candidate, _current);
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsBuilding { get => _isBuilding; private set => Set(ref _isBuilding, value); }
    public bool IsStale => Mode == ProjectPresentationAllTracksModeV3.Compiled
        && (!_current || _compiled is null || _compiled.Fingerprint != _requested?.Fingerprint);
    public TimelineEditorSettings EditorSettings { get; } = new();
    public TimelineRenderSnapshot Snapshot { get; } = new(0, "all-tracks-readonly", [],
        Enumerable.Range(0, 128).Select(i => TimelineWorkspaceViewModel.MidiNoteName(127 - i)).ToArray());
    public long StartTick { get => _startTick; set => Set(ref _startTick, Math.Max(0, value)); }
    public long? PlaybackCursorTick { get => _playbackCursorTick; private set => Set(ref _playbackCursorTick, value); }
    public void UpdatePlaybackCursor(MidoraProject project, long projectTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (IsDisposed) return;
        PlaybackCursorTick = Math.Max(0, projectTick);
    }
    public long TickSpan { get => _tickSpan; set => Set(ref _tickSpan, Math.Clamp(value, 16, 1L << 50)); }
    public long ExtentEndTick { get => _extent; private set => Set(ref _extent, Math.Max(1, value)); }
    public int FirstLane { get => _firstLane; set => Set(ref _firstLane, Math.Clamp(value, 0, 127)); }
    public double LaneHeight { get => _laneHeight; set => Set(ref _laneHeight, Math.Clamp(value, 3, 128)); }
    public override void Rebuild(MidoraProject project, long revision)
    {
        if (IsDisposed || IsPresentationSuspended) return;
        EditorSettings.ConfigureProject(project, 0);
        _tracks = OnionPresentation.CaptureTracks(project);
        _rawMidiTrackIds = project.PureMidiTracks.Select(t => t.Id).ToHashSet();
        _rawExtent = Math.Max(project.TicksPerQuarterNote * 16L,
            _tracks.SelectMany(t => t.Clips).Select(c => c.EndTick + c.Shift).DefaultIfEmpty(0).Max());
        Publish();
    }
    public void UpdateCompilation(CanonicalCompiledResult? result, bool isCurrent)
    {
        if (IsDisposed || IsPresentationSuspended) return;
        _candidate = result;
        _current = isCurrent && result is not null;
        if (Mode != ProjectPresentationAllTracksModeV3.Compiled) return;
        if (result is not null && _compiled?.Fingerprint == result.Fingerprint && !IsBuilding)
        { _requested = result; _buildError = null; Publish(); return; }
        if (result is null || ReferenceEquals(result, _requested)) { Publish(); return; }
        if (_requested?.Fingerprint == result.Fingerprint
            && (IsBuilding || _compiled?.Fingerprint == result.Fingerprint))
        { _requested = result; Publish(); return; } // Recompiling identical content must not restart an expensive index.
        CancelIndexBuild();
        _requested = result;
        _buildError = null;
        _buildProgress = "Preparing read-only notes…";
        _buildCancellation.Dispose(); _buildCancellation = new();
        long generation = _generation;
        var token = _buildCancellation.Token;
        var owner = new WeakReference<AllTracksWorkspaceViewModel>(this);
        var progressDispatch = new BuildProgressDispatch(owner, _dispatcher, generation, token);
        _progressDispatch = progressDispatch;
        IsBuilding = true;
        Publish();
        if (CanPublishBuild(generation, token))
            _ = BuildAsync(owner, _dispatcher, result, generation, token, progressDispatch);
    }
    public void CancelBuild()
    {
        if (IsDisposed) return;
        var requested = _requested;
        CancelIndexBuild();
        // An explicit cancellation stays canceled until Refresh or a new result;
        // routine session notifications must not immediately restart this work.
        _requested = requested;
        _buildError = "Preparation canceled. Use Refresh to retry."; Publish();
    }
    public void RetryBuild()
    { if (!IsDisposed) { _requested = null; UpdateCompilation(_candidate, _current); } }
    private void CancelIndexBuild()
    {
        ++_generation;
        _buildCancellation.Cancel();
        _progressDispatch?.Dispose(); _progressDispatch = null;
        _requested = null;
        IsBuilding = false;
    }
    private static async Task BuildAsync(WeakReference<AllTracksWorkspaceViewModel> owner,
        Dispatcher dispatcher, CanonicalCompiledResult result, long generation, CancellationToken token,
        BuildProgressDispatch progressDispatch)
    {
        CompiledOnionNoteIndex? next = null;
        try
        {
            await BuildGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                next = await Task.Run(() => CompiledOnionNoteIndex.Build(result, token,
                    progress: progressDispatch.Report, scope: CompiledOnionNoteScope.LogicalTracks), token).ConfigureAwait(false);
            }
            finally { BuildGate.Release(); }
            await dispatcher.InvokeAsync(() =>
            {
                if (!owner.TryGetTarget(out var workspace) || !workspace.CanPublishBuild(generation, token)) return;
                var accepted = next!; next = null;
                workspace.AcceptCompiledIndex(accepted);
            }, DispatcherPriority.Background, token).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!dispatcher.HasShutdownStarted && !token.IsCancellationRequested)
                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (owner.TryGetTarget(out var workspace) && workspace.CanPublishBuild(generation, token))
                        { workspace.IsBuilding = false; workspace._buildError = error.Message; workspace.Publish(); }
                    }, DispatcherPriority.Background, token).Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { } // Dispatcher shutdown, not a Project failure.
        }
        finally { progressDispatch.Dispose(); next?.Dispose(); }
    }
    private bool CanPublishBuild(long generation, CancellationToken token)
        => !IsDisposed && !IsPresentationSuspended && !token.IsCancellationRequested && generation == _generation;
    private void AcceptCompiledIndex(CompiledOnionNoteIndex next)
    {
        var previous = _compiled; _compiled = next;
        try
        {
            var known = _tracks.Select(t => (t.Id, t.Color)).ToList();
            var included = known.Select(t => t.Id).ToHashSet();
            var compiledIds = next.TrackIds.ToHashSet();
            // Deleted Logical sources remain part of the last successful canonical result.
            foreach (var style in _compiledTrackStyles)
                if (compiledIds.Contains(style.Id) && included.Add(style.Id)) known.Add(style);
            foreach (var id in compiledIds.OrderBy(id => id))
                if (included.Add(id)) known.Add((id, 0xff7c8597));
            _compiledTrackStyles = known;
            IsBuilding = false;
            Publish();
        }
        finally { previous?.Dispose(); }
    }
    private void Publish()
    {
        if (IsDisposed || IsPresentationSuspended) return;
        Raise(nameof(IsStale));
        bool projectionChanged = !ReferenceEquals(_publishedTracks, _tracks)
            || !ReferenceEquals(_publishedCompiled, _compiled) || _publishedMode != Mode;
        _publishedTracks = _tracks; _publishedCompiled = _compiled; _publishedMode = Mode;
        if (Mode == ProjectPresentationAllTracksModeV3.Raw)
        {
            ExtentEndTick = _rawExtent;
            if (projectionChanged) OnionSnapshot = new(_identity + ":tracks", _tracks, .8);
            Status = "Raw · Read-only"; return;
        }
        if (projectionChanged)
        {
            List<TimelineOnionTrack> displayed = [];
            foreach (var track in _tracks)
            {
                if (_rawMidiTrackIds.Contains(track.Id)) displayed.Add(track);
                else if (_compiled is not null) displayed.Add(ExpandedTrack(track.Id, track.Color));
            }
            var seen = _tracks.Select(t => t.Id).ToHashSet();
            foreach (var style in _compiledTrackStyles)
                if (_compiled is not null && seen.Add(style.Id) && _compiled.TrackIds.Contains(style.Id))
                    displayed.Add(ExpandedTrack(style.Id, style.Color));
            OnionSnapshot = new(_identity + ":tracks", displayed, .8);

            TimelineOnionTrack ExpandedTrack(MidoraId id, uint color) => new(id, color,
                [new(new CompiledOnionItemSource(_compiled!, id), 0, _compiled!.GetTrackEndTick(id), 0)]);
        }
        if (_compiled is null)
        { ExtentEndTick = _rawExtent; Status = _buildError is not null ? $"Compiled · MIDI source / Logical unavailable · {_buildError}"
            : IsBuilding ? $"Compiled · MIDI source / Logical: {_buildProgress}" : "Compiled · MIDI source / No successful logical compilation"; return; }
        ExtentEndTick = Math.Max(_rawExtent, _compiled.EndTick);
        Status = _buildError is not null ? $"Compiled · MIDI source / Logical stale · {_buildError}"
            : IsBuilding ? $"Compiled · MIDI source / Logical stale · {_buildProgress}"
            : !IsStale ? "Compiled · Current · MIDI source + logical expansion" : "Compiled · MIDI source / Logical stale · Last successful result";
    }
    public override void CancelBackgroundPresentationWork()
    {
        if (IsDisposed) return;
        base.CancelBackgroundPresentationWork();
        CancelIndexBuild();
        ClearSourceProjection();
    }
    private void ClearSourceProjection()
    {
        OnionSnapshot = null;
        _tracks = []; _rawMidiTrackIds = [];
        _publishedTracks = null; _publishedCompiled = null; _publishedMode = null;
        _requested = null; _candidate = null;
    }
    protected override void DisposeCore()
    {
        CancelIndexBuild();
        ClearSourceProjection();
        _buildCancellation.Dispose();
        _compiled?.Dispose(); _compiled = null;
        _compiledTrackStyles = []; _setMode = null;
        base.DisposeCore();
    }

    /// <summary>One revocable Dispatcher operation per build, with no strong Workspace ownership.</summary>
    private sealed class BuildProgressDispatch(WeakReference<AllTracksWorkspaceViewModel> owner,
        Dispatcher dispatcher, long generation, CancellationToken token) : IDisposable
    {
        private readonly object _sync = new();
        private DispatcherOperation? _pending;
        private CompiledOnionBuildProgress _latest;
        private bool _disposed;
        public void Report(CompiledOnionBuildProgress progress)
        {
            lock (_sync)
            {
                if (_disposed || token.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
                _latest = progress;
                if (_pending is not null) return;
                _pending = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
            }
        }
        private void Apply()
        {
            CompiledOnionBuildProgress progress;
            lock (_sync)
            {
                _pending = null;
                if (_disposed) return;
                progress = _latest;
            }
            if (!owner.TryGetTarget(out var workspace) || !workspace.CanPublishBuild(generation, token)) return;
            workspace._buildProgress = progress.Total > 0
                ? $"{progress.Stage} · {Math.Clamp(progress.Completed * 100d / progress.Total, 0, 100):F0}%" : progress.Stage;
            workspace.Publish();
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _pending?.Abort(); _pending = null;
            }
        }
    }
}
