namespace Midora.Application;

/// <summary>
/// One cancellation/progress and storage budget for a complete user operation,
/// including nested commands. Disposing the scope does not dispose transferred
/// record stores: their immutable result/history/clipboard owner owns them.
/// </summary>
internal sealed class BulkEditPreparationContext : IDisposable
{
    private static readonly AsyncLocal<BulkEditPreparationContext?> Slot = new();
    private readonly BulkEditPreparationContext? _previous;
    private readonly IProgress<TimelineEditPreparationProgress>? _progress;
    private long _lastReport;
    private double _lastFraction = -1;
    private bool _disposed;
    private readonly CancellationTokenSource? _linked;

    private BulkEditPreparationContext(CancellationToken token,
        IProgress<TimelineEditPreparationProgress>? progress,
        BoundedEditResources? resources, Midora.Domain.MidoraProject? project,
        int? preferredPageRecordCount)
    {
        _previous = Slot.Value;
        if (!token.CanBeCanceled) Token = _previous?.Token ?? token;
        else if (_previous?.Token is { CanBeCanceled: true } previousToken && previousToken != token)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(token, previousToken);
            Token = _linked.Token;
        }
        else Token = token;
        _progress = progress;
        Resources = resources ?? _previous?.Resources ?? new BoundedEditResources();
        if (preferredPageRecordCount is < 64 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(preferredPageRecordCount));
        PreferredPageRecordCount = preferredPageRecordCount.HasValue
            ? Math.Min(preferredPageRecordCount.Value, Resources.Budget.PageRecordCount)
            : ReferenceEquals(Resources, _previous?.Resources)
                ? _previous!.PreferredPageRecordCount : Resources.Budget.PageRecordCount;
        Project = _previous?.Project ?? project;
        Slot.Value = this;
    }

    public static BulkEditPreparationContext? Current => Slot.Value;
    public CancellationToken Token { get; }
    public BoundedEditResources Resources { get; }
    internal int PreferredPageRecordCount { get; }
    public Midora.Domain.MidoraProject? Project { get; }

    public static BulkEditPreparationContext Enter(CancellationToken token = default,
        IProgress<TimelineEditPreparationProgress>? progress = null,
        BoundedEditResources? resources = null, Midora.Domain.MidoraProject? project = null,
        int? preferredPageRecordCount = null) =>
        new(token, progress, resources, project, preferredPageRecordCount);

    public void Checkpoint(long completed, long total,
        TimelineEditPreparationPhase phase = TimelineEditPreparationPhase.Planning)
        => Report(new TimelineEditPreparationProgress(phase, completed, total));

    public void Checkpoint(long completed, long total,
        TimelineEditPreparationPhase phase, double start, double length)
        => Report(new TimelineEditPreparationProgress(phase, completed, total).InWorkRange(start, length));

    internal IProgress<TimelineEditPreparationProgress> ProgressInRange(double start, double length)
        => new BulkEditProgressRange(new ContextProgress(this), start, length);
    internal IProgress<TimelineEditPreparationProgress> ProgressInWorkRange(double start, double length)
        => new BulkEditWorkProgressRange(new ContextProgress(this), start, length);

    private sealed class ContextProgress(BulkEditPreparationContext context)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => context.Report(value);
    }

    public void Report(TimelineEditPreparationProgress value)
    {
        Token.ThrowIfCancellationRequested();
        _previous?.Token.ThrowIfCancellationRequested();
        if (_progress is null)
        {
            _previous?.Report(value);
            return;
        }
        long now = Environment.TickCount64;
        // Work is counted on every checkpoint; notifications are throttled, not
        // synthesized by a timer and never allowed to move the task backwards.
        if (value.OverallFraction < _lastFraction) return;
        if (value.Completed != value.Total && now - _lastReport < 50) return;
        _lastReport = now;
        _lastFraction = value.OverallFraction;
        _progress.Report(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!ReferenceEquals(Slot.Value, this))
            throw new InvalidOperationException("Bulk preparation scopes must be disposed in nesting order.");
        Slot.Value = _previous;
        _linked?.Dispose();
    }
}
