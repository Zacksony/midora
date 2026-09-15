using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>Freezes the destination selection context, independently of the old selection.</summary>
internal sealed class TimelineCreationEditCommand(
    IProjectEditCommand command, WorkspaceTimelineSelectionSource resultSource)
    : IProgressReportingProjectEditCommand, IDisposable
{
    public string Name => command.Name;
    public WorkspaceTimelineSelectionSource ResultSource { get; } = resultSource;
    public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, default, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) => Prepare(project, token, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token,
        IProgress<TimelineEditPreparationProgress>? progress) => command switch
    {
        IProgressReportingProjectEditCommand reporting => reporting.Prepare(project, token, progress),
        ICancellableProjectEditCommand cancellable => cancellable.Prepare(project, token),
        _ => throw new InvalidOperationException("Timeline creation must support cancellation.")
    };
    public void Dispose() => (command as IDisposable)?.Dispose();
}
