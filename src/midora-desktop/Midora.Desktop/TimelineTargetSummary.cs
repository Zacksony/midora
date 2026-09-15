namespace Midora.Desktop;

internal sealed class TimelineTargetSummary<T> where T : notnull
{
    public TimelineTargetSummary(IReadOnlyDictionary<T, int> counts, IComparer<T> comparer)
    { Counts = counts; Targets = counts.Keys.Order(comparer).ToArray(); }
    public IReadOnlyDictionary<T, int> Counts { get; }
    public IReadOnlyList<T> Targets { get; }
}
