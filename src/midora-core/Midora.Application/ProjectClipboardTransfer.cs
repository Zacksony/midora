namespace Midora.Application;

/// <summary>A frozen clipboard result and optional already-prepared Cut deletion.</summary>
public sealed class PreparedProjectClipboardTransfer : IDisposable
{
    private readonly ProjectDocumentSession _document;
    private readonly long _revision;
    private bool _payloadTransferred;

    internal PreparedProjectClipboardTransfer(ProjectDocumentSession document, long revision,
        ProjectObjectClipboardPayload payload, StagedProjectEdit? deletion)
    {
        _document = document;
        _revision = revision;
        Payload = payload;
        Deletion = deletion;
    }

    public ProjectObjectClipboardPayload Payload { get; }
    public StagedProjectEdit? Deletion { get; }

    public void ValidatePublication(ProjectDocumentSession document)
    {
        if (!ReferenceEquals(document, _document) || document.PublicationRevision != _revision)
            throw new InvalidOperationException("The Project changed while copying. No clipboard changes were published.");
    }

    public ProjectObjectClipboardPayload TakePayload()
    {
        if (_payloadTransferred) throw new InvalidOperationException("The clipboard payload was already published.");
        _payloadTransferred = true;
        return Payload;
    }

    public void Dispose()
    {
        Deletion?.Dispose();
        if (!_payloadTransferred) Payload.Dispose();
    }
}

public static partial class ProjectObjectClipboard
{
    public static PreparedProjectClipboardTransfer PrepareTransfer(
        ProjectDocumentSession document,
        Func<ProjectObjectClipboardPayload> copy,
        IProjectEditCommand? delete = null,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(copy);
        using BulkEditPreparationContext scope = BulkEditPreparationContext.Enter(
            cancellationToken, progress, project: document.Project);
        long revision = document.PublicationRevision;
        cancellationToken.ThrowIfCancellationRequested();
        scope.Checkpoint(0, 1, TimelineEditPreparationPhase.ResolvingSelection);
        ProjectObjectClipboardPayload payload;
        using (BulkEditPreparationContext copyScope = BulkEditPreparationContext.Enter(cancellationToken,
            scope.ProgressInRange(0, delete is null ? 0.99 : 0.5), project: document.Project))
            payload = copy();
        StagedProjectEdit? deletion = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delete is not null)
                deletion = document.PrepareEdit(delete, cancellationToken, scope.ProgressInRange(0.5, 0.49));
            cancellationToken.ThrowIfCancellationRequested();
            PreparedProjectClipboardTransfer result = new(document, revision, payload, deletion);
            result.ValidatePublication(document);
            scope.Checkpoint(1, 1, TimelineEditPreparationPhase.Ready);
            return result;
        }
        catch
        {
            deletion?.Dispose();
            payload.Dispose();
            throw;
        }
    }
}
