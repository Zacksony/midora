using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class BoundedBulkProgressRepairTests
{
    [Fact]
    public void UnknownWorkRemainsIndeterminateAndNestedRangesPreserveActualCounts()
    {
        var unknown = new TimelineEditPreparationProgress(TimelineEditPreparationPhase.Sorting, 40, 0)
            .InWorkRange(0.6, 0.2).InRange(0.1, 0.8);
        Assert.True(unknown.IsIndeterminate);
        Assert.Equal(0.58, unknown.OverallFraction, 10);
        Assert.Equal(40, unknown.Completed);
        var known = new TimelineEditPreparationProgress(TimelineEditPreparationPhase.ReadingSelection, 25, 100)
            .InWorkRange(0.2, 0.4).InRange(0.1, 0.5);
        Assert.Equal(0.25, known.OverallFraction, 10);
        Assert.False(known.IsIndeterminate);
        Assert.Throws<ArgumentOutOfRangeException>(() => known.InRange(double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => known.InWorkRange(0.8, 0.4));
    }

    [Fact]
    public void ContextForwardsExplicitRangesRatherThanRecomputingLegacyBands()
    {
        List<TimelineEditPreparationProgress> values = [];
        using var outer = BulkEditPreparationContext.Enter(progress: new InlineProgress(values.Add));
        using (var child = BulkEditPreparationContext.Enter())
            child.Checkpoint(100, 100, TimelineEditPreparationPhase.ReadingSelection, 0.2, 0.4);
        Assert.Equal(0.6, Assert.Single(values).OverallFraction, 10);
    }

    [Fact]
    public void SortOnlyTakesOverProgressAfterInputHasBeenReadAndCompletesRealMergeCounts()
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 256 * 1024, maximumResidentBytes: 1024, pageRecordCount: 64));
        bool inputFinished = false;
        List<TimelineEditPreparationProgress> reports = [];
        using (var result = BoundedEditSort.Sort(Input(), Comparer<int>.Default, resources, default,
            new InlineProgress(value => { Assert.True(inputFinished); reports.Add(value); })))
        {
            Assert.Equal(Enumerable.Range(0, 100_000), result.ReadValues());
            Assert.Equal(1, reports[^1].OverallFraction);
            Assert.Contains(reports, p => p.Total == 100_000 && p.Completed > 0);
            Assert.True(reports.Zip(reports.Skip(1), (a, b) => a.OverallFraction <= b.OverallFraction).All(x => x));
            Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
        }
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.SpillBytes);
        IEnumerable<int> Input()
        {
            for (int i = 99_999; i >= 0; i--) yield return i;
            inputFinished = true;
        }
    }

    [Fact]
    public void CancellingFinalSpillReleasesPagesAndNeverPublishesReady()
    {
        using var project = new MidoraProject(480);
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        using var cancellation = new CancellationTokenSource();
        var resources = new BoundedEditResources();
        using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
        List<TimelineEditPreparationProgress> reports = [];
        var command = new BoundedProjectEditCommand("Prepare test pages", (_, token, _) =>
        {
            var store = new BoundedEditRecordStore<long>(resources);
            store.AddRange(Enumerable.Range(0, 12_000).Select(i => (long)i), token);
            store.Seal();
            _ = new BoundedImmutableValueSource<long>(store);
            return new NoChange();
        });
        Assert.Throws<OperationCanceledException>(() => document.PrepareEdit(command, cancellation.Token,
            new InlineProgress(value =>
            {
                reports.Add(value);
                if (value.Phase == TimelineEditPreparationPhase.WritingStorage && value.Completed > 0)
                    cancellation.Cancel();
            })));
        Assert.DoesNotContain(reports, p => p.Phase == TimelineEditPreparationPhase.Ready);
        Assert.Empty(document.History);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
        Assert.Equal(0, resources.WorkingBytes);
    }

    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> callback)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => callback(value);
    }
    private sealed class NoChange : IPreparedProjectEdit
    {
        public bool HasChanges => false;
        public ProjectChangeSet Changes { get; } = new();
        public void Apply(MidoraProject project) { }
        public void Undo(MidoraProject project) { }
    }
}
