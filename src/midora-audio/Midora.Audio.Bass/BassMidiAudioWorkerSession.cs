using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Midora.Common;

namespace Midora.Audio.Bass;

public readonly record struct BassMidiAudioWorkerProbeResult(
    int ActualSampleRate,
    int ActualDeviceBufferFrameCount);

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioWorkerSession : IDisposable, IBassMidiAudioWorkerSession
{
    private readonly MidoraOwnedTemporaryDirectoryLease _ownedTemporaryDirectoryLease;
    private readonly string _ownedTemporaryDirectory;
    private readonly SharedAudioWorkerControl _control;
    private readonly Process _process;
    private readonly Thread _monitorThread;
    private readonly IAudioPcmCacheSessionAccess? _audioCache;
    private readonly AudioSegmentCacheStaging? _cacheStaging;
    private readonly PlaybackSpanCacheStaging? _playbackSpanCacheStaging;
    private readonly MidiRenderEventStreamProducer? _eventStreamProducer;
    private readonly long _totalFrameCount;
    private string? _standardError;
    private int _exitCode = int.MinValue;
    private int _monitorFaulted;
    private int _cachePublished;
    private long _nextHeldPreviewPlanGeneration;
    private bool _disposed;

    public BassMidiAudioWorkerSession(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        string? deviceId,
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout,
        IAudioPcmCacheSessionAccess? audioCache = null,
        string? bufferingRecoverySpoolPath = null,
        long bufferingRecoveryMemoryFrameCapacity = 0,
        bool playbackSpanCacheEnabled = false,
        string? soundFontSetCacheIdentity = null)
        : this(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds,
            deviceId,
            workerPath,
            bassNativeDirectory,
            preparingTimeout,
            allowManagedTestWorker: false,
            audioCache,
            bufferingRecoverySpoolPath,
            bufferingRecoveryMemoryFrameCapacity,
            playbackSpanCacheEnabled,
            soundFontSetCacheIdentity)
    {
    }

    internal BassMidiAudioWorkerSession(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        string? deviceId,
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker,
        IAudioPcmCacheSessionAccess? audioCache = null,
        string? bufferingRecoverySpoolPath = null,
        long bufferingRecoveryMemoryFrameCapacity = 0,
        bool playbackSpanCacheEnabled = false,
        string? soundFontSetCacheIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        ValidateCommon(
            workerPath,
            bassNativeDirectory,
            deviceBufferRequestMilliseconds,
            preparingTimeout,
            allowManagedTestWorker);
        if (renderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(renderAheadMilliseconds));
        }
        InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds);
        if (!File.Exists(soundFontPath))
        {
            throw new FileNotFoundException(
                "The enabled application SoundFont does not exist.",
                soundFontPath);
        }
        workerPath = Path.GetFullPath(workerPath);
        bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        soundFontPath = Path.GetFullPath(soundFontPath);
        if (bufferingRecoverySpoolPath is not null)
        {
            bufferingRecoverySpoolPath = Path.GetFullPath(bufferingRecoverySpoolPath);
            if (!File.Exists(bufferingRecoverySpoolPath))
            {
                throw new FileNotFoundException(
                    "The Buffering recovery spool does not exist.",
                    bufferingRecoverySpoolPath);
            }
        }
        if (audioCache is not null)
        {
            if (soundFontSetCacheIdentity is null)
            {
                throw new ArgumentException(
                    "An application SoundFont-set cache identity is required when reusable audio caching is enabled.",
                    nameof(soundFontSetCacheIdentity));
            }
            AudioUnitCacheStaging.ValidateSoundFontSetCacheIdentity(soundFontSetCacheIdentity);
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
            "midora-audio-worker");
        _ownedTemporaryDirectory = _ownedTemporaryDirectoryLease.DirectoryPath;
        SharedAudioWorkerControl? createdControl = null;
        Process? startedProcess = null;
        Thread? startedMonitorThread = null;
        try
        {
            if (playbackSpanCacheEnabled)
            {
                try
                {
                    _playbackSpanCacheStaging = PlaybackSpanCacheStaging.Create(
                        plan,
                        audioCache,
                        soundFontSetCacheIdentity ?? string.Empty,
                        bassNativeDirectory,
                        rendererSettings.MaximumSampleVoicesPerUnitStream,
                        masterSettings);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    audioCache?.DisableReusableAudioRetention(
                        "Playback-span cache staging failed; playback will use Unit PCM or live synthesis. "
                            + exception.Message);
                    _playbackSpanCacheStaging = null;
                }
            }
            try
            {
                _cacheStaging = AudioSegmentCacheStaging.Create(
                    plan,
                    audioCache,
                    soundFontSetCacheIdentity ?? string.Empty,
                    bassNativeDirectory,
                    rendererSettings.MaximumSampleVoicesPerUnitStream,
                    _ownedTemporaryDirectory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                // Reusable retention is opportunistic. Failure to stage it must not prevent
                // formal realtime playback; the renderer falls back to live synthesis.
                audioCache?.DisableReusableAudioRetention(
                    "Reusable Segment audio cache staging failed; new cache misses will be rendered without retention. "
                        + exception.Message);
                _cacheStaging = null;
            }
            plan = _cacheStaging?.Plan ?? plan;
            _eventStreamProducer = MidiRenderEventStreamProducer.Create(
                plan,
                _ownedTemporaryDirectory);
            if (_eventStreamProducer is not null)
            {
                plan = plan.WithEventStreamDescriptor(_eventStreamProducer.Descriptor);
            }
            string planPath = Path.Combine(_ownedTemporaryDirectory, "compiled-audio-plan.mdap");
            MidiRenderPlanFile.Write(planPath, plan);
            createdControl = SharedAudioWorkerControl.Create(
                $"Midora.Audio.Control.{Guid.NewGuid():N}");
            _control = createdControl;
            ProcessStartInfo startInfo = CreateStartInfo(workerPath);
            AddPlaybackArguments(
                startInfo,
                _control.Name,
                planPath,
                soundFontPath,
                bassNativeDirectory,
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
            startedProcess = AudioWorkerProcessGroup.Start(
                startInfo,
                "Could not start the Midora audio worker process.");
            _process = startedProcess;
            Task<string> standardError = startedProcess.StandardError.ReadToEndAsync();
            Task<string> standardOutput = startedProcess.StandardOutput.ReadToEndAsync();
            startedMonitorThread = new Thread(
                () => MonitorProcess(standardError, standardOutput))
            {
                IsBackground = true,
                Name = "Midora Audio Worker Monitor"
            };
            _monitorThread = startedMonitorThread;
            _monitorThread.Start();
            WaitUntilPlaybackStarted(preparingTimeout);
        }
        catch
        {
            if (startedProcess is not null)
            {
                TerminateProcess(startedProcess);
                if (startedMonitorThread is { IsAlive: true })
                {
                    startedMonitorThread.Join();
                }
                startedProcess.Dispose();
            }
            createdControl?.Dispose();
            _cacheStaging?.Dispose();
            _playbackSpanCacheStaging?.Dispose();
            _eventStreamProducer?.Dispose();
            _ownedTemporaryDirectoryLease.Dispose();
            throw;
        }
    }

    public AudioWorkerStatus Status => _control.ReadStatus();

    public int? ExitCode => Volatile.Read(ref _exitCode) == int.MinValue
        ? null
        : Volatile.Read(ref _exitCode);

    public string? StandardError => _standardError;

    public static BassMidiAudioWorkerProbeResult Probe(
        string workerPath,
        string bassNativeDirectory,
        string? deviceId,
        int deviceBufferRequestMilliseconds,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateCommon(
            workerPath,
            bassNativeDirectory,
            deviceBufferRequestMilliseconds,
            timeout,
            allowManagedTestWorker: false);
        workerPath = Path.GetFullPath(workerPath);
        bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.Probe.{Guid.NewGuid():N}");
        using Process process = StartProbe(
            workerPath,
            control.Name,
            bassNativeDirectory,
            deviceId,
            deviceBufferRequestMilliseconds);
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TerminateProcess(process);
                _ = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            AudioWorkerStatus status = control.ReadStatus();
            if (status.State == AudioWorkerState.Prepared)
            {
                if (!process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(process);
                    _ = standardError.GetAwaiter().GetResult();
                    _ = standardOutput.GetAwaiter().GetResult();
                    throw new TimeoutException("The audio worker probe did not exit within the Preparing timeout.");
                }
                string error = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new MidoraAudioException(
                        $"The audio worker probe exited with code {process.ExitCode}: {error}");
                }
                return new(status.ActualSampleRate, status.ActualDeviceBufferFrameCount);
            }
            if (status.State == AudioWorkerState.Faulted || process.HasExited)
            {
                if (!process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(process);
                }
                string error = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                throw new MidoraAudioException(
                    $"The audio worker probe failed; fault={status.FaultCode}; exitCode={process.ExitCode}; stderr={error}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                TerminateProcess(process);
                _ = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                throw new TimeoutException("The audio worker probe did not complete within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _eventStreamProducer?.ApplyMonitoringCommands(
            commands,
            Math.Clamp(Status.PositionFrame, 0, _totalFrameCount));
        if (!commands.IsEmpty)
        {
            _playbackSpanCacheStaging?.InvalidateCapture();
        }
        if (!_control.TryEnqueueMonitoringCommands(commands))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
    }

    public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_control.TryEnqueueHeldPreviewPause())
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        AudioWorkerStatus status = WaitForHeldPreviewStatus(
            static value => value.State == AudioWorkerState.HeldPreviewPaused,
            timeout,
            "pause");
        return status.RenderPositionFrame;
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
        if (!_control.TryEnqueueHeldPreviewApplyPlan(generation))
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
        if (Status.State != AudioWorkerState.HeldPreviewPaused)
        {
            throw new InvalidOperationException("The audio worker held-preview producer is not paused.");
        }
        if (!_control.TryEnqueueHeldPreviewResume())
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

    public void Stop(bool flush, TimeSpan timeout)
    {
        if (_disposed)
        {
            return;
        }
        if (!_process.HasExited)
        {
            if (!_control.TryEnqueueStop(flush))
            {
                throw new InvalidOperationException("The bounded audio worker command ring is full.");
            }
            if (!_process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
            {
                TerminateProcess(_process);
                _monitorThread.Join();
                throw new TimeoutException("The audio worker did not stop within the requested timeout.");
            }
        }
        _monitorThread.Join();
        AudioWorkerStatus status = Status;
        if (!IsSuccessfulTerminalExit(status.State, ExitCode))
        {
            throw new MidoraAudioException(
                $"The audio worker did not stop cleanly; state={status.State}; fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
        }
        PublishCompletedCache(status.RenderPositionFrame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_process.HasExited)
        {
            if (!_control.TryEnqueueStop() || !_process.WaitForExit(5_000))
            {
                TerminateProcess(_process);
            }
        }
        _monitorThread.Join();
        if (IsSuccessfulTerminalExit(Status.State, ExitCode))
        {
            PublishCompletedCache(Status.RenderPositionFrame);
        }
        _process.Dispose();
        _control.Dispose();
        _cacheStaging?.Dispose();
        _playbackSpanCacheStaging?.Dispose();
        _eventStreamProducer?.Dispose();
        _ownedTemporaryDirectoryLease.Dispose();
    }

    private static Process StartProbe(
        string workerPath,
        string controlName,
        string nativeDirectory,
        string? deviceId,
        int deviceBufferRequestMilliseconds)
    {
        ProcessStartInfo startInfo = CreateStartInfo(workerPath);
        startInfo.ArgumentList.Add("probe");
        startInfo.ArgumentList.Add(controlName);
        startInfo.ArgumentList.Add(nativeDirectory);
        startInfo.ArgumentList.Add(deviceId ?? string.Empty);
        startInfo.ArgumentList.Add(deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture));
        return AudioWorkerProcessGroup.Start(
            startInfo,
            "Could not start the Midora audio worker probe.");
    }

    private static ProcessStartInfo CreateStartInfo(string workerPath)
    {
        ProcessStartInfo result = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.FileName = "dotnet";
            result.ArgumentList.Add(workerPath);
        }
        else
        {
            result.FileName = workerPath;
        }
        return result;
    }

    private static void AddPlaybackArguments(
        ProcessStartInfo startInfo,
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
        long measuredCacheWriteBytesPerSecond)
    {
        startInfo.ArgumentList.Add("play");
        startInfo.ArgumentList.Add(controlName);
        startInfo.ArgumentList.Add(planPath);
        startInfo.ArgumentList.Add(soundFontPath);
        startInfo.ArgumentList.Add(nativeDirectory);
        startInfo.ArgumentList.Add(deviceId ?? string.Empty);
        startInfo.ArgumentList.Add(renderAheadMilliseconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumWorkFrameCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.VolumeDecibels.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterCeiling.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterReleaseMilliseconds.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterEnabled ? "1" : "0");
        startInfo.ArgumentList.Add(expectedSampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(cacheStagingPath ?? string.Empty);
        startInfo.ArgumentList.Add(cacheReadManifestPath ?? string.Empty);
        startInfo.ArgumentList.Add(bufferingRecoverySpoolPath ?? string.Empty);
        startInfo.ArgumentList.Add(bufferingRecoveryMemoryFrameCapacity.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(playbackSpanCacheStagingPath ?? string.Empty);
        startInfo.ArgumentList.Add(playbackSpanCacheHit ? "1" : "0");
        startInfo.ArgumentList.Add(rollingPreparationEnabled ? "1" : "0");
        startInfo.ArgumentList.Add(
            measuredCacheWriteBytesPerSecond.ToString(CultureInfo.InvariantCulture));
    }

    public void BeginBufferingRecovery(long recoveryEndFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Status.State != AudioWorkerState.Buffering)
        {
            throw new InvalidOperationException("The audio Worker has no latched underrun.");
        }
        if (!_control.TryEnqueueBufferingRecovery(recoveryEndFrame))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
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
        _ = _audioCache.TryCompactReusableAudio(
            isStopped: true,
            idleDuration: TimeSpan.Zero);
    }

    private void WaitUntilPlaybackStarted(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
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
            if (status.State == AudioWorkerState.Faulted || ExitCode is not null)
            {
                if (!_process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(_process);
                }
                _monitorThread.Join();
                throw new MidoraAudioException(
                    $"The audio worker failed during Preparing; fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
            }
            if (Volatile.Read(ref _monitorFaulted) != 0)
            {
                throw new MidoraAudioException(
                    $"The audio worker monitor failed during Preparing: {StandardError}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                TerminateProcess(_process);
                _monitorThread.Join();
                throw new TimeoutException("The audio worker did not start within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private AudioWorkerStatus WaitForHeldPreviewStatus(
        Func<AudioWorkerStatus, bool> predicate,
        TimeSpan timeout,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            AudioWorkerStatus status = Status;
            if (predicate(status))
            {
                return status;
            }
            if (status.State is AudioWorkerState.Faulted
                or AudioWorkerState.OutputDeviceUnavailable
                || ExitCode is not null)
            {
                throw new MidoraAudioException(
                    $"The audio worker could not {operation} held Preview; state={status.State}; "
                    + $"fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    $"The audio worker did not {operation} held Preview within the requested timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private void MonitorProcess(
        Task<string> standardError,
        Task<string> standardOutput)
    {
        try
        {
            _process.WaitForExit();
            _standardError = standardError.GetAwaiter().GetResult();
            _ = standardOutput.GetAwaiter().GetResult();
            Volatile.Write(ref _exitCode, _process.ExitCode);
        }
        catch (Exception exception)
        {
            _standardError = $"The audio worker monitor failed: {exception}";
            Volatile.Write(ref _monitorFaulted, 1);
        }
    }

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : checked((int)Math.Min(remaining, int.MaxValue));
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The child exited between HasExited and Kill.
        }
        process.WaitForExit();
    }

    private static void ValidateCommon(
        string workerPath,
        string nativeDirectory,
        int deviceBufferRequestMilliseconds,
        TimeSpan timeout,
        bool allowManagedTestWorker)
    {
        ValidateWorkerLaunchPath(workerPath, allowManagedTestWorker);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        if (!Directory.Exists(nativeDirectory))
        {
            throw new DirectoryNotFoundException(nativeDirectory);
        }
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceBufferRequestMilliseconds));
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    internal static void ValidateWorkerLaunchPath(string workerPath, bool allowManagedTestWorker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora Native AOT audio worker was not found.", workerPath);
        }

        string extension = Path.GetExtension(workerPath);
        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            && !(allowManagedTestWorker
                && string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.");
        }
    }

    internal static bool IsSuccessfulTerminalExit(AudioWorkerState state, int? exitCode) =>
        exitCode == 0
        && state is AudioWorkerState.Stopped
            or AudioWorkerState.Completed
            or AudioWorkerState.OutputDeviceUnavailable;

}
