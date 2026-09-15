using System.Globalization;
using System.Runtime.Versioning;
using Midora.Common;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
internal sealed class PersistentBassMidiAudioWorkerSession : IBassMidiAudioWorkerSession
{
    private readonly PersistentBassMidiAudioWorkerHost _host;
    private readonly MidoraOwnedTemporaryDirectoryLease _ownedTemporaryDirectoryLease;
    private readonly string _ownedTemporaryDirectory;
    private readonly IAudioPcmCacheSessionAccess? _audioCache;
    private readonly AudioSegmentCacheStaging? _cacheStaging;
    private readonly PlaybackSpanCacheStaging? _playbackSpanCacheStaging;
    private readonly MidiRenderEventStreamProducer? _eventStreamProducer;
    private readonly long _generation;
    private readonly long _totalFrameCount;
    private string? _standardError;
    private int? _exitCode;
    private int _cachePublished;
    private long _nextHeldPreviewPlanGeneration;
    private bool _completed;
    private bool _disposed;

    public PersistentBassMidiAudioWorkerSession(
        PersistentBassMidiAudioWorkerHost host,
        MidiRenderPlan plan,
        string soundFontSetCacheIdentity,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        string? deviceId,
        TimeSpan preparingTimeout,
        IAudioPcmCacheSessionAccess? audioCache,
        string? bufferingRecoverySpoolPath,
        long bufferingRecoveryMemoryFrameCapacity,
        bool playbackSpanCacheEnabled)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(plan);
        AudioUnitCacheStaging.ValidateSoundFontSetCacheIdentity(soundFontSetCacheIdentity);
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds);
        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }
        if (bufferingRecoveryMemoryFrameCapacity < 0
            || bufferingRecoveryMemoryFrameCapacity > plan.TotalFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferingRecoveryMemoryFrameCapacity));
        }

        _audioCache = audioCache;
        _totalFrameCount = plan.TotalFrameCount;
        _ownedTemporaryDirectoryLease = MidoraOwnedTemporaryDirectoryLease.Create(
            MidoraProgramData.Current.AudioWorkerExchangeDirectory,
            "midora-audio-task");
        _ownedTemporaryDirectory = _ownedTemporaryDirectoryLease.DirectoryPath;
        try
        {
            if (plan.EventPageProvider is not null) playbackSpanCacheEnabled = false;
            if (playbackSpanCacheEnabled)
            {
                try
                {
                    _playbackSpanCacheStaging = PlaybackSpanCacheStaging.Create(
                        plan,
                        audioCache,
                        soundFontSetCacheIdentity,
                        host.NativeDirectory,
                        rendererSettings.MaximumSampleVoicesPerUnitStream,
                        masterSettings);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    audioCache?.DisableReusableAudioRetention(
                        "Playback-span cache staging failed; playback will use Segment PCM or live synthesis. "
                            + exception.Message);
                }
            }
            try
            {
                _cacheStaging = AudioSegmentCacheStaging.Create(
                    plan,
                    audioCache,
                    soundFontSetCacheIdentity,
                    host.NativeDirectory,
                    rendererSettings.MaximumSampleVoicesPerUnitStream,
                    _ownedTemporaryDirectory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                audioCache?.DisableReusableAudioRetention(
                    "Reusable Segment audio cache staging failed; new cache misses will be rendered without retention. "
                        + exception.Message);
            }
            plan = _cacheStaging?.Plan ?? plan;
            _eventStreamProducer = MidiRenderEventStreamProducer.Create(
                plan,
                _ownedTemporaryDirectory);
            if (_eventStreamProducer is not null)
                plan = plan.WithEventStreamDescriptor(_eventStreamProducer.Descriptor);
            string planPath = Path.Combine(_ownedTemporaryDirectory, "compiled-audio-plan.mdap");
            MidiRenderPlanFile.Write(planPath, plan);
            string[] arguments = BuildPlaybackArguments(
                host.Control.Name,
                planPath,
                host.SoundFontPath,
                host.NativeDirectory,
                deviceId,
                renderAheadMilliseconds,
                deviceBufferRequestMilliseconds,
                rendererSettings,
                masterSettings,
                plan.SampleRate,
                _cacheStaging?.FilePath,
                _cacheStaging?.ReadManifestPath,
                bufferingRecoverySpoolPath,
                bufferingRecoveryMemoryFrameCapacity,
                _playbackSpanCacheStaging?.FilePath,
                _playbackSpanCacheStaging?.Hit == true,
                playbackSpanCacheEnabled,
                audioCache?.AudioCacheSnapshot?.SequentialWriteBytesPerSecond ?? 0);
            _generation = host.StartPlayback(arguments);
            WaitUntilPlaybackStarted(preparingTimeout);
        }
        catch (Exception startFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                _host.CancelActivePlayback(preparingTimeout);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                try
                {
                    _host.Dispose();
                }
                catch (Exception disposalException)
                {
                    cleanupFailure = new AggregateException(
                        cleanupFailure,
                        disposalException);
                }
            }
            _cacheStaging?.Dispose();
            _playbackSpanCacheStaging?.Dispose();
            _eventStreamProducer?.Dispose();
            CleanupOwnedTemporaryDirectory();
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Persistent playback preparation and cleanup both failed.",
                    startFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public AudioWorkerStatus Status => _host.Status;
    public int? ExitCode => _exitCode ?? (_host.HasExited ? 1 : null);
    public string? StandardError => _standardError ?? _host.StandardError;

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioWorkerStatus status = Status;
        // Status.PositionFrame is callback-consumed rather than guaranteed audible.
        // Rewind by the complete device buffer so the newly published event
        // generation always begins at or before the Worker's exact audible
        // frontier. The Worker then seeks forward to that exact frontier.
        long conservativeRewindFrame = Math.Max(
            0,
            status.PositionFrame - status.ActualDeviceBufferFrameCount);
        _eventStreamProducer?.ApplyMonitoringCommands(
            commands,
            Math.Clamp(conservativeRewindFrame, 0, _totalFrameCount));
        if (!_host.Control.TryEnqueueMonitoringCommands(commands))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
    }

    public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_host.Control.TryEnqueueHeldPreviewPause())
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        return WaitForHeldPreviewStatus(
            static value => value.State == AudioWorkerState.HeldPreviewPaused,
            timeout,
            "pause").RenderPositionFrame;
    }

    public void ReplaceHeldPreviewFutureAndResume(
        MidiRenderPlan plan,
        long producerFrontierFrame,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        AudioWorkerStatus paused = Status;
        if (paused.State != AudioWorkerState.HeldPreviewPaused
            || paused.RenderPositionFrame != producerFrontierFrame
            || plan.TotalFrameCount < producerFrontierFrame)
        {
            throw new InvalidOperationException(
                "The audio worker is not paused at the requested held-preview frontier.");
        }
        long generation = checked(++_nextHeldPreviewPlanGeneration);
        string path = Path.Combine(
            _ownedTemporaryDirectory,
            HeldPreviewPlanExchange.GetFileName(generation));
        MidiRenderPlanFile.Write(path, plan);
        if (!_host.Control.TryEnqueueHeldPreviewApplyPlan(generation))
        {
            File.Delete(path);
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        _ = WaitForHeldPreviewStatus(
            value => value.HeldPreviewPlanGeneration == generation
                && value.State is AudioWorkerState.Playing
                    or AudioWorkerState.Buffering
                    or AudioWorkerState.Completed,
            timeout,
            "apply a replacement plan and resume");
    }

    public void ResumeHeldPreviewFromProducerFrontier(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_host.Control.TryEnqueueHeldPreviewResume())
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        _ = WaitForHeldPreviewStatus(
            static value => value.State is AudioWorkerState.Playing
                or AudioWorkerState.Buffering
                or AudioWorkerState.Completed,
            timeout,
            "resume");
    }

    public void BeginBufferingRecovery(long recoveryEndFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_host.Control.TryEnqueueBufferingRecovery(recoveryEndFrame))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
    }

    public void Stop(bool flush, TimeSpan timeout)
    {
        if (_disposed || _completed)
        {
            return;
        }
        AudioWorkerState state = Status.State;
        if (state is not AudioWorkerState.Completed
            and not AudioWorkerState.Stopped
            and not AudioWorkerState.OutputDeviceUnavailable
            and not AudioWorkerState.Faulted
            && !_host.Control.TryEnqueueStop(flush))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        PersistentAudioWorkerResponse response = _host.CompletePlayback(_generation, timeout);
        _completed = true;
        _exitCode = response.Succeeded ? 0 : 1;
        _standardError = response.Error;
        AudioWorkerStatus terminal = Status;
        if (!response.Succeeded
            || terminal.State is not AudioWorkerState.Completed
                and not AudioWorkerState.Stopped
                and not AudioWorkerState.OutputDeviceUnavailable)
        {
            throw new MidoraAudioException(
                $"The audio worker did not stop cleanly; state={terminal.State}; "
                + $"fault={terminal.FaultCode}; error={response.Error}");
        }
        PublishCompletedCache(terminal.RenderPositionFrame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            if (!_completed)
            {
                Stop(flush: true, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _disposed = true;
            _cacheStaging?.Dispose();
            _playbackSpanCacheStaging?.Dispose();
            _eventStreamProducer?.Dispose();
            CleanupOwnedTemporaryDirectory();
        }
    }

    private void PublishCompletedCache(long completedRenderFrame)
    {
        if (_audioCache is null || Interlocked.Exchange(ref _cachePublished, 1) != 0)
        {
            return;
        }
        _cacheStaging?.PublishCompleted(_audioCache, completedRenderFrame);
        _playbackSpanCacheStaging?.PublishCompleted(
            _audioCache,
            completedRenderFrame,
            _totalFrameCount);
        _ = _audioCache.TryCompactReusableAudio(isStopped: true, idleDuration: TimeSpan.Zero);
    }

    private void WaitUntilPlaybackStarted(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            AudioWorkerStatus status = Status;
            if (status.State is AudioWorkerState.Playing
                or AudioWorkerState.Buffering
                or AudioWorkerState.Completed
                or AudioWorkerState.OutputDeviceUnavailable)
            {
                return;
            }
            if (status.State == AudioWorkerState.Faulted || _host.HasExited)
            {
                PersistentAudioWorkerResponse response = _host.CompletePlayback(
                    _generation,
                    TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)));
                _completed = true;
                _exitCode = response.Succeeded ? 0 : 1;
                _standardError = response.Error;
                throw new MidoraAudioException(
                    $"The persistent audio worker failed during Preparing: {response.Error}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("The persistent audio worker did not start within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private AudioWorkerStatus WaitForHeldPreviewStatus(
        Func<AudioWorkerStatus, bool> predicate,
        TimeSpan timeout,
        string operation)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            AudioWorkerStatus status = Status;
            if (predicate(status))
            {
                return status;
            }
            if (status.State is AudioWorkerState.Faulted or AudioWorkerState.OutputDeviceUnavailable
                || _host.HasExited)
            {
                throw new MidoraAudioException(
                    $"The audio worker could not {operation} held Preview; state={status.State}; "
                    + $"fault={status.FaultCode}; stderr={StandardError}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    $"The audio worker did not {operation} held Preview within the requested timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private static string[] BuildPlaybackArguments(
        string controlName,
        string planPath,
        string soundFontPath,
        string nativeDirectory,
        string? deviceId,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int expectedSampleRate,
        string? cacheStagingPath,
        string? cacheReadManifestPath,
        string? bufferingRecoverySpoolPath,
        long bufferingRecoveryMemoryFrameCapacity,
        string? playbackSpanCacheStagingPath,
        bool playbackSpanCacheHit,
        bool rollingPreparationEnabled,
        long measuredCacheWriteBytesPerSecond) =>
        [
            "play",
            controlName,
            planPath,
            soundFontPath,
            nativeDirectory,
            deviceId ?? string.Empty,
            renderAheadMilliseconds.ToString(CultureInfo.InvariantCulture),
            deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture),
            rendererSettings.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture),
            rendererSettings.MaximumWorkFrameCount.ToString(CultureInfo.InvariantCulture),
            masterSettings.VolumeDecibels.ToString("R", CultureInfo.InvariantCulture),
            masterSettings.LimiterCeiling.ToString("R", CultureInfo.InvariantCulture),
            masterSettings.LimiterReleaseMilliseconds.ToString("R", CultureInfo.InvariantCulture),
            masterSettings.LimiterEnabled ? "1" : "0",
            expectedSampleRate.ToString(CultureInfo.InvariantCulture),
            cacheStagingPath ?? string.Empty,
            cacheReadManifestPath ?? string.Empty,
            bufferingRecoverySpoolPath ?? string.Empty,
            bufferingRecoveryMemoryFrameCapacity.ToString(CultureInfo.InvariantCulture),
            playbackSpanCacheStagingPath ?? string.Empty,
            playbackSpanCacheHit ? "1" : "0",
            rollingPreparationEnabled ? "1" : "0",
            measuredCacheWriteBytesPerSecond.ToString(CultureInfo.InvariantCulture)
        ];

    private void CleanupOwnedTemporaryDirectory()
    {
        _ownedTemporaryDirectoryLease.Dispose();
    }
}
