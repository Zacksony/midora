namespace Midora.Application.Tests;

[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class BoundedEditSortAdaptiveBufferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TinyInputsDoNotAllocateAFullRun(bool knownLength)
    {
        BoundedEditResources resources = new();
        IEnumerable<long> source = knownLength ? new long[] { 7, 2, 4 } : Descending(3);
        using (var warm = BoundedEditSort.Sort(source, Comparer<long>.Default, resources)) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (var result = BoundedEditSort.Sort(source, Comparer<long>.Default, resources))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            // Includes the unchanged 4K-record output page, but not a 65K run.
            Assert.InRange(allocated, 1, 96 * 1024);
            Assert.Equal(3, result.Count);
            Assert.True(result[0] <= result[1] && result[1] <= result[2]);
            Assert.InRange(resources.PeakWorkingBytes, 1, (4096 + 64) * sizeof(long));
        }
        AssertReleased(resources);
    }

    [Fact]
    public void OneThousandTinyOwnerSortsDoNotAccumulateFullRunGarbage()
    {
        BoundedEditResources resources = new();
        using (var warm = BoundedEditSort.Sort(Descending(1), Comparer<long>.Default, resources)) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int owner = 0; owner < 1000; owner++)
        {
            using var result = BoundedEditSort.Sort(Descending(1), Comparer<long>.Default, resources);
            Assert.Equal(0, result[0]);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1, 48L * 1024 * 1024);
        AssertReleased(resources);
    }

    [Fact]
    public void GrowthBudgetIncludesOldAndNewArraysWhileCopying()
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 16 * 1024, pageRecordCount: 64));
        using (var result = BoundedEditSort.Sort(Descending(257), Comparer<long>.Default, resources))
        {
            Assert.Equal(257, result.Count);
            // Unknown length grows 64 -> 128 -> 256 -> 512. At the final
            // copy, the 256 and 512 element arrays must both be budgeted.
            Assert.Equal((256 + 512) * sizeof(long), resources.PeakWorkingBytes);
        }
        AssertReleased(resources);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(257)]
    public void TinyWorkingBudgetAndExistingReservationStillSupportPreviousSortSizes(int count)
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 4096, pageRecordCount: 64));
        using (resources.ReserveWorking(512))
        {
            using (var result = BoundedEditSort.Sort(Descending(count), Comparer<long>.Default, resources))
                Assert.Equal(Enumerable.Range(0, count).Select(static value => (long)value), result.ReadValues());
            Assert.Equal(512, resources.WorkingBytes);
            Assert.InRange(resources.PeakWorkingBytes, 512, resources.Budget.MaximumWorkingBytes);
        }
        AssertReleased(resources);
    }

    [Fact]
    public void FailedGrowthReservationReleasesOnlyItsOwnWorkingBuffers()
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 16 * 1024, pageRecordCount: 64));
        using (resources.ReserveWorking(11 * 1024))
        {
            Assert.Throws<InvalidOperationException>(() =>
                BoundedEditSort.Sort(Descending(257), Comparer<long>.Default, resources));
            Assert.Equal(11 * 1024, resources.WorkingBytes);
            Assert.Equal(0, resources.ResidentBytes);
            Assert.Equal(0, resources.SpillBytes);
            Assert.InRange(resources.PeakWorkingBytes, 11 * 1024, resources.Budget.MaximumWorkingBytes);
        }
        AssertReleased(resources);
    }

    [Fact]
    public void MillionReverseRecordsRemainSortedBoundedAndFullyReleased()
    {
        WithDirectory(path =>
        {
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumWorkingBytes: 2 * 1024 * 1024, maximumResidentBytes: 512 * 1024,
                maximumSpillBytes: 32 * 1024 * 1024, pageRecordCount: 1024), path);
            using (var result = BoundedEditSort.Sort(Descending(1_000_000), Comparer<long>.Default, resources))
            {
                Assert.Equal(1_000_000, result.Count);
                long expected = 0;
                foreach (long value in result.ReadValues()) Assert.Equal(expected++, value);
                Assert.Equal(1_000_000, expected);
                Assert.True(resources.PeakSpillBytes > 0);
                Assert.InRange(resources.PeakWorkingBytes, 1, resources.Budget.MaximumWorkingBytes);
                Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
                Assert.InRange(resources.PeakSpillBytes, 1, resources.Budget.MaximumSpillBytes);
            }
            AssertReleased(resources);
            Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        });
    }

    [Fact]
    public void AdaptiveGrowthPreservesPreviousRunBoundariesAndEqualKeyMergeOrder()
    {
        const int count = 150_000;
        var comparer = Comparer<Item>.Create(static (left, right) => left.Key.CompareTo(right.Key));
        Item[] input = Enumerable.Range(0, count).Select(static value => new Item(value % 13, value)).ToArray();
        List<Item[]> expectedRuns = [];
        for (int offset = 0; offset < input.Length; offset += 65_536)
        {
            Item[] run = input.AsSpan(offset, Math.Min(65_536, input.Length - offset)).ToArray();
            Array.Sort(run, comparer);
            expectedRuns.Add(run);
        }
        var queue = new PriorityQueue<(int Run, int Offset), (Item Value, int Run)>(
            Comparer<(Item Value, int Run)>.Create((left, right) =>
            {
                int order = comparer.Compare(left.Value, right.Value);
                return order != 0 ? order : left.Run.CompareTo(right.Run);
            }));
        for (int run = 0; run < expectedRuns.Count; run++) queue.Enqueue((run, 0), (expectedRuns[run][0], run));
        var resources = new BoundedEditResources();
        // Force the unknown-size path rather than an array's O(1) count hint.
        using (var sorted = BoundedEditSort.Sort(UnknownInput(), comparer, resources))
        {
            using var actual = sorted.ReadValues().GetEnumerator();
            while (queue.TryDequeue(out var position, out var priority))
            {
                Assert.True(actual.MoveNext());
                Assert.Equal(priority.Value, actual.Current);
                int next = position.Offset + 1;
                if (next < expectedRuns[position.Run].Length)
                    queue.Enqueue((position.Run, next), (expectedRuns[position.Run][next], position.Run));
            }
            Assert.False(actual.MoveNext());
        }
        AssertReleased(resources);
        IEnumerable<Item> UnknownInput() { foreach (Item value in input) yield return value; }
    }

    [Theory]
    [InlineData(65)]
    [InlineData(300_000)]
    public void CancellationDuringGrowthOrAfterSpilledRunsLeavesNothingOwned(int cancelAt)
    {
        WithDirectory(path =>
        {
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumWorkingBytes: 2 * 1024 * 1024, maximumResidentBytes: 0,
                pageRecordCount: 1024, cancellationCheckInterval: 1), path);
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                BoundedEditSort.Sort(Input(), Comparer<long>.Default, resources, cancellation.Token));
            AssertReleased(resources);
            Assert.Empty(Directory.EnumerateFileSystemEntries(path));
            IEnumerable<long> Input()
            {
                for (int value = 0; value < 1_000_000; value++)
                {
                    if (value == cancelAt) cancellation.Cancel();
                    yield return value;
                }
            }
        });
    }

    private static IEnumerable<long> Descending(int count)
    { for (long value = count - 1; value >= 0; value--) yield return value; }

    private static void AssertReleased(BoundedEditResources resources)
    {
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    private static void WithDirectory(Action<string> run)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ".tmp", "sort-growth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { run(path); }
        finally { Directory.Delete(path, recursive: true); }
    }

    private readonly record struct Item(int Key, int SourceOrdinal);
}
