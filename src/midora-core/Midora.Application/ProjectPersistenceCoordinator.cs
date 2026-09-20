using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

public enum ProjectPersistenceUnavailability
{
    DamagedProjectObjects
}

public sealed class ProjectPersistenceUnavailableException : InvalidOperationException
{
    public ProjectPersistenceUnavailableException(
        ProjectPersistenceUnavailability unavailability,
        string message)
        : base(message)
    {
        Unavailability = unavailability;
    }

    public ProjectPersistenceUnavailability Unavailability { get; }
}

public sealed class ProjectPersistenceCoordinator
{
    private readonly object _sync = new();
    private readonly ProjectDocumentSession _document;
    private readonly MidoraProjectPackageV1 _packages;
    private readonly ProjectPresentationSessionV3 _presentation;
    private string? _protectedSourceProjectPath;
    private MidoraLegacyProjectSourceIdentityV3? _legacySourceIdentity;
    private string? _currentProjectPath;
    private MidoraProjectFileInformationV1? _fileInformation;
    private bool _operationActive;

    public ProjectPersistenceCoordinator(
        ProjectDocumentSession document,
        MidoraProjectPackageV1 packages,
        string? currentProjectPath = null,
        MidoraProjectFileInformationV1? fileInformation = null,
        string? protectedSourceProjectPath = null,
        ProjectPresentationStateV3? presentationState = null,
        bool presentationRecoveryDirty = false,
        MidoraLegacyProjectSourceIdentityV3? legacySourceIdentity = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _presentation = new(presentationState, presentationRecoveryDirty);
        bool hasPath = currentProjectPath is not null;
        if (hasPath != document.HasPersistentOrigin)
        {
            throw new ArgumentException(
                "The Project path must agree with the document's persistent origin.",
                nameof(currentProjectPath));
        }
        if (hasPath && fileInformation is null)
        {
            throw new ArgumentException(
                "Persisted Projects require file-version information.",
                nameof(fileInformation));
        }
        if (hasPath && protectedSourceProjectPath is not null)
        {
            throw new ArgumentException(
                "A persisted Project cannot also carry a protected migration source path.",
                nameof(protectedSourceProjectPath));
        }

        _currentProjectPath = currentProjectPath is null
            ? null
            : NormalizePath(currentProjectPath, nameof(currentProjectPath));
        _fileInformation = fileInformation;
        _protectedSourceProjectPath = protectedSourceProjectPath is null
            ? null
            : NormalizePath(protectedSourceProjectPath, nameof(protectedSourceProjectPath));
        if ((_protectedSourceProjectPath is null) != (legacySourceIdentity is null))
        {
            throw new ArgumentException(
                "A protected migration source requires its frozen source identity.",
                nameof(legacySourceIdentity));
        }
        if (legacySourceIdentity is not null
            && !PathsEqual(_protectedSourceProjectPath!, legacySourceIdentity.SourcePath))
        {
            throw new ArgumentException(
                "The migration source path and source identity do not match.",
                nameof(legacySourceIdentity));
        }
        _legacySourceIdentity = legacySourceIdentity;
        document.PresentationObjectsCloned += _presentation.CopyForDuplicate;
    }

    public ProjectDocumentSession Document => _document;
    public ProjectPresentationSessionV3 Presentation => _presentation;

    public string? CurrentProjectPath
    {
        get
        {
            lock (_sync)
            {
                return _currentProjectPath;
            }
        }
    }

    public MidoraProjectFileInformationV1? FileInformation
    {
        get
        {
            lock (_sync)
            {
                return _fileInformation;
            }
        }
    }

    public string? ProtectedSourceProjectPath
    {
        get
        {
            lock (_sync)
            {
                return _protectedSourceProjectPath;
            }
        }
    }

    public int? LegacySourceFileFormatVersion
    {
        get
        {
            lock (_sync)
            {
                return _legacySourceIdentity?.SourceFileFormatVersion;
            }
        }
    }

