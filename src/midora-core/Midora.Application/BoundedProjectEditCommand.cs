using Midora.Domain;

namespace Midora.Application;

internal sealed class BoundedProjectEditCommand(string name,
    Func<MidoraProject, CancellationToken, IProgress<TimelineEditPreparationProgress>?, IPreparedProjectEdit> prepare)
    : IProgressReportingProjectEditCommand
{
    public string Name => name;
    public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, BulkEditPreparationContext.Current?.Token ?? default, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) => Prepare(project, token, null);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        return prepare(project, scope.Token, progress);
    }
}
