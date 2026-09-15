using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application;

public enum ProjectOpenCandidateStage
{
    ValidatingInput,
    ReadingAndValidatingPackage,
    CandidateReady
}

public sealed record ProjectOpenCandidateProgress(
    ProjectOpenCandidateStage Stage,
    string CandidatePath);

public sealed class ProjectOpenCandidate : IDisposable, IAsyncDisposable
{
    public const string RecoveredSourceDirtyReason = "RecoveredProjectSourceData";
    public const string FormatUpgradeDirtyReason = "ProjectFormatUpgradeRequired";

    private readonly MidoraProjectPackageV1 _packages;
    private readonly MidoraProjectOpenResultV1 _openResult;
    private int _disposeStarted;

    internal ProjectOpenCandidate(
        MidoraProjectPackageV1 packages,
        MidoraProjectOpenResultV1 openResult,
        string currentProjectPath)
    {
        _packages = packages;
        _openResult = openResult;
        SourceProjectPath = currentProjectPath;
        CurrentProjectPath = openResult.RequiresFormatUpgrade
            ? null
            : currentProjectPath;
        Diagnostics = Array.AsReadOnly(openResult.Diagnostics.ToArray());
    }

    public MidoraProject Project => _openResult.Project;
    public ProjectDocumentOrigin Origin => _openResult.RequiresFormatUpgrade
        ? ProjectDocumentOrigin.Unsaved
        : ProjectDocumentOrigin.Persisted;
    public string SourceProjectPath { get; }
    public string? CurrentProjectPath { get; }
    public MidoraProjectFileInformationV1 FileInformation => _openResult.FileInformation;
    public bool RequiresSave => _openResult.IsModified;
    public IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics { get; }
    public bool HasDamagedProjectObjects =>
        Project.DamagedEventInstruments.Count != 0
        || Project.DamagedLogicalTracks.Count != 0
        || Project.DamagedMidiChannelRoots.Count != 0
        || Project.DamagedPureMidiTracks.Count != 0;
    public bool CanSaveProject => !HasDamagedProjectObjects;

    public ProjectDocumentSession CreateDocumentSession(
        ProjectCompilationSession compilation)
    {
        ThrowIfDisposed();
        RequireCandidateProject(compilation.Project);
        ProjectDocumentSession document = new(
            compilation,
            Origin);
        if (RequiresSave)
        {
            document.MarkExternallyModified(_openResult.RequiresFormatUpgrade
                ? FormatUpgradeDirtyReason
                : RecoveredSourceDirtyReason);
        }
        return document;
    }

    public ProjectPersistenceCoordinator CreatePersistenceCoordinator(
        ProjectDocumentSession document)
    {
        ThrowIfDisposed();
        RequireCandidateProject(document.Project);
        return new(
            document,
            _packages,
            CurrentProjectPath,
            FileInformation,
            _openResult.RequiresFormatUpgrade ? SourceProjectPath : null,
            _openResult.Presentation,
            _openResult.IsPresentationModified,
            _openResult.LegacySourceIdentity);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        _openResult.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        await _openResult.DisposeAsync().ConfigureAwait(false);
    }

    private void RequireCandidateProject(MidoraProject project)
    {
        if (!ReferenceEquals(Project, project))
        {
            throw new InvalidOperationException(
                "The requested session belongs to a different Project candidate.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);

}

public sealed class ProjectOpenCoordinator
{
    private readonly MidoraProjectPackageV1 _packages;

    public ProjectOpenCoordinator(MidoraProjectPackageV1 packages)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
    }

    public async Task<ProjectOpenCandidate> OpenAsync(
        string candidatePath,
        IProgress<ProjectOpenCandidateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        progress?.Report(new(
            ProjectOpenCandidateStage.ValidatingInput,
            candidatePath));
        if (!Path.IsPathFullyQualified(candidatePath))
        {
            throw new ArgumentException(
                "Project candidate paths must be fully qualified.",
                nameof(candidatePath));
        }
        string path = Path.GetFullPath(candidatePath);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(
            ProjectOpenCandidateStage.ReadingAndValidatingPackage,
            path));

        MidoraProjectOpenResultV1? opened = null;
        try
        {
            opened = await _packages.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ProjectOpenCandidate result = new(_packages, opened, path);
            opened = null;
            try
            {
                progress?.Report(new(ProjectOpenCandidateStage.CandidateReady, path));
                return result;
            }
            catch
            {
                await result.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
