namespace Midora.Application;

/// <summary>Preserves real phase counters while assigning each nested owner a
/// disjoint share of the outer task. No timer invents progress.</summary>
internal sealed class BulkEditProgressRange(IProgress<TimelineEditPreparationProgress>? target,
    double start, double length) : IProgress<TimelineEditPreparationProgress>
{
    public void Report(TimelineEditPreparationProgress value) => target?.Report(value.InRange(start, length));
}

/// <summary>Maps a single phase's actual work counter into its caller's task.</summary>
internal sealed class BulkEditWorkProgressRange(IProgress<TimelineEditPreparationProgress>? target,
    double start, double length) : IProgress<TimelineEditPreparationProgress>
{
    public void Report(TimelineEditPreparationProgress value) => target?.Report(value.InWorkRange(start, length));
}
