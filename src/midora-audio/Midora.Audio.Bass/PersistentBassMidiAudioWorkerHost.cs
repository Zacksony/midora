using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Midora.Common;
using Midora.AudioDevice;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
internal sealed class PersistentBassMidiAudioWorkerHost : IDisposable
{
    private readonly object _sync = new();
    private readonly MidoraOwnedTemporaryDirectoryLease _ownedDirectoryLease;
    private readonly string _ownedDirectory;
    private readonly SharedAudioWorkerControl _control;
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private readonly Task<string> _standardOutput;
    private readonly TimeSpan _defaultTimeout;
    private long _nextGeneration;
    private long _activePlaybackGeneration;
    private bool _pitchAuditionConfigured;
    private bool _pitchAuditionActive;
    private string? _pitchAuditionDeviceId;
    private int _pitchAuditionDeviceBufferRequestMilliseconds;
    private bool _disposed;

    public PersistentBassMidiAudioWorkerHost(
        string workerPath,
        string bassNativeDirectory,
        string soundFontPath,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker = false)
        : this(
            workerPath,
            bassNativeDirectory,
            [new SoundFontConfiguration(soundFontPath, null)],
            preparingTimeout,
            allowManagedTestWorker)
    {
    }

    public PersistentBassMidiAudioWorkerHost(
        string workerPath,
        string bassNativeDirectory,
        IReadOnlyList<string> soundFontPaths,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker = false)
        : this(
            workerPath,
            bassNativeDirectory,
            soundFontPaths
                .Select(path => new SoundFontConfiguration(path, null))
                .ToArray(),
            preparingTimeout,
            allowManagedTestWorker)
    {
    }

    public PersistentBassMidiAudioWorkerHost(
        string workerPath,
        string bassNativeDirectory,
        IReadOnlyList<SoundFontConfiguration> soundFonts,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker = false)
    {
        BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(workerPath, allowManagedTestWorker);
        ArgumentException.ThrowIfNullOrWhiteSpace(bassNativeDirectory);
        ArgumentNullException.ThrowIfNull(soundFonts);
        if (soundFonts.Count == 0)
        {
            throw new ArgumentException("At least one enabled SoundFont is required.", nameof(soundFonts));
        }
        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }
        string nativeDirectory = Path.GetFullPath(bassNativeDirectory);
        SoundFontConfiguration[] normalizedSoundFonts = soundFonts
            .Select(value => value.Normalize())
            .ToArray();
        string[] fontPaths = normalizedSoundFonts.Select(value => value.Path).ToArray();
        if (!Directory.Exists(nativeDirectory))
        {
            throw new DirectoryNotFoundException(nativeDirectory);
        }
        string? missingPath = fontPaths.FirstOrDefault(path => !File.Exists(path));
        if (missingPath is not null)
        {
            throw new FileNotFoundException("An enabled application SoundFont does not exist.", missingPath);
        }

