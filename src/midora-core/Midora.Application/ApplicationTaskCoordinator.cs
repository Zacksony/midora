using Midora.AudioRender;
using Midora.Compiler;
using Midora.Domain;
using Midora.MidiExport;
using Midora.Playback;

namespace Midora.Application;

public enum ApplicationTaskKind
{
    None,
    MainPlayback,
    SegmentPreview,
    EventInstrumentPreview,
    SubVoicePreview,
    ExplicitCompile,
    NewProject,
    OpenProject,
    SaveProject,
    SaveCopy,
    CloseProject,
    Exit,
    MidiExport,
    AudioRender
}

public enum ApplicationTaskPhase
{
    Idle,
    Preparing,
    Running,
    Buffering,
    Stopping,
    Cancelling,
    Finalizing,
    Error
}

public enum ApplicationLockLevel
{
    Normal,
    ProjectEdit,
    MainWindowModal,
    FullApplication
}

public enum ApplicationTaskOutcome
{
    Completed,
    Cancelled,
    Failed,
    RejectedBusy,
    RequiresPlaybackCleanupConfirmation
}

public sealed class ApplicationTaskExecution<T>
{
    internal ApplicationTaskExecution(
        ApplicationTaskKind taskKind,
        ApplicationTaskOutcome outcome,
        T? value,
        Exception? error,
        Exception? playbackCleanupError,
        PlaybackCleanupContinuation? playbackCleanupContinuation)
    {
        TaskKind = taskKind;
        Outcome = outcome;
        Value = value;
        Error = error;
        PlaybackCleanupError = playbackCleanupError;
        PlaybackCleanupContinuation = playbackCleanupContinuation;
    }

    public ApplicationTaskKind TaskKind { get; }
    public ApplicationTaskOutcome Outcome { get; }
    public T? Value { get; }
    public Exception? Error { get; }
    public Exception? PlaybackCleanupError { get; }
    public PlaybackCleanupContinuation? PlaybackCleanupContinuation { get; }
    public bool Succeeded => Outcome == ApplicationTaskOutcome.Completed;
}

public readonly record struct SegmentNotePlacementPreviewCompletion(
    HeldPreviewGateEndReport? GateEndReport,
    Exception? PreviewError);

public sealed class PlaybackCleanupContinuation
{
    private readonly ApplicationTaskCoordinator _owner;
    private int _consumed;

    internal PlaybackCleanupContinuation(
        ApplicationTaskCoordinator owner,
        ApplicationTaskKind taskKind,
        Exception cleanupError)
    {
        _owner = owner;
        TaskKind = taskKind;
        CleanupError = cleanupError;
    }

    public ApplicationTaskKind TaskKind { get; }
    public Exception CleanupError { get; }
    internal bool BelongsTo(ApplicationTaskCoordinator owner) => ReferenceEquals(_owner, owner);
    internal bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}

public sealed class ApplicationTaskContext
{
    private readonly ApplicationTaskCoordinator _owner;
    private readonly long _generation;

    internal ApplicationTaskContext(ApplicationTaskCoordinator owner, long generation)
    {
        _owner = owner;
        _generation = generation;
    }

    public void ReportPhase(ApplicationTaskPhase phase) =>
        _owner.ReportTaskPhase(_generation, phase);
}

