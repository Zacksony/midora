using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Playback;

public enum PlaybackState
{
    Stopped,
    Preparing,
    Playing,
    Buffering,
    Stopping,
    Error
}

public enum PlaybackTaskKind
{
    None,
    MainTimeline,
    SegmentPreview,
    EventInstrumentPreview,
    SubVoicePreview,
    InstrumentPresetPreview
}

public interface IRealtimePlaybackBackend : IDisposable
{
    int ActualSampleRate { get; }
    long PositionFrames { get; }
    long RenderPositionFrames { get; }
    bool IsBuffering { get; }
    bool IsCompleted { get; }
    bool IsFaulted { get; }
    string? FaultDescription { get; }
    bool OutputDeviceSelectionRequired { get; }
    string? OutputDeviceSelectionReason { get; }
    int Prepare();
    void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master);
    void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands);
    void Stop(bool flush);
    void Reset();
    void SelectOutputDevice(string? deviceId);
}

public interface ICancellableRealtimePlaybackPreparationBackend
{
    int Prepare(CancellationToken cancellationToken);
}

public interface IHeldPreviewRealtimePlaybackBackend
{
    long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout);
    void ReplaceHeldPreviewFutureAndResume(
        MidiRenderPlan plan,
        long producerFrontierFrame,
        TimeSpan timeout);
    void ResumeHeldPreviewFromProducerFrontier();
}

public interface IRealtimePlaybackCacheStore : IAudioPcmCacheSessionAccess
{
    bool TryReadReusableAudio(string key, out byte[] payload);
    AudioCachePublishResult PublishReusableAudio(string key, ReadOnlySpan<byte> payload);
}

public interface IRealtimePlaybackCacheBackend
{
    void SetAudioCacheStore(IRealtimePlaybackCacheStore cacheStore);
    void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode);
}

public interface IRealtimePlaybackSoundFontBackend
{
    void SetSoundFontSet(
        IReadOnlyList<SoundFontConfiguration> soundFonts,
        string? cacheIdentity);
}

public interface ISimplePitchAuditionRealtimePlaybackBackend
{
    void BeginPitchAudition(int pitch, int velocity);
    void EndPitchAudition();
}

public enum RealtimePlaybackCacheMode
{
    Disabled,
    UnitPcm,
    UnitPcmAndPlaybackSpan
}

public interface IBufferingRecoveryRealtimePlaybackBackend
{
    void SetNextBufferingRecoveryStorage(
        AudioCacheSessionStore.AudioRecoverySpool? recoverySpool,
        long memoryFallbackFrameCapacity);

    bool HasBufferingRecoveryStorage { get; }

    void BeginBufferingRecovery(long recoveryEndFrame);
}

public sealed class OutputDeviceSelectionRequiredException : InvalidOperationException
{
    public OutputDeviceSelectionRequiredException(string message)
        : base(message)
    {
    }
}

public readonly record struct PlaybackMasterConfiguration(float VolumeDecibels, bool LimiterEnabled);

public readonly record struct HeldPreviewGateEndReport(
    long FinalGateLengthTicks,
    long ConsumedFrameAtGateEnd,
    long ProducerFrontierFrame,
    long QueuedLatencyFrameCount,
    double QueuedLatencyMilliseconds);

public sealed class PlaybackController : IDisposable
{
    private const int HeldPreviewWindowSeconds = 8;
    private const int HeldPreviewRenewalThresholdSeconds = 4;
    private static readonly TimeSpan HeldPreviewBackendTimeout = TimeSpan.FromSeconds(5);
    private readonly ProjectCompilationSession _session;
    private readonly IRealtimePlaybackBackend _backend;
    private readonly HashSet<MidoraId> _mutedTracks = [];
    private readonly HashSet<MidoraId> _soloTracks = [];
    private readonly HashSet<MidoraId> _mutedSharedGroups = [];
    private readonly HashSet<MidoraId> _soloSharedGroups = [];
    private readonly HashSet<MidoraId> _audibleTracks = [];
    private readonly object _backendPreparationSync = new();
    private readonly object _prewarmSync = new();
    private readonly CancellationTokenSource _prewarmCancellation = new();
    private Task _prewarmTask = Task.CompletedTask;
    private long _prewarmRequestGeneration;
    private int _knownSampleRate;
    private CanonicalCompiledResult? _activeResultValue;
    private MidiRenderPlan? _activePlanValue;
    private IDisposable? _activeResultStorage;
    private IDisposable? _activePlanStorage;
    private CanonicalCompiledResult? _activeResult
    {
        get => _activeResultValue;
        set => ReplaceActiveStorage(ref _activeResultValue, ref _activeResultStorage, value);
    }
    private MidiRenderPlan? _activePlan
    {
        get => _activePlanValue;
        set => ReplaceActiveStorage(ref _activePlanValue, ref _activePlanStorage, value);
    }

    private void ReplaceActiveStorage<T>(ref T? target, ref IDisposable? lease, T? value)
        where T : class, Midora.Common.IRetainedStorageSource
    {
        if (ReferenceEquals(target, value)) return;
        IDisposable? next = value is null ? null : _session.RetainPreparationStorage(value);
        IDisposable? previous = lease;
        target = value;
        lease = next;
        previous?.Dispose();
    }
    private TempoSampleMap? _activeTempoMap;
    private long _taskStartTick;
    private long _cursorTick;
    private long? _requestedEndTick;
    private TickRange? _loopRange;
    private IDisposable? _editLockLease;
    private Func<long, CanonicalCompiledResult>? _heldPreviewOpenCompiler;
    private Func<long, long, CanonicalCompiledResult>? _heldPreviewEndCompiler;
    private decimal _heldPreviewTempo;
    private long _heldPreviewWindowEndTick;
    private bool _heldPreviewGateOpen;
    private bool _releaseEditLockAtHeldGateEnd;
    private AudioRecoveryStorageUnavailableException? _recoveryStorageFailure;
    private bool _bufferingRecoveryRequested;
    private long _bufferingRecoveryStartFrame = -1;
    private long _bufferingRecoveryEndFrame = -1;
    private PlaybackMasterConfiguration _masterConfiguration;
    private StopCursorBehavior _stopCursorBehavior;
    private bool _disposed;

