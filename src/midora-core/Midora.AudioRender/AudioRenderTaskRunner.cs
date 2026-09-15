using System.Collections.ObjectModel;
using System.Diagnostics;
using Midora.Audio;
using Midora.AudioDevice;
using Midora.AudioDevice.Wave;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.AudioRender;

public enum AudioRenderTaskStatus
{
    Preparing,
    Rendering,
    Cancelling,
    Finalizing,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled
}

public enum AudioRenderOutputStatus
{
    NotStarted,
    Succeeded,
    Failed,
    Cancelled
}

public readonly record struct AudioRenderTaskProgress(
    AudioRenderTaskStatus Status,
    int CurrentOutputIndex,
    int OutputCount,
    string? CurrentSourceKey,
    long ProcessedFrameCount,
    long TotalFrameCount,
    TimeSpan Elapsed);

public sealed class AudioRenderTaskRequest
{
    public required AudioRenderCompilationResult Compilation { get; init; }
    public required AudioRenderFrozenOutputPlan OutputPlan { get; init; }
    public required AudioRenderSoundFontSnapshot SoundFont { get; init; }
    public required int SampleRate { get; init; }
    public required int MaximumSampleVoicesPerUnitStream { get; init; }
    public required double MasterVolumeDecibels { get; init; }
    public bool OverwriteAuthorized { get; init; }
}

public sealed class AudioRenderOutputResult
{
    internal AudioRenderOutputResult(
        AudioRenderPlannedTarget target,
        AudioRenderOutputStatus status,
        long frameCount,
        long? fileByteCount,
        IEnumerable<CompilerDiagnostic> compilerDiagnostics,
        AudioRenderDiagnostic[] diagnostics)
    {
        Target = target;
        Status = status;
        FrameCount = frameCount;
        FileByteCount = fileByteCount;
        CompilerDiagnostics = CompilerDiagnosticSequence.Wrap(compilerDiagnostics);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public AudioRenderPlannedTarget Target { get; }
    public AudioRenderOutputStatus Status { get; }
    public long FrameCount { get; }
    public long? FileByteCount { get; }
    public ICompilerDiagnosticSequence CompilerDiagnostics { get; }
    public ReadOnlyCollection<AudioRenderDiagnostic> Diagnostics { get; }
}

public sealed class AudioRenderTaskResult
{
    internal AudioRenderTaskResult(
        AudioRenderTaskStatus status,
        TimeSpan elapsed,
        AudioRenderOutputResult[] outputs,
        AudioRenderDiagnostic[] diagnostics)
    {
        Status = status;
        Elapsed = elapsed;
        Outputs = Array.AsReadOnly(outputs);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public AudioRenderTaskStatus Status { get; }
    public TimeSpan Elapsed { get; }
    public ReadOnlyCollection<AudioRenderOutputResult> Outputs { get; }
    public ReadOnlyCollection<AudioRenderDiagnostic> Diagnostics { get; }
    public int SuccessCount => Outputs.Count(value => value.Status == AudioRenderOutputStatus.Succeeded);
    public int FailureCount => Outputs.Count(value => value.Status == AudioRenderOutputStatus.Failed);
    public bool HasCompletedOutputs => SuccessCount != 0;
    public bool CancelledWithCompletedOutputs =>
        Status == AudioRenderTaskStatus.Cancelled && HasCompletedOutputs;
}

public sealed class AudioRenderTaskRunner
{
    private readonly IAudioFileRenderWorker _worker;
    private readonly IAudioRenderFileOperations _files;

    public AudioRenderTaskRunner(IAudioFileRenderWorker worker)
        : this(worker, new AudioRenderFileOperations())
    {
    }

