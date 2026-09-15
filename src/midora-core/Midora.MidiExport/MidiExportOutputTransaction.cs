using System.Collections.ObjectModel;
using Midora.Midi;

namespace Midora.MidiExport;

public enum MidiExportOutputStage
{
    Preflight,
    Staging,
    SelfValidation,
    Finalizing,
    Rollback,
    Cleanup,
    Encoding
}

public enum MidiExportOutputItemState
{
    ReadyToPublish,
    Completed,
    Failed,
    Cleaned,
    CleanupFailed
}

public sealed class MidiExportPreparedArtifact
{
    private readonly Action<Stream, CancellationToken, IProgress<StandardMidiFileWriteProgress>?> _writer;
    private byte[]? _content;

    public MidiExportPreparedArtifact(string sourceKey, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);
        SourceKey = sourceKey;
        _content = content.ToArray();
        _writer = (output, token, _) =>
        {
            token.ThrowIfCancellationRequested();
            output.Write(_content);
        };
    }

    internal MidiExportPreparedArtifact(string sourceKey, Action<Stream> writer)
        : this(sourceKey, (output, _) => writer(output))
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    internal MidiExportPreparedArtifact(string sourceKey, Action<Stream, CancellationToken> writer)
        : this(sourceKey, (output, token, _) => writer(output, token))
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    internal MidiExportPreparedArtifact(string sourceKey,
        Action<Stream, CancellationToken, IProgress<StandardMidiFileWriteProgress>?> writer)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);
        SourceKey = sourceKey;
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public string SourceKey { get; }

    public ReadOnlyMemory<byte> Content
    {
        get
        {
            if (_content is null)
            {
                using MemoryStream output = new();
                _writer(output, CancellationToken.None, null);
                _content = output.ToArray();
            }
            return _content;
        }
    }

    internal void WriteTo(Stream output, CancellationToken cancellationToken = default,
        IProgress<StandardMidiFileWriteProgress>? progress = null) => _writer(output, cancellationToken, progress);
}

public sealed record MidiExportOutputItemResult(
    string SourceKey,
    string FullPath,
    MidiExportOutputItemState State);

public sealed record MidiExportOutputDiagnostic(
    string Code,
    string Message,
    string? Path = null);

public sealed class MidiExportOutputResult
{
    internal MidiExportOutputResult(
        MidiExportOutputItemResult[] items,
        MidiExportOutputDiagnostic[] diagnostics)
    {
        Items = Array.AsReadOnly(items);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public bool Succeeded => Items.All(item => item.State == MidiExportOutputItemState.Completed);
    public ReadOnlyCollection<MidiExportOutputItemResult> Items { get; }
    public ReadOnlyCollection<MidiExportOutputDiagnostic> Diagnostics { get; }
}

public sealed class MidiExportOutputException : IOException
{
    internal MidiExportOutputException(
        MidiExportOutputStage stage,
        string message,
        string outputDirectory,
        string? stagingDirectory,
        IReadOnlyList<MidiExportOutputItemResult> items,
        bool rollbackSucceeded,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        OutputDirectory = outputDirectory;
        StagingDirectory = stagingDirectory;
        Items = Array.AsReadOnly(items.ToArray());
        RollbackSucceeded = rollbackSucceeded;
    }

    public MidiExportOutputStage Stage { get; }
    public string OutputDirectory { get; }
    public string? StagingDirectory { get; }
    public IReadOnlyList<MidiExportOutputItemResult> Items { get; }
    public bool RollbackSucceeded { get; }
    public MidiExportArtifactDiagnostic? EncodingDiagnostic { get; internal init; }
}

public sealed class MidiExportOutputTransaction
{
    private readonly IMidiExportOutputFaultInjector _faultInjector;

    public MidiExportOutputTransaction()
        : this(NoOpMidiExportOutputFaultInjector.Instance)
    {
    }

    internal MidiExportOutputTransaction(IMidiExportOutputFaultInjector faultInjector)
    {
        _faultInjector = faultInjector ?? throw new ArgumentNullException(nameof(faultInjector));
    }

    public async Task<MidiExportOutputResult> PublishAsync(
        MidiExportFrozenOutputPlan plan,
        IEnumerable<MidiExportPreparedArtifact> artifacts,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default,
        IProgress<MidiExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!plan.Succeeded)
        {
            throw Failure(
                MidiExportOutputStage.Preflight,
                "The frozen MIDI export output plan contains blocking diagnostics.",
                plan,
                null,
                [],
                rollbackSucceeded: true);
        }
        if (plan.RequiresOverwriteAuthorization && !overwriteAuthorized)
        {
            throw Failure(
                MidiExportOutputStage.Preflight,
                "One or more frozen MIDI export targets exist without explicit overwrite authorization.",
                plan,
                null,
                ReadyItems(plan),
                rollbackSucceeded: true);
        }