    public PlaybackController(
        ProjectCompilationSession session,
        IRealtimePlaybackBackend backend,
        PlaybackMasterConfiguration? masterConfiguration = null,
        StopCursorBehavior stopCursorBehavior = StopCursorBehavior.ReturnToPlaybackStart)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _masterConfiguration = masterConfiguration ?? new(-0.1f, true);
        if (!Enum.IsDefined(stopCursorBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(stopCursorBehavior));
        }
        _stopCursorBehavior = stopCursorBehavior;
        if (backend is IRealtimePlaybackCacheBackend cacheBackend)
        {
            cacheBackend.SetAudioCacheStore(session);
        }
        RebuildAudibleTracks();
        _session.CompilationChanged += HandleCompilationChangedForPrewarm;
        _session.EffectiveSoundFontChanged += HandleEffectiveSoundFontChanged;
        RefreshBackendSoundFontIdentity();
    }

    public void ConfigurePlaybackPreferences(
        PlaybackMasterConfiguration masterConfiguration,
        StopCursorBehavior stopCursorBehavior)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "Playback preferences can only change while playback is stopped.");
        }
        if (!float.IsFinite(masterConfiguration.VolumeDecibels)
            || masterConfiguration.VolumeDecibels > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(masterConfiguration));
        }
        if (!Enum.IsDefined(stopCursorBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(stopCursorBehavior));
        }
        _masterConfiguration = masterConfiguration;
        _stopCursorBehavior = stopCursorBehavior;
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public PlaybackTaskKind ActiveTaskKind { get; private set; }
    public Exception? LastError { get; private set; }
    public bool OutputDeviceSelectionRequired { get; private set; }
    public bool IsHeldPreviewGateOpen => _heldPreviewGateOpen;
    public HeldPreviewGateEndReport? LastHeldPreviewGateEndReport { get; private set; }
    public TickRange? LoopRange => _loopRange;
    public double? BufferingProgress
    {
        get
        {
            if (State != PlaybackState.Buffering
                || !_bufferingRecoveryRequested
                || _bufferingRecoveryStartFrame < 0
                || _bufferingRecoveryEndFrame <= _bufferingRecoveryStartFrame)
            {
                return null;
            }
            long preparedThroughFrame = Math.Clamp(
                _backend.RenderPositionFrames,
                _bufferingRecoveryStartFrame,
                _bufferingRecoveryEndFrame);
            return (double)(preparedThroughFrame - _bufferingRecoveryStartFrame)
                / (_bufferingRecoveryEndFrame - _bufferingRecoveryStartFrame);
        }
    }
    public event EventHandler? StateChanged;

    public void BeginDefaultPlaybackPreparation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_prewarmSync)
        {
            _prewarmRequestGeneration = checked(_prewarmRequestGeneration + 1);
            if (_prewarmTask.IsCompleted)
            {
                _prewarmTask = Task.Run(
                    RunDefaultPlaybackPreparationLoop,
                    _prewarmCancellation.Token);
            }
        }
    }

    public long CurrentTick
    {
        get
        {
            if (ActiveTaskKind is not PlaybackTaskKind.MainTimeline)
            {
                return _cursorTick;
            }
            return CurrentTaskTick;
        }
    }

    public long CurrentTaskTick
    {
        get
        {
            CanonicalCompiledResult? active = _activeResult;
            TempoSampleMap? map = _activeTempoMap;
            if (active is null || map is null || State is PlaybackState.Stopped or PlaybackState.Error)
            {
                return _cursorTick;
            }
            return map.SampleFrameToTick(
                _backend.PositionFrames, active.StartTick, _backend.ActualSampleRate, active.EndTick);
        }
    }

    public void Start(long? cursorTick = null, long? endTick = null)
    {
        EnsureCanStartTask();
        long effectiveCursorTick = cursorTick ?? _cursorTick;
        if (effectiveCursorTick < 0 || endTick.HasValue && endTick < effectiveCursorTick)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorTick), "Playback requires a non-negative, non-reversed range.");
        }
        long? effectiveEndTick = EffectiveEndTick(endTick);
        if (effectiveEndTick < effectiveCursorTick)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorTick),
                "The effective playback or loop end must not precede the playback cursor.");
        }
        _ = RequireEffectiveSoundFont("Playback");
        _taskStartTick = effectiveCursorTick;
        _cursorTick = effectiveCursorTick;
        _requestedEndTick = endTick;
        StartPreparedRange(effectiveCursorTick, effectiveEndTick, acquireEditLock: true);
    }

    public void StartEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanStartTask();
        StartPreview(
            () => new PreviewCompiler().CompileEventInstrument(_session.Project, request),
            request.SubVoiceId.HasValue
                ? PlaybackTaskKind.SubVoicePreview
                : PlaybackTaskKind.EventInstrumentPreview);
    }

    private Guid? _instrumentPresetPreviewOwner;

    public void StartInstrumentPresetPreview(Guid owner, InstrumentPresetPreviewRequest request, bool held)
    {
        if (owner == Guid.Empty) throw new ArgumentException("A preview owner is required.", nameof(owner));
        request.Validate();
        EnsureCanStartTask();
        _instrumentPresetPreviewOwner = owner;
        try
        {
        if (held)
            StartHeldPreviewCore(window => InstrumentPresetPreviewCompiler.Compile(request, heldWindowEndTick: window),
                (_, effective) => InstrumentPresetPreviewCompiler.Compile(request, effectiveGateEndTick: effective),
                InstrumentPresetPreviewCompiler.Tempo, PlaybackTaskKind.InstrumentPresetPreview, false,
                InstrumentPresetPreviewCompiler.TicksPerQuarterNote);
        else StartPreview(() => InstrumentPresetPreviewCompiler.Compile(request), PlaybackTaskKind.InstrumentPresetPreview);
        }
        catch (Exception exception)
        {
            // Only this newly acquired preview can have touched the backend.
            // A failed Start must not leave a partially started native task.
            FailHeldPreview(exception);
            _instrumentPresetPreviewOwner = null;
            throw;
        }
    }

    public void StopInstrumentPresetPreview(Guid owner)
    {
        if (_instrumentPresetPreviewOwner != owner) return;
        try
        {
            if (ActiveTaskKind == PlaybackTaskKind.InstrumentPresetPreview)
                StopCore(applyCursorBehavior: false, releaseEditLock: true);
        }
        finally { _instrumentPresetPreviewOwner = null; }
    }

    public void ReleaseInstrumentPresetPreviewKey(Guid owner)
    {
        if (_instrumentPresetPreviewOwner == owner && ActiveTaskKind == PlaybackTaskKind.InstrumentPresetPreview
            && IsHeldPreviewGateOpen) EndHeldPreviewGate();
    }

    public void BeginPitchAudition(int pitch, int velocity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pitch is < 0 or > 127 || velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "Pitch audition is unavailable while a formal audio task is active.");
        }
        _ = RequireEffectiveSoundFont("Pitch audition");
        if (_backend is not ISimplePitchAuditionRealtimePlaybackBackend auditionBackend)
        {
            throw new NotSupportedException(
                "The selected realtime backend does not support simple pitch audition.");
        }
        auditionBackend.BeginPitchAudition(pitch, velocity);
    }

    public void EndPitchAudition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is ISimplePitchAuditionRealtimePlaybackBackend auditionBackend)
        {
            auditionBackend.EndPitchAudition();
        }
    }

    public void StartHeldEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview Gate Start must not carry a final Gate Length.",
                nameof(request));
        }
        decimal previewTempo = ResolvePreviewTempo(_session.Project, request);
        StartHeldPreviewCore(
            windowEndTick => new PreviewCompiler().CompileHeldEventInstrumentGateOpen(
                _session.Project,
                request,
                windowEndTick),
            (finalGateLength, effectiveGateEndTick) =>
                new PreviewCompiler().CompileHeldEventInstrumentGateEnd(
                    _session.Project,
                    request,
                    finalGateLength,
                    effectiveGateEndTick),
            previewTempo,
            request.SubVoiceId.HasValue
                ? PlaybackTaskKind.SubVoicePreview
                : PlaybackTaskKind.EventInstrumentPreview,
            releaseEditLockAtGateEnd: false);
    }

    public void StartHeldSegmentPitchRulerPreview(
        MidoraId trackId,
        MidoraId segmentId,
        int pitch,
        int velocity,
        decimal previewTempo)
    {
        LogicalTrack track = _session.Project.Tracks.FirstOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentOutOfRangeException(nameof(trackId));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == segmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
        MidoraId instrumentId = _session.Project.ResolveEventInstrumentDefinitionId(track)
            ?? throw new InvalidOperationException(
                "Pitch Ruler preview requires a bound Event Instrument.");
        if (!_session.Project.EventInstruments.Any(value => value.Id == instrumentId))
        {
            throw new InvalidOperationException(
                "The Pitch Ruler preview Event Instrument binding is missing or damaged.");
        }
        StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(
            instrumentId,
            Pitch: pitch,
            Velocity: velocity,
            Tempo: previewTempo,
            CursorTick: segment.ProjectStartTick));
    }

    public void StartHeldSegmentNotePreview(SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LogicalTrack track = _session.Project.Tracks.FirstOrDefault(
            value => value.Id == request.TrackId)
            ?? throw new ArgumentOutOfRangeException(nameof(request));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == request.SegmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(request));
        long projectStartTick = checked(
            segment.ProjectStartTick + (request.StartTick - segment.ContentOffsetTick));
        decimal previewTempo = ResolvePreviewTempo(_session.Project, projectStartTick);
        StartHeldPreviewCore(
            windowLengthTicks => new PreviewCompiler().CompileHeldSegmentNoteGateOpen(
                _session.Project,
                request,
                windowLengthTicks),
            (finalGateLength, effectiveGateEndTick) =>
                new PreviewCompiler().CompileHeldSegmentNoteGateEnd(
                    _session.Project,
                    request,
                    finalGateLength,
                    effectiveGateEndTick),
            previewTempo,
            PlaybackTaskKind.EventInstrumentPreview,
            releaseEditLockAtGateEnd: true);
    }

    private void StartHeldPreviewCore(
        Func<long, CanonicalCompiledResult> compileOpen,
        Func<long, long, CanonicalCompiledResult> compileEnd,
        decimal previewTempo,
        PlaybackTaskKind taskKind,
        bool releaseEditLockAtGateEnd,
        int? ticksPerQuarterNote = null)
    {
        ArgumentNullException.ThrowIfNull(compileOpen);
        ArgumentNullException.ThrowIfNull(compileEnd);
        EnsureCanStartTask();
        if (_backend is not IHeldPreviewRealtimePlaybackBackend)
        {
            throw new NotSupportedException(
                "The selected realtime backend does not support causal held Preview.");
        }
        _ = RequireEffectiveSoundFont("Held Preview");

        try
        {
            _editLockLease = _session.AcquireProjectEditLock();
            ActiveTaskKind = taskKind;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Held Preview");
            long initialWindowEndTick = CalculateHeldPreviewWindowTicks(
                ticksPerQuarterNote ?? _session.Project.TicksPerQuarterNote,
                previewTempo,
                HeldPreviewWindowSeconds);
            CanonicalCompiledResult compiled = compileOpen(initialWindowEndTick);
            RequireConsumablePreview(compiled);
            int actualSampleRate = PrepareBackend();
            _session.InvalidateSampleDomainCaches();
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
                compiled,
                actualSampleRate);
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(RealtimePlaybackCacheMode.Disabled);
            _activeResult = compiled;
            _activePlan = plan;
            _backend.Start(plan, soundFont, _masterConfiguration);
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            _heldPreviewOpenCompiler = compileOpen;
            _heldPreviewEndCompiler = compileEnd;
            _heldPreviewTempo = previewTempo;
            _heldPreviewWindowEndTick = initialWindowEndTick;
            _heldPreviewGateOpen = true;
            _releaseEditLockAtHeldGateEnd = releaseEditLockAtGateEnd;
            LastHeldPreviewGateEndReport = null;
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    public HeldPreviewGateEndReport EndHeldPreviewGate(long? finalGateLengthTicks = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_heldPreviewGateOpen
            || _heldPreviewEndCompiler is null
            || _activeResult is null
            || _activePlan is null
            || _activeTempoMap is null
            || _backend is not IHeldPreviewRealtimePlaybackBackend heldBackend
            || State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            throw new InvalidOperationException("No held-preview Gate is active.");
        }
        if (finalGateLengthTicks.HasValue && finalGateLengthTicks.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }

        long consumedFrame = _backend.PositionFrames;
        try
        {
            long producerFrontier = heldBackend.PauseHeldPreviewAtProducerFrontier(
                HeldPreviewBackendTimeout);
            long frozenGateLength = finalGateLengthTicks
                ?? Math.Max(
                    1,
                    _activeTempoMap.SampleFrameToTick(
                        consumedFrame,
                        _activeResult.StartTick,
                        _backend.ActualSampleRate,
                        _activeResult.EndTick) - _activeResult.StartTick);
            long effectiveGateEndTick = Math.Max(
                1,
                FirstTickAtOrAfterFrame(
                    _activeTempoMap,
                    producerFrontier,
                    _activeResult.StartTick,
                    _backend.ActualSampleRate,
                    _activeResult.EndTick) - _activeResult.StartTick);
            CanonicalCompiledResult continuation = _heldPreviewEndCompiler(
                frozenGateLength,
                effectiveGateEndTick);
            RequireConsumablePreview(continuation);
            MidiRenderPlan continuationPlan = MidiRenderPlanAdapter.CreateRealtime(
                continuation,
                _backend.ActualSampleRate);
            MidiRenderPlan replacement = MidiRenderPlanSplicer
                .SpliceHeldGateEndAtProducerFrontier(
                    _activePlan,
                    continuationPlan,
                    producerFrontier);
            using IDisposable replacementResultStorage = _session.RetainPreparationStorage(continuation);
            using IDisposable replacementPlanStorage = _session.RetainPreparationStorage(replacement);
            heldBackend.ReplaceHeldPreviewFutureAndResume(
                replacement,
                producerFrontier,
                HeldPreviewBackendTimeout);
            _activeResult = continuation;
            _activePlan = replacement;
            _activeTempoMap = new(
                continuation.TicksPerQuarterNote,
                continuation.Tempos);
            _heldPreviewGateOpen = false;
            _heldPreviewOpenCompiler = null;
            _heldPreviewEndCompiler = null;
            _heldPreviewTempo = 0;
            _heldPreviewWindowEndTick = 0;
            if (_releaseEditLockAtHeldGateEnd)
            {
                ReleaseEditLock();
            }
            _releaseEditLockAtHeldGateEnd = false;
            long latencyFrames = Math.Max(0, producerFrontier - consumedFrame);
            HeldPreviewGateEndReport report = new(
                frozenGateLength,
                consumedFrame,
                producerFrontier,
                latencyFrames,
                latencyFrames * 1_000d / _backend.ActualSampleRate);
            LastHeldPreviewGateEndReport = report;
            return report;
        }
        catch (Exception exception)
        {
            FailHeldPreview(exception);
            throw;
        }
    }

    public void CancelHeldPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_heldPreviewGateOpen)
        {
            return;
        }
        StopCore(applyCursorBehavior: false, releaseEditLock: true);
    }

    public void StartSegmentPreview(MidoraId trackId, MidoraId segmentId)
    {
        EnsureCanStartTask();
        StartPreview(
            () => new PreviewCompiler().CompileSegment(_session.Project, trackId, segmentId),
            PlaybackTaskKind.SegmentPreview);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCore(applyCursorBehavior: true, releaseEditLock: true);
    }

    public void SelectOutputDevice(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (deviceId is { Length: 0 })
        {
            throw new ArgumentException(
                "The output device ID must be null for System Default or non-empty.",
                nameof(deviceId));
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "The playback output device can only be selected while playback is stopped.");
        }

        _backend.SelectOutputDevice(deviceId);
        Volatile.Write(ref _knownSampleRate, 0);
        _session.InvalidateSampleDomainCaches();
        OutputDeviceSelectionRequired = false;
        LastError = null;
        BeginDefaultPlaybackPreparation();
    }

    public void Seek(long tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        if ((State is PlaybackState.Playing or PlaybackState.Buffering)
            && ActiveTaskKind == PlaybackTaskKind.MainTimeline)
        {
            long? effectiveEndTick = EffectiveEndTick(_requestedEndTick);
            if (effectiveEndTick < tick)
            {
                throw new ArgumentOutOfRangeException(nameof(tick),
                    "The seek target must not follow the effective playback or loop end.");
            }
            RestartAt(tick, effectiveEndTick);
        }
        else if ((State is PlaybackState.Stopped or PlaybackState.Error)
            && ActiveTaskKind == PlaybackTaskKind.None)
        {
            _cursorTick = tick;
        }
        else
        {
            throw new InvalidOperationException("Seek is unavailable in the current playback state.");
        }
    }

    public void SetLoop(TickRange? range)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (range.HasValue && (!range.Value.IsValid || range.Value.Length <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
        if (_loopRange == range)
        {
            return;
        }
        _loopRange = range;
        if (ActiveTaskKind != PlaybackTaskKind.MainTimeline
            || State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            return;
        }

        long tick = CurrentTick;
        if (range.HasValue)
        {
            if (tick >= range.Value.EndTick)
            {
                tick = range.Value.StartTick;
            }
            RestartAt(tick, range.Value.EndTick);
            return;
        }

        if (_requestedEndTick.HasValue && tick >= _requestedEndTick.Value)
        {
            StopCore(applyCursorBehavior: true, releaseEditLock: true);
            return;
        }
        RestartAt(tick, _requestedEndTick);
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            return;
        }
        if (_backend.OutputDeviceSelectionRequired)
        {
            EnterOutputDeviceSelectionRequired();
            return;
        }
        if (_backend.IsFaulted)
        {
            EnterBackendError();
            return;
        }
        if (_backend.IsBuffering)
        {
            if (!_bufferingRecoveryRequested && !TryBeginBufferingRecovery())
            {
                return;
            }
            SetState(PlaybackState.Buffering);
            return;
        }
        _bufferingRecoveryRequested = false;
        _bufferingRecoveryStartFrame = -1;
        _bufferingRecoveryEndFrame = -1;
        if (_heldPreviewGateOpen)
        {
            ExtendHeldPreviewWindowIfNeeded();
        }
        SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        if (!_backend.IsCompleted)
        {
            return;
        }
        if (_loopRange.HasValue && ActiveTaskKind == PlaybackTaskKind.MainTimeline)
        {
            RestartAt(_loopRange.Value.StartTick, _loopRange.Value.EndTick);
        }
        else
        {
            StopCore(applyCursorBehavior: true, releaseEditLock: true);
        }
    }

    public void SetTrackMuted(MidoraId trackId, bool muted)
    {
        EnsureTrackExists(trackId);
        bool changed = muted ? _mutedTracks.Add(trackId) : _mutedTracks.Remove(trackId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (muted) _mutedTracks.Remove(trackId);
            else _mutedTracks.Add(trackId);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void SetTrackSolo(MidoraId trackId, bool solo)
    {
        EnsureTrackExists(trackId);
        bool changed = solo ? _soloTracks.Add(trackId) : _soloTracks.Remove(trackId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (solo) _soloTracks.Remove(trackId);
            else _soloTracks.Add(trackId);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void ResetMonitoringStates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutedTracks.Count == 0
            && _soloTracks.Count == 0
            && _mutedSharedGroups.Count == 0
            && _soloSharedGroups.Count == 0)
        {
            return;
        }

        HashSet<MidoraId> mutedTracks = new(_mutedTracks);
        HashSet<MidoraId> soloTracks = new(_soloTracks);
        HashSet<MidoraId> mutedSharedGroups = new(_mutedSharedGroups);
        HashSet<MidoraId> soloSharedGroups = new(_soloSharedGroups);
        _mutedTracks.Clear();
        _soloTracks.Clear();
        _mutedSharedGroups.Clear();
        _soloSharedGroups.Clear();
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            _mutedTracks.UnionWith(mutedTracks);
            _soloTracks.UnionWith(soloTracks);
            _mutedSharedGroups.UnionWith(mutedSharedGroups);
            _soloSharedGroups.UnionWith(soloSharedGroups);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void SetTrackAudible(MidoraId trackId, bool audible) => SetTrackMuted(trackId, !audible);

    public void ResetPlaybackEngine()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? stopFailure = null;
        if (State != PlaybackState.Stopped)
        {
            try
            {
                StopCore(applyCursorBehavior: true, releaseEditLock: true);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
        }

        Exception? resetFailure = null;
        try
        {
            _backend.Reset();
        }
        catch (Exception exception)
        {
            resetFailure = exception;
        }
        finally
        {
            Volatile.Write(ref _knownSampleRate, 0);
            _session.InvalidateSampleDomainCaches();
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ActiveTaskKind = PlaybackTaskKind.None;
            ReleaseEditLock();
        }

        if (resetFailure is null)
        {
            if (!OutputDeviceSelectionRequired)
            {
                LastError = null;
            }
            SetState(PlaybackState.Stopped);
            return;
        }

        LastError = stopFailure is null
            ? resetFailure
            : new AggregateException(
                "Playback stop and backend reset both failed.",
                stopFailure,
                resetFailure);
        SetState(PlaybackState.Error);
        throw LastError;
    }

    /// <summary>
    /// Initializes the current realtime backend and keeps its persistent audio Worker ready for
    /// subsequent playback and preview. This does not compile Project content or start playback.
    /// </summary>
    public int WarmUpAudioBackend(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "The audio Worker can only be initialized while playback is stopped.");
        }
        _ = RequireEffectiveSoundFont("Audio Worker initialization");
        return PrepareBackend(cancellationToken);
    }

    public void RecoverFromError()
    {
        if (State == PlaybackState.Error)
        {
            ResetPlaybackEngine();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.CompilationChanged -= HandleCompilationChangedForPrewarm;
        _session.EffectiveSoundFontChanged -= HandleEffectiveSoundFontChanged;
        _prewarmCancellation.Cancel();
        Task prewarmTask;
        lock (_prewarmSync)
        {
            prewarmTask = _prewarmTask;
        }
        try
        {
            prewarmTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        if (State != PlaybackState.Stopped)
        {
            try
            {
                StopCore(applyCursorBehavior: true, releaseEditLock: true);
            }
            catch
            {
                ReleaseEditLock();
            }
        }
        try { _backend.Dispose(); }
        finally
        {
            _activeResult = null;
            _activePlan = null;
            _prewarmCancellation.Dispose();
            _disposed = true;
        }
    }

    public void SetSharedGroupMuted(MidoraId sharedGroupId, bool muted)
    {
        EnsureSharedGroupExists(sharedGroupId);
        bool changed = muted
            ? _mutedSharedGroups.Add(sharedGroupId)
            : _mutedSharedGroups.Remove(sharedGroupId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (muted) _mutedSharedGroups.Remove(sharedGroupId);
            else _mutedSharedGroups.Add(sharedGroupId);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void SetSharedGroupSolo(MidoraId sharedGroupId, bool solo)
    {
        EnsureSharedGroupExists(sharedGroupId);
        bool changed = solo
            ? _soloSharedGroups.Add(sharedGroupId)
            : _soloSharedGroups.Remove(sharedGroupId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (solo) _soloSharedGroups.Remove(sharedGroupId);
            else _soloSharedGroups.Add(sharedGroupId);
            RebuildAudibleTracks();
            throw;
        }
    }

    private void HandleEffectiveSoundFontChanged(object? sender, EventArgs e) =>
        RefreshBackendSoundFontIdentity();

    private void RefreshBackendSoundFontIdentity()
    {
        if (_backend is not IRealtimePlaybackSoundFontBackend soundFontBackend)
        {
            return;
        }
        IReadOnlyList<SoundFontConfiguration> soundFonts =
            _session.EffectiveSoundFontConfigurations;
        string? identity = soundFonts.Count == 0
            ? null
            : _session.EffectiveSoundFontSetCacheIdentity;
        lock (_backendPreparationSync)
        {
            soundFontBackend.SetSoundFontSet(soundFonts, identity);
            Volatile.Write(ref _knownSampleRate, 0);
        }
    }

    private void StartPreparedRange(long cursorTick, long? endTick, bool acquireEditLock)
    {
        _cursorTick = cursorTick;
        try
        {
            if (acquireEditLock)
            {
                _editLockLease = _session.AcquireProjectEditLock();
            }
            ActiveTaskKind = PlaybackTaskKind.MainTimeline;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Playback");
            WaitForDefaultPlaybackPreparation();
            int actualSampleRate = PrepareBackend();
            CanonicalCompiledResult compiled = _session.CompileForPlayback(cursorTick, endTick);
            if (!compiled.IsConsumable)
            {
                throw new CompilationRejectedException(compiled.Diagnostics);
            }
            if (compiled.EndTick < compiled.StartTick)
            {
                throw new InvalidOperationException("Playback range must not be reversed.");
            }
            if (compiled.EndTick == compiled.StartTick)
            {
                _activeResult = null;
                _activePlan = null;
                _activeTempoMap = null;
                ClearHeldPreviewState();
                ReleaseEditLock();
                ActiveTaskKind = PlaybackTaskKind.None;
                LastError = null;
                SetState(PlaybackState.Stopped);
                return;
            }
            // Runtime monitoring state is intentionally not part of Project or
            // canonical compilation. Rebuild it only after the edit lock has
            // frozen the Project so Tracks created since this controller was
            // constructed cannot be omitted from the first realtime plan.
            RebuildAudibleTracks();
            MidiRenderPlan plan = _session.GetOrCreateRealtimeRenderPlan(
                compiled,
                actualSampleRate,
                _audibleTracks);
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(RealtimePlaybackCacheMode.UnitPcmAndPlaybackSpan);
            _activeResult = compiled;
            _activePlan = plan;
            _backend.Start(plan, soundFont, _masterConfiguration);
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void StartPreview(Func<CanonicalCompiledResult> compile, PlaybackTaskKind taskKind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compile);
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException("A playback or preview task is already active.");
        }
        _ = RequireEffectiveSoundFont("Preview");

        try
        {
            _editLockLease = _session.AcquireProjectEditLock();
            ActiveTaskKind = taskKind;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Preview");
            CanonicalCompiledResult compiled = compile();
            if (!compiled.IsConsumable)
            {
                throw new CompilationRejectedException(compiled.Diagnostics);
            }
            int actualSampleRate = PrepareBackend();
            _session.InvalidateSampleDomainCaches();
            if (compiled.EndTick <= compiled.StartTick)
            {
                ReleaseEditLock();
                ActiveTaskKind = PlaybackTaskKind.None;
                SetState(PlaybackState.Stopped);
                return;
            }
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, actualSampleRate);
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(taskKind == PlaybackTaskKind.SegmentPreview
                ? RealtimePlaybackCacheMode.UnitPcm
                : RealtimePlaybackCacheMode.Disabled);
            _activeResult = compiled;
            _activePlan = plan;
            _backend.Start(plan, soundFont, _masterConfiguration);
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void ExtendHeldPreviewWindowIfNeeded()
    {
        if (!_heldPreviewGateOpen
            || _heldPreviewOpenCompiler is null
            || _activePlan is null
            || _backend is not IHeldPreviewRealtimePlaybackBackend heldBackend)
        {
            return;
        }
        long thresholdFrames = checked(
            (long)_backend.ActualSampleRate * HeldPreviewRenewalThresholdSeconds);
        if (_activePlan.TotalFrameCount - _backend.RenderPositionFrames > thresholdFrames)
        {
            return;
        }

        try
        {
            long producerFrontier = heldBackend.PauseHeldPreviewAtProducerFrontier(
                HeldPreviewBackendTimeout);
            long extensionTicks = CalculateHeldPreviewWindowTicks(
                _activeResult?.TicksPerQuarterNote ?? _session.Project.TicksPerQuarterNote,
                _heldPreviewTempo,
                HeldPreviewWindowSeconds);
            long replacementWindowEndTick = _heldPreviewWindowEndTick >= long.MaxValue - extensionTicks
                ? long.MaxValue
                : _heldPreviewWindowEndTick + extensionTicks;
            if (replacementWindowEndTick == _heldPreviewWindowEndTick)
            {
                heldBackend.ResumeHeldPreviewFromProducerFrontier();
                return;
            }
            CanonicalCompiledResult expanded = _heldPreviewOpenCompiler(
                replacementWindowEndTick);
            RequireConsumablePreview(expanded);
            MidiRenderPlan expandedPlan = MidiRenderPlanAdapter.CreateRealtime(
                expanded,
                _backend.ActualSampleRate);
            MidiRenderPlan replacement = MidiRenderPlanSplicer.SpliceAtProducerFrontier(
                _activePlan,
                expandedPlan,
                producerFrontier);
            using IDisposable replacementResultStorage = _session.RetainPreparationStorage(expanded);
            using IDisposable replacementPlanStorage = _session.RetainPreparationStorage(replacement);
            heldBackend.ReplaceHeldPreviewFutureAndResume(
                replacement,
                producerFrontier,
                HeldPreviewBackendTimeout);
            _activeResult = expanded;
            _activePlan = replacement;
            _activeTempoMap = new(expanded.TicksPerQuarterNote, expanded.Tempos);
            _heldPreviewWindowEndTick = replacementWindowEndTick;
        }
        catch (Exception exception)
        {
            FailHeldPreview(exception);
            throw;
        }
    }

    private static long CalculateHeldPreviewWindowTicks(
        int ticksPerQuarterNote,
        decimal tempo,
        int seconds)
    {
        if (tempo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tempo), "Preview Tempo must be positive.");
        }
        try
        {
            decimal ticks = decimal.Ceiling(
                checked(tempo * ticksPerQuarterNote * seconds / 60m));
            return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, checked((long)ticks));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static decimal ResolvePreviewTempo(
        MidoraProject project,
        EventInstrumentPreviewRequest request) => request.Tempo
        ?? ResolvePreviewTempo(project, request.CursorTick);

    private static decimal ResolvePreviewTempo(
        MidoraProject project,
        long tick) => project.Conductor.Tempos
            .Where(value => value.Tick <= tick)
            .OrderBy(value => value.Tick)
            .LastOrDefault()?.BeatsPerMinute
        ?? project.Conductor.Tempos
            .OrderBy(value => value.Tick)
            .FirstOrDefault()?.BeatsPerMinute
        ?? 120m;

    private static long FirstTickAtOrAfterFrame(
        TempoSampleMap map,
        long frame,
        long originTick,
        int sampleRate,
        long maximumTick)
    {
        long tick = map.SampleFrameToTick(frame, originTick, sampleRate, maximumTick);
        if (tick < maximumTick
            && map.TickToSampleFrame(tick, originTick, sampleRate) < frame)
        {
            tick++;
        }
        return tick;
    }

    private static void RequireConsumablePreview(CanonicalCompiledResult compiled)
    {
        if (!compiled.IsConsumable)
        {
            throw new CompilationRejectedException(compiled.Diagnostics);
        }
    }

    private void FailHeldPreview(Exception failure)
    {
        Exception? cleanupFailure = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        LastError = cleanupFailure is null
            ? failure
            : new AggregateException(
                "Held Preview failed and backend cleanup also failed.",
                failure,
                cleanupFailure);
        SetState(PlaybackState.Error);
    }

    private void ClearHeldPreviewState()
    {
        _heldPreviewOpenCompiler = null;
        _heldPreviewEndCompiler = null;
        _heldPreviewTempo = 0;
        _heldPreviewWindowEndTick = 0;
        _heldPreviewGateOpen = false;
        _releaseEditLockAtHeldGateEnd = false;
    }

    private void RestartAt(long tick, long? endTick)
    {
        long stoppedTick = CurrentTaskTick;
        bool backendStopped = false;
        SetState(PlaybackState.Stopping);
        try
        {
            _backend.Stop(flush: true);
            backendStopped = true;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            StartPreparedRange(tick, endTick, acquireEditLock: false);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            _cursorTick = backendStopped ? tick : stoppedTick;
            ActiveTaskKind = PlaybackTaskKind.None;
            ReleaseEditLock();
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void StopCore(bool applyCursorBehavior, bool releaseEditLock)
    {
        if (State == PlaybackState.Stopped) return;
        PlaybackTaskKind stoppedTask = ActiveTaskKind;
        long stoppedTick = CurrentTaskTick;
        SetState(PlaybackState.Stopping);
        try
        {
            _backend.Stop(flush: true);
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            if (stoppedTask == PlaybackTaskKind.MainTimeline)
            {
                _cursorTick = applyCursorBehavior
                    && _stopCursorBehavior == StopCursorBehavior.ReturnToPlaybackStart
                    ? _taskStartTick
                    : stoppedTick;
            }
            LastError = null;
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Stopped);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            if (stoppedTask == PlaybackTaskKind.MainTimeline)
            {
                _cursorTick = stoppedTick;
            }
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
        finally
        {
            if (releaseEditLock) ReleaseEditLock();
        }
    }

    private long? EffectiveEndTick(long? requested) => _loopRange?.EndTick ?? requested;

    private void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode)
    {
        if (_backend is IRealtimePlaybackCacheBackend cacheBackend)
        {
            cacheBackend.SetNextPlaybackCacheMode(mode);
        }
    }

    private void ConfigureNextBufferingRecovery(
        CanonicalCompiledResult compiled,
        MidiRenderPlan plan)
    {
        _bufferingRecoveryRequested = false;
        _bufferingRecoveryStartFrame = -1;
        _bufferingRecoveryEndFrame = -1;
        _recoveryStorageFailure = null;
        if (_backend is not IBufferingRecoveryRealtimePlaybackBackend recoveryBackend)
        {
            return;
        }

        long maximumFrames = CalculateMaximumRecoveryFrameCount(compiled, plan);
        try
        {
            AudioCacheSessionStore.AudioRecoverySpool spool =
                _session.CreateBufferingRecoverySpool(checked(
                    maximumFrames * 2L * sizeof(float)));
            recoveryBackend.SetNextBufferingRecoveryStorage(spool, maximumFrames);
        }
        catch (AudioRecoveryStorageUnavailableException exception)
        {
            _recoveryStorageFailure = exception;
            recoveryBackend.SetNextBufferingRecoveryStorage(null, maximumFrames);
        }
    }

    private static long CalculateMaximumRecoveryFrameCount(
        CanonicalCompiledResult compiled,
        MidiRenderPlan plan)
    {
        decimal minimumTempo = compiled.Tempos.ToArray()
            .Select(value => value.BeatsPerMinute)
            .DefaultIfEmpty(120m)
            .Min();
        decimal upperBound = decimal.Ceiling(
            BufferingRecoveryPlanner.MaximumQuarterNoteCount
            * 60m
            * plan.SampleRate
            / minimumTempo) + 2m;
        long frames = upperBound >= long.MaxValue
            ? long.MaxValue
            : decimal.ToInt64(upperBound);
        return Math.Max(1, Math.Min(frames, plan.TotalFrameCount));
    }

    private bool TryBeginBufferingRecovery()
    {
        CanonicalCompiledResult compiled = _activeResult
            ?? throw new InvalidOperationException(
                "The active canonical playback result is unavailable during Buffering.");
        TempoSampleMap map = _activeTempoMap
            ?? throw new InvalidOperationException(
                "The active Tempo map is unavailable during Buffering.");
        if (_backend is not IBufferingRecoveryRealtimePlaybackBackend recoveryBackend)
        {
            EnterBufferingRecoveryError(new NotSupportedException(
                "The selected realtime backend cannot prepare a complete natural recovery interval."));
            return false;
        }
        if (!recoveryBackend.HasBufferingRecoveryStorage)
        {
            EnterBufferingRecoveryError(_recoveryStorageFailure
                ?? new AudioRecoveryStorageUnavailableException(
                    "The complete Buffering recovery interval has no reserved storage.",
                    new IOException("No recovery spool is active.")));
            return false;
        }

        try
        {
            long failureFrame = _backend.PositionFrames;
            long failureTick = map.SampleFrameToTick(
                failureFrame,
                compiled.StartTick,
                _backend.ActualSampleRate,
                compiled.EndTick);
            if (failureTick >= compiled.EndTick)
            {
                return true;
            }
            BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
                compiled,
                failureTick,
                compiled.EndTick);
            long recoveryEndFrame = map.TickToSampleFrame(
                interval.EndTick,
                compiled.StartTick,
                _backend.ActualSampleRate);
            if (recoveryEndFrame <= failureFrame)
            {
                throw new InvalidDataException(
                    "The natural Buffering recovery interval did not advance a sample frame.");
            }
            _bufferingRecoveryStartFrame = failureFrame;
            _bufferingRecoveryEndFrame = recoveryEndFrame;
            recoveryBackend.BeginBufferingRecovery(recoveryEndFrame);
            _bufferingRecoveryRequested = true;
            return true;
        }
        catch (Exception exception)
        {
            EnterBufferingRecoveryError(exception);
            return false;
        }
    }

    private void EnterBufferingRecoveryError(Exception failure)
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        _bufferingRecoveryRequested = false;
        _bufferingRecoveryStartFrame = -1;
        _bufferingRecoveryEndFrame = -1;
        LastError = cleanupError is null
            ? failure
            : new AggregateException(
                "Buffering recovery failed and backend cleanup also failed.",
                failure,
                cleanupError);
        SetState(PlaybackState.Error);
    }

    private string RequireEffectiveSoundFont(string operation)
    {
        string soundFont = _session.EffectiveSoundFontPath
            ?? throw new InvalidOperationException(
                $"{operation} requires at least one enabled application SoundFont.");
        string? missing = _session.EffectiveSoundFontPaths.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
        {
            throw new FileNotFoundException(
                "An enabled application SoundFont does not exist.",
                missing);
        }
        _session.RefreshEffectiveSoundFontCacheIdentity();
        return soundFont;
    }

    private void ApplyMonitoringChange()
    {
        bool active = ActiveTaskKind == PlaybackTaskKind.MainTimeline
            && (State is PlaybackState.Playing or PlaybackState.Buffering);
        HashSet<MidoraId> previouslyAudible = new(_audibleTracks);
        RebuildAudibleTracks();
        if (!active)
        {
            return;
        }

        CanonicalCompiledResult compiled = _activeResult
            ?? throw new InvalidOperationException("The active canonical playback result is unavailable.");
        MidiRenderPlan plan = _activePlan
            ?? throw new InvalidOperationException("The active sample-domain playback plan is unavailable.");
        long renderTick = _activeTempoMap!.SampleFrameToTick(
            _backend.PositionFrames,
            compiled.StartTick,
            _backend.ActualSampleRate,
            compiled.EndTick);
        MidoraId[] newlyDisabled = previouslyAudible.Except(_audibleTracks).OrderBy(value => value).ToArray();
        MidoraId[] newlyEnabled = _audibleTracks.Except(previouslyAudible).OrderBy(value => value).ToArray();
        if (newlyDisabled.Length == 0 && newlyEnabled.Length == 0)
        {
            return;
        }

        CanonicalCompiledResult? restoreResult = newlyEnabled.Length == 0
            ? null
            : _session.CompileForPlayback(renderTick, EffectiveEndTick(_requestedEndTick));
        if (restoreResult is not null && !restoreResult.IsConsumable)
        {
            throw new InvalidOperationException("A monitoring cold-start state could not be compiled.");
        }
        ChannelUnitAllocation[] activeAllocations = compiled.Allocations.ToArray()
            .Where(value => value.StartTick <= renderTick && value.EndTick > renderTick)
            .ToArray();
        Dictionary<(MidoraId TrackId, MidoraId InstanceId, MidoraId SubVoiceId), ChannelUnitAllocation>
            activeLogicalRouting = activeAllocations
                .Where(value => value.MidiChannelRootId == default)
                .Where(value => value.StartTick <= renderTick && value.EndTick > renderTick)
                .ToDictionary(
                    value => (value.TrackId, value.InstanceId, value.SubVoiceId),
                    value => value);
        Dictionary<MidoraId, ChannelUnitAllocation> activeRootRouting = activeAllocations
            .Where(value => value.MidiChannelRootId != default)
            .GroupBy(value => value.MidiChannelRootId)
            .ToDictionary(
                value => value.Key,
                value => value.OrderBy(item => item.StartTick).ThenBy(item => item.EndTick).First());

        List<MidiMonitoringCommand> commands = [];
        foreach (MidoraId trackId in newlyDisabled)
        {
            int sourceIndex = plan.FindSourceIndex(trackId.Value);
            if (sourceIndex < 0) continue;
            commands.Add(MidiMonitoringCommand.DisableSource(sourceIndex));
            AppendTrackCleanup(commands, compiled, trackId, renderTick);
        }
        foreach (MidoraId trackId in newlyEnabled)
        {
            int sourceIndex = plan.FindSourceIndex(trackId.Value);
            if (sourceIndex < 0) continue;
            commands.Add(MidiMonitoringCommand.EnableSource(sourceIndex));
            foreach (CanonicalMidiEvent value in restoreResult!.EnumerateResidentAndLogicalEvents(
                restoreResult.StartTick, restoreResult.StartTick, includeEnd: true))
            {
                if (value.Tick == restoreResult.StartTick
                    && value.Role == CanonicalEventRole.RangeRestore
                    && value.Source.TrackId == trackId)
                {
                    ChannelUnitAllocation activeAllocation;
                    bool routed = value.Source.MidiChannelRootId != default
                        ? activeRootRouting.TryGetValue(
                            value.Source.MidiChannelRootId,
                            out activeAllocation)
                        : activeLogicalRouting.TryGetValue(
                            (
                                value.Source.TrackId,
                                value.Source.LogicalNoteId,
                                value.Source.SubVoiceId),
                            out activeAllocation);
                    if (!routed)
                    {
                        throw new InvalidOperationException(
                            "A monitoring restore event could not be routed to its active canonical Channel Unit.");
                    }
                    commands.Add(MidiMonitoringCommand.Send(
                        activeAllocation.ZeroBasedPort,
                        WithChannel(value.Message, activeAllocation.ZeroBasedChannel)));
                }
            }
        }
        if (commands.Count != 0)
        {
            _backend.ApplyMonitoringCommands(CollectionsMarshal.AsSpan(commands));
        }
    }

    private static MidiMessage WithChannel(MidiMessage message, byte channel)
    {
        if (!message.IsChannelVoiceMessage)
        {
            throw new InvalidOperationException(
                "Monitoring state restore accepts only canonical MIDI channel messages.");
        }
        uint packed = message.PackedValue & ~MidiMessage.ChannelNumberMask | channel;
        return MidiMessage.FromPackedValue(packed);
    }

    private readonly MonitoringNoteBalance _monitoringNoteBalance = new();

    private void AppendTrackCleanup(
        List<MidiMonitoringCommand> commands,
        CanonicalCompiledResult compiled,
        MidoraId trackId,
        long tick)
    {
        Dictionary<(byte Port, byte Channel, byte Key), int> activeNotes =
            _monitoringNoteBalance.Read(compiled, trackId, tick);

        foreach (((byte port, byte channel, byte key), int count) in activeNotes
            .OrderBy(value => value.Key.Port)
            .ThenBy(value => value.Key.Channel)
            .ThenBy(value => value.Key.Key))
        {
            for (int index = 0; index < count; index++)
            {
                commands.Add(MidiMonitoringCommand.Send(
                    port,
                    MidiMessage.NoteOff(channel, key, 0)));
            }
        }
    }

    /// <summary>Incremental prefix counts, not retained MIDI events or live source readers.</summary>
    private sealed class MonitoringNoteBalance
    {
        private WeakReference<CanonicalCompiledResult>? _source;
        private long _tick;
        private readonly Dictionary<(MidoraId Track, byte Port, byte Channel, byte Key), int> _counts = [];

        public Dictionary<(byte Port, byte Channel, byte Key), int> Read(
            CanonicalCompiledResult source, MidoraId track, long tick)
        {
            if (_source is null || !_source.TryGetTarget(out var previous)
                || !ReferenceEquals(previous, source) || tick < _tick)
            {
                _source = new(source); _tick = source.StartTick; _counts.Clear();
            }
            try
            {
                foreach (CanonicalMidiEvent value in source.EnumerateResidentAndLogicalEvents(_tick, tick, false))
                {
                    MidiMessage message = value.Message;
                    bool on = message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0;
                    bool off = message.MessageType == MidiMessageType.NoteOff
                        || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0;
                    if (!on && !off) continue;
                    var key = (value.Source.TrackId, value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                    int count = _counts.GetValueOrDefault(key);
                    if (on) _counts[key] = checked(count + 1);
                    else if (count > 1) _counts[key] = count - 1;
                    else _counts.Remove(key);
                }
                _tick = tick;
            }
            catch { _source = null; _counts.Clear(); throw; }
            Dictionary<(byte Port, byte Channel, byte Key), int> result = [];
            foreach (var (key, count) in _counts)
                if (key.Track == track) result[(key.Port, key.Channel, key.Key)] = count;
            return result;
        }
    }

    private void RebuildAudibleTracks()
    {
        _audibleTracks.Clear();
        MidoraProject project = _session.Project;
        HashSet<MidoraId> existingGroups = project.EventInstrumentUsages
            .Select(value => value.Id)
            .Concat(project.MidiChannelRoots.Select(value => value.Id))
            .ToHashSet();
        _mutedSharedGroups.IntersectWith(existingGroups);
        _soloSharedGroups.IntersectWith(existingGroups);
        bool hasSharedGroupSolo = _soloSharedGroups.Count != 0;
        bool hasTrackSolo = !hasSharedGroupSolo && _soloTracks.Count != 0;
        foreach (ArrangementTrackReference track in project.TracksInArrangementOrder())
        {
            if (!TryResolveSharedGroupId(project, track, out MidoraId sharedGroupId))
            {
                // Empty unbound Logical Tracks are legal creation shells but have
                // no runtime monitoring identity until a Usage is assigned.
                continue;
            }
            bool audible = !_mutedTracks.Contains(track.TrackId)
                && !_mutedSharedGroups.Contains(sharedGroupId)
                && (hasSharedGroupSolo
                    ? _soloSharedGroups.Contains(sharedGroupId)
                    : !hasTrackSolo || _soloTracks.Contains(track.TrackId));
            if (audible)
            {
                _audibleTracks.Add(track.TrackId);
            }
        }
    }

    private void EnsureTrackExists(MidoraId trackId)
    {
        if (!_session.Project.Tracks.Any(value => value.Id == trackId)
            && !_session.Project.PureMidiTracks.Any(value => value.Id == trackId))
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }
    }

    private static bool TryResolveSharedGroupId(
        MidoraProject project,
        ArrangementTrackReference track,
        out MidoraId sharedGroupId)
    {
        if (track.Kind == ArrangementTrackKind.LogicalTrack)
        {
            MidoraId? usageId = project.Tracks
                .Single(value => value.Id == track.TrackId)
                .EventInstrumentUsageId;
            sharedGroupId = usageId ?? default;
            return usageId.HasValue;
        }
        if (track.Kind == ArrangementTrackKind.PureMidiTrack)
        {
            sharedGroupId = project.PureMidiTracks
                .Single(value => value.Id == track.TrackId)
                .MidiChannelRootId;
            return true;
        }
        throw new InvalidOperationException("Unknown Arrangement Track kind.");
    }

    private void EnsureSharedGroupExists(MidoraId sharedGroupId)
    {
        if (!_session.Project.EventInstrumentUsages.Any(value => value.Id == sharedGroupId)
            && !_session.Project.MidiChannelRoots.Any(value => value.Id == sharedGroupId))
        {
            throw new ArgumentOutOfRangeException(nameof(sharedGroupId));
        }
    }

    private void EnsureCanStartTask()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OutputDeviceSelectionRequired)
        {
            throw new OutputDeviceSelectionRequiredException(
                "The active output device became unavailable. Select an output device explicitly before starting playback or preview again.");
        }
        if (State == PlaybackState.Error)
        {
            ResetPlaybackEngine();
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException("A playback or preview task is already active.");
        }
    }

    private void EnterBackendError()
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        string description = _backend.FaultDescription ?? "The realtime playback backend reported an unrecoverable fault.";
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        LastError = cleanupError is null
            ? new InvalidOperationException(description)
            : new AggregateException(description, cleanupError);
        SetState(PlaybackState.Error);
    }

    private void HandleCompilationChangedForPrewarm(object? sender, EventArgs eventArgs)
    {
        if (_disposed
            || State != PlaybackState.Stopped
            || Volatile.Read(ref _knownSampleRate) == 0
            || !_session.IsCompilationCurrent
            || _session.CompilationState != ProjectCompilationState.Succeeded)
        {
            return;
        }
        BeginDefaultPlaybackPreparation();
    }

    private void RunDefaultPlaybackPreparationLoop()
    {
        CancellationToken cancellationToken = _prewarmCancellation.Token;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long requestGeneration;
            lock (_prewarmSync)
            {
                requestGeneration = _prewarmRequestGeneration;
            }
            try
            {
                int sampleRate = Volatile.Read(ref _knownSampleRate);
                if (sampleRate == 0)
                {
                    sampleRate = PrepareBackend(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _session.PrewarmDefaultRealtimeRenderPlan(sampleRate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best effort only. Formal preparation and diagnostics still run
                // synchronously when playback is explicitly requested.
            }
            lock (_prewarmSync)
            {
                if (requestGeneration == _prewarmRequestGeneration)
                {
                    return;
                }
            }
        }
    }

    private void WaitForDefaultPlaybackPreparation()
    {
        Task task;
        lock (_prewarmSync)
        {
            task = _prewarmTask;
        }
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_prewarmCancellation.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(PlaybackController));
        }
    }

    private int PrepareBackend()
        => PrepareBackend(CancellationToken.None);

    private int PrepareBackend(CancellationToken cancellationToken)
    {
        lock (_backendPreparationSync)
        {
            int sampleRate = _backend is ICancellableRealtimePlaybackPreparationBackend cancellable
                ? cancellable.Prepare(cancellationToken)
                : _backend.Prepare();
            Volatile.Write(ref _knownSampleRate, sampleRate);
            return sampleRate;
        }
    }

    private void EnterOutputDeviceSelectionRequired()
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        string description = _backend.OutputDeviceSelectionReason
            ?? "The active output device became unavailable.";
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: false);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        OutputDeviceSelectionRequired = true;
        OutputDeviceSelectionRequiredException selectionRequired = new(
            $"{description} Select an output device explicitly before playback or preview can start again.");
        LastError = cleanupError is null
            ? selectionRequired
            : new AggregateException(selectionRequired.Message, selectionRequired, cleanupError);
        SetState(cleanupError is null ? PlaybackState.Stopped : PlaybackState.Error);
    }

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseEditLock()
    {
        _editLockLease?.Dispose();
        _editLockLease = null;
    }
}