public sealed class ApplicationTaskCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly ProjectCompilationSession _session;
    private readonly PlaybackController _playback;
    private ApplicationTaskKind _activeTaskKind;
    private ApplicationTaskKind _pendingTaskKind;
    private ApplicationTaskPhase _phase;
    private ApplicationLockLevel _lockLevel;
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _preferenceUpdateActive;
    private bool _activeHeldEventInstrumentKeyboardPreview;
    private bool _releasedHeldEventInstrumentKeyboardPreview;
    private bool _disposed;

    public ApplicationTaskCoordinator(
        ProjectCompilationSession session,
        PlaybackController playback)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _playback.StateChanged += PlaybackStateChanged;
    }

    public ApplicationTaskKind ActiveTaskKind
    {
        get
        {
            lock (_sync)
            {
                return _activeTaskKind;
            }
        }
    }

    public ApplicationTaskPhase Phase
    {
        get
        {
            lock (_sync)
            {
                return _phase;
            }
        }
    }

    public ApplicationLockLevel LockLevel
    {
        get
        {
            lock (_sync)
            {
                return _lockLevel;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _activeTaskKind != ApplicationTaskKind.None
                    || _pendingTaskKind != ApplicationTaskKind.None
                    || _preferenceUpdateActive;
            }
        }
    }

    public bool CanReplaceReleasedHeldEventInstrumentKeyboardPreview
    {
        get
        {
            lock (_sync)
            {
                return !_disposed
                    && _activeHeldEventInstrumentKeyboardPreview
                    && _releasedHeldEventInstrumentKeyboardPreview
                    && _activeTaskKind is ApplicationTaskKind.EventInstrumentPreview
                        or ApplicationTaskKind.SubVoicePreview;
            }
        }
    }

    public void StartMainPlayback(long? cursorTick = null, long? endTick = null) =>
        StartPlaybackTask(
            ApplicationTaskKind.MainPlayback,
            () => _playback.Start(cursorTick, endTick));

    public void StartSegmentPreview(MidoraId trackId, MidoraId segmentId) =>
        StartPlaybackTask(
            ApplicationTaskKind.SegmentPreview,
            () => _playback.StartSegmentPreview(trackId, segmentId));

    public void StartEventInstrumentPreview(EventInstrumentPreviewRequest request) =>
        StartPlaybackTask(
            request.SubVoiceId.HasValue
                ? ApplicationTaskKind.SubVoicePreview
                : ApplicationTaskKind.EventInstrumentPreview,
            () => _playback.StartEventInstrumentPreview(request));

    public void StartHeldEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StopReleasedHeldEventInstrumentKeyboardPreview();
        ApplicationTaskKind taskKind = request.SubVoiceId.HasValue
            ? ApplicationTaskKind.SubVoicePreview
            : ApplicationTaskKind.EventInstrumentPreview;
        StartPlaybackTask(
            taskKind,
            () => _playback.StartHeldEventInstrumentPreview(request));
        lock (_sync)
        {
            if (_activeTaskKind == taskKind)
            {
                _activeHeldEventInstrumentKeyboardPreview = true;
                _releasedHeldEventInstrumentKeyboardPreview = false;
            }
        }
    }

    public void StartInstrumentPresetPreview(Guid owner, InstrumentPresetPreviewRequest request, bool held)
    {
        _playback.StopInstrumentPresetPreview(owner);
        StartPlaybackTask(ApplicationTaskKind.EventInstrumentPreview,
            () => _playback.StartInstrumentPresetPreview(owner, request, held));
    }

    public void StopInstrumentPresetPreview(Guid owner) => _playback.StopInstrumentPresetPreview(owner);
    public void ReleaseInstrumentPresetPreviewKey(Guid owner) => _playback.ReleaseInstrumentPresetPreviewKey(owner);

    public void StartHeldSegmentPitchRulerPreview(
        MidoraId trackId,
        MidoraId segmentId,
        int pitch,
        int velocity,
        decimal previewTempo) =>
        StartPlaybackTask(
            ApplicationTaskKind.EventInstrumentPreview,
            () => _playback.StartHeldSegmentPitchRulerPreview(
                trackId,
                segmentId,
                pitch,
                velocity,
                previewTempo));

    public void StartHeldSegmentNotePreview(SegmentNotePreviewRequest request) =>
        StartPlaybackTask(
            ApplicationTaskKind.EventInstrumentPreview,
            () => _playback.StartHeldSegmentNotePreview(request));

    public Exception? TryStartHeldSegmentNotePreview(SegmentNotePreviewRequest request)
    {
        try
        {
            StartHeldSegmentNotePreview(request);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public SegmentNotePlacementPreviewCompletion CompleteSegmentNotePlacement(
        long finalGateLengthTicks,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (finalGateLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }

        HeldPreviewGateEndReport? report = null;
        Exception? previewError = null;
        if (_playback.IsHeldPreviewGateOpen)
        {
            try
            {
                report = EndHeldPreviewGate(finalGateLengthTicks);
            }
            catch (Exception exception)
            {
                previewError = exception;
            }
        }

        try
        {
            commit();
        }
        catch (Exception commitFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                StopPlayback();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            if (previewError is null && cleanupFailure is null)
            {
                throw;
            }
            List<Exception> failures = [commitFailure];
            if (previewError is not null)
            {
                failures.Add(previewError);
            }
            if (cleanupFailure is not null)
            {
                failures.Add(cleanupFailure);
            }
            throw new AggregateException(
                "The Note placement failed; preview errors did not replace the edit failure.",
                failures);
        }

        return new(report, previewError);
    }

    public HeldPreviewGateEndReport EndHeldPreviewGate(long? finalGateLengthTicks = null)
    {
        bool eventInstrumentKeyboardPreview;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeTaskKind is not ApplicationTaskKind.EventInstrumentPreview
                and not ApplicationTaskKind.SubVoicePreview)
            {
                throw new InvalidOperationException("No held-preview task is active.");
            }
            eventInstrumentKeyboardPreview = _activeHeldEventInstrumentKeyboardPreview;
        }
        HeldPreviewGateEndReport report = _playback.EndHeldPreviewGate(finalGateLengthTicks);
        if (eventInstrumentKeyboardPreview)
        {
            lock (_sync)
            {
                if (_activeHeldEventInstrumentKeyboardPreview
                    && _activeTaskKind is ApplicationTaskKind.EventInstrumentPreview
                        or ApplicationTaskKind.SubVoicePreview)
                {
                    _releasedHeldEventInstrumentKeyboardPreview = true;
                }
            }
        }
        return report;
    }

    public void CancelHeldPreview()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeTaskKind is not ApplicationTaskKind.EventInstrumentPreview
                and not ApplicationTaskKind.SubVoicePreview)
            {
                return;
            }
            _phase = ApplicationTaskPhase.Stopping;
        }
        _playback.CancelHeldPreview();
    }

    public bool StopHeldEventInstrumentKeyboardPreview()
    {
        bool shouldStop;
        lock (_sync)
        {
            ThrowIfDisposed();
            shouldStop = _activeHeldEventInstrumentKeyboardPreview
                && _activeTaskKind is ApplicationTaskKind.EventInstrumentPreview
                    or ApplicationTaskKind.SubVoicePreview;
            if (shouldStop)
            {
                _phase = ApplicationTaskPhase.Stopping;
            }
        }
        if (!shouldStop)
        {
            return false;
        }

        // Stop, rather than only cancelling an open Gate, because the keyboard
        // preview may still be rendering its release after the pointer was released.
        _playback.Stop();
        return true;
    }

    public void StopPlayback()
    {
        bool shouldStop;
        lock (_sync)
        {
            ThrowIfDisposed();
            shouldStop = IsPlaybackTask(_activeTaskKind)
                || _playback.State is not PlaybackState.Stopped;
            if (shouldStop && IsPlaybackTask(_activeTaskKind))
                _phase = ApplicationTaskPhase.Stopping;
        }
        if (!shouldStop) return;
        _playback.Stop();
    }

    public void ResetPlaybackEngine()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (IsPlaybackTask(_activeTaskKind))
                _phase = ApplicationTaskPhase.Stopping;
        }
        _playback.ResetPlaybackEngine();
    }

    public Task<ApplicationTaskExecution<MidiExportTaskResult>> ExecuteMidiExportAsync(
        Func<MidiExportTaskRequest> requestFactory,
        MidiExportTaskRunner runner,
        PlaybackCleanupContinuation? playbackCleanupContinuation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(runner);
        return ExecuteAsync(
            ApplicationTaskKind.MidiExport,
            (_, token) => runner.ExecuteAsync(requestFactory(), token),
            playbackCleanupContinuation,
            cancellationToken);
    }

    public Task<ApplicationTaskExecution<AudioRenderTaskResult>> ExecuteAudioRenderAsync(
        Func<AudioRenderTaskRequest> requestFactory,
        AudioRenderTaskRunner runner,
        PlaybackCleanupContinuation? playbackCleanupContinuation = null,
        IProgress<AudioRenderTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(runner);
        return ExecuteAsync(
            ApplicationTaskKind.AudioRender,
            (context, token) => runner.ExecuteAsync(
                requestFactory(),
                _session,
                new InlineProgress<AudioRenderTaskProgress>(value =>
                {
                    context.ReportPhase(ToApplicationPhase(value.Status));
                    progress?.Report(value);
                }),
                token),
            playbackCleanupContinuation,
            cancellationToken);
    }

    public Task<ApplicationTaskExecution<T>> ExecuteAsync<T>(
        ApplicationTaskKind taskKind,
        Func<ApplicationTaskContext, CancellationToken, Task<T>> operation,
        PlaybackCleanupContinuation? playbackCleanupContinuation = null,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            taskKind,
            operation,
            acquireProjectEditLock: true,
            playbackCleanupContinuation,
            cancellationToken);

    public Task<ApplicationTaskExecution<ProjectSwitchGuardResult<T>>> ExecuteProjectSwitchAsync<T>(
        ApplicationTaskKind taskKind,
        IProjectSwitchGuardActions<T> actions,
        PlaybackCleanupContinuation? playbackCleanupContinuation = null,
        CancellationToken cancellationToken = default)
    {
        if (taskKind is not ApplicationTaskKind.NewProject
            and not ApplicationTaskKind.OpenProject
            and not ApplicationTaskKind.CloseProject
            and not ApplicationTaskKind.Exit)
        {
            throw new ArgumentOutOfRangeException(nameof(taskKind));
        }
        ArgumentNullException.ThrowIfNull(actions);
        return ExecuteCoreAsync(
            taskKind,
            async (_, token) =>
            {
                using IDisposable editLock = _session.AcquireProjectEditLock();
                if (actions.HasUnsavedProjectChanges)
                {
                    UnsavedProjectResolution unsavedResolution =
                        await actions.ResolveUnsavedProjectAsync(token).ConfigureAwait(false);
                    switch (unsavedResolution)
                    {
                        case UnsavedProjectResolution.SaveProject when !actions.CanSaveProject:
                            return ProjectSwitchGuardResult<T>.SaveUnavailable();
                        case UnsavedProjectResolution.SaveProject:
                            // Once the save transaction begins it is non-cancellable, even when
                            // the surrounding Open Project command remains cancellable.
                            await actions.SaveProjectAsync(CancellationToken.None).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            break;
                        case UnsavedProjectResolution.CloseWithoutSaving:
                            break;
                        case UnsavedProjectResolution.Cancel:
                            return ProjectSwitchGuardResult<T>.Cancelled();
                        default:
                            throw new InvalidOperationException("Unknown unsaved Project resolution.");
                    }
                }

                bool projectSwitchCompleted = false;
                _session.BeginProjectClosing();
                try
                {
                    T value = await actions.PerformProjectSwitchAsync(token).ConfigureAwait(false);
                    projectSwitchCompleted = true;
                    return ProjectSwitchGuardResult<T>.Completed(value);
                }
                finally
                {
                    if (!projectSwitchCompleted)
                    {
                        _session.CancelProjectClosing();
                    }
                }
            },
            acquireProjectEditLock: false,
            playbackCleanupContinuation,
            cancellationToken);
    }

    private async Task<ApplicationTaskExecution<T>> ExecuteCoreAsync<T>(
        ApplicationTaskKind taskKind,
        Func<ApplicationTaskContext, CancellationToken, Task<T>> operation,
        bool acquireProjectEditLock,
        PlaybackCleanupContinuation? playbackCleanupContinuation,
        CancellationToken cancellationToken)
    {
        ValidateNonPlaybackTaskKind(taskKind);
        ArgumentNullException.ThrowIfNull(operation);

        TaskAdmission admission = BeginAdmission(taskKind, playbackCleanupContinuation);
        if (!admission.Accepted)
        {
            return Result<T>(taskKind, ApplicationTaskOutcome.RejectedBusy);
        }

        Exception? cleanupError = admission.CarriedCleanupError;
        if (admission.MustStopPlayback)
        {
            try
            {
                _playback.Stop();
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            if (cleanupError is not null
                && RequiresCleanupConfirmation(taskKind))
            {
                CancelPendingAdmission(taskKind);
                PlaybackCleanupContinuation continuation = new(this, taskKind, cleanupError);
                return Result<T>(
                    taskKind,
                    ApplicationTaskOutcome.RequiresPlaybackCleanupConfirmation,
                    playbackCleanupError: cleanupError,
                    playbackCleanupContinuation: continuation);
            }
        }

        TaskStart start = StartAdmittedTask(taskKind, cancellationToken);
        if (!start.Started)
        {
            return Result<T>(taskKind, ApplicationTaskOutcome.RejectedBusy);
        }

        IDisposable? editLock = null;
        try
        {
            if (acquireProjectEditLock)
            {
                editLock = _session.AcquireProjectEditLock();
            }
            if (start.CancellationToken.IsCancellationRequested)
            {
                return Result<T>(
                    taskKind,
                    ApplicationTaskOutcome.Cancelled,
                    playbackCleanupError: cleanupError);
            }

            ReportTaskPhase(start.Generation, ApplicationTaskPhase.Running);
            T value = await operation(
                new ApplicationTaskContext(this, start.Generation),
                start.CancellationToken).ConfigureAwait(false);
            return Result(
                taskKind,
                ApplicationTaskOutcome.Completed,
                value,
                playbackCleanupError: cleanupError);
        }
        catch (OperationCanceledException) when (start.CancellationToken.IsCancellationRequested)
        {
            return Result<T>(
                taskKind,
                ApplicationTaskOutcome.Cancelled,
                playbackCleanupError: cleanupError);
        }
        catch (Exception exception)
        {
            return Result<T>(
                taskKind,
                ApplicationTaskOutcome.Failed,
                error: exception,
                playbackCleanupError: cleanupError);
        }
        finally
        {
            ReportTaskPhase(start.Generation, ApplicationTaskPhase.Finalizing);
            editLock?.Dispose();
            CompleteTask(start.Generation);
        }
    }

    public bool RequestCancellation()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!IsCancellable(_activeTaskKind)
                || _phase is ApplicationTaskPhase.Finalizing or ApplicationTaskPhase.Idle
                || _activeCancellation is null)
            {
                return false;
            }
            _phase = ApplicationTaskPhase.Cancelling;
            cancellation = _activeCancellation;
        }
        cancellation.Cancel();
        return true;
    }

    internal IDisposable? TryAcquirePreferenceUpdateLock(
        bool requiresPlaybackStopped,
        out ApplicationPreferenceAdmissionFailure failure)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (requiresPlaybackStopped && _playback.State != PlaybackState.Stopped)
            {
                failure = ApplicationPreferenceAdmissionFailure.PlaybackNotStopped;
                return null;
            }
            if (_activeTaskKind != ApplicationTaskKind.None
                || _pendingTaskKind != ApplicationTaskKind.None
                || _preferenceUpdateActive)
            {
                failure = ApplicationPreferenceAdmissionFailure.ApplicationBusy;
                return null;
            }
            _preferenceUpdateActive = true;
        }

        try
        {
            failure = ApplicationPreferenceAdmissionFailure.None;
            return new PreferenceUpdateLockLease(
                this,
                _session.AcquireProjectEditLock());
        }
        catch
        {
            CompletePreferenceUpdate();
            throw;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        bool stopPlayback;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _playback.StateChanged -= PlaybackStateChanged;
            cancellation = _activeCancellation;
            stopPlayback = IsPlaybackTask(_activeTaskKind);
        }
        cancellation?.Cancel();
        if (stopPlayback)
        {
            try
            {
                _playback.Stop();
            }
            catch
            {
                // PlaybackController retains the cleanup error and releases its Project lock.
            }
        }
    }

    internal void ReportTaskPhase(long generation, ApplicationTaskPhase phase)
    {
        if (phase is ApplicationTaskPhase.Idle
            or ApplicationTaskPhase.Stopping
            or ApplicationTaskPhase.Error)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        lock (_sync)
        {
            if (_generation != generation || _activeTaskKind == ApplicationTaskKind.None)
            {
                return;
            }
            if (_phase == ApplicationTaskPhase.Cancelling
                && phase is not ApplicationTaskPhase.Finalizing)
            {
                return;
            }
            _phase = phase;
        }
    }

    private void StartPlaybackTask(ApplicationTaskKind taskKind, Action start)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeTaskKind != ApplicationTaskKind.None
                || _pendingTaskKind != ApplicationTaskKind.None
                || _preferenceUpdateActive)
            {
                throw new InvalidOperationException("Another global application task is already active.");
            }
        }
        // PlaybackController recovers Error by emitting a transient Stopped state.
        // Recover before registering the new application task so that transition cannot
        // clear the task which is about to start.
        if (_playback.State == PlaybackState.Error)
        {
            _playback.RecoverFromError();
        }
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeTaskKind != ApplicationTaskKind.None
                || _pendingTaskKind != ApplicationTaskKind.None
                || _preferenceUpdateActive)
            {
                throw new InvalidOperationException("Another global application task is already active.");
            }
            _activeTaskKind = taskKind;
            _phase = ApplicationTaskPhase.Preparing;
            _lockLevel = ApplicationLockLevel.ProjectEdit;
        }
        try
        {
            start();
        }
        catch
        {
            lock (_sync)
            {
                if (_activeTaskKind == taskKind)
                {
                    ClearActiveTask();
                }
            }
            throw;
        }
    }

    private void StopReleasedHeldEventInstrumentKeyboardPreview()
    {
        bool stopReleasedPreview;
        lock (_sync)
        {
            ThrowIfDisposed();
            stopReleasedPreview = _activeHeldEventInstrumentKeyboardPreview
                && _releasedHeldEventInstrumentKeyboardPreview
                && _activeTaskKind is ApplicationTaskKind.EventInstrumentPreview
                    or ApplicationTaskKind.SubVoicePreview;
            if (stopReleasedPreview)
            {
                _phase = ApplicationTaskPhase.Stopping;
            }
        }
        if (stopReleasedPreview)
        {
            _playback.Stop();
        }
    }

    private TaskAdmission BeginAdmission(
        ApplicationTaskKind taskKind,
        PlaybackCleanupContinuation? continuation)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pendingTaskKind != ApplicationTaskKind.None || _preferenceUpdateActive)
            {
                return default;
            }
            if (continuation is not null)
            {
                if (_activeTaskKind != ApplicationTaskKind.None)
                {
                    return default;
                }
                if (!continuation.BelongsTo(this)
                    || continuation.TaskKind != taskKind
                    || !continuation.TryConsume())
                {
                    throw new InvalidOperationException(
                        "The playback cleanup continuation is invalid, belongs to another command, or was already consumed.");
                }
                _pendingTaskKind = taskKind;
                return new(true, false, continuation.CleanupError);
            }
            if (_activeTaskKind == ApplicationTaskKind.None)
            {
                _pendingTaskKind = taskKind;
                return new(true, false, null);
            }
            if (!IsPlaybackTask(_activeTaskKind) || !MayAutoStopPlayback(taskKind))
            {
                return default;
            }
            _pendingTaskKind = taskKind;
            _phase = ApplicationTaskPhase.Stopping;
            return new(true, true, null);
        }
    }

    private TaskStart StartAdmittedTask(
        ApplicationTaskKind taskKind,
        CancellationToken externalCancellation)
    {
        lock (_sync)
        {
            if (_disposed || _pendingTaskKind != taskKind
                || _activeTaskKind != ApplicationTaskKind.None)
            {
                if (_pendingTaskKind == taskKind)
                {
                    _pendingTaskKind = ApplicationTaskKind.None;
                }
                return default;
            }

            _pendingTaskKind = ApplicationTaskKind.None;
            _activeTaskKind = taskKind;
            _phase = ApplicationTaskPhase.Preparing;
            _lockLevel = LockLevelFor(taskKind);
            _generation = checked(_generation + 1);
            CancellationToken token;
            if (IsCancellable(taskKind))
            {
                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
                token = _activeCancellation.Token;
            }
            else
            {
                _activeCancellation = null;
                token = CancellationToken.None;
            }
            return new(true, _generation, token);
        }
    }

    private void CancelPendingAdmission(ApplicationTaskKind taskKind)
    {
        lock (_sync)
        {
            if (_pendingTaskKind == taskKind)
            {
                _pendingTaskKind = ApplicationTaskKind.None;
            }
            if (_activeTaskKind == ApplicationTaskKind.None)
            {
                _phase = ApplicationTaskPhase.Idle;
                _lockLevel = ApplicationLockLevel.Normal;
            }
        }
    }

    private void CompleteTask(long generation)
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (_generation != generation)
            {
                return;
            }
            cancellation = _activeCancellation;
            _activeCancellation = null;
            ClearActiveTask();
        }
        cancellation?.Dispose();
    }

    private void CompletePreferenceUpdate()
    {
        lock (_sync)
        {
            _preferenceUpdateActive = false;
        }
    }

    private void PlaybackStateChanged(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            if (!IsPlaybackTask(_activeTaskKind))
            {
                return;
            }
            switch (_playback.State)
            {
                case PlaybackState.Preparing:
                    _phase = ApplicationTaskPhase.Preparing;
                    break;
                case PlaybackState.Playing:
                    _phase = ApplicationTaskPhase.Running;
                    break;
                case PlaybackState.Buffering:
                    _phase = ApplicationTaskPhase.Buffering;
                    break;
                case PlaybackState.Stopping:
                    _phase = ApplicationTaskPhase.Stopping;
                    break;
                case PlaybackState.Error:
                    ClearActiveTask(ApplicationTaskPhase.Error);
                    break;
                case PlaybackState.Stopped:
                    ClearActiveTask();
                    break;
            }
        }
    }

    private void ClearActiveTask(ApplicationTaskPhase finalPhase = ApplicationTaskPhase.Idle)
    {
        _activeTaskKind = ApplicationTaskKind.None;
        _phase = finalPhase;
        _lockLevel = ApplicationLockLevel.Normal;
        _activeHeldEventInstrumentKeyboardPreview = false;
        _releasedHeldEventInstrumentKeyboardPreview = false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static ApplicationTaskExecution<T> Result<T>(
        ApplicationTaskKind taskKind,
        ApplicationTaskOutcome outcome,
        T? value = default,
        Exception? error = null,
        Exception? playbackCleanupError = null,
        PlaybackCleanupContinuation? playbackCleanupContinuation = null) =>
        new(
            taskKind,
            outcome,
            value,
            error,
            playbackCleanupError,
            playbackCleanupContinuation);

    private static bool IsPlaybackTask(ApplicationTaskKind taskKind) => taskKind is
        ApplicationTaskKind.MainPlayback
        or ApplicationTaskKind.SegmentPreview
        or ApplicationTaskKind.EventInstrumentPreview
        or ApplicationTaskKind.SubVoicePreview;

    private static bool MayAutoStopPlayback(ApplicationTaskKind taskKind) => taskKind is
        ApplicationTaskKind.NewProject
        or ApplicationTaskKind.OpenProject
        or ApplicationTaskKind.SaveProject
        or ApplicationTaskKind.SaveCopy
        or ApplicationTaskKind.CloseProject
        or ApplicationTaskKind.Exit
        or ApplicationTaskKind.MidiExport
        or ApplicationTaskKind.AudioRender;

    private static bool RequiresCleanupConfirmation(ApplicationTaskKind taskKind) => taskKind is not
        ApplicationTaskKind.SaveProject
        and not ApplicationTaskKind.SaveCopy;

    private static bool IsCancellable(ApplicationTaskKind taskKind) => taskKind is
        ApplicationTaskKind.ExplicitCompile
        or ApplicationTaskKind.OpenProject
        or ApplicationTaskKind.MidiExport
        or ApplicationTaskKind.AudioRender;

    private static ApplicationLockLevel LockLevelFor(ApplicationTaskKind taskKind) => taskKind switch
    {
        ApplicationTaskKind.ExplicitCompile => ApplicationLockLevel.ProjectEdit,
        ApplicationTaskKind.AudioRender => ApplicationLockLevel.FullApplication,
        _ => ApplicationLockLevel.MainWindowModal
    };

    private static void ValidateNonPlaybackTaskKind(ApplicationTaskKind taskKind)
    {
        if (taskKind == ApplicationTaskKind.None || IsPlaybackTask(taskKind))
        {
            throw new ArgumentOutOfRangeException(nameof(taskKind));
        }
    }

    private static ApplicationTaskPhase ToApplicationPhase(AudioRenderTaskStatus status) => status switch
    {
        AudioRenderTaskStatus.Preparing => ApplicationTaskPhase.Preparing,
        AudioRenderTaskStatus.Rendering => ApplicationTaskPhase.Running,
        AudioRenderTaskStatus.Cancelling => ApplicationTaskPhase.Cancelling,
        AudioRenderTaskStatus.Finalizing => ApplicationTaskPhase.Finalizing,
        _ => ApplicationTaskPhase.Finalizing
    };

    private readonly record struct TaskAdmission(
        bool Accepted,
        bool MustStopPlayback,
        Exception? CarriedCleanupError);
    private readonly record struct TaskStart(bool Started, long Generation, CancellationToken CancellationToken);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class PreferenceUpdateLockLease(
        ApplicationTaskCoordinator owner,
        IDisposable projectEditLock) : IDisposable
    {
        private ApplicationTaskCoordinator? _owner = owner;
        private IDisposable? _projectEditLock = projectEditLock;

        public void Dispose()
        {
            IDisposable? editLock = Interlocked.Exchange(ref _projectEditLock, null);
            ApplicationTaskCoordinator? currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return;
            }
            try
            {
                editLock?.Dispose();
            }
            finally
            {
                currentOwner.CompletePreferenceUpdate();
            }
        }
    }
}

internal enum ApplicationPreferenceAdmissionFailure
{
    None,
    PlaybackNotStopped,
    ApplicationBusy
}