    public bool RequiresFormatUpgrade
    {
        get
        {
            lock (_sync)
            {
                return _legacySourceIdentity is not null;
            }
        }
    }

    public bool IsOperationActive
    {
        get
        {
            lock (_sync)
            {
                return _operationActive;
            }
        }
    }

    public bool CanSaveProject
    {
        get
        {
            MidoraProject project = _document.Project;
            if (project.DamagedEventInstruments.Count != 0
                || project.DamagedLogicalTracks.Count != 0
                || project.DamagedMidiChannelRoots.Count != 0
                || project.DamagedPureMidiTracks.Count != 0)
            {
                return false;
            }
            return true;
        }
    }

    public async Task<MidoraProjectSaveResultV1> SaveProjectAsync(
        string? firstSaveTargetPath = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        OperationSnapshot operation = BeginOperation();
        try
        {
            RequireNoDamagedProjectObjects();
            string targetPath;
            bool effectiveOverwriteAuthorization;
            if (operation.CurrentProjectPath is null)
            {
                if (firstSaveTargetPath is null)
                {
                    throw new InvalidOperationException(
                        "An unsaved Project requires a target path for its first save.");
                }
                targetPath = NormalizePath(firstSaveTargetPath, nameof(firstSaveTargetPath));
                RequireNotProtectedSourcePath(targetPath, operation.ProtectedSourceProjectPath);
                effectiveOverwriteAuthorization = overwriteAuthorized;
            }
            else
            {
                if (firstSaveTargetPath is not null
                    && !PathsEqual(
                        operation.CurrentProjectPath,
                        NormalizePath(firstSaveTargetPath, nameof(firstSaveTargetPath))))
                {
                    throw new InvalidOperationException(
                        "Initial release Save Project cannot change the current Project path; use Save Copy for a separate file.");
                }
                targetPath = operation.CurrentProjectPath;
                effectiveOverwriteAuthorization = true;
            }

            ProjectPresentationSaveSnapshotV3 presentation =
                _presentation.CreateSaveSnapshot(_document.Project);
            MidoraProjectSaveResultV1 result = await _packages.SaveProjectAsync(
                _document.Project,
                presentation.State,
                targetPath,
                operation.FileInformation,
                _document.Compilation.EditingTimeSession,
                effectiveOverwriteAuthorization,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _currentProjectPath = result.TargetPath;
                _fileInformation = result.FileInformation;
            }
            _document.MarkSaveSucceeded();
            // Preserve presentation dirty state when the package intentionally
            // omitted a damaged or over-budget section.
            if (result.OmittedPresentationSections is not { Count: > 0 })
            {
                _presentation.MarkSaveSucceeded(presentation);
            }
            return result;
        }
        finally
        {
            CompleteOperation();
        }
    }