    internal AudioRenderTaskRunner(
        IAudioFileRenderWorker worker,
        IAudioRenderFileOperations files)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<AudioRenderTaskResult> ExecuteAsync(
        AudioRenderTaskRequest request,
        IProgress<AudioRenderTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCoreAsync(
            request,
            audioCache: null,
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task<AudioRenderTaskResult> ExecuteAsync(
        AudioRenderTaskRequest request,
        IAudioPcmCacheSessionAccess audioCache,
        IProgress<AudioRenderTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioCache);
        return await ExecuteCoreAsync(
            request,
            audioCache,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AudioRenderTaskResult> ExecuteCoreAsync(
        AudioRenderTaskRequest request,
        IAudioPcmCacheSessionAccess? audioCache,
        IProgress<AudioRenderTaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<AudioRenderDiagnostic> taskDiagnostics = [];
        try
        {
            PreparedTask prepared = Prepare(request, taskDiagnostics, cancellationToken);
            if (taskDiagnostics.Any(value => value.Severity == AudioRenderDiagnosticSeverity.Error))
            {
                return Result(
                    AudioRenderTaskStatus.Failed,
                    stopwatch,
                    CreateUnstartedOutputs(request, prepared.FrameCount),
                    taskDiagnostics);
            }

            progress?.Report(new(
                AudioRenderTaskStatus.Preparing,
                -1,
                prepared.Outputs.Length,
                null,
                0,
                prepared.TotalFrameCount,
                stopwatch.Elapsed));
            cancellationToken.ThrowIfCancellationRequested();
            _files.CreateDirectory(request.OutputPlan.OutputDirectory);
            await _worker.PrepareAsync(
                new(
                    request.SoundFont.SoundFonts,
                    request.SampleRate,
                    request.MaximumSampleVoicesPerUnitStream,
                    checked((float)request.MasterVolumeDecibels)),
                cancellationToken).ConfigureAwait(false);

            List<AudioRenderOutputResult> outputs = [];
            long processedFrames = 0;
            bool cancelled = false;
            for (int index = 0; index < prepared.Outputs.Length; index++)
            {
                PreparedOutput output = prepared.Outputs[index];
                if (cancelled || cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.NotStarted,
                        null,
                        []));
                    continue;
                }

                if (!output.Compilation.Succeeded || output.Plan is null)
                {
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.Failed,
                        null,
                        [new(
                            "MIDORA-AUDIO-RENDER-COMPILE-FAILED",
                            AudioRenderDiagnosticSeverity.Error,
                            "The canonical audio render compilation for this output failed.",
                            output.Target.SourceKey,
                            output.Target.TrackId,
                            output.Target.FullPath)]));
                    processedFrames = checked(processedFrames + prepared.FrameCount);
                    Report(
                        progress,
                        prepared,
                        index,
                        output,
                        processedFrames,
                        AudioRenderTaskStatus.Rendering,
                        stopwatch.Elapsed);
                    continue;
                }

                if (!output.Target.ExistedAtFreeze
                    && (_files.FileExists(output.Target.FullPath)
                        || _files.DirectoryExists(output.Target.FullPath)))
                {
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.Failed,
                        null,
                        [new(
                            "MIDORA-AUDIO-RENDER-NEW-TARGET-CONFLICT",
                            AudioRenderDiagnosticSeverity.Error,
                            "The target did not exist when paths were frozen and appeared before publication; it was not overwritten.",
                            output.Target.SourceKey,
                            output.Target.TrackId,
                            output.Target.FullPath)]));
                    processedFrames = checked(processedFrames + prepared.FrameCount);
                    continue;
                }

