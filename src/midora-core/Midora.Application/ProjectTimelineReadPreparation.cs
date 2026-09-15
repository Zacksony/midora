using Midora.Domain;

namespace Midora.Application;

/// <summary>Prepares a frozen source's read-only address cache under the same
/// bounded storage and cancellation rules as editing. It never publishes an edit.</summary>
public static partial class ProjectTimelineReadPreparation
{
    public static void PrepareOrdinalLookup<T>(MidoraProject project,
        ITimelineObjectSource<T> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        using var context = BulkEditPreparationContext.Enter(cancellationToken, project: project);
        using var lease = context.Resources.BeginResourceLease();
        source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
        // The source retains only a fully installed independent index. Anything
        // left provisional is released by the lease, including on cancellation.
        context.Token.ThrowIfCancellationRequested();
    }
}