        WorkerPath = Path.GetFullPath(workerPath);
        NativeDirectory = nativeDirectory;
        SoundFonts = Array.AsReadOnly(normalizedSoundFonts);
        SoundFontPaths = Array.AsReadOnly(fontPaths);
        _defaultTimeout = preparingTimeout;
        _ownedDirectoryLease = MidoraOwnedTemporaryDirectoryLease.Create(
            MidoraProgramData.Current.AudioWorkerExchangeDirectory,
            "midora-audio-host");
        _ownedDirectory = _ownedDirectoryLease.DirectoryPath;
        string soundFontSetPath;
        try
        {
            soundFontSetPath = Path.Combine(_ownedDirectory, "soundfonts.masf");
            SoundFontSetFile.Write(soundFontSetPath, SoundFonts);
            _control = SharedAudioWorkerControl.Create($"Midora.Audio.Host.{Guid.NewGuid():N}");
        }
        catch
        {
            _ownedDirectoryLease.Dispose();
            throw;
        }
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(WorkerPath);
            startInfo.ArgumentList.Add("realtime-host");
            startInfo.ArgumentList.Add(_control.Name);
            startInfo.ArgumentList.Add(_ownedDirectory);
            startInfo.ArgumentList.Add(soundFontSetPath);
            startInfo.ArgumentList.Add(NativeDirectory);
            _process = AudioWorkerProcessGroup.Start(
                startInfo,
                "Could not start the persistent Midora audio worker.");
            _standardError = _process.StandardError.ReadToEndAsync();
            _standardOutput = _process.StandardOutput.ReadToEndAsync();
            PersistentAudioWorkerResponse ready = WaitForResponse(0, preparingTimeout);
            if (!ready.Succeeded)
            {
                throw new MidoraAudioException(
                    "The persistent audio worker failed to initialize: " + ready.Error);
            }
        }
        catch
        {
            DisposeAfterConstructionFailure();
            throw;
        }
    }

    public string WorkerPath { get; }
    public string NativeDirectory { get; }
    public IReadOnlyList<SoundFontConfiguration> SoundFonts { get; }
    public IReadOnlyList<string> SoundFontPaths { get; }
    public string SoundFontPath => SoundFontPaths[0];
    public SharedAudioWorkerControl Control => _control;
    public AudioWorkerStatus Status => _control.ReadStatus();
    public bool HasExited => _process.HasExited;
    internal int ProcessId => _process.Id;

    public string StandardError
    {
        get
        {
            if (!_process.HasExited)
            {
                return string.Empty;
            }
            return _standardError.IsCompletedSuccessfully ? _standardError.Result : string.Empty;
        }
    }

    public BassMidiAudioWorkerProbeResult Probe(
        string? deviceId,
        int deviceBufferRequestMilliseconds)
    {
        lock (_sync)
        {
            RequireIdle();
            long generation = BeginRequest(
                [deviceId ?? string.Empty, deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture)],
                _control.TryEnqueuePersistentProbe);
            PersistentAudioWorkerResponse response = WaitForResponse(generation, _defaultTimeout);
            CompleteExchange(generation);
            ResetPitchAuditionConfiguration();
            if (!response.Succeeded)
            {
                throw new MidoraAudioDeviceException(response.Error);
            }
            return new(response.ActualSampleRate, response.ActualDeviceBufferFrameCount);
        }
    }

    public long StartPlayback(IReadOnlyList<string> arguments)
    {
        lock (_sync)
        {
            RequireIdle();
            long generation = BeginRequest(
                arguments,
                _control.TryEnqueuePersistentStartPlayback);
            _activePlaybackGeneration = generation;
            WaitForPlaybackAcceptance(generation, _defaultTimeout);
            ResetPitchAuditionConfiguration();
            return generation;
        }
    }

    public PersistentAudioWorkerResponse CompletePlayback(long generation, TimeSpan timeout)
    {
        lock (_sync)
        {
            if (_activePlaybackGeneration != generation)
            {
                throw new InvalidOperationException("The persistent audio worker playback generation is not active.");
            }
            PersistentAudioWorkerResponse response = WaitForResponse(generation, timeout);
            _activePlaybackGeneration = 0;
            CompleteExchange(generation);
            return response;
        }
    }

    public void CancelActivePlayback(TimeSpan timeout)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activePlaybackGeneration == 0)
            {
                return;
            }
            long generation = _activePlaybackGeneration;
            if (_control.ReadStatus().PersistentResponseGeneration < generation
                && !_control.TryEnqueueStop())
            {
                throw new InvalidOperationException(
                    "The persistent audio worker command ring is full while cancelling playback.");
            }
            _ = WaitForResponse(generation, timeout);
            _activePlaybackGeneration = 0;
            CompleteExchange(generation);
        }
    }

    public void BeginPitchAudition(
        string? deviceId,
        int deviceBufferRequestMilliseconds,
        int pitch,
        int velocity)
    {
        if (pitch is < 0 or > 127 || velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        lock (_sync)
        {
            RequireIdle();
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (_pitchAuditionConfigured
                && string.Equals(_pitchAuditionDeviceId, deviceId, StringComparison.Ordinal)
                && _pitchAuditionDeviceBufferRequestMilliseconds
                    == deviceBufferRequestMilliseconds)
            {
                if (!_control.TryEnqueuePitchAuditionUpdate(pitch, velocity))
                {
                    throw new InvalidOperationException(
                        "The persistent audio worker command ring is full while updating pitch audition.");
                }
                _pitchAuditionActive = true;
                return;
            }

            long generation = BeginRequest(
                [
                    deviceId ?? string.Empty,
                    deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture),
                    pitch.ToString(CultureInfo.InvariantCulture),
                    velocity.ToString(CultureInfo.InvariantCulture)
                ],
                _control.TryEnqueuePitchAuditionNoteOn);
            PersistentAudioWorkerResponse response = WaitForResponse(generation, _defaultTimeout);
            CompleteExchange(generation);
            if (!response.Succeeded)
            {
                throw new MidoraAudioException(response.Error);
            }
            _pitchAuditionConfigured = true;
            _pitchAuditionActive = true;
            _pitchAuditionDeviceId = deviceId;
            _pitchAuditionDeviceBufferRequestMilliseconds = deviceBufferRequestMilliseconds;
        }
    }

    public void EndPitchAudition()
    {
        lock (_sync)
        {
            RequireIdle();
            if (!_pitchAuditionActive)
            {
                return;
            }
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (!_control.TryEnqueuePitchAuditionEnd())
            {
                throw new InvalidOperationException(
                    "The persistent audio worker command ring is full while ending pitch audition.");
            }
            _pitchAuditionActive = false;
        }
    }

    private void ResetPitchAuditionConfiguration()
    {
        _pitchAuditionConfigured = false;
        _pitchAuditionActive = false;
        _pitchAuditionDeviceId = null;
        _pitchAuditionDeviceBufferRequestMilliseconds = 0;
    }

    private long BeginRequest(IReadOnlyList<string> arguments, Func<long, bool> enqueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited)
        {
            throw WorkerExitedException();
        }
        long generation = checked(++_nextGeneration);
        PersistentAudioWorkerExchange.WriteRequest(_ownedDirectory, generation, arguments);
        if (!enqueue(generation))
        {
            PersistentAudioWorkerExchange.DeleteExchange(_ownedDirectory, generation);
            throw new InvalidOperationException("The persistent audio worker command ring is full.");
        }
        return generation;
    }

    private PersistentAudioWorkerResponse WaitForResponse(long generation, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            AudioWorkerStatus status = _control.ReadStatus();
            if (status.PersistentResponseGeneration == generation)
            {
                if (!PersistentAudioWorkerExchange.TryReadResponse(
                        _ownedDirectory,
                        generation,
                        out PersistentAudioWorkerResponse response))
                {
                    throw new InvalidDataException(
                        "The persistent audio worker published a response without its immutable payload.");
                }
                return response;
            }
            if (status.PersistentResponseGeneration > generation)
            {
                throw new InvalidDataException(
                    "The persistent audio worker published a future response generation.");
            }
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("The persistent audio worker did not respond within the requested timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private void WaitForPlaybackAcceptance(long generation, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            AudioWorkerStatus status = _control.ReadStatus();
            if (status.PersistentPlaybackAcceptedGeneration == generation)
            {
                return;
            }
            if (status.PersistentPlaybackAcceptedGeneration > generation)
            {
                throw new InvalidDataException(
                    "The persistent audio worker acknowledged a future playback generation.");
            }
            if (status.PersistentResponseGeneration == generation)
            {
                if (!PersistentAudioWorkerExchange.TryReadResponse(
                        _ownedDirectory,
                        generation,
                        out PersistentAudioWorkerResponse response))
                {
                    throw new InvalidDataException(
                        "The persistent audio worker published a response without its immutable payload.");
                }
                _activePlaybackGeneration = 0;
                CompleteExchange(generation);
                throw new MidoraAudioException(
                    response.Succeeded
                        ? "The persistent audio worker completed without accepting the playback task."
                        : response.Error);
            }
            if (status.PersistentResponseGeneration > generation)
            {
                throw new InvalidDataException(
                    "The persistent audio worker published a future response generation.");
            }
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    "The persistent audio worker did not accept playback within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private Exception WorkerExitedException()
    {
        _process.WaitForExit();
        string error = _standardError.GetAwaiter().GetResult();
        _ = _standardOutput.GetAwaiter().GetResult();
        return new MidoraAudioException(
            $"The persistent audio worker exited unexpectedly with code {_process.ExitCode}: {error}");
    }

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activePlaybackGeneration != 0)
        {
            throw new InvalidOperationException("A persistent audio worker playback task is already active.");
        }
    }

    private void CompleteExchange(long generation) =>
        PersistentAudioWorkerExchange.DeleteExchange(_ownedDirectory, generation);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    if (_activePlaybackGeneration != 0)
                    {
                        _ = _control.TryEnqueueStop();
                        try
                        {
                            _ = WaitForResponse(_activePlaybackGeneration, TimeSpan.FromSeconds(5));
                        }
                        catch
                        {
                            // Forced process termination below is the final cleanup boundary.
                        }
                        _activePlaybackGeneration = 0;
                    }
                    long generation = checked(++_nextGeneration);
                    PersistentAudioWorkerExchange.WriteRequest(_ownedDirectory, generation, []);
                    if (_control.TryEnqueuePersistentShutdown(generation))
                    {
                        try
                        {
                            _ = WaitForResponse(generation, TimeSpan.FromSeconds(5));
                        }
                        catch
                        {
                            // Forced process termination below is the final cleanup boundary.
                        }
                    }
                }
            }
            finally
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process.WaitForExit();
                _process.Dispose();
                _control.Dispose();
                TryDeleteOwnedDirectory();
            }
        }
    }

    private void DisposeAfterConstructionFailure()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process?.Dispose();
        }
        catch
        {
        }
        _control.Dispose();
        TryDeleteOwnedDirectory();
    }

    private void TryDeleteOwnedDirectory()
    {
        _ownedDirectoryLease.Dispose();
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
}
