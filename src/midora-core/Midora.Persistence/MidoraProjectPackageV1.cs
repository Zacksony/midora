using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;

namespace Midora.Persistence;

public enum MidoraPackageDiagnosticSeverityV1
{
    Error,
    Warning,
    Information
}

public enum MidoraPackageDiagnosticCategoryV1
{
    FileFormat,
    FileDamage,
    VersionCompatibility,
    Resource,
    SaveTransaction
}

public enum MidoraPackageStageV1
{
    Preflight,
    Staging,
    Container,
    Manifest,
    VersionPreflight,
    HashValidation,
    Structure,
    Serialization,
    SelfValidation,
    Backup,
    Publish,
    Cleanup
}

public sealed record MidoraPackageDiagnosticV1(
    MidoraPackageDiagnosticSeverityV1 Severity,
    MidoraPackageDiagnosticCategoryV1 Category,
    string Code,
    string Message,
    string? PackagePath = null);

public sealed record MidoraProjectFileInformationV1(
    string CreatedWithSoftwareVersion,
    string LastSavedWithSoftwareVersion);

public sealed record MidoraLegacyProjectSourceIdentityV3(
    string SourcePath,
    int SourceFileFormatVersion,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256);

public sealed record MidoraLegacyProjectUpgradePlanV3(
    MidoraLegacyProjectSourceIdentityV3 SourceIdentity,
    int TargetFileFormatVersion,
    string PermanentBackupPath);