    public async Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        string targetPath,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizePath(targetPath, nameof(targetPath));
        OperationSnapshot operation = BeginOperation();
        try
        {
            RequireNoDamagedProjectObjects();
            if (operation.CurrentProjectPath is not null
                && PathsEqual(operation.CurrentProjectPath, normalizedTarget))
            {
                throw new InvalidOperationException(
                    "Save Copy cannot overwrite the current Project file; use Save Project.");
            }
            RequireNotProtectedSourcePath(
                normalizedTarget,
                operation.ProtectedSourceProjectPath);

            ProjectPresentationSaveSnapshotV3 presentation =
                _presentation.CreateSaveSnapshot(_document.Project);
            return await _packages.SaveCopyAsync(
                _document.Project,
                presentation.State,
                normalizedTarget,
                operation.FileInformation,
                _document.Compilation.EditingTimeSession,
                overwriteAuthorized,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private OperationSnapshot BeginOperation()
    {
        lock (_sync)
        {
            if (_operationActive)
            {
                throw new InvalidOperationException(
                    "Another Project persistence operation is already active.");
            }
            _operationActive = true;
            return new(
                _currentProjectPath,
                _fileInformation,
                _protectedSourceProjectPath,
                _legacySourceIdentity);
        }
    }

    private void CompleteOperation()
    {
        lock (_sync)
        {
            _operationActive = false;
        }
    }

    private void RequireNoDamagedProjectObjects()
    {
        if (_document.Project.DamagedEventInstruments.Count != 0
            || _document.Project.DamagedLogicalTracks.Count != 0
            || _document.Project.DamagedMidiChannelRoots.Count != 0
            || _document.Project.DamagedPureMidiTracks.Count != 0)
        {
            throw new ProjectPersistenceUnavailableException(
                ProjectPersistenceUnavailability.DamagedProjectObjects,
                "Projects containing damaged object placeholders cannot be saved or copied.");
        }
    }

    private static string NormalizePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Project file paths must be fully qualified.", parameterName);
        }
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void RequireNotProtectedSourcePath(
        string targetPath,
        string? protectedSourceProjectPath)
    {
        if (protectedSourceProjectPath is not null
            && PathsEqual(targetPath, protectedSourceProjectPath))
        {
            throw new InvalidOperationException(
                "A migrated Format 1 or Format 2 source can only be replaced by the explicit in-place upgrade operation.");
        }
    }

    public async Task<MidoraProjectSaveResultV1> UpgradeLegacyProjectInPlaceAsync(
        CancellationToken cancellationToken = default)
    {
        MidoraLegacyProjectUpgradePlanV3 plan = await PrepareLegacyProjectUpgradeAsync(
            cancellationToken).ConfigureAwait(false);
        return await UpgradeLegacyProjectInPlaceAsync(plan, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MidoraLegacyProjectUpgradePlanV3> PrepareLegacyProjectUpgradeAsync(
        CancellationToken cancellationToken = default)
    {
        OperationSnapshot operation = BeginOperation();
        try
        {
            MidoraLegacyProjectSourceIdentityV3 sourceIdentity =
                operation.LegacySourceIdentity
                ?? throw new InvalidOperationException(
                    "The current Project does not require an in-place format upgrade.");
            return await _packages.PrepareLegacyProjectUpgradeAsync(
                sourceIdentity,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    public async Task<MidoraProjectSaveResultV1> UpgradeLegacyProjectInPlaceAsync(
        MidoraLegacyProjectUpgradePlanV3 plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        OperationSnapshot operation = BeginOperation();
        try
        {
            RequireNoDamagedProjectObjects();
            MidoraLegacyProjectSourceIdentityV3 sourceIdentity =
                operation.LegacySourceIdentity
                ?? throw new InvalidOperationException(
                    "The current Project does not require an in-place format upgrade.");
            if (!Equals(plan.SourceIdentity, sourceIdentity))
            {
                throw new InvalidOperationException(
                    "The confirmed upgrade plan does not belong to the current legacy Project source.");
            }
            MidoraProjectFileInformationV1 fileInformation = operation.FileInformation
                ?? throw new InvalidOperationException(
                    "The migrated Project has no source file information.");
            ProjectPresentationSaveSnapshotV3 presentation =
                _presentation.CreateSaveSnapshot(_document.Project);
            MidoraProjectSaveResultV1 result = await _packages
                .UpgradeLegacyProjectInPlaceAsync(
                    _document.Project,
                    presentation.State,
                    plan,
                    fileInformation,
                    _document.Compilation.EditingTimeSession,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _currentProjectPath = result.TargetPath;
                _fileInformation = result.FileInformation;
                _protectedSourceProjectPath = null;
                _legacySourceIdentity = null;
            }
            _document.MarkSaveSucceeded();
            if (result.OmittedPresentationSections is not { Count: > 0 })
            {
                _presentation.MarkSaveSucceeded(presentation);
            }
            return result;
        }
        finally
        {
            CompleteOperation();
        }
    }

    private readonly record struct OperationSnapshot(
        string? CurrentProjectPath,
        MidoraProjectFileInformationV1? FileInformation,
        string? ProtectedSourceProjectPath,
        MidoraLegacyProjectSourceIdentityV3? LegacySourceIdentity);
}