                string temporaryPath = _files.CreateTemporaryPath(output.Target.FullPath);
                string? backupPath = null;
                List<AudioRenderDiagnostic> outputDiagnostics = [];
                try
                {
                    Report(
                        progress,
                        prepared,
                        index,
                        output,
                        processedFrames,
                        AudioRenderTaskStatus.Rendering,
                        stopwatch.Elapsed);
                    WorkerProgressBridge? bridge = progress is null
                        ? null
                        : new(
                            progress,
                            prepared,
                            index,
                            output,
                            processedFrames,
                            stopwatch);
                    AudioFileRenderWorkerResult rendered = await _worker.RenderAsync(
                        new(
                            output.Plan,
                            request.SoundFont.SoundFonts,
                            request.SoundFont.CacheIdentity,
                            temporaryPath,
                            request.MaximumSampleVoicesPerUnitStream,
                            checked((float)request.MasterVolumeDecibels),
                            audioCache),
                        bridge,
                        cancellationToken).ConfigureAwait(false);
                    WaveFileSize validated = WaveFileValidation.ValidateInitialReleaseFile(
                        temporaryPath,
                        request.SampleRate,
                        prepared.FrameCount);
                    if (rendered.FrameCount != prepared.FrameCount
                        || rendered.FileByteCount != validated.FileByteCount)
                    {
                        throw new InvalidDataException(
                            "The audio worker result does not match the validated frozen WAVE output.");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    backupPath = Publish(output.Target, temporaryPath, request.OverwriteAuthorized);
                    if (backupPath is not null)
                    {
                        try
                        {
                            _files.DeleteFile(backupPath);
                            backupPath = null;
                        }
                        catch (Exception cleanupFailure) when (IsFileSystemFailure(cleanupFailure))
                        {
                            outputDiagnostics.Add(new(
                                "MIDORA-AUDIO-RENDER-BACKUP-CLEANUP",
                                AudioRenderDiagnosticSeverity.Warning,
                                "The WAV was published, but its transaction backup could not be removed: "
                                    + cleanupFailure.Message,
                                output.Target.SourceKey,
                                output.Target.TrackId,
                                output.Target.FullPath,
                                backupPath));
                        }
                    }
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.Succeeded,
                        validated.FileByteCount,
                        outputDiagnostics));
                }
                catch (OperationCanceledException cancellationFailure) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    if (cancellationFailure is AudioFileRenderCancelledException workerCancellation)
                    {
                        outputDiagnostics.AddRange(workerCancellation.ResidualPaths.Select(path => new AudioRenderDiagnostic(
                            "MIDORA-AUDIO-RENDER-CANCEL-CLEANUP",
                            AudioRenderDiagnosticSeverity.Warning,
                            "The cancelled Worker left an incomplete file that is not a valid output.",
                            output.Target.SourceKey,
                            output.Target.TrackId,
                            output.Target.FullPath,
                            path)));
                    }
                    outputDiagnostics.AddRange(CleanupTemporary(output, temporaryPath));
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.Cancelled,
                        null,
                        outputDiagnostics));
                    progress?.Report(new(
                        AudioRenderTaskStatus.Cancelling,
                        index,
                        prepared.Outputs.Length,
                        output.Target.SourceKey,
                        processedFrames,
                        prepared.TotalFrameCount,
                        stopwatch.Elapsed));
                }
                catch (Exception failure) when (failure is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or MidoraAudioDeviceException
                    or MidoraAudioException)
                {
                    outputDiagnostics.Add(new(
                        "MIDORA-AUDIO-RENDER-OUTPUT-FAILED",
                        AudioRenderDiagnosticSeverity.Error,
                        failure.Message,
                        output.Target.SourceKey,
                        output.Target.TrackId,
                        output.Target.FullPath,
                        temporaryPath));
                    if (failure is AudioFileRenderWorkerException workerFailure)
                    {
                        outputDiagnostics.AddRange(workerFailure.ResidualPaths.Select(path => new AudioRenderDiagnostic(
                            "MIDORA-AUDIO-RENDER-WORKER-CLEANUP",
                            AudioRenderDiagnosticSeverity.Warning,
                            "The failed Worker left an incomplete file that is not a valid output.",
                            output.Target.SourceKey,
                            output.Target.TrackId,
                            output.Target.FullPath,
                            path)));
                    }
                    outputDiagnostics.AddRange(CleanupTemporary(output, temporaryPath));
                    if (backupPath is not null && _files.FileExists(backupPath))
                    {
                        outputDiagnostics.Add(new(
                            "MIDORA-AUDIO-RENDER-BACKUP-RETAINED",
                            AudioRenderDiagnosticSeverity.Warning,
                            "A transaction backup was retained for recovery after publication failed.",
                            output.Target.SourceKey,
                            output.Target.TrackId,
                            output.Target.FullPath,
                            backupPath));
                    }
                    outputs.Add(CreateOutputResult(
                        output,
                        AudioRenderOutputStatus.Failed,
                        null,
                        outputDiagnostics));
                }
                processedFrames = checked(processedFrames + prepared.FrameCount);
            }

            AudioRenderTaskStatus status = cancelled
                ? AudioRenderTaskStatus.Cancelled
                : outputs.All(value => value.Status == AudioRenderOutputStatus.Succeeded)
                    ? AudioRenderTaskStatus.Completed
                    : outputs.Any(value => value.Status == AudioRenderOutputStatus.Succeeded)
                        ? AudioRenderTaskStatus.CompletedWithErrors
                        : AudioRenderTaskStatus.Failed;
            return Result(status, stopwatch, outputs, taskDiagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                AudioRenderTaskStatus.Cancelled,
                stopwatch,
                CreateUnstartedOutputs(request, frameCount: 0),
                taskDiagnostics);
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or MidoraAudioDeviceException
            or MidoraAudioException)
        {
            taskDiagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-PREPARING-FAILED",
                AudioRenderDiagnosticSeverity.Error,
                failure.Message));
            return Result(
                AudioRenderTaskStatus.Failed,
                stopwatch,
                CreateUnstartedOutputs(request, frameCount: 0),
                taskDiagnostics);
        }
    }

    private static PreparedTask Prepare(
        AudioRenderTaskRequest request,
        List<AudioRenderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Compilation);
        ArgumentNullException.ThrowIfNull(request.OutputPlan);
        ArgumentNullException.ThrowIfNull(request.SoundFont);
        if (request.SampleRate is < AudioRenderSettingsPolicy.MinimumSampleRate
            or > AudioRenderSettingsPolicy.MaximumSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(request.SampleRate));
        }
        if (request.MaximumSampleVoicesPerUnitStream
            is < AudioRenderSettingsPolicy.MinimumSampleVoicesPerUnitStream
            or > AudioRenderSettingsPolicy.MaximumSampleVoicesPerUnitStreamLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumSampleVoicesPerUnitStream));
        }
        if (!double.IsFinite(request.MasterVolumeDecibels) || request.MasterVolumeDecibels > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MasterVolumeDecibels));
        }
        if (request.Compilation.Mode != request.OutputPlan.Mode)
        {
            throw new ArgumentException("The frozen audio compilation and output plan modes differ.", nameof(request));
        }

        diagnostics.AddRange(request.Compilation.Diagnostics);
        diagnostics.AddRange(request.OutputPlan.Diagnostics);
        diagnostics.AddRange(request.SoundFont.Diagnostics);
        if (!request.Compilation.HasRenderableOutput)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-NO-COMPILABLE-OUTPUT",
                AudioRenderDiagnosticSeverity.Error,
                "No selected output has a consumable canonical compiled result."));
        }
        if (!request.OutputPlan.Succeeded)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-OUTPUT-PLAN-BLOCKED",
                AudioRenderDiagnosticSeverity.Error,
                "The frozen audio render output plan is blocked."));
        }
        if (request.OutputPlan.RequiresOverwriteAuthorization && !request.OverwriteAuthorized)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-OVERWRITE-REQUIRED",
                AudioRenderDiagnosticSeverity.Error,
                "One or more frozen targets already exist and require explicit overwrite authorization."));
        }

        Dictionary<string, AudioRenderCompilationItem> compilationBySource =
            request.Compilation.Items.ToDictionary(value => value.SourceKey, StringComparer.Ordinal);
        List<PreparedOutput> outputs = [];
        long? frameCount = null;
        foreach (AudioRenderPlannedTarget target in request.OutputPlan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!compilationBySource.TryGetValue(target.SourceKey, out AudioRenderCompilationItem? compilation))
            {
                diagnostics.Add(new(
                    "MIDORA-AUDIO-RENDER-FROZEN-INPUT-MISMATCH",
                    AudioRenderDiagnosticSeverity.Error,
                    "A frozen output target has no corresponding compilation item.",
                    target.SourceKey,
                    target.TrackId,
                    target.FullPath));
                continue;
            }
            MidiRenderPlan? plan = null;
            if (compilation.Succeeded)
            {
                try
                {
                    plan = MidiRenderPlanAdapter.Create(compilation.CompiledResult, request.SampleRate,
                        cancellationToken: cancellationToken);
                    _ = WaveFileSize.Calculate(plan.TotalFrameCount);
                    if (frameCount.HasValue && frameCount.Value != plan.TotalFrameCount)
                    {
                        diagnostics.Add(new(
                            "MIDORA-AUDIO-RENDER-FRAME-LENGTH-MISMATCH",
                            AudioRenderDiagnosticSeverity.Error,
                            "Per-track canonical results do not share one frozen final frame length.",
                            target.SourceKey,
                            target.TrackId,
                            target.FullPath));
                    }
                    frameCount ??= plan.TotalFrameCount;
                }
                catch (Exception exception) when (exception is ArgumentException
                    or ArgumentOutOfRangeException
                    or OverflowException)
                {
                    diagnostics.Add(new(
                        "MIDORA-AUDIO-RENDER-RIFF-LIMIT",
                        AudioRenderDiagnosticSeverity.Error,
                        "The planned WAV cannot be represented by the initial-release RIFF format: "
                            + exception.Message,
                        target.SourceKey,
                        target.TrackId,
                        target.FullPath));
                }
            }
            outputs.Add(new(target, compilation, plan));
        }
        if (outputs.Count != request.OutputPlan.Targets.Count
            || outputs.Count != request.Compilation.Items.Count)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-FROZEN-INPUT-MISMATCH",
                AudioRenderDiagnosticSeverity.Error,
                "The frozen compilation and output target sets differ."));
        }

        long frozenFrameCount = frameCount ?? 0;
        long totalFrames;
        try
        {
            totalFrames = checked(frozenFrameCount * outputs.Count);
        }
        catch (OverflowException exception)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-PROGRESS-OVERFLOW",
                AudioRenderDiagnosticSeverity.Error,
                exception.Message));
            totalFrames = 0;
        }
        return new(outputs.ToArray(), frozenFrameCount, totalFrames);
    }

    private string? Publish(
        AudioRenderPlannedTarget target,
        string temporaryPath,
        bool overwriteAuthorized)
    {
        if (target.ExistedAtFreeze)
        {
            if (!overwriteAuthorized)
            {
                throw new IOException("The frozen existing WAV target was not authorized for overwrite.");
            }
            if (!_files.FileExists(target.FullPath))
            {
                _files.MoveFile(temporaryPath, target.FullPath, overwrite: false);
                return null;
            }
            string backupPath = _files.CreateBackupPath(target.FullPath);
            _files.ReplaceFile(temporaryPath, target.FullPath, backupPath);
            return backupPath;
        }

        _files.MoveFile(temporaryPath, target.FullPath, overwrite: false);
        return null;
    }

    private IEnumerable<AudioRenderDiagnostic> CleanupTemporary(
        PreparedOutput output,
        string temporaryPath)
    {
        if (!_files.FileExists(temporaryPath))
        {
            return [];
        }
        try
        {
            _files.DeleteFile(temporaryPath);
            return [];
        }
        catch (Exception cleanupFailure) when (IsFileSystemFailure(cleanupFailure))
        {
            return
            [
                new(
                    "MIDORA-AUDIO-RENDER-TEMP-CLEANUP",
                    AudioRenderDiagnosticSeverity.Warning,
                    "The incomplete temporary output could not be removed and is not a valid finished WAV: "
                        + cleanupFailure.Message,
                    output.Target.SourceKey,
                    output.Target.TrackId,
                    output.Target.FullPath,
                    temporaryPath)
            ];
        }
    }

    private static AudioRenderOutputResult CreateOutputResult(
        PreparedOutput output,
        AudioRenderOutputStatus status,
        long? fileByteCount,
        IEnumerable<AudioRenderDiagnostic> diagnostics) =>
        new(
            output.Target,
            status,
            output.Plan?.TotalFrameCount ?? 0,
            fileByteCount,
            output.Compilation.Diagnostics,
            StableDiagnostics(diagnostics));

    private static List<AudioRenderOutputResult> CreateUnstartedOutputs(
        AudioRenderTaskRequest request,
        long frameCount) =>
        request.OutputPlan.Targets.Select(target =>
        {
            AudioRenderCompilationItem? compilation = request.Compilation.Items
                .FirstOrDefault(value => value.SourceKey == target.SourceKey);
            return new AudioRenderOutputResult(
                target,
                AudioRenderOutputStatus.NotStarted,
                frameCount,
                null,
                compilation?.Diagnostics ?? CompilerDiagnosticList.Empty,
                []);
        }).ToList();

    private static AudioRenderTaskResult Result(
        AudioRenderTaskStatus status,
        Stopwatch stopwatch,
        IEnumerable<AudioRenderOutputResult> outputs,
        IEnumerable<AudioRenderDiagnostic> diagnostics)
    {
        stopwatch.Stop();
        return new(status, stopwatch.Elapsed, outputs.ToArray(), StableDiagnostics(diagnostics));
    }

    private static void Report(
        IProgress<AudioRenderTaskProgress>? progress,
        PreparedTask prepared,
        int outputIndex,
        PreparedOutput output,
        long processedFrames,
        AudioRenderTaskStatus status,
        TimeSpan elapsed = default) =>
        progress?.Report(new(
            status,
            outputIndex,
            prepared.Outputs.Length,
            output.Target.SourceKey,
            processedFrames,
            prepared.TotalFrameCount,
            elapsed));

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static AudioRenderDiagnostic[] StableDiagnostics(
        IEnumerable<AudioRenderDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(value => value.Severity switch
            {
                AudioRenderDiagnosticSeverity.Error => 0,
                AudioRenderDiagnosticSeverity.Warning => 1,
                _ => 2
            })
            .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
            .ThenBy(value => value.FinalPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Message, StringComparer.Ordinal)
            .ToArray();

    private sealed record PreparedOutput(
        AudioRenderPlannedTarget Target,
        AudioRenderCompilationItem Compilation,
        MidiRenderPlan? Plan);

    private sealed record PreparedTask(
        PreparedOutput[] Outputs,
        long FrameCount,
        long TotalFrameCount);

    private sealed class WorkerProgressBridge(
        IProgress<AudioRenderTaskProgress> progress,
        PreparedTask prepared,
        int outputIndex,
        PreparedOutput output,
        long completedBefore,
        Stopwatch stopwatch) : IProgress<AudioFileRenderWorkerProgress>
    {
        public void Report(AudioFileRenderWorkerProgress value)
        {
            long current = Math.Clamp(value.RenderedFrameCount, 0, prepared.FrameCount);
            progress.Report(new(
                value.State switch
                {
                    AudioWorkerState.Created or AudioWorkerState.Preparing or AudioWorkerState.Prepared =>
                        AudioRenderTaskStatus.Preparing,
                    AudioWorkerState.Cancelling or AudioWorkerState.Cancelled =>
                        AudioRenderTaskStatus.Cancelling,
                    AudioWorkerState.Finalizing => AudioRenderTaskStatus.Finalizing,
                    _ => AudioRenderTaskStatus.Rendering
                },
                outputIndex,
                prepared.Outputs.Length,
                output.Target.SourceKey,
                checked(completedBefore + current),
                prepared.TotalFrameCount,
                stopwatch.Elapsed));
        }
    }
}

internal interface IAudioRenderFileOperations
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    string CreateTemporaryPath(string finalPath);
    string CreateBackupPath(string finalPath);
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);
    void ReplaceFile(string sourcePath, string destinationPath, string backupPath);
    void DeleteFile(string path);
}

internal sealed class AudioRenderFileOperations : IAudioRenderFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string CreateTemporaryPath(string finalPath) => Path.Combine(
        Path.GetDirectoryName(finalPath)!,
        $".{Path.GetFileName(finalPath)}.midora-render-{Guid.NewGuid():N}.tmp");

    public string CreateBackupPath(string finalPath) => Path.Combine(
        Path.GetDirectoryName(finalPath)!,
        $".{Path.GetFileName(finalPath)}.midora-backup-{Guid.NewGuid():N}.tmp");

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void ReplaceFile(string sourcePath, string destinationPath, string backupPath) =>
        File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);

    public void DeleteFile(string path) => File.Delete(path);
}