        Dictionary<string, MidiExportPreparedArtifact> bySourceKey = MaterializeArtifacts(artifacts);
        ValidateArtifactSet(plan, bySourceKey);
        cancellationToken.ThrowIfCancellationRequested();

        string transactionId = Guid.NewGuid().ToString("N");
        string? outputParent = Path.GetDirectoryName(plan.OutputDirectory);
        if (outputParent is null)
        {
            throw Failure(
                MidiExportOutputStage.Preflight,
                "The selected MIDI export output directory has no parent directory.",
                plan,
                null,
                ReadyItems(plan),
                rollbackSucceeded: true);
        }

        string stagingDirectory = Path.Combine(outputParent, $".midora-midi-export-{transactionId}");
        List<PublishedItem> published = [];
        List<MidiExportOutputDiagnostic> diagnostics = [];
        bool finalizing = false;
        MidiExportPlannedTarget? currentTarget = null;
        try
        {
            try
            {
                Directory.CreateDirectory(outputParent);
                _faultInjector.ThrowIfRequested(
                    MidiExportOutputFaultPoint.BeforeStagingDirectory,
                    stagingDirectory);
                Directory.CreateDirectory(stagingDirectory);
                File.SetAttributes(
                    stagingDirectory,
                    File.GetAttributes(stagingDirectory) | FileAttributes.Hidden);
                // README contains actual successful encoding statistics, never predictions.
                foreach (MidiExportPlannedTarget target in plan.Targets.OrderBy(target => target.SourceKey == "readme"))
                {
                    currentTarget = target;
                    progress?.Report(new(target.FileName, "Encoding"));
                    cancellationToken.ThrowIfCancellationRequested();
                    string stagedPath = Path.Combine(stagingDirectory, target.FileName);
                    _faultInjector.ThrowIfRequested(
                        MidiExportOutputFaultPoint.BeforeArtifactWrite,
                        stagedPath);
                    await using FileStream staged = new(
                        stagedPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        256 * 1024,
                        FileOptions.SequentialScan);
                    bySourceKey[target.SourceKey].WriteTo(staged, cancellationToken,
                        progress is null ? null : new EncodingProgress(progress, target.FileName));
                    await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is MidiExportEncodingException or MidoraMidiException
                or ArgumentException or OverflowException)
            {
                MidiExportDiagnostic diagnostic = exception is MidiExportEncodingException encoding
                    ? encoding.Diagnostic : MidiExportEncodingErrors.FromException(exception);
                string message = $"MIDI encoding failed for {currentTarget?.FileName ?? "output"}: {diagnostic.Message}";
                throw new MidiExportOutputException(MidiExportOutputStage.Encoding, message, plan.OutputDirectory,
                    stagingDirectory, ReadyItems(plan), rollbackSucceeded: true, exception)
                {
                    EncodingDiagnostic = new(currentTarget?.SourceKey ?? "output", diagnostic with { Message = message })
                };
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                throw Failure(
                    MidiExportOutputStage.Staging,
                    $"The MIDI export artifacts could not be written to the staging directory ({currentTarget?.FileName ?? "output"}): {exception.Message}",
                    plan,
                    stagingDirectory,
                    ReadyItems(plan),
                    rollbackSucceeded: true,
                    exception);
            }

            try
            {
                _faultInjector.ThrowIfRequested(
                    MidiExportOutputFaultPoint.BeforeSelfValidation,
                    stagingDirectory);
                foreach (MidiExportPlannedTarget target in plan.Targets)
                {
                    currentTarget = target;
                    progress?.Report(new(target.FileName, "Validating"));
                    string stagedPath = Path.Combine(stagingDirectory, target.FileName);
                    if (target.FileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                    {
                        await using FileStream staged = new(
                            stagedPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            256 * 1024,
                            FileOptions.SequentialScan);
                        StandardMidiFile.ValidateType1(staged, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or MidoraMidiException
                or ArgumentException)
            {
                string message = $"A staged MIDI export artifact ({currentTarget?.FileName}) failed strict SMF Type 1 self-validation: {exception.Message}";
                throw new MidiExportOutputException(
                    MidiExportOutputStage.SelfValidation, message, plan.OutputDirectory,
                    stagingDirectory,
                    ReadyItems(plan),
                    rollbackSucceeded: true,
                    exception)
                {
                    EncodingDiagnostic = exception is MidoraMidiException
                        ? new(currentTarget?.SourceKey ?? "output", MidiExportEncodingErrors.FromException(exception) with { Message = message })
                        : null
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("", "Finalizing"));
            finalizing = true;
            if (!plan.OutputDirectoryExistedAtFreeze)
            {
                PublishNewDirectory(plan, stagingDirectory, published);
                stagingDirectory = string.Empty;
            }
            else
            {
                PublishIntoExistingDirectory(plan, stagingDirectory, published);
            }

            TryCleanupDirectory(stagingDirectory, diagnostics);
            return new(
                plan.Targets.Select(target => new MidiExportOutputItemResult(
                    target.SourceKey,
                    target.FullPath,
                    MidiExportOutputItemState.Completed)).ToArray(),
                diagnostics.ToArray());
        }
        catch (OperationCanceledException) when (!finalizing)
        {
            TryCleanupDirectory(stagingDirectory, diagnostics);
            if (Directory.Exists(stagingDirectory))
            {
                throw Failure(
                    MidiExportOutputStage.Cleanup,
                    "MIDI export was cancelled but its staging directory could not be cleaned.",
                    plan,
                    stagingDirectory,
                    ReadyItems(plan),
                    rollbackSucceeded: true);
            }
            throw;
        }
        catch (MidiExportOutputException exception) when (!finalizing)
        {
            TryCleanupDirectory(stagingDirectory, diagnostics);
            if (Directory.Exists(stagingDirectory))
            {
                throw Failure(
                    MidiExportOutputStage.Cleanup,
                    "MIDI export failed and its staging directory could not be cleaned.",
                    plan,
                    stagingDirectory,
                    exception.Items,
                    rollbackSucceeded: true,
                    exception);
            }
            throw;
        }
        catch (Exception exception) when (!finalizing)
        {
            // A deferred source/progress callback may fail with a non-I/O exception.
            // It must not bypass staging cleanup or become a partially published result.
            TryCleanupDirectory(stagingDirectory, diagnostics);
            bool cleaned = !Directory.Exists(stagingDirectory);
            throw Failure(cleaned ? MidiExportOutputStage.Staging : MidiExportOutputStage.Cleanup,
                $"MIDI export aborted before publication ({currentTarget?.FileName ?? "output"}): {exception.Message}" +
                (cleaned ? "" : " Its staging directory could not be cleaned."),
                plan, stagingDirectory, ReadyItems(plan), rollbackSucceeded: true, exception);
        }
        catch (Exception exception) when (finalizing)
        {
            try
            {
                RollBackPublished(plan, stagingDirectory, published);
                TryCleanupDirectory(stagingDirectory, diagnostics);
                throw Failure(
                    MidiExportOutputStage.Finalizing,
                    "The MIDI export could not publish every frozen output; all published items were rolled back.",
                    plan,
                    Directory.Exists(stagingDirectory) ? stagingDirectory : null,
                    FailedItems(plan, published, rolledBack: true),
                    rollbackSucceeded: true,
                    exception);
            }
            catch (MidiExportOutputException)
            {
                throw;
            }
            catch (Exception rollbackException)
            {
                throw Failure(
                    MidiExportOutputStage.Rollback,
                    "MIDI export publication failed and the original output set could not be fully restored.",
                    plan,
                    stagingDirectory,
                    FailedItems(plan, published, rolledBack: false),
                    rollbackSucceeded: false,
                    new AggregateException(exception, rollbackException));
            }
        }
    }

    private sealed class EncodingProgress(IProgress<MidiExportProgress> target, string fileName)
        : IProgress<StandardMidiFileWriteProgress>
    {
        public void Report(StandardMidiFileWriteProgress value) => target.Report(new(fileName, "Encoding", value));
    }

    private void PublishNewDirectory(
        MidiExportFrozenOutputPlan plan,
        string stagingDirectory,
        ICollection<PublishedItem> published)
    {
        _faultInjector.ThrowIfRequested(MidiExportOutputFaultPoint.BeforePublish, plan.OutputDirectory);
        if (Directory.Exists(plan.OutputDirectory) || File.Exists(plan.OutputDirectory))
        {
            throw new IOException("The frozen output directory target appeared before publication.");
        }
        Directory.Move(stagingDirectory, plan.OutputDirectory);
        foreach (MidiExportPlannedTarget target in plan.Targets)
        {
            published.Add(new(target, null, WasCreated: true));
        }
    }

    private void PublishIntoExistingDirectory(
        MidiExportFrozenOutputPlan plan,
        string stagingDirectory,
        ICollection<PublishedItem> published)
    {
        if (!Directory.Exists(plan.OutputDirectory))
        {
            throw new IOException("The frozen output directory disappeared before publication.");
        }
        for (int index = 0; index < plan.Targets.Count; index++)
        {
            MidiExportPlannedTarget target = plan.Targets[index];
            string stagedPath = Path.Combine(stagingDirectory, target.FileName);
            string? backupPath = target.ExistedAtFreeze
                ? Path.Combine(stagingDirectory, $".backup-{index:D4}")
                : null;
            _faultInjector.ThrowIfRequested(MidiExportOutputFaultPoint.BeforePublish, target.FullPath);
            if (target.ExistedAtFreeze)
            {
                if (!File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
                {
                    throw new IOException("A frozen existing MIDI export target changed type or disappeared.");
                }
                File.Replace(stagedPath, target.FullPath, backupPath);
            }
            else
            {
                if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
                {
                    throw new IOException("A new MIDI export target appeared after the output plan was frozen.");
                }
                File.Move(stagedPath, target.FullPath);
            }
            published.Add(new(target, backupPath, !target.ExistedAtFreeze));
        }
    }

    private void RollBackPublished(
        MidiExportFrozenOutputPlan plan,
        string stagingDirectory,
        IReadOnlyList<PublishedItem> published)
    {
        for (int index = published.Count - 1; index >= 0; index--)
        {
            PublishedItem item = published[index];
            _faultInjector.ThrowIfRequested(
                MidiExportOutputFaultPoint.BeforeRollback,
                item.Target.FullPath);
            if (item.WasCreated)
            {
                if (File.Exists(item.Target.FullPath))
                {
                    File.Delete(item.Target.FullPath);
                }
                continue;
            }

            string backupPath = item.BackupPath
                ?? throw new IOException("A replaced MIDI export target has no transaction backup.");
            if (File.Exists(item.Target.FullPath))
            {
                File.Replace(backupPath, item.Target.FullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(backupPath, item.Target.FullPath);
            }
        }

        if (!plan.OutputDirectoryExistedAtFreeze && Directory.Exists(plan.OutputDirectory))
        {
            Directory.Move(plan.OutputDirectory, stagingDirectory);
        }
    }

    private void TryCleanupDirectory(
        string? path,
        ICollection<MidiExportOutputDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }
        try
        {
            _faultInjector.ThrowIfRequested(MidiExportOutputFaultPoint.BeforeCleanup, path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-CLEANUP-FAILED",
                exception.Message,
                path));
        }
    }

    private static Dictionary<string, MidiExportPreparedArtifact> MaterializeArtifacts(
        IEnumerable<MidiExportPreparedArtifact> artifacts)
    {
        Dictionary<string, MidiExportPreparedArtifact> result = new(StringComparer.Ordinal);
        foreach (MidiExportPreparedArtifact artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!result.TryAdd(artifact.SourceKey, artifact))
            {
                throw new ArgumentException(
                    $"The MIDI export artifact set contains duplicate source key '{artifact.SourceKey}'.",
                    nameof(artifacts));
            }
        }
        return result;
    }

    private static void ValidateArtifactSet(
        MidiExportFrozenOutputPlan plan,
        IReadOnlyDictionary<string, MidiExportPreparedArtifact> artifacts)
    {
        string[] planned = plan.Targets.Select(target => target.SourceKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] supplied = artifacts.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!planned.SequenceEqual(supplied, StringComparer.Ordinal))
        {
            throw Failure(
                MidiExportOutputStage.Preflight,
                "The prepared artifact set does not exactly match the frozen MIDI export output plan.",
                plan,
                null,
                ReadyItems(plan),
                rollbackSucceeded: true);
        }
    }

    private static MidiExportOutputItemResult[] ReadyItems(MidiExportFrozenOutputPlan plan) =>
        plan.Targets.Select(target => new MidiExportOutputItemResult(
            target.SourceKey,
            target.FullPath,
            MidiExportOutputItemState.ReadyToPublish)).ToArray();

    private static MidiExportOutputItemResult[] FailedItems(
        MidiExportFrozenOutputPlan plan,
        IReadOnlyCollection<PublishedItem> published,
        bool rolledBack)
    {
        HashSet<string> publishedKeys = published.Select(item => item.Target.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        return plan.Targets.Select(target => new MidiExportOutputItemResult(
            target.SourceKey,
            target.FullPath,
            publishedKeys.Contains(target.SourceKey) && rolledBack
                ? MidiExportOutputItemState.Cleaned
                : MidiExportOutputItemState.Failed)).ToArray();
    }

    private static MidiExportOutputException Failure(
        MidiExportOutputStage stage,
        string message,
        MidiExportFrozenOutputPlan plan,
        string? stagingDirectory,
        IReadOnlyList<MidiExportOutputItemResult> items,
        bool rollbackSucceeded,
        Exception? innerException = null) =>
        new(
            stage,
            message,
            plan.OutputDirectory,
            stagingDirectory,
            items,
            rollbackSucceeded,
            innerException);

    private sealed record PublishedItem(
        MidiExportPlannedTarget Target,
        string? BackupPath,
        bool WasCreated);
}
