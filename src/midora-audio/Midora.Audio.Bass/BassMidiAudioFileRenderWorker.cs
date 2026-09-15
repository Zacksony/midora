using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Midora.Common;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioFileRenderWorker : IAudioFileRenderWorker
{
    private readonly string _workerPath;
    private readonly string _bassNativeDirectory;
    private readonly TimeSpan _preparingTimeout;

    public BassMidiAudioFileRenderWorker(
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout)
        : this(workerPath, bassNativeDirectory, preparingTimeout, allowManagedTestWorker: false)
    {
    }

    internal BassMidiAudioFileRenderWorker(
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bassNativeDirectory);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora Native AOT audio worker was not found.", workerPath);
        }
        if (!allowManagedTestWorker
            && !string.Equals(Path.GetExtension(workerPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Formal audio file rendering requires the win-x64 Native AOT .exe worker; other launch forms are test-only.");
        }
        if (!Directory.Exists(bassNativeDirectory))
        {
            throw new DirectoryNotFoundException(bassNativeDirectory);
        }
        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }

        _workerPath = Path.GetFullPath(workerPath);
        _bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        _preparingTimeout = preparingTimeout;
    }

    public async Task PrepareAsync(
        AudioFileRenderWorkerPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ValidateCommon(
            preparation.SoundFonts,
            preparation.SampleRate,
            preparation.MaximumSampleVoicesPerUnitStream,
            preparation.MasterVolumeDecibels);
        using MidoraOwnedTemporaryDirectoryLease ownedDirectoryLease =
            MidoraOwnedTemporaryDirectoryLease.Create(
                MidoraProgramData.Current.AudioWorkerExchangeDirectory,
                "midora-audio-file-probe");
        string soundFontSetPath = Path.Combine(
            ownedDirectoryLease.DirectoryPath,
            "soundfonts.masf");
        SoundFontSetFile.Write(soundFontSetPath, preparation.SoundFonts);
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.FileProbe.{Guid.NewGuid():N}");
        using Process process = Start(
            CreateProbeStartInfo(preparation, control.Name, soundFontSetPath));
        await ObserveProcessAsync(
            process,
            control,
            totalFrameCount: 0,
            temporaryOutputPath: null,
            progress: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioFileRenderWorkerResult> RenderAsync(
        AudioFileRenderWorkerRequest request,
        IProgress<AudioFileRenderWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ValidateCommon(
            request.SoundFonts,
            request.Plan.SampleRate,
            request.MaximumSampleVoicesPerUnitStream,
            request.MasterVolumeDecibels);
        string temporaryOutputPath = Path.GetFullPath(request.TemporaryOutputPath);
        string? outputDirectory = Path.GetDirectoryName(temporaryOutputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(outputDirectory);
        }
        if (File.Exists(temporaryOutputPath) || Directory.Exists(temporaryOutputPath))
        {
            throw new IOException("The authorized audio worker temporary target already exists.");
        }

        using MidoraOwnedTemporaryDirectoryLease ownedDirectoryLease =
            MidoraOwnedTemporaryDirectoryLease.Create(
                MidoraProgramData.Current.AudioWorkerExchangeDirectory,
                "midora-audio-file-worker");
        string ownedDirectory = ownedDirectoryLease.DirectoryPath;
        string planPath = Path.Combine(ownedDirectory, "compiled-audio-plan.mdap");
        string soundFontSetPath = Path.Combine(ownedDirectory, "soundfonts.masf");
        AudioUnitCacheStaging? cacheStaging = null;
        MidiRenderEventStreamProducer? eventStreamProducer = null;
        try
        {
            try
            {
                cacheStaging = AudioUnitCacheStaging.Create(
                    request.Plan,
                    request.AudioCache,
                    request.SoundFontSetCacheIdentity,
                    _bassNativeDirectory,
                    request.MaximumSampleVoicesPerUnitStream);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                // Offline output remains formal without reusable retention.
                request.AudioCache?.DisableReusableAudioRetention(
                    "Reusable audio cache staging failed; new cache misses will be rendered without retention. "
                        + exception.Message);
            }
            MidiRenderPlan plan = cacheStaging?.Plan ?? request.Plan;
            eventStreamProducer = MidiRenderEventStreamProducer.Create(plan, ownedDirectory);
            if (eventStreamProducer is not null)
            {
                plan = plan.WithEventStreamDescriptor(eventStreamProducer.Descriptor);
            }
            MidiRenderPlanFile.Write(planPath, plan);
            SoundFontSetFile.Write(soundFontSetPath, request.SoundFonts);
            using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
                $"Midora.Audio.FileRender.{Guid.NewGuid():N}");
            using Process process = Start(
                CreateRenderStartInfo(
                    request,
                    control.Name,
                    planPath,
                    soundFontSetPath,
                    temporaryOutputPath,
                    cacheStaging?.FilePath));
            AudioWorkerStatus status = await ObserveProcessAsync(
                process,
                control,
                plan.TotalFrameCount,
                temporaryOutputPath,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temporaryOutputPath))
            {
                throw new AudioFileRenderWorkerException(
                    AudioFileRenderWorkerFailureStage.Finalizing,
                    "The audio worker completed without publishing its authorized temporary WAV target.");
            }
            if (cacheStaging is not null && request.AudioCache is not null)
            {
                cacheStaging.PublishCompleted(request.AudioCache, status.RenderPositionFrame);
            }
            return new(
                plan.TotalFrameCount,
                new FileInfo(temporaryOutputPath).Length,
                status.RenderingAllocatedBytes);
        }
        finally
        {
            eventStreamProducer?.Dispose();
            cacheStaging?.Dispose();
        }
    }

    private async Task<AudioWorkerStatus> ObserveProcessAsync(
        Process process,
        SharedAudioWorkerControl control,
        long totalFrameCount,
        string? temporaryOutputPath,
        IProgress<AudioFileRenderWorkerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        long preparingDeadline = Environment.TickCount64
            + checked((long)_preparingTimeout.TotalMilliseconds);
        bool stopSent = false;
        AudioWorkerStatus last = default;
        bool hasLast = false;
        while (!process.HasExited)
        {
            AudioWorkerStatus current = control.ReadStatus();
            if (!hasLast
                || current.State != last.State
                || current.RenderPositionFrame != last.RenderPositionFrame)
            {
                progress?.Report(new(
                    current.State,
                    current.RenderPositionFrame,
                    totalFrameCount));
                last = current;
                hasLast = true;
            }
            if (current.State == AudioWorkerState.Faulted)
            {
                break;
            }
            if (current.State is AudioWorkerState.Created or AudioWorkerState.Preparing
                && Environment.TickCount64 >= preparingDeadline)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                _ = await standardOutput.ConfigureAwait(false);
                string error = await standardError.ConfigureAwait(false);
                IReadOnlyList<string> residual = CleanupWorkerIntermediates(temporaryOutputPath);
                throw new AudioFileRenderWorkerException(
                    AudioFileRenderWorkerFailureStage.Preparing,
                    "The Native AOT audio worker exceeded the Preparing timeout. " + error,
                    residual);
            }
            if (cancellationToken.IsCancellationRequested && !stopSent)
            {
                if (!control.TryEnqueueStop())
                {
                    throw new AudioFileRenderWorkerException(
                        CurrentStage(current.State),
                        "The bounded audio worker control ring could not accept cancellation.");
                }
                stopSent = true;
            }
            await Task.Delay(2).ConfigureAwait(false);
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        _ = await standardOutput.ConfigureAwait(false);
        string stderr = await standardError.ConfigureAwait(false);
        AudioWorkerStatus final = control.ReadStatus();
        progress?.Report(new(final.State, final.RenderPositionFrame, totalFrameCount));
        IReadOnlyList<string> residualPaths = process.ExitCode == 0
            ? Array.Empty<string>()
            : CleanupWorkerIntermediates(temporaryOutputPath);
        if (cancellationToken.IsCancellationRequested)
        {
            residualPaths = residualPaths
                .Concat(CleanupWorkerIntermediates(temporaryOutputPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (temporaryOutputPath is not null && File.Exists(temporaryOutputPath))
            {
                try
                {
                    File.Delete(temporaryOutputPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    residualPaths = residualPaths.Concat([temporaryOutputPath]).Distinct().ToArray();
                }
            }
            throw new AudioFileRenderCancelledException(cancellationToken, residualPaths);
        }
        if (process.ExitCode != 0 || final.State == AudioWorkerState.Faulted)
        {
            throw new AudioFileRenderWorkerException(
                CurrentStage(final.State),
                $"The Native AOT audio worker failed; exitCode={process.ExitCode}; fault={final.FaultCode}; stderr={stderr}",
                residualPaths);
        }
        if (final.State != AudioWorkerState.Completed)
        {
            throw new AudioFileRenderWorkerException(
                CurrentStage(final.State),
                $"The Native AOT audio worker exited without the required Completed state; state={final.State}.",
                CleanupWorkerIntermediates(temporaryOutputPath));
        }
        return final;
    }

    private ProcessStartInfo CreateProbeStartInfo(
        AudioFileRenderWorkerPreparation preparation,
        string controlName,
        string soundFontSetPath)
    {
        ProcessStartInfo result = CreateStartInfo();
        result.ArgumentList.Add("file-probe");
        result.ArgumentList.Add(controlName);
        result.ArgumentList.Add(soundFontSetPath);
        result.ArgumentList.Add(_bassNativeDirectory);
        result.ArgumentList.Add(preparation.SampleRate.ToString(CultureInfo.InvariantCulture));
        result.ArgumentList.Add(preparation.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture));
        AddMasterSettings(result, preparation.MasterVolumeDecibels);
        return result;
    }

    private ProcessStartInfo CreateRenderStartInfo(
        AudioFileRenderWorkerRequest request,
        string controlName,
        string planPath,
        string soundFontSetPath,
        string temporaryOutputPath,
        string? cacheStagingPath)
    {
        ProcessStartInfo result = CreateStartInfo();
        result.ArgumentList.Add("file-render");
        result.ArgumentList.Add(controlName);
        result.ArgumentList.Add(planPath);
        result.ArgumentList.Add(soundFontSetPath);
        result.ArgumentList.Add(_bassNativeDirectory);
        result.ArgumentList.Add(temporaryOutputPath);
        result.ArgumentList.Add(request.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture));
        AddMasterSettings(result, request.MasterVolumeDecibels);
        result.ArgumentList.Add(cacheStagingPath ?? string.Empty);
        return result;
    }

    private ProcessStartInfo CreateStartInfo()
    {
        ProcessStartInfo result = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetExtension(_workerPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.FileName = "dotnet";
            result.ArgumentList.Add(_workerPath);
        }
        else
        {
            result.FileName = _workerPath;
        }
        return result;
    }

    private static void AddMasterSettings(ProcessStartInfo startInfo, float volumeDecibels)
    {
        startInfo.ArgumentList.Add(volumeDecibels.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(AudioMasterSettings.LimiterCeilingV2.ToString(
            "R",
            CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(AudioMasterSettings.LimiterReleaseMillisecondsV2.ToString(
            "R",
            CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("1");
    }

    private static Process Start(ProcessStartInfo startInfo) =>
        AudioWorkerProcessGroup.Start(
            startInfo,
            "Could not start the Midora Native AOT audio worker process.");

    private static void ValidateCommon(
        IReadOnlyList<SoundFontConfiguration> soundFonts,
        int sampleRate,
        int maximumSampleVoicesPerUnitStream,
        float masterVolumeDecibels)
    {
        ArgumentNullException.ThrowIfNull(soundFonts);
        if (soundFonts.Count == 0)
        {
            throw new ArgumentException(
                "At least one enabled application SoundFont is required.",
                nameof(soundFonts));
        }
        foreach (SoundFontConfiguration value in soundFonts)
        {
            SoundFontConfiguration soundFont = value.Normalize();
            string soundFontPath = soundFont.Path;
            ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
            if (!File.Exists(soundFontPath))
            {
                throw new FileNotFoundException(
                    "An enabled application SoundFont does not exist.",
                    soundFontPath);
            }
        }
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (maximumSampleVoicesPerUnitStream is < 1
            or > BassMidiPolyphonyConfiguration.MaximumSampleVoicesPerUnitStream)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }
        if (!float.IsFinite(masterVolumeDecibels) || masterVolumeDecibels > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(masterVolumeDecibels));
        }
    }

    private static AudioFileRenderWorkerFailureStage CurrentStage(AudioWorkerState state) =>
        state switch
        {
            AudioWorkerState.Created or AudioWorkerState.Preparing or AudioWorkerState.Prepared =>
                AudioFileRenderWorkerFailureStage.Preparing,
            AudioWorkerState.Finalizing or AudioWorkerState.Completed =>
                AudioFileRenderWorkerFailureStage.Finalizing,
            _ => AudioFileRenderWorkerFailureStage.Rendering
        };

    private static IReadOnlyList<string> CleanupWorkerIntermediates(string? temporaryOutputPath)
    {
        if (temporaryOutputPath is null)
        {
            return Array.Empty<string>();
        }
        string? directory = Path.GetDirectoryName(temporaryOutputPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }
        string prefix = "." + Path.GetFileName(temporaryOutputPath) + ".";
        List<string> residual = [];
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || !name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                File.Delete(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                residual.Add(candidate);
            }
        }
        return residual;
    }

}