public sealed record MidoraProjectOpenResultV1(
    MidoraProject Project,
    MidoraProjectFileInformationV1 FileInformation,
    bool IsModified,
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics,
    bool RequiresFormatUpgrade = false,
    int SourceFileFormatVersion = PersistenceContractV4.FileFormatVersion,
    ProjectPresentationStateV3? PresentationState = null,
    bool IsPresentationModified = false,
    MidoraLegacyProjectSourceIdentityV3? LegacySourceIdentity = null) : IDisposable, IAsyncDisposable
{
    public ProjectPresentationStateV3 Presentation =>
        PresentationState ?? ProjectPresentationStateV3.Empty;

    public void Dispose() => Project.Dispose();

    public ValueTask DisposeAsync()
    {
        Project.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record MidoraProjectSaveResultV1(
    string TargetPath,
    MidoraProjectFileInformationV1 FileInformation,
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics,
    string? PermanentLegacyBackupPath = null,
    IReadOnlyList<string>? OmittedPresentationSections = null);

public class MidoraPackageExceptionV1 : IOException
{
    public MidoraPackageExceptionV1(
        MidoraPackageStageV1 stage,
        string message,
        string? targetPath = null,
        string? packagePath = null,
        string? backupPath = null,
        string? temporaryPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        TargetPath = targetPath;
        PackagePath = packagePath;
        BackupPath = backupPath;
        TemporaryPath = temporaryPath;
    }

    public MidoraPackageStageV1 Stage { get; }
    public string? TargetPath { get; }
    public string? PackagePath { get; }
    public string? BackupPath { get; }
    public string? TemporaryPath { get; }
}

public sealed class MidoraPackageVersionCompatibilityExceptionV1 : MidoraPackageExceptionV1
{
    internal MidoraPackageVersionCompatibilityExceptionV1(
        string targetPath,
        ManifestVersionHeaderV1 header)
        : base(
            MidoraPackageStageV1.VersionPreflight,
            "The Midora package requires a newer file-format or manifest-schema reader.",
            targetPath,
            MidoraPackagePathsV1.Manifest)
    {
        FileFormatVersion = header.FileFormatVersion;
        MinimumReadableVersion = header.MinimumReadableVersion;
        ManifestSchemaVersion = header.ManifestSchemaVersion;
    }

    public int FileFormatVersion { get; }
    public int MinimumReadableVersion { get; }
    public int ManifestSchemaVersion { get; }
    public int SupportedFileFormatVersion => PersistenceContractV4.FileFormatVersion;
    public int SupportedManifestSchemaVersion => PersistenceContractV4.ManifestSchemaVersion;
}

public sealed class MidoraProjectPackageV1
{
    private static readonly DateTimeOffset CanonicalZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _softwareVersion;
    private readonly TimeProvider _timeProvider;
    private readonly IMidoraPackageFaultInjectorV1 _faultInjector;
    private readonly IInstrumentChangeStorageLoader? _instrumentChangeStorage;

    public MidoraProjectPackageV1(string softwareVersion, TimeProvider? timeProvider = null,
        IInstrumentChangeStorageLoader? instrumentChangeStorage = null)
        : this(softwareVersion, timeProvider, NoOpMidoraPackageFaultInjectorV1.Instance)
    {
        _instrumentChangeStorage = instrumentChangeStorage;
    }

    internal MidoraProjectPackageV1(
        string softwareVersion,
        TimeProvider? timeProvider,
        IMidoraPackageFaultInjectorV1 faultInjector)
    {
        PersistenceValueValidationV1.ValidateShortText(
            softwareVersion, nameof(softwareVersion), allowEmpty: false);
        _softwareVersion = softwareVersion;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector ?? throw new ArgumentNullException(nameof(faultInjector));
    }

    public async Task<MidoraProjectOpenResultV1> OpenAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        string path = NormalizeFilePath(packagePath, nameof(packagePath));
        if (!File.Exists(path))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The Midora package does not exist.",
                targetPath: path);
        }

        try
        {
            _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforeContainerOpen, path);
            await using FileStream sourceLease = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            MidoraProjectOpenResultV1 opened = await OpenCoreAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (!opened.RequiresFormatUpgrade)
            {
                return opened;
            }

            sourceLease.Position = 0;
            byte[] digest = await SHA256.HashDataAsync(sourceLease, cancellationToken)
                .ConfigureAwait(false);
            MidoraLegacyProjectSourceIdentityV3 identity = new(
                path,
                opened.SourceFileFormatVersion,
                sourceLease.Length,
                File.GetLastWriteTimeUtc(path),
                Convert.ToHexStringLower(digest));
            return opened with { LegacySourceIdentity = identity };
        }
        catch (MidoraPackageExceptionV1)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Structure,
                "The Midora package contains invalid Project data.",
                targetPath: path,
                innerException: exception);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The Midora package could not be read.",
                targetPath: path,
                innerException: exception);
        }
    }

    public Task<MidoraProjectSaveResultV1> SaveProjectAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            ProjectPresentationStateV3.Empty,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: true,
            legacyUpgrade: null,
            cancellationToken);

    public Task<MidoraProjectSaveResultV1> SaveProjectAsync(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            presentation,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: true,
            legacyUpgrade: null,
            cancellationToken);

    public Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            ProjectPresentationStateV3.Empty,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: false,
            legacyUpgrade: null,
            cancellationToken);

    public Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            presentation,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: false,
            legacyUpgrade: null,
            cancellationToken);

    public async Task<MidoraLegacyProjectUpgradePlanV3> PrepareLegacyProjectUpgradeAsync(
        MidoraLegacyProjectSourceIdentityV3 sourceIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ValidateLegacySourceIdentity(sourceIdentity);
        string backupPath = await PlanPermanentLegacyBackupPathAsync(
            sourceIdentity,
            cancellationToken).ConfigureAwait(false);
        return new(
            sourceIdentity,
            PersistenceContractV4.FileFormatVersion,
            backupPath);
    }

    public async Task<MidoraProjectSaveResultV1> UpgradeLegacyProjectInPlaceAsync(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        MidoraLegacyProjectSourceIdentityV3 sourceIdentity,
        MidoraProjectFileInformationV1 fileInformation,
        ProjectEditingTimeSession? editingTimeSession = null,
        CancellationToken cancellationToken = default)
    {
        MidoraLegacyProjectUpgradePlanV3 plan = await PrepareLegacyProjectUpgradeAsync(
            sourceIdentity,
            cancellationToken).ConfigureAwait(false);
        return await UpgradeLegacyProjectInPlaceAsync(
            project,
            presentation,
            plan,
            fileInformation,
            editingTimeSession,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<MidoraProjectSaveResultV1> UpgradeLegacyProjectInPlaceAsync(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        MidoraLegacyProjectUpgradePlanV3 plan,
        MidoraProjectFileInformationV1 fileInformation,
        ProjectEditingTimeSession? editingTimeSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateLegacyUpgradePlan(plan);
        return SaveCoreAsync(
            project,
            presentation,
            plan.SourceIdentity.SourcePath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized: true,
            updateCurrentProject: true,
            legacyUpgrade: plan,
            cancellationToken);
    }

    private async Task<MidoraProjectSaveResultV1> SaveCoreAsync(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation,
        ProjectEditingTimeSession? editingTimeSession,
        bool overwriteAuthorized,
        bool updateCurrentProject,
        MidoraLegacyProjectUpgradePlanV3? legacyUpgrade,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(presentation);
        string target = NormalizeFilePath(targetPath, nameof(targetPath));
        string? directory = Path.GetDirectoryName(target);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The target directory does not exist.",
                targetPath: target);
        }
        bool targetExisted = File.Exists(target);
        MidoraLegacyProjectSourceIdentityV3? legacySourceIdentity = legacyUpgrade?.SourceIdentity;
        if (legacyUpgrade is not null
            && (!PathsEqual(target, legacySourceIdentity!.SourcePath)
                || !targetExisted))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The legacy in-place upgrade source identity is invalid.",
                targetPath: target);
        }
        if (targetExisted && !overwriteAuthorized)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The target exists but overwrite was not explicitly authorized.",
                targetPath: target);
        }
        DateTimeOffset savedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        _ = editingTimeSession?.SnapshotTotalEditingTimeMilliseconds();
        ProjectMetadataSnapshot metadata = project.Metadata.Snapshot() with { ModifiedAtUtc = savedAtUtc };
        MidoraProjectFileInformationV1 outputFileInformation = new(
            fileInformation?.CreatedWithSoftwareVersion ?? _softwareVersion,
            _softwareVersion);

        ProjectPresentationSerializationResultV4 preparedPresentation;
        List<MidoraPackageDiagnosticV1> saveDiagnostics = [];
        try
        {
            ValidateSupportedProject(project, cancellationToken);
            preparedPresentation = ProjectPresentationCodecV4.PrepareForSave(presentation, project);
            if (preparedPresentation.OmittedSections.Count != 0)
            {
                saveDiagnostics.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Warning,
                    MidoraPackageDiagnosticCategoryV1.Resource,
                    "MIDORA-PERSIST-PRESENTATION-SECTION-OMITTED",
                    $"Some presentation sections were omitted while saving because they were invalid or exceeded the presentation byte budget: {string.Join(", ", preparedPresentation.OmittedSections)}.",
                    MidoraPackagePathsV1.ProjectPresentation));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Serialization,
                "The current Project cannot be serialized as the current package format.",
                targetPath: target,
                innerException: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Serialization,
                "Project validation could not complete its temporary storage work; the target was not changed.",
                targetPath: target,
                innerException: exception);
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string temporaryDirectory = Path.Combine(directory, $".midora-save-{transactionId}");
        string temporaryPackage = Path.Combine(directory, $".{Path.GetFileName(target)}.midora-temp-{transactionId}");
        string? backupPath = targetExisted && legacyUpgrade is null
            ? Path.Combine(directory, $".{Path.GetFileName(target)}.midora-backup-{transactionId}.bak")
            : null;
        string? permanentLegacyBackupPath = null;
        FileStream? legacySourceLease = null;
        bool publishAttempted = false;
        List<MidoraPackageDiagnosticV1> cleanupDiagnostics = [];
        PackageContentV1? content = null;

        try
        {
            if (legacyUpgrade is not null)
            {
                legacySourceLease = await OpenAndVerifyLegacySourceAsync(
                    legacySourceIdentity!,
                    cancellationToken).ConfigureAwait(false);
            }
            if (backupPath is not null)
            {
                try
                {
                    _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforeBackup, backupPath);
                    File.Copy(target, backupPath, overwrite: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new MidoraPackageExceptionV1(
                        MidoraPackageStageV1.Backup,
                        "The existing target could not be copied to the transaction backup.",
                        target,
                        backupPath: backupPath,
                        temporaryPath: temporaryPackage,
                        innerException: exception);
                }
            }

            try
            {
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeContentWrite,
                    temporaryDirectory);
                Directory.CreateDirectory(temporaryDirectory);
                File.SetAttributes(
                    temporaryDirectory,
                    File.GetAttributes(temporaryDirectory) | FileAttributes.Hidden);
                try
                {
                    content = BuildContent(
                        project,
                        preparedPresentation.State,
                        metadata,
                        outputFileInformation,
                        temporaryDirectory,
                        knownPureMidiPackEntries: null,
                        cancellationToken,
                        precomputedPresentationBytes: preparedPresentation.Bytes);
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or ArgumentException
                    or OverflowException)
                {
                    throw new MidoraPackageExceptionV1(
                        MidoraPackageStageV1.Serialization,
                        "The current Project cannot be serialized as the current package format.",
                        targetPath: target,
                        temporaryPath: temporaryDirectory,
                        innerException: exception);
                }
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeZipWrite,
                    temporaryPackage);
                await WriteZipAsync(temporaryDirectory, temporaryPackage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Staging,
                    "The save transaction could not build its temporary package.",
                    target,
                    backupPath: backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            MidoraProjectOpenResultV1 reopened;
            try
            {
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeSelfValidation,
                    temporaryPackage);
                reopened = await OpenCoreAsync(
                    temporaryPackage,
                    cancellationToken,
                    trustedStagingContentRoot: temporaryDirectory).ConfigureAwait(false);
                using (reopened)
                {
                    if (reopened.IsModified || reopened.Diagnostics.Count != 0)
                    {
                        string details = string.Join(
                            " | ",
                            reopened.Diagnostics.Select(value => $"{value.PackagePath}: {value.Message}"));
                        throw new InvalidDataException(
                            $"The temporary package reopened with recovery diagnostics: {details}");
                    }
                    PackageContentV1 reopenedContent = BuildContent(
                        reopened.Project,
                        reopened.Presentation,
                        reopened.Project.Metadata.Snapshot(),
                        reopened.FileInformation,
                        contentRoot: temporaryDirectory,
                        content.PureMidiPackEntries,
                        cancellationToken,
                        compareStagedContent: true);
                    RequireEqualContent(content.Files, reopenedContent.Files);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.SelfValidation,
                    "The temporary package failed strict reopen self-validation.",
                    target,
                    backupPath: backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            if (legacyUpgrade is not null)
            {
                FileStream sourceLease = legacySourceLease
                    ?? throw new InvalidOperationException(
                        "The verified legacy source lease is unavailable.");
                permanentLegacyBackupPath = await CreateOrReusePermanentLegacyBackupAsync(
                    sourceLease,
                    legacySourceIdentity!,
                    legacyUpgrade.PermanentBackupPath,
                    cancellationToken).ConfigureAwait(false);
                sourceLease.Dispose();
                legacySourceLease = null;
            }

            publishAttempted = true;
            try
            {
                _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforePublish, target);
                if (!targetExisted && File.Exists(target))
                {
                    throw new IOException(
                        "The target appeared after preflight; overwrite authorization and backup were not frozen for it.");
                }
                if (File.Exists(target))
                {
                    File.Replace(temporaryPackage, target, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPackage, target);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Publish,
                    "The validated temporary package could not atomically replace or create the target.",
                    target,
                    backupPath: permanentLegacyBackupPath ?? backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            TryDeleteFile(
                backupPath,
                cleanupDiagnostics,
                MidoraPackageFaultPointV1.BeforeBackupCleanup);
            TryDeleteDirectory(
                temporaryDirectory,
                cleanupDiagnostics,
                MidoraPackageFaultPointV1.BeforeStagingDirectoryCleanup);
            if (updateCurrentProject)
            {
                project.Metadata.CommitSuccessfulSave(savedAtUtc);
            }
            return new MidoraProjectSaveResultV1(
                target,
                outputFileInformation,
                saveDiagnostics.Concat(cleanupDiagnostics).ToArray(),
                permanentLegacyBackupPath,
                preparedPresentation.OmittedSections);
        }
        catch
        {
            legacySourceLease?.Dispose();
            if (publishAttempted)
            {
                TryDeleteDirectory(temporaryDirectory, cleanupDiagnostics);
            }
            else
            {
                TryDeleteFile(temporaryPackage, cleanupDiagnostics);
                TryDeleteDirectory(temporaryDirectory, cleanupDiagnostics);
                TryDeleteFile(backupPath, cleanupDiagnostics);
            }
            throw;
        }
    }

    private async Task<MidoraProjectOpenResultV1> OpenCoreAsync(
        string path,
        CancellationToken cancellationToken,
        string? trustedStagingContentRoot = null)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using ZipArchive archive = OpenZipArchive(stream, path);
        Dictionary<string, ZipArchiveEntry> entries = ValidateContainer(archive, path);
        if (!entries.TryGetValue(MidoraPackagePathsV1.Manifest, out ZipArchiveEntry? manifestEntry))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is missing from the package root.",
                path,
                MidoraPackagePathsV1.Manifest);
        }

        byte[] manifestBytes;
        try
        {
            _faultInjector.ThrowIfRequested(
                MidoraPackageFaultPointV1.BeforeManifestRead,
                MidoraPackagePathsV1.Manifest);
            manifestBytes = await ReadEntryAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }

        ManifestVersionHeaderV1 versionHeader;
        try
        {
            versionHeader = ManifestCodecV1.ReadVersionHeader(manifestBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }
        if (versionHeader.FileFormatVersion > PersistenceContractV4.FileFormatVersion
            || versionHeader.MinimumReadableVersion > PersistenceContractV4.FileFormatVersion
            || versionHeader.ManifestSchemaVersion > PersistenceContractV4.ManifestSchemaVersion)
        {
            throw new MidoraPackageVersionCompatibilityExceptionV1(path, versionHeader);
        }

        PackageManifestView manifest;
        try
        {
            manifest = versionHeader.FileFormatVersion switch
            {
                PersistenceContractV1.FileFormatVersion => PackageManifestView.FromV1(
                    ManifestCodecV1.Parse(manifestBytes)),
                PersistenceContractV2.FileFormatVersion => PackageManifestView.FromV2(
                    ManifestCodecV2.Parse(manifestBytes)),
                PersistenceContractV3.FileFormatVersion => PackageManifestView.FromV3(
                    ManifestCodecV3.Parse(manifestBytes)),
                PersistenceContractV4.FileFormatVersion => PackageManifestView.FromV4(
                    ManifestCodecV4.Parse(manifestBytes)),
                _ => throw new InvalidDataException(
                    $"Unsupported Midora file-format version {versionHeader.FileFormatVersion}.")
            };
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }

        bool requiresFormatUpgrade = manifest.FileFormatVersion < PersistenceContractV4.FileFormatVersion;
        Dictionary<string, ManifestFileEntryJsonV1> index = ValidateManifestIndex(manifest.Files, path);
        List<MidoraPackageDiagnosticV1> diagnostics = CollectExtraEntryDiagnostics(entries, index);
        if (requiresFormatUpgrade)
        {
            diagnostics.Add(new(
                MidoraPackageDiagnosticSeverityV1.Information,
                MidoraPackageDiagnosticCategoryV1.VersionCompatibility,
                "MIDORA-PERSIST-FORMAT-MIGRATED",
                $"The Format {manifest.FileFormatVersion} Project was migrated in memory. Saving will write Format 4.",
                MidoraPackagePathsV1.Manifest));
        }

        byte[] projectBytes = await ReadRequiredValidatedAsync(
            MidoraPackagePathsV1.Project, "core-json", entries, index, path, cancellationToken)
            .ConfigureAwait(false);
        ProjectJsonV1 projectIndex;
        try
        {
            projectIndex = ProjectCodecV1.Parse(projectBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            throw StructureFailure(path, MidoraPackagePathsV1.Project, "project.json is invalid.", exception);
        }
        diagnostics.AddRange(CollectOrphanObjectDiagnostics(index, projectIndex));
        bool isModified = requiresFormatUpgrade;
        ProjectSettingsJsonV1 projectSettings;
        try
        {
            byte[]? projectSettingsBytes = await TryReadValidatedAsync(
                MidoraPackagePathsV1.ProjectSettings, "settings-json", entries, index, path, cancellationToken)
                .ConfigureAwait(false);
            projectSettings = projectSettingsBytes is null
                ? throw new InvalidDataException("project-settings.json is missing.")
                : ProjectSettingsCodecV1.Parse(projectSettingsBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException
            or MidoraPackageExceptionV1
        {
            Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
        })
        {
            projectSettings = new ProjectSettingsJsonV1
            {
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                TicksPerQuarterNote = 192,
                GlobalInitialState = MidiStateCodecV1.FromDomain(new MidiInitialState())
            };
            AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.ProjectSettings, exception.Message);
            isModified = true;
        }

        long storedNextStableId = ProjectCodecV1.GetNextStableId(projectIndex);
        MidoraProject project = new(
            projectSettings.TicksPerQuarterNote,
            storedNextStableId,
            _timeProvider.GetUtcNow());
        PureMidiContentPackExtractionV1 pureMidiContent = new(
            project,
            trustedStagingContentRoot);
        try
        {
            MidiStateCodecV1.Restore(project.GlobalInitialState, projectSettings.GlobalInitialState);

            await RestoreProjectObjectsAsync(
                project,
                projectIndex,
                manifest.FileFormatVersion,
                entries,
                index,
                path,
                diagnostics,
                pureMidiContent,
                cancellationToken).ConfigureAwait(false);

            bool conductorFallback = false;
            try
            {
                ZipArchiveEntry? conductorEntry = await TryReadValidatedEntryAsync(
                    MidoraPackagePathsV1.ConductorTrack, "conductor-json", entries, index, path, cancellationToken)
                    .ConfigureAwait(false);
                if (conductorEntry is null) throw new InvalidDataException("conductor-track.json is missing.");
                using Stream conductorInput = conductorEntry.Open();
                ConductorTrackCodecV1.Restore(project, conductorInput, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException
                or System.Text.Json.JsonException
                or MidoraPackageExceptionV1
            {
                Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
            })
            {
                conductorFallback = true;
                AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.ConductorTrack, exception.Message);
                isModified = true;
            }

            if (manifest.FileFormatVersion >= PersistenceContractV4.FileFormatVersion)
            {
                var associationEntry = await TryReadValidatedEntryAsync(PersistenceContractV4.InstrumentChangesPath,
                    "instrument-changes-pb", entries, index, path, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The Instrument Changes component is missing.");
                using var associationInput = associationEntry.Open();
                InstrumentChangesProtobufCodecV1.Restore(project, associationInput, cancellationToken, _instrumentChangeStorage);
            }

            ValidateLoadedStableIds(
                projectIndex,
                project,
                conductorFallback ? null : project.Conductor,
                storedNextStableId,
                cancellationToken);
            project.RestoreNextStableId(storedNextStableId);
            if (conductorFallback)
            {
                try
                {
                    project.Conductor.Tempos.Add(new TempoChange(project, 0, 120m));
                    project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 0, 4, 4));
                }
                catch (InvalidOperationException exception)
                {
                    throw StructureFailure(
                        path,
                        MidoraPackagePathsV1.ConductorTrack,
                        "The damaged Conductor Track cannot be replaced because the stable ID space is exhausted.",
                        exception);
                }
            }

            byte[]? metadataBytes = await TryReadValidatedAsync(
                MidoraPackagePathsV1.Metadata, "core-json", entries, index, path, cancellationToken)
                .ConfigureAwait(false);
            if (metadataBytes is null)
            {
                AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.Metadata, "metadata.json is missing.");
                isModified = true;
            }
            else
            {
                try
                {
                    MetadataCodecV1.Restore(project.Metadata, metadataBytes);
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
                {
                    throw StructureFailure(path, MidoraPackagePathsV1.Metadata, "metadata.json is present but invalid.", exception);
                }
            }

            GlobalResetDefaultsJsonV1? reset = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.GlobalResetDefaults,
                bytes => GlobalResetDefaultsCodecV1.Parse(bytes),
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);
            if (reset is not null) MidiStateCodecV1.Restore(project.GlobalResetDefaults, reset.State);

            _ = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.GlobalEventScopeDefaults,
                bytes =>
                {
                    GlobalEventScopeDefaultsCodecV1.Parse(bytes);
                    return true;
                },
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);

            ProjectPresentationStateV3 presentation = ProjectPresentationStateV3.Empty;
            bool presentationModified = requiresFormatUpgrade;
            if (manifest.FileFormatVersion >= PersistenceContractV3.FileFormatVersion)
            {
                try
                {
                    byte[]? presentationBytes = await TryReadValidatedAsync(
                        MidoraPackagePathsV1.ProjectPresentation,
                        "project-presentation-json",
                        entries,
                        index,
                        path,
                        cancellationToken,
                        expectedSchemaVersion: index[MidoraPackagePathsV1.ProjectPresentation].SchemaVersion!.Value).ConfigureAwait(false);
                    if (presentationBytes is null)
                    {
                        throw new InvalidDataException("project-presentation.json is missing.");
                    }
                    ProjectPresentationParseResultV4 parsedPresentation =
                        ProjectPresentationCodecV3.ParseWithRecovery(
                            presentationBytes,
                            project,
                            index[MidoraPackagePathsV1.ProjectPresentation].SchemaVersion);
                    presentation = parsedPresentation.State;
                    if (parsedPresentation.Recovered)
                    {
                        diagnostics.Add(new(
                            MidoraPackageDiagnosticSeverityV1.Warning,
                            MidoraPackageDiagnosticCategoryV1.FileDamage,
                            "MIDORA-PERSIST-PRESENTATION-SECTION-RECOVERED",
                            $"Some project presentation sections were reset to defaults: {string.Join(", ", parsedPresentation.RecoveredSections)}.",
                            MidoraPackagePathsV1.ProjectPresentation));
                        presentationModified = true;
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or System.Text.Json.JsonException
                    or MidoraPackageExceptionV1
                    {
                        Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
                    })
                {
                    diagnostics.Add(new(
                        MidoraPackageDiagnosticSeverityV1.Warning,
                        MidoraPackageDiagnosticCategoryV1.FileDamage,
                        "MIDORA-PERSIST-PRESENTATION-RECOVERED",
                        $"Project presentation data was ignored and reset to defaults. {exception.Message}",
                        MidoraPackagePathsV1.ProjectPresentation));
                    presentationModified = true;
                }
            }

            return new MidoraProjectOpenResultV1(
                project,
                new MidoraProjectFileInformationV1(
                    manifest.CreatedWithSoftwareVersion,
                    manifest.LastSavedWithSoftwareVersion),
                isModified,
                diagnostics,
                requiresFormatUpgrade,
                manifest.FileFormatVersion,
                presentation,
                presentationModified);
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    private PackageContentV1 BuildContent(
        MidoraProject project,
        ProjectPresentationStateV3 presentation,
        ProjectMetadataSnapshot metadata,
        MidoraProjectFileInformationV1 fileInformation,
        string contentRoot,
        IReadOnlyList<ManifestFileEntryJsonV1>? knownPureMidiPackEntries,
        CancellationToken cancellationToken,
        bool compareStagedContent = false,
        byte[]? precomputedPresentationBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        Dictionary<string, StructuralFileV1> content = new(StringComparer.Ordinal);
        AddBytes(MidoraPackagePathsV1.Project, () => ProjectCodecV1.Serialize(project));
        AddBytes(MidoraPackagePathsV1.Metadata, () => MetadataCodecV1.Serialize(metadata));
        Add(MidoraPackagePathsV1.ConductorTrack,
            stream => ConductorTrackCodecV1.Serialize(project, stream, cancellationToken));
        AddBytes(MidoraPackagePathsV1.ProjectSettings, () => ProjectSettingsCodecV1.Serialize(project));
        AddBytes(MidoraPackagePathsV1.GlobalResetDefaults,
            () => GlobalResetDefaultsCodecV1.Serialize(project.GlobalResetDefaults));
        AddBytes(MidoraPackagePathsV1.GlobalEventScopeDefaults, GlobalEventScopeDefaultsCodecV1.Serialize);
        AddBytes(MidoraPackagePathsV1.ProjectPresentation,
            () => precomputedPresentationBytes ?? ProjectPresentationCodecV3.Serialize(presentation, project));
        Add(PersistenceContractV4.InstrumentChangesPath,
            stream => InstrumentChangesProtobufCodecV1.Serialize(project, stream, cancellationToken, _instrumentChangeStorage));
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            Add(
                $"event-instruments/ei_{instrument.Id}.pb",
                stream => EventInstrumentProtobufCodecV2.Serialize(instrument, stream, cancellationToken));
        }
        foreach (EventInstrumentUsage usage in project.EventInstrumentUsages)
        {
            AddBytes(
                $"event-instrument-usages/eiu_{usage.Id}.pb",
                () => EventInstrumentUsageProtobufCodecV1.Serialize(usage));
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            Add(
                $"logical-tracks/lt_{track.Id}.pb",
                stream => LogicalTrackProtobufCodecV1.Serialize(track, stream, cancellationToken));
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            AddBytes(
                $"midi-channel-roots/mcr_{root.Id}.pb",
                () => MidiChannelRootProtobufCodecV1.Serialize(root));
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            string contentPackPath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
            AddBytes(
                $"midi-tracks/mt_{track.Id}.pb",
                () => PureMidiTrackProtobufCodecV1.Serialize(track, contentPackPath));
        }
        ManifestFileEntryJsonV1[] pureMidiPackEntries;
        if (knownPureMidiPackEntries is not null)
        {
            pureMidiPackEntries = knownPureMidiPackEntries
                .OrderBy(value => value.Path, StringComparer.Ordinal)
                .ToArray();
            string[] expected = project.PureMidiTracks
                .Select(value => MidoraPackagePathsV1.PureMidiContentPack(value.Id))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!pureMidiPackEntries.Select(value => value.Path).SequenceEqual(expected))
                throw new InvalidDataException("Pure MIDI content-pack entries do not match Project tracks.");
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
            pureMidiPackEntries = project.PureMidiTracks
                .OrderBy(value => value.Id)
                .Select(track => PureMidiContentPackPersistenceV1.Materialize(
                    track,
                    contentRoot,
                    cancellationToken))
                .ToArray();
        }
        ManifestFileEntryJsonV1[] manifestFiles = content.Select(item => new ManifestFileEntryJsonV1
        {
            Path = item.Key,
            Kind = GetExpectedKind(item.Key),
            SchemaVersion = GetCurrentSchemaVersion(item.Key),
            Sha256 = item.Value.Sha256
        })
            .Concat(pureMidiPackEntries)
            .ToArray();
        ManifestJsonV4 manifest = new()
        {
            Magic = "midora-project",
            FileFormatVersion = PersistenceContractV4.FileFormatVersion,
            MinimumReadableVersion = PersistenceContractV4.FileFormatVersion,
            ManifestSchemaVersion = PersistenceContractV4.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = fileInformation.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = fileInformation.LastSavedWithSoftwareVersion,
            Files = manifestFiles
        };
        AddBytes(MidoraPackagePathsV1.Manifest, () => ManifestCodecV4.Serialize(manifest));
        return new(content, pureMidiPackEntries);

        void AddBytes(string path, Func<byte[]> serialize) => Add(path, stream => stream.Write(serialize()));

        void Add(string path, Action<Stream> serialize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = Path.Combine(contentRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!compareStagedContent) Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using FileStream file = new(filePath,
                compareStagedContent ? FileMode.Open : FileMode.CreateNew,
                compareStagedContent ? FileAccess.Read : FileAccess.Write,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using PackageContentStreamV1 contentStream = new(file, compareStagedContent, cancellationToken);
            using BufferedStream buffered = new(contentStream, 64 * 1024);
            serialize(buffered);
            buffered.Flush();
            (long length, string hash) = contentStream.Complete();
            // Staging is disposable transaction input, not a recovery artifact.
            // Flush managed buffers for ZIP/self-validation reads; durable flush
            // belongs to the final package/backup publication, not every entry.
            if (!compareStagedContent) file.Flush();
            content.Add(path, new(length, hash));
        }
    }

    private static void ValidateSupportedProject(MidoraProject project, CancellationToken cancellationToken)
    {
        if (project.DamagedEventInstruments.Count != 0
            || project.DamagedEventInstrumentUsages.Count != 0
            || project.DamagedLogicalTracks.Count != 0
            || project.DamagedMidiChannelRoots.Count != 0
            || project.DamagedPureMidiTracks.Count != 0)
        {
            throw new InvalidDataException(
                "Projects containing damaged object placeholders cannot be saved.");
        }
        ValidateArrangementGraph(project);

        using StableIdValidatorV1 ids = new(cancellationToken);
        foreach (MidoraId id in EnumerateConductorIds(project.Conductor))
        {
            AddId(id, project.NextStableId, ids, "Conductor event");
        }
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            foreach (MidoraId id in EnumerateEventInstrumentIds(instrument))
            {
                AddId(id, project.NextStableId, ids, "Event Instrument object");
            }
        }
        foreach (EventInstrumentUsage usage in project.EventInstrumentUsages)
        {
            AddId(usage.Id, project.NextStableId, ids, "Event Instrument Usage");
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            foreach (MidoraId id in EnumerateLogicalTrackIds(track))
            {
                AddId(id, project.NextStableId, ids, "Logical Track object");
            }
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            AddId(root.Id, project.NextStableId, ids, "MIDI Channel Root");
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            foreach (MidoraId id in EnumeratePureMidiTrackIds(track, cancellationToken))
            {
                AddId(id, project.NextStableId, ids, "Pure MIDI Track object");
            }
        }
        ids.Complete();
    }

    private static void ValidateArrangementGraph(MidoraProject project)
    {
        HashSet<MidoraId> definitionIds = project.EventInstruments.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> usageIds = project.EventInstrumentUsages.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> logicalTrackIds = project.Tracks.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> rootIds = project.MidiChannelRoots.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> midiTrackIds = project.PureMidiTracks.Select(value => value.Id).ToHashSet();
        if (definitionIds.Count != project.EventInstruments.Count
            || usageIds.Count != project.EventInstrumentUsages.Count
            || logicalTrackIds.Count != project.Tracks.Count
            || rootIds.Count != project.MidiChannelRoots.Count
            || midiTrackIds.Count != project.PureMidiTracks.Count)
        {
            throw new InvalidDataException(
                "The Arrangement graph contains duplicate repository identities.");
        }

        HashSet<(ArrangementTrackKind Kind, MidoraId TrackId)> arranged = [];
        foreach (ArrangementTrackReference reference in project.ArrangementTracks)
        {
            bool exists = reference.Kind switch
            {
                ArrangementTrackKind.LogicalTrack => logicalTrackIds.Contains(reference.TrackId),
                ArrangementTrackKind.PureMidiTrack => midiTrackIds.Contains(reference.TrackId),
                _ => false
            };
            if (!exists || !arranged.Add((reference.Kind, reference.TrackId)))
            {
                throw new InvalidDataException(
                    "The global Arrangement Track order contains a missing, duplicated, or kind-mismatched Track.");
            }
        }
        if (arranged.Count != project.Tracks.Count + project.PureMidiTracks.Count
            || project.Tracks.Any(value => !arranged.Contains((ArrangementTrackKind.LogicalTrack, value.Id)))
            || project.PureMidiTracks.Any(value => !arranged.Contains((ArrangementTrackKind.PureMidiTrack, value.Id))))
        {
            throw new InvalidDataException(
                "Every Logical and Pure MIDI Track must appear exactly once in the global Arrangement Track order.");
        }

        foreach (EventInstrumentUsage usage in project.EventInstrumentUsages)
        {
            if (!definitionIds.Contains(usage.EventInstrumentId))
            {
                throw new InvalidDataException(
                    "An Event Instrument Usage references a missing Definition.");
            }
            if (!project.Tracks.Any(value => value.EventInstrumentUsageId == usage.Id))
            {
                throw new InvalidDataException(
                    "An Event Instrument Usage cannot be persisted without a Logical Track member.");
            }
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            if (track.EventInstrumentUsageId is MidoraId usageId)
            {
                if (!usageIds.Contains(usageId))
                {
                    throw new InvalidDataException(
                        "A Logical Track references a missing Event Instrument Usage.");
                }
            }
            else if (track.Segments.Count != 0)
            {
                throw new InvalidDataException(
                    "An unbound Logical Track must be empty.");
            }
        }

        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            if (string.IsNullOrWhiteSpace(root.Name)
                || root.Name != root.Name.Trim()
                || !Enum.IsDefined(root.RoutingMode)
                || !Enum.IsDefined(root.ChannelMode)
                || root.FixedZeroBasedPort > 15
                || root.FixedZeroBasedChannel > 15)
            {
                throw new InvalidDataException(
                    "A MIDI Channel Root has an invalid name, route, mode, Port, or Channel.");
            }
            if (!project.PureMidiTracks.Any(value => value.MidiChannelRootId == root.Id))
            {
                throw new InvalidDataException(
                    "A MIDI Channel Root cannot be persisted without a Pure MIDI Track member.");
            }
        }
        if (project.PureMidiTracks.Any(value => !rootIds.Contains(value.MidiChannelRootId)))
        {
            throw new InvalidDataException(
                "A Pure MIDI Track references a missing MIDI Channel Root.");
        }
        if (project.MidiChannelRoots
            .Where(value => value.RoutingMode == MidiChannelRootRoutingMode.Fixed)
            .GroupBy(value => (value.FixedZeroBasedPort, value.FixedZeroBasedChannel))
            .Any(value => value.Count() > 1))
        {
            throw new InvalidDataException(
                "Fixed MIDI Channel Roots cannot own the same Port.Channel.");
        }

        Dictionary<MidoraId, int> positions = project.ArrangementTracks
            .Select((value, index) => (value.TrackId, index))
            .ToDictionary(value => value.TrackId, value => value.index);
        foreach (IGrouping<MidoraId, LogicalTrack> group in project.Tracks
            .Where(value => value.EventInstrumentUsageId.HasValue)
            .GroupBy(value => value.EventInstrumentUsageId!.Value))
        {
            RequireContiguous(group.Select(value => value.Id), positions,
                "Logical Tracks sharing one Event Instrument Usage must be contiguous.");
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots
            .Where(value => value.RoutingMode == MidiChannelRootRoutingMode.Auto))
        {
            RequireContiguous(
                project.PureMidiTracks
                    .Where(value => value.MidiChannelRootId == root.Id)
                    .Select(value => value.Id),
                positions,
                "Pure MIDI Tracks sharing one Auto Root must be contiguous.");
        }
    }

    private static void RequireContiguous(
        IEnumerable<MidoraId> trackIds,
        IReadOnlyDictionary<MidoraId, int> positions,
        string message)
    {
        int[] ordered = trackIds.Select(value => positions[value]).Order().ToArray();
        if (ordered.Length > 1 && ordered[^1] - ordered[0] + 1 != ordered.Length)
        {
            throw new InvalidDataException(message);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateContainer(ZipArchive archive, string targetPath)
    {
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        HashSet<string> insensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            bool isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            string validatedName = isDirectory ? name[..^1] : name;
            try
            {
                PersistenceValueValidationV1.ValidateRelativePath(validatedName, "Zip entry path");
            }
            catch (InvalidDataException exception)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains an invalid Zip entry path.",
                    targetPath,
                    name,
                    innerException: exception);
            }
            if (!insensitivePaths.Add(validatedName))
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains duplicate or case-conflicting Zip entry paths.",
                    targetPath,
                    name);
            }
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            int expectedUnixType = isDirectory ? 0x4000 : 0x8000;
            if (unixType != 0 && unixType != expectedUnixType)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains a non-regular Zip entry.",
                    targetPath,
                    name);
            }
            EnsureZipEntryCanBeOpened(entry, targetPath);
            if (!isDirectory)
            {
                entries.Add(name, entry);
            }
        }
        return entries;
    }

    private static void EnsureZipEntryCanBeOpened(ZipArchiveEntry entry, string targetPath)
    {
        try
        {
            using Stream probe = entry.Open();
            _ = probe.ReadByte();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The package contains an encrypted, corrupt, or unsupported Zip entry.",
                targetPath,
                entry.FullName,
                innerException: exception);
        }
    }

    private static ZipArchive OpenZipArchive(Stream stream, string targetPath)
    {
        try
        {
            ValidateZipEncryptionFlags(stream);
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The file is not a valid Zip container.",
                targetPath,
                innerException: exception);
        }
    }

    private static void ValidateZipEncryptionFlags(Stream stream)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const uint zip64EndOfCentralDirectorySignature = 0x06064b50;
        const uint zip64LocatorSignature = 0x07064b50;
        const uint centralDirectoryEntrySignature = 0x02014b50;
        const int endOfCentralDirectoryMinimumSize = 22;
        const int maximumCommentSize = ushort.MaxValue;

        long originalPosition = stream.Position;
        try
        {
            int tailLength = checked((int)Math.Min(
                stream.Length,
                endOfCentralDirectoryMinimumSize + maximumCommentSize));
            byte[] tail = new byte[tailLength];
            long tailOffset = stream.Length - tailLength;
            stream.Position = tailOffset;
            stream.ReadExactly(tail);

            int endIndex = -1;
            for (int index = tail.Length - endOfCentralDirectoryMinimumSize; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4))
                        == endOfCentralDirectorySignature
                    && index + endOfCentralDirectoryMinimumSize
                        + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2)) == tail.Length)
                {
                    endIndex = index;
                    break;
                }
            }
            if (endIndex < 0)
            {
                throw new InvalidDataException("The Zip end-of-central-directory record is missing.");
            }

            ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(endIndex + 10, 2));
            ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endIndex + 16, 4));
            if (entryCount == ushort.MaxValue || centralDirectoryOffset == uint.MaxValue)
            {
                long endRecordOffset = tailOffset + endIndex;
                if (endRecordOffset < 20)
                {
                    throw new InvalidDataException("The Zip64 locator is missing.");
                }
                Span<byte> locator = stackalloc byte[20];
                stream.Position = endRecordOffset - locator.Length;
                stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != zip64LocatorSignature)
                {
                    throw new InvalidDataException("The Zip64 locator is invalid.");
                }
                ulong zip64RecordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                if (zip64RecordOffset > long.MaxValue || zip64RecordOffset > (ulong)(stream.Length - 56))
                {
                    throw new InvalidDataException("The Zip64 end record offset is invalid.");
                }
                Span<byte> zip64Record = stackalloc byte[56];
                stream.Position = (long)zip64RecordOffset;
                stream.ReadExactly(zip64Record);
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Record) != zip64EndOfCentralDirectorySignature)
                {
                    throw new InvalidDataException("The Zip64 end record is invalid.");
                }
                entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record[32..]);
                centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record[48..]);
            }

            if (centralDirectoryOffset > long.MaxValue || centralDirectoryOffset > (ulong)stream.Length)
            {
                throw new InvalidDataException("The Zip central-directory offset is invalid.");
            }
            stream.Position = (long)centralDirectoryOffset;
            Span<byte> header = stackalloc byte[46];
            for (ulong index = 0; index < entryCount; index++)
            {
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralDirectoryEntrySignature)
                {
                    throw new InvalidDataException("The Zip central directory is invalid.");
                }
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
                if ((flags & 0x2041) != 0)
                {
                    throw new InvalidDataException("Encrypted Zip entries are not supported.");
                }
                int variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
                if (stream.Position > stream.Length - variableLength)
                {
                    throw new InvalidDataException("The Zip central-directory entry length is invalid.");
                }
                stream.Position += variableLength;
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static Dictionary<string, ManifestFileEntryJsonV1> ValidateManifestIndex(
        IReadOnlyList<ManifestFileEntryJsonV1> files,
        string targetPath)
    {
        Dictionary<string, ManifestFileEntryJsonV1> result = new(StringComparer.Ordinal);
        HashSet<string> insensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFileEntryJsonV1 item in files)
        {
            if (item.Path == MidoraPackagePathsV1.Manifest
                || !result.TryAdd(item.Path, item)
                || !insensitivePaths.Add(item.Path))
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Manifest,
                    "The manifest contains a self-entry, duplicate path, or case-conflicting path.",
                    targetPath,
                    item.Path);
            }
        }
        return result;
    }

    private static List<MidoraPackageDiagnosticV1> CollectExtraEntryDiagnostics(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index)
    {
        List<MidoraPackageDiagnosticV1> result = [];
        foreach (string entry in entries.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (entry != MidoraPackagePathsV1.Manifest && !index.ContainsKey(entry))
            {
                result.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Information,
                    MidoraPackageDiagnosticCategoryV1.FileFormat,
                    "MIDORA-PERSIST-INFO-UNINDEXED",
                    "The unindexed package entry is not Project content and will not be preserved on save.",
                    entry));
            }
        }
        foreach (ManifestFileEntryJsonV1 item in index.Values.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            if (!IsKnownKind(item.Kind))
            {
                result.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Information,
                    MidoraPackageDiagnosticCategoryV1.FileFormat,
                    "MIDORA-PERSIST-INFO-UNKNOWN-KIND",
                    "The manifest entry uses an unknown file kind and is not Project content.",
                    item.Path));
            }
        }
        return result;
    }

    private static IEnumerable<MidoraPackageDiagnosticV1> CollectOrphanObjectDiagnostics(
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        ProjectJsonV1 projectIndex)
    {
        HashSet<string> referencedPaths = projectIndex.EventInstruments
            .Select(value => value.Path)
            .Concat(projectIndex.EventInstrumentUsages.Select(value => value.Path))
            .Concat(projectIndex.MidiChannelRoots.Select(value => value.Path))
            .Concat(projectIndex.ArrangementTracks.Select(value => value.Path))
            .Concat(projectIndex.ArrangementTracks
                .Where(value => value.Kind == "pure-midi-track")
                .Select(value => MidoraPackagePathsV1.PureMidiContentPack(
                    ParseId(value.Id, "project.json Pure MIDI Track ID"))))
            .ToHashSet(StringComparer.Ordinal);
        foreach (ManifestFileEntryJsonV1 item in manifestIndex.Values
            .Where(value => value.Kind is "event-instrument-pb" or "event-instrument-usage-pb"
                or "logical-track-pb"
                or "midi-channel-root-pb" or "pure-midi-track-pb" or "pure-midi-content-pack")
            .Where(value => !referencedPaths.Contains(value.Path))
            .OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            yield return new(
                MidoraPackageDiagnosticSeverityV1.Information,
                MidoraPackageDiagnosticCategoryV1.FileFormat,
                "MIDORA-PERSIST-INFO-ORPHAN-OBJECT",
                "The manifest object/content entry is not referenced by project.json and will not be preserved on save.",
                item.Path);
        }
    }

    private static async Task RestoreProjectObjectsAsync(
        MidoraProject project,
        ProjectJsonV1 projectIndex,
        int fileFormatVersion,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        string targetPath,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        PureMidiContentPackExtractionV1 pureMidiContent,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < projectIndex.EventInstruments.Length; index++)
        {
            ProjectObjectIndexJsonV1 item = projectIndex.EventInstruments[index];
            MidoraId expectedId = ParseId(item.Id, "project.json Event Instrument ID");
            ObjectPayloadV1 payload = await ReadObjectPayloadAsync(
                item.Path,
                "event-instrument-pb",
                fileFormatVersion == PersistenceContractV1.FileFormatVersion
                    ? PersistenceContractV1.SchemaVersion
                    : PersistenceContractV2.EventInstrumentSchemaVersion,
                entries,
                manifestIndex,
                targetPath,
                cancellationToken,
                streamPayload: true).ConfigureAwait(false);
            if (payload.Entry is null)
            {
                AddDamagedObject(
                    project.DamagedEventInstruments,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    payload.Error!,
                    diagnostics);
                continue;
            }
            try
            {
                using Stream input = payload.Entry.Open();
                EventInstrument instrument = fileFormatVersion switch
                {
                    PersistenceContractV1.FileFormatVersion =>
                        RestoreFormat1EventInstrument(project, input, cancellationToken),
                    PersistenceContractV2.FileFormatVersion =>
                        EventInstrumentProtobufCodecV2.Restore(project, input, cancellationToken),
                    PersistenceContractV3.FileFormatVersion or PersistenceContractV4.FileFormatVersion =>
                        EventInstrumentProtobufCodecV2.Restore(project, input, cancellationToken),
                    _ => throw new InvalidDataException(
                        $"Unsupported Event Instrument file-format version {fileFormatVersion}.")
                };
                if (instrument.Id != expectedId)
                {
                    throw new InvalidDataException(
                        "The Event Instrument identity does not match project.json.");
                }
                project.EventInstruments.Add(instrument);
            }
            catch (ProtobufObjectHeaderExceptionV1 exception)
            {
                throw StructureFailure(targetPath, item.Path, exception.Message, exception);
            }
            catch (InvalidDataException exception)
            {
                AddDamagedObject(
                    project.DamagedEventInstruments,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    exception.Message,
                    diagnostics);
            }
        }

        for (int index = 0; index < projectIndex.EventInstrumentUsages.Length; index++)
        {
            EventInstrumentUsageIndexJsonV1 item = projectIndex.EventInstrumentUsages[index];
            MidoraId expectedId = ParseId(item.Id, "project.json Event Instrument Usage ID");
            MidoraId expectedInstrumentId = ParseId(
                item.EventInstrumentId,
                "project.json Event Instrument Usage Definition ID");
            ObjectPayloadV1 payload = await ReadObjectPayloadAsync(
                item.Path,
                "event-instrument-usage-pb",
                PersistenceContractV2.ReusedComponentSchemaVersion,
                entries,
                manifestIndex,
                targetPath,
                cancellationToken).ConfigureAwait(false);
            if (payload.Bytes is null)
            {
                AddDamagedObject(
                    project.DamagedEventInstrumentUsages,
                    expectedId,
                    $"Usage {expectedId}",
                    item.Path,
                    index,
                    payload.Error!,
                    diagnostics,
                    parentId: expectedInstrumentId);
                continue;
            }
            try
            {
                EventInstrumentUsage usage = EventInstrumentUsageProtobufCodecV1.Restore(
                    project,
                    payload.Bytes);
                if (usage.Id != expectedId || usage.EventInstrumentId != expectedInstrumentId)
                {
                    throw new InvalidDataException(
                        "The Event Instrument Usage identity or Definition does not match project.json.");
                }
                project.EventInstrumentUsages.Add(usage);
            }
            catch (ProtobufObjectHeaderExceptionV1 exception)
            {
                throw StructureFailure(targetPath, item.Path, exception.Message, exception);
            }
            catch (InvalidDataException exception)
            {
                AddDamagedObject(
                    project.DamagedEventInstrumentUsages,
                    expectedId,
                    $"Usage {expectedId}",
                    item.Path,
                    index,
                    exception.Message,
                    diagnostics,
                    parentId: expectedInstrumentId);
            }
        }

        for (int index = 0; index < projectIndex.MidiChannelRoots.Length; index++)
        {
            ProjectObjectIndexJsonV1 item = projectIndex.MidiChannelRoots[index];
            MidoraId expectedId = ParseId(item.Id, "project.json MIDI Channel Root ID");
            ObjectPayloadV1 payload = await ReadObjectPayloadAsync(
                item.Path,
                "midi-channel-root-pb",
                PersistenceContractV2.ReusedComponentSchemaVersion,
                entries,
                manifestIndex,
                targetPath,
                cancellationToken).ConfigureAwait(false);
            if (payload.Bytes is null)
            {
                AddDamagedObject(
                    project.DamagedMidiChannelRoots,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    payload.Error!,
                    diagnostics);
                continue;
            }
            try
            {
                MidiChannelRoot root = MidiChannelRootProtobufCodecV1.Restore(
                    project,
                    payload.Bytes);
                if (root.Id != expectedId)
                {
                    throw new InvalidDataException(
                        "The MIDI Channel Root identity does not match project.json.");
                }
                project.MidiChannelRoots.Add(root);
            }
            catch (ProtobufObjectHeaderExceptionV1 exception)
            {
                throw StructureFailure(targetPath, item.Path, exception.Message, exception);
            }
            catch (InvalidDataException exception)
            {
                AddDamagedObject(
                    project.DamagedMidiChannelRoots,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    exception.Message,
                    diagnostics);
            }
        }

        for (int index = 0; index < projectIndex.ArrangementTracks.Length; index++)
        {
            ArrangementTrackIndexJsonV1 item = projectIndex.ArrangementTracks[index];
            MidoraId expectedId = ParseId(item.Id, "project.json Arrangement Track ID");
            MidoraId? expectedGroupId = item.SharedGroupId is StableIdJsonV1 groupId
                ? ParseId(groupId, "project.json Arrangement Track shared group ID")
                : null;
            bool logical = item.Kind == "logical-track";
            project.ArrangementTracks.Add(new(
                logical ? ArrangementTrackKind.LogicalTrack : ArrangementTrackKind.PureMidiTrack,
                expectedId));
            ObjectPayloadV1 payload = await ReadObjectPayloadAsync(
                item.Path,
                logical ? "logical-track-pb" : "pure-midi-track-pb",
                PersistenceContractV2.ReusedComponentSchemaVersion,
                entries,
                manifestIndex,
                targetPath,
                cancellationToken,
                streamPayload: logical).ConfigureAwait(false);
            if (payload.Bytes is null && payload.Entry is null)
            {
                AddDamagedObject(
                    logical ? project.DamagedLogicalTracks : project.DamagedPureMidiTracks,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    payload.Error!,
                    diagnostics,
                    parentId: expectedGroupId);
                continue;
            }
            try
            {
                if (logical)
                {
                    using Stream input = payload.Entry!.Open();
                    LogicalTrack track = LogicalTrackProtobufCodecV1.Restore(project, input, cancellationToken);
                    if (track.Id != expectedId)
                    {
                        throw new InvalidDataException(
                            "The Logical Track identity does not match project.json.");
                    }
                    if (track.EventInstrumentUsageId != expectedGroupId)
                    {
                        throw new InvalidDataException(
                            "The Logical Track shared group does not match project.json.");
                    }
                    project.Tracks.Add(track);
                }
                else
                {
                    RestoredPureMidiTrackV1 restored = PureMidiTrackProtobufCodecV1.Restore(
                        project,
                        payload.Bytes);
                    PureMidiTrack track = restored.Track;
                    if (track.Id != expectedId)
                    {
                        throw new InvalidDataException(
                            "The Pure MIDI Track identity does not match project.json.");
                    }
                    if (expectedGroupId is not MidoraId expectedRootId
                        || track.MidiChannelRootId != expectedRootId)
                    {
                        throw new InvalidDataException(
                            "The Pure MIDI Track shared group does not match project.json.");
                    }
                    await pureMidiContent.AttachAsync(
                        track,
                        restored.ContentPackPath,
                        entries,
                        manifestIndex,
                        cancellationToken).ConfigureAwait(false);
                    project.PureMidiTracks.Add(track);
                }
            }
            catch (ProtobufObjectHeaderExceptionV1 exception)
            {
                throw StructureFailure(targetPath, item.Path, exception.Message, exception);
            }
            catch (InvalidDataException exception)
            {
                AddDamagedObject(
                    logical ? project.DamagedLogicalTracks : project.DamagedPureMidiTracks,
                    expectedId,
                    item.NameSnapshot,
                    item.Path,
                    index,
                    exception.Message,
                    diagnostics,
                    parentId: expectedGroupId);
            }
        }
    }

    private static async Task<ObjectPayloadV1> ReadObjectPayloadAsync(
        string packagePath,
        string expectedKind,
        int expectedSchemaVersion,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        string targetPath,
        CancellationToken cancellationToken,
        bool streamPayload = false)
    {
        if (!manifestIndex.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry))
        {
            throw StructureFailure(
                targetPath,
                packagePath,
                "project.json references an object that is absent from manifest.json.");
        }
        if (manifestEntry.Kind != expectedKind
            || manifestEntry.SchemaVersion != expectedSchemaVersion)
        {
            throw StructureFailure(
                targetPath,
                packagePath,
                "Object file kind or schemaVersion is inconsistent with project.json.");
        }
        if (!entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry))
        {
            return new(null, "The object is indexed by project.json and manifest.json but its Zip entry is missing.");
        }
        byte[]? bytes = streamPayload
            ? null : await ReadEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        string actualHash = bytes is not null
            ? Convert.ToHexStringLower(SHA256.HashData(bytes))
            : await HashEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifestEntry.Sha256, StringComparison.Ordinal))
        {
            return new(null, "The object SHA-256 does not match manifest.json.");
        }
        return new(bytes, null, streamPayload ? archiveEntry : null);
    }

    private static void AddDamagedObject(
        ICollection<DamagedProjectObject> target,
        MidoraId id,
        string nameSnapshot,
        string packagePath,
        int originalIndex,
        string error,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraId? parentId = null,
        IReadOnlyList<MidoraId>? childIds = null)
    {
        target.Add(new(
            id,
            nameSnapshot,
            packagePath,
            error,
            originalIndex,
            parentId,
            childIds is null ? null : Array.AsReadOnly(childIds.ToArray())));
        diagnostics.Add(new(
            MidoraPackageDiagnosticSeverityV1.Error,
            MidoraPackageDiagnosticCategoryV1.FileDamage,
            "MIDORA-PERSIST-DAMAGED-OBJECT",
            $"The object could not be loaded and is represented by a damaged placeholder. {error}",
            packagePath));
    }

    private sealed record ObjectPayloadV1(byte[]? Bytes, string? Error, ZipArchiveEntry? Entry = null);

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using Stream input = entry.Open();
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<ZipArchiveEntry?> TryReadValidatedEntryAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!index.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry)
            || !entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry)) return null;
        if (manifestEntry.Kind != expectedKind
            || manifestEntry.SchemaVersion != PersistenceContractV1.SchemaVersion)
            throw StructureFailure(targetPath, packagePath, "Package file kind or schemaVersion is inconsistent.");
        string actualHash = await HashEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifestEntry.Sha256, StringComparison.Ordinal))
            throw new MidoraPackageExceptionV1(MidoraPackageStageV1.HashValidation,
                "Package file SHA-256 does not match manifest.json.", targetPath, packagePath);
        return archiveEntry;
    }

    private static async Task<byte[]> ReadRequiredValidatedAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        CancellationToken cancellationToken)
    {
        byte[]? result = await TryReadValidatedAsync(
            packagePath, expectedKind, entries, index, targetPath, cancellationToken).ConfigureAwait(false);
        return result ?? throw StructureFailure(targetPath, packagePath, $"Required package file '{packagePath}' is missing.");
    }

    private static async Task<byte[]?> TryReadValidatedAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        CancellationToken cancellationToken,
        int expectedSchemaVersion = PersistenceContractV1.SchemaVersion)
    {
        if (!index.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry)
            || !entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry))
        {
            return null;
        }
        if (manifestEntry.Kind != expectedKind
            || manifestEntry.SchemaVersion != expectedSchemaVersion)
        {
            throw StructureFailure(targetPath, packagePath, "Package file kind or schemaVersion is inconsistent.");
        }
        byte[] bytes = await ReadEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, manifestEntry.Sha256, StringComparison.Ordinal))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.HashValidation,
                "Package file SHA-256 does not match manifest.json.",
                targetPath,
                packagePath);
        }
        return bytes;
    }

    private static async Task<T?> RestoreOrdinarySettingsAsync<T>(
        string packagePath,
        Func<ReadOnlySpan<byte>, T> parse,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        CancellationToken cancellationToken,
        Action markModified)
    {
        try
        {
            byte[]? bytes = await TryReadValidatedAsync(
                packagePath, "settings-json", entries, index, targetPath, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null) throw new InvalidDataException($"{packagePath} is missing.");
            return parse(bytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException
            or MidoraPackageExceptionV1
        {
            Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
        })
        {
            AddRecoveryDiagnostic(diagnostics, packagePath, exception.Message);
            markModified();
            return default;
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using Stream input = entry.Open();
        using MemoryStream output = entry.Length <= int.MaxValue
            ? new MemoryStream((int)entry.Length)
            : new MemoryStream();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task WriteZipAsync(
        string contentRoot,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using FileStream output = new(
            packagePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);
        string[] contentPaths = Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(contentRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        foreach (string entryName in GetStableEntryOrder(contentPaths))
        {
            CompressionLevel compressionLevel =
                entryName.StartsWith("midi-content/", StringComparison.Ordinal)
                && entryName.EndsWith(".mpk", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
            ZipArchiveEntry entry = archive.CreateEntry(entryName, compressionLevel);
            entry.LastWriteTime = CanonicalZipTimestamp;
            await using Stream target = entry.Open();
            string sourcePath = Path.Combine(contentRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<FileStream> OpenAndVerifyLegacySourceAsync(
        MidoraLegacyProjectSourceIdentityV3 identity,
        CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                identity.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != identity.Length
                || File.GetLastWriteTimeUtc(identity.SourcePath) != identity.LastWriteTimeUtc)
            {
                throw new InvalidDataException(
                    "The legacy source changed after it was opened.");
            }
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                Convert.ToHexStringLower(digest),
                identity.Sha256,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The legacy source bytes changed after it was opened.");
            }
            stream.Position = 0;
            FileStream result = stream;
            stream = null;
            return result;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The legacy source could not be locked and verified for an in-place upgrade.",
                targetPath: identity.SourcePath,
                innerException: exception);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static async Task<string> CreateOrReusePermanentLegacyBackupAsync(
        FileStream source,
        MidoraLegacyProjectSourceIdentityV3 identity,
        string plannedBackupPath,
        CancellationToken cancellationToken)
    {
        string candidate = NormalizeFilePath(plannedBackupPath, nameof(plannedBackupPath));
        string directory = Path.GetDirectoryName(identity.SourcePath)!;
        if (!string.Equals(
            Path.GetDirectoryName(candidate),
            directory,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Backup,
                "The confirmed permanent legacy backup path is not beside the source Project.",
                targetPath: identity.SourcePath,
                backupPath: candidate);
        }

        string currentlyAvailable = await PlanPermanentLegacyBackupPathAsync(
            identity,
            cancellationToken).ConfigureAwait(false);
        if (!PathsEqual(currentlyAvailable, candidate))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Backup,
                "The confirmed permanent legacy backup path is no longer available. Confirm the upgrade again.",
                targetPath: identity.SourcePath,
                backupPath: candidate);
        }

        if (File.Exists(candidate))
        {
            FileInfo existing = new(candidate);
            if (existing.Length == identity.Length
                && await FileHasSha256Async(candidate, identity.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                return candidate;
            }
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Backup,
                "The confirmed permanent legacy backup path was occupied by different bytes.",
                targetPath: identity.SourcePath,
                backupPath: candidate);
        }

        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(candidate)}.{Guid.NewGuid():N}.tmp");
        try
        {
            source.Position = 0;
            await using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(output, 128 * 1024, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length != identity.Length
                || !await FileHasSha256Async(temporary, identity.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The permanent legacy backup failed exact-byte verification.");
            }
            File.Move(temporary, candidate, overwrite: false);
            return candidate;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Backup,
                "The permanent exact-byte legacy backup could not be created at the confirmed path.",
                targetPath: identity.SourcePath,
                backupPath: candidate,
                temporaryPath: temporary,
                innerException: exception);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                // Best effort: the permanent backup or primary failure remains authoritative.
            }
        }
    }

    private static async Task<string> PlanPermanentLegacyBackupPathAsync(
        MidoraLegacyProjectSourceIdentityV3 identity,
        CancellationToken cancellationToken)
    {
        ValidateLegacySourceIdentity(identity);
        string directory = Path.GetDirectoryName(identity.SourcePath)!;
        string sourceStem = Path.GetFileNameWithoutExtension(identity.SourcePath)
            .Normalize(NormalizationForm.FormC);
        for (int suffix = 1; suffix < int.MaxValue; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = Path.Combine(
                directory,
                BuildLegacyBackupFileName(
                    sourceStem,
                    identity.SourceFileFormatVersion,
                    suffix));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
            FileInfo existing = new(candidate);
            if (existing.Length == identity.Length
                && await FileHasSha256Async(candidate, identity.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                return candidate;
            }
        }
        throw new MidoraPackageExceptionV1(
            MidoraPackageStageV1.Backup,
            "No collision-free permanent legacy backup name is available.",
            targetPath: identity.SourcePath);
    }

    private static string BuildLegacyBackupFileName(
        string sourceStem,
        int sourceFormat,
        int suffix)
    {
        string collisionSuffix = suffix == 1
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" ({suffix})");
        string fixedTail = string.Create(
            CultureInfo.InvariantCulture,
            $" - Original Format {sourceFormat} before Format {PersistenceContractV4.FileFormatVersion}{collisionSuffix}.midora");
        int prefixBudget = 255 - fixedTail.Length;
        if (prefixBudget <= 0)
        {
            throw new InvalidDataException(
                "The permanent legacy backup suffix exceeds the Windows filename budget.");
        }
        string prefix = TruncateByTextElement(sourceStem, prefixBudget).TrimEnd(' ', '.');
        if (prefix.Length == 0)
        {
            prefix = "Project";
        }
        return prefix + fixedTail;
    }

    private static string TruncateByTextElement(string value, int maximumCodeUnits)
    {
        if (value.Length <= maximumCodeUnits)
        {
            return value;
        }
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(value);
        int length = 0;
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (length + element.Length > maximumCodeUnits)
            {
                break;
            }
            length += element.Length;
        }
        return value[..length];
    }

    private static void ValidateLegacySourceIdentity(
        MidoraLegacyProjectSourceIdentityV3 identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Path.IsPathFullyQualified(identity.SourcePath)
            || identity.SourceFileFormatVersion is < PersistenceContractV1.FileFormatVersion
                or >= PersistenceContractV4.FileFormatVersion
            || identity.Length < 0
            || identity.Sha256.Length != 64
            || identity.Sha256.Any(value => value is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The legacy source identity is invalid.",
                nameof(identity));
        }
    }

    private static void ValidateLegacyUpgradePlan(MidoraLegacyProjectUpgradePlanV3 plan)
    {
        ValidateLegacySourceIdentity(plan.SourceIdentity);
        if (plan.TargetFileFormatVersion != PersistenceContractV4.FileFormatVersion
            || !Path.IsPathFullyQualified(plan.PermanentBackupPath))
        {
            throw new ArgumentException(
                "The legacy Project upgrade plan is invalid.",
                nameof(plan));
        }
    }

    private static async Task<bool> FileHasSha256Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(digest),
            expected,
            StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetStableEntryOrder(IEnumerable<string> paths)
    {
        HashSet<string> available = paths.ToHashSet(StringComparer.Ordinal);
        string[] fixedOrder =
        [
            MidoraPackagePathsV1.Manifest,
            MidoraPackagePathsV1.Project,
            MidoraPackagePathsV1.Metadata,
            MidoraPackagePathsV1.ConductorTrack,
            MidoraPackagePathsV1.ProjectSettings,
            MidoraPackagePathsV1.GlobalResetDefaults,
            MidoraPackagePathsV1.GlobalEventScopeDefaults,
            MidoraPackagePathsV1.ProjectPresentation,
            PersistenceContractV4.InstrumentChangesPath
        ];
        foreach (string path in fixedOrder)
        {
            if (available.Remove(path)) yield return path;
        }
        foreach (string path in available.OrderBy(value => value, StringComparer.Ordinal)) yield return path;
    }

    private static string GetExpectedKind(string path) => path switch
    {
        PersistenceContractV4.InstrumentChangesPath => "instrument-changes-pb",
        MidoraPackagePathsV1.ProjectPresentation => "project-presentation-json",
        MidoraPackagePathsV1.Project or MidoraPackagePathsV1.Metadata => "core-json",
        MidoraPackagePathsV1.ConductorTrack => "conductor-json",
        _ when path.StartsWith("settings/", StringComparison.Ordinal) => "settings-json",
        _ when path.StartsWith("event-instruments/", StringComparison.Ordinal) => "event-instrument-pb",
        _ when path.StartsWith("event-instrument-usages/", StringComparison.Ordinal) =>
            "event-instrument-usage-pb",
        _ when path.StartsWith("logical-tracks/", StringComparison.Ordinal) => "logical-track-pb",
        _ when path.StartsWith("midi-channel-roots/", StringComparison.Ordinal) => "midi-channel-root-pb",
        _ when path.StartsWith("midi-tracks/", StringComparison.Ordinal) => "pure-midi-track-pb",
        _ when path.StartsWith("midi-content/", StringComparison.Ordinal) => "pure-midi-content-pack",
        _ => throw new InvalidDataException($"No manifest kind is defined for '{path}'.")
    };

    private static int GetCurrentSchemaVersion(string path) => path switch
    {
        MidoraPackagePathsV1.ProjectPresentation =>
            PersistenceContractV4.ProjectPresentationSchemaVersion,
        _ when path.StartsWith("event-instruments/", StringComparison.Ordinal) =>
            PersistenceContractV3.EventInstrumentSchemaVersion,
        _ => PersistenceContractV3.ReusedComponentSchemaVersion
    };

    private static EventInstrument RestoreFormat1EventInstrument(
        MidoraProject project,
        Stream input,
        CancellationToken cancellationToken)
    {
        EventInstrument instrument = EventInstrumentProtobufCodecV1.Restore(project, input, cancellationToken);
        instrument.PreRollTicks = 0;
        return instrument;
    }

    private static bool IsKnownKind(string kind) => kind is
        "core-json" or "settings-json" or "conductor-json" or "instrument-changes-pb" or
        "project-presentation-json" or
        "event-instrument-pb" or "event-instrument-usage-pb" or "logical-track-pb" or
        "midi-channel-root-pb" or "pure-midi-track-pb" or "pure-midi-content-pack";

    private static void ValidateLoadedStableIds(
        ProjectJsonV1 projectIndex,
        MidoraProject project,
        ConductorTrack? conductor,
        long nextStableId,
        CancellationToken cancellationToken)
    {
        using StableIdValidatorV1 ids = new(cancellationToken);
        if (conductor is not null)
        {
            foreach (MidoraId id in EnumerateConductorIds(conductor))
            {
                AddId(id, nextStableId, ids, "Conductor event");
            }
        }
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments.ToDictionary(value => value.Id);
        Dictionary<MidoraId, EventInstrumentUsage> usages = project.EventInstrumentUsages
            .ToDictionary(value => value.Id);
        Dictionary<MidoraId, LogicalTrack> tracks = project.Tracks.ToDictionary(value => value.Id);
        Dictionary<MidoraId, MidiChannelRoot> roots = project.MidiChannelRoots.ToDictionary(value => value.Id);
        Dictionary<MidoraId, PureMidiTrack> midiTracks = project.PureMidiTracks.ToDictionary(value => value.Id);
        HashSet<MidoraId> damagedInstrumentIds = project.DamagedEventInstruments.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedUsageIds = project.DamagedEventInstrumentUsages
            .Select(value => value.Id)
            .ToHashSet();
        HashSet<MidoraId> damagedTrackIds = project.DamagedLogicalTracks.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedRootIds = project.DamagedMidiChannelRoots.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedMidiTrackIds = project.DamagedPureMidiTracks.Select(value => value.Id).ToHashSet();

        foreach (ProjectObjectIndexJsonV1 item in projectIndex.EventInstruments)
        {
            MidoraId id = ParseId(item.Id, "project.json Event Instrument ID");
            AddId(id, nextStableId, ids, "Event Instrument");
            if (instruments.TryGetValue(id, out EventInstrument? instrument))
            {
                foreach (MidoraId nestedId in EnumerateEventInstrumentIds(instrument).Skip(1))
                {
                    AddId(nestedId, nextStableId, ids, "Event Instrument nested object");
                }
            }
            else if (!damagedInstrumentIds.Contains(id))
            {
                throw new InvalidDataException(
                    "An indexed Event Instrument was neither loaded nor isolated as damaged.");
            }
        }

        foreach (EventInstrumentUsageIndexJsonV1 item in projectIndex.EventInstrumentUsages)
        {
            MidoraId id = ParseId(item.Id, "project.json Event Instrument Usage ID");
            AddId(id, nextStableId, ids, "Event Instrument Usage");
            if (!usages.ContainsKey(id) && !damagedUsageIds.Contains(id))
            {
                throw new InvalidDataException(
                    "An indexed Event Instrument Usage was neither loaded nor isolated as damaged.");
            }
        }

        foreach (ProjectObjectIndexJsonV1 item in projectIndex.MidiChannelRoots)
        {
            MidoraId id = ParseId(item.Id, "project.json MIDI Channel Root ID");
            AddId(id, nextStableId, ids, "MIDI Channel Root");
            if (!roots.ContainsKey(id) && !damagedRootIds.Contains(id))
            {
                throw new InvalidDataException(
                    "An indexed MIDI Channel Root was neither loaded nor isolated as damaged.");
            }
        }

        foreach (ArrangementTrackIndexJsonV1 item in projectIndex.ArrangementTracks)
        {
            MidoraId id = ParseId(item.Id, "project.json Arrangement Track ID");
            AddId(id, nextStableId, ids, "Arrangement Track");
            if (item.Kind == "logical-track")
            {
                if (tracks.TryGetValue(id, out LogicalTrack? track))
                {
                    foreach (MidoraId nestedId in EnumerateLogicalTrackIds(track).Skip(1))
                    {
                        AddId(nestedId, nextStableId, ids, "Logical Track nested object");
                    }
                }
                else if (!damagedTrackIds.Contains(id))
                {
                    throw new InvalidDataException(
                        "An indexed Logical Track was neither loaded nor isolated as damaged.");
                }
            }
            else if (item.Kind == "pure-midi-track")
            {
                if (midiTracks.TryGetValue(id, out PureMidiTrack? midiTrack))
                {
                    foreach (MidoraId nestedId in EnumeratePureMidiTrackIds(midiTrack, cancellationToken).Skip(1))
                    {
                        AddId(nestedId, nextStableId, ids, "Pure MIDI Track nested object");
                    }
                }
                else if (!damagedMidiTrackIds.Contains(id))
                {
                    throw new InvalidDataException(
                        "An indexed Pure MIDI Track was neither loaded nor isolated as damaged.");
                }
            }
        }
        ids.Complete();
    }

    private static IEnumerable<MidoraId> EnumerateConductorIds(ConductorTrack conductor)
    {
        foreach (TempoChange item in conductor.Tempos) yield return item.Id;
        foreach (TimeSignatureChange item in conductor.TimeSignatures) yield return item.Id;
        foreach (KeySignatureChange item in conductor.KeySignatures) yield return item.Id;
        foreach (ProjectMarker item in conductor.Markers) yield return item.Id;
        if (conductor.EndMarker is not null) yield return conductor.EndMarker.Id;
    }

    private static IEnumerable<MidoraId> EnumerateEventInstrumentIds(EventInstrument instrument)
    {
        yield return instrument.Id;
        foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
        {
            yield return parameter.Id;
            foreach (LogicalParameterEnumItem item in parameter.EnumItems) yield return item.Id;
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            yield return voice.Id;
            foreach (var change in voice.InstrumentChanges.Values) yield return change.Id;
            foreach (SubVoiceEventMapping mapping in voice.EventMappings)
            {
                foreach (MidoraId id in EnumerateMappingChainIds(mapping.Steps)) yield return id;
            }
            foreach (TemplateEventSnapshotValue templateEvent in voice.Events.CreateQuerySnapshot().EnumerateAll())
            {
                yield return templateEvent.Id;
            }
            foreach (ValueCurve curve in voice.Curves)
            {
                yield return curve.Id;
                foreach (CurvePointSnapshotValue point in curve.Points.CreateQuerySnapshot().EnumerateAll()) yield return point.Id;
            }
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes) yield return envelope.Id;
        foreach (CSharpMappingFunction function in instrument.MappingFunctions) yield return function.Id;
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            yield return mapping.Id;
            foreach (MidoraId id in EnumerateMappingChainIds(mapping.Steps)) yield return id;
        }
    }

    private static IEnumerable<MidoraId> EnumerateMappingChainIds(MappingChain chain)
    {
        yield return chain.Id;
        foreach (ValueMappingStep step in chain) yield return step.Id;
    }

    private static IEnumerable<MidoraId> EnumerateLogicalTrackIds(LogicalTrack track)
    {
        yield return track.Id;
        foreach (Segment segment in track.Segments)
        {
            yield return segment.Id;
            foreach (LogicalNoteSnapshotValue note in segment.Notes.CreateQuerySnapshot().EnumerateAll()) yield return note.Id;
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                yield return lane.Id;
                foreach (CurvePointSnapshotValue point in lane.Points.CreateQuerySnapshot().EnumerateAll()) yield return point.Id;
            }
        }
    }

    private static IEnumerable<MidoraId> EnumeratePureMidiTrackIds(
        PureMidiTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return track.Id;
        foreach (MidiSegment segment in track.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return segment.Id;
            foreach (DirectMidiNoteValue note in segment.Notes.EnumerateValues(cancellationToken)) yield return note.Id;
            foreach (DirectMidiChannelEventValue directEvent in segment.ChannelEvents.EnumerateValues(cancellationToken))
            {
                yield return directEvent.Id;
            }
            foreach (OpaqueMidiEventValue opaque in segment.OpaqueEvents.EnumerateValues(cancellationToken)) yield return opaque.Id;
            foreach (var change in segment.InstrumentChanges.Values) yield return change.Id;
        }
    }

    private static void AddId(MidoraId id, long nextStableId, StableIdValidatorV1 ids, string source) =>
        ids.Add(id, nextStableId, source);

    private static MidoraId ParseId(StableIdJsonV1? value, string fieldName)
    {
        if (value is not StableIdJsonV1 id || id.Value <= 0)
        {
            throw new InvalidDataException($"{fieldName} is not canonical.");
        }
        return id.ToDomain();
    }

    private static void RequireEqualContent(
        IReadOnlyDictionary<string, StructuralFileV1> expected,
        IReadOnlyDictionary<string, StructuralFileV1> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException("Reopened package content count changed.");
        }
        foreach ((string path, StructuralFileV1 expectedFile) in expected)
        {
            if (!actual.TryGetValue(path, out StructuralFileV1? actualFile)
                || expectedFile != actualFile)
            {
                throw new InvalidDataException($"Reopened package content changed at '{path}'.");
            }
        }
    }

    private static void AddRecoveryDiagnostic(
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        string packagePath,
        string detail) => diagnostics.Add(new(
            MidoraPackageDiagnosticSeverityV1.Error,
            MidoraPackageDiagnosticCategoryV1.FileDamage,
            "MIDORA-PERSIST-RECOVERED-DEFAULT",
            $"The file was missing or invalid and the current default was used. {detail}",
            packagePath));

    private static MidoraPackageExceptionV1 StructureFailure(
        string targetPath,
        string packagePath,
        string message,
        Exception? exception = null) => new(
            MidoraPackageStageV1.Structure,
            message,
            targetPath,
            packagePath,
            innerException: exception);

    private static string NormalizeFilePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new ArgumentException("Path is not a valid file-system path.", parameterName, exception);
        }
    }

    private void TryDeleteFile(
        string? path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraPackageFaultPointV1? faultPoint = null)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            if (faultPoint.HasValue)
            {
                _faultInjector.ThrowIfRequested(faultPoint.Value, path);
            }
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CleanupWarning(path, exception.Message));
        }
    }

    private void TryDeleteDirectory(
        string path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraPackageFaultPointV1? faultPoint = null)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if (faultPoint.HasValue)
            {
                _faultInjector.ThrowIfRequested(faultPoint.Value, path);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CleanupWarning(path, exception.Message));
        }
    }

    private static MidoraPackageDiagnosticV1 CleanupWarning(string path, string detail) => new(
        MidoraPackageDiagnosticSeverityV1.Warning,
        MidoraPackageDiagnosticCategoryV1.SaveTransaction,
        "MIDORA-PERSIST-CLEANUP-FAILED",
        $"The save completed but a transaction artifact could not be removed. {detail}",
        path);

    private sealed record PackageContentV1(
        Dictionary<string, StructuralFileV1> Files,
        IReadOnlyList<ManifestFileEntryJsonV1> PureMidiPackEntries);

    private sealed record StructuralFileV1(long Length, string Sha256);

    private sealed record PackageManifestView(
        int FileFormatVersion,
        string CreatedWithSoftwareVersion,
        string LastSavedWithSoftwareVersion,
        IReadOnlyList<ManifestFileEntryJsonV1> Files)
    {
        public static PackageManifestView FromV1(ManifestJsonV1 value) => new(
            value.FileFormatVersion,
            value.CreatedWithSoftwareVersion,
            value.LastSavedWithSoftwareVersion,
            value.Files);

        public static PackageManifestView FromV2(ManifestJsonV2 value) => new(
            value.FileFormatVersion,
            value.CreatedWithSoftwareVersion,
            value.LastSavedWithSoftwareVersion,
            value.Files);

        public static PackageManifestView FromV3(ManifestJsonV3 value) => new(
            value.FileFormatVersion,
            value.CreatedWithSoftwareVersion,
            value.LastSavedWithSoftwareVersion,
            value.Files);

        public static PackageManifestView FromV4(ManifestJsonV4 value) => new(
            value.FileFormatVersion, value.CreatedWithSoftwareVersion, value.LastSavedWithSoftwareVersion, value.Files);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
