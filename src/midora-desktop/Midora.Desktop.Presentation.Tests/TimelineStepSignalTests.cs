using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineStepSignalTests
{
    [Fact]
    public void PredecessorIsStrictOwnLaneAndSameTickUsesOrderNotEnumeration()
    {
        TimelineStepPoint[] points = [new(100, 9, .8), new(10, 1, .2), new(100, 2, .4)];
        var source = Source(points);
        Assert.False(source.TryGetBefore(10, out _));
        Assert.True(source.TryGetBefore(100, out var old)); Assert.Equal(.2, old.Value);
        Assert.True(source.TryGetBefore(101, out var last)); Assert.Equal(.8, last.Value);
        var summary = source.Summarize(100, 101);
        Assert.Equal(.4, summary.First.Value); Assert.Equal(.8, summary.Last.Value);
        Assert.False(Source([]).TryGetBefore(long.MaxValue, out _));
    }

    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void RasterHasHorizontalHoldsVerticalJumpsAndNoInventedPrefix(double dpi)
    {
        var source = Source([new(30, 0, .75), new(80, 1, .25)]);
        var raster = TimelineStepSignalRasterizer.Rasterize(source, dpi, 200 * dpi, 0, 0, dpi, dpi, Colors.Blue);
        int gx = TimelineEventPointTileRasterizer.GetGutter(dpi), gy = gx;
        bool Painted(int x, int y) => raster.Pixels[(((int)Math.Floor(y * dpi) + gy) * raster.Width + (int)Math.Floor(x * dpi) + gx) * 4 + 3] != 0;
        Assert.False(Painted(10, 50)); Assert.True(Painted(50, 50));
        Assert.True(Painted(80, 90));
        if (dpi == 1) Assert.True(Painted(180, 150));
        var later = TimelineStepSignalRasterizer.Rasterize(source, .01, 200, 2, 0, 1, 1, Colors.Blue);
        Assert.Contains(later.Pixels, b => b != 0);
    }

    [Fact]
    public void ColdSummariesAreBoundedAndWarmQueriesDoNotRescan()
    {
        int visits = 0;
        var source = new TimelineStepSignalSource(Guid.NewGuid().ToString(), (_, _) => 1, Query);
        _ = TimelineStepSignalRasterizer.Rasterize(source, .001, 200, 0, 0, 1, 1, Colors.Blue);
        int first = visits;
        _ = TimelineStepSignalRasterizer.Rasterize(source, .001, 200, 0, 0, 1, 1, Colors.Blue);
        Assert.Equal(first, visits); Assert.InRange(first, 1, 280_000);
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            for (long t = start; t < end; t++) { visits++; yield return new(t, t, (t % 128) / 127d); }
        }
    }

    [Fact]
    public void CanceledSummaryIsNeverPublished()
    {
        using var cancellation = new CancellationTokenSource();
        int visits = 0;
        var source = new TimelineStepSignalSource(Guid.NewGuid().ToString(), (_, _) => 1, Query);
        Assert.ThrowsAny<OperationCanceledException>(() => source.Summarize(0, 1000, cancellation.Token));
        Assert.Equal(1000, source.Summarize(0, 1000).Count);
        IEnumerable<TimelineStepPoint> Query(long start, long end, CancellationToken token)
        {
            for (long i = start; i < end; i++) { if (++visits == 5) cancellation.Cancel(); yield return new(i, i, .5); }
        }
    }

    [Fact]
    public void ChangedPredecessorInvalidatesLaterEmptyTilesButUnrelatedEditsDoNot()
    {
        string owner = Guid.NewGuid().ToString();
        var first = new TimelineStepSignalSource(owner, (s, e) => s <= 10 && e > 10 ? 1UL : 0, Query(.1));
        var second = new TimelineStepSignalSource(owner, (s, e) => s <= 10 && e > 10 ? 2UL : 0, Query(.9));
        Assert.NotEqual(first.GetFingerprint(100, 200), second.GetFingerprint(100, 200));
        Assert.Equal(first.GetFingerprint(0, 9), second.GetFingerprint(0, 9));
        static Func<long, long, CancellationToken, IEnumerable<TimelineStepPoint>> Query(double value) =>
            (s, e, _) => s <= 10 && e > 10 ? [new(10, 0, value)] : [];
    }

    private static TimelineStepSignalSource Source(TimelineStepPoint[] points) => new(Guid.NewGuid().ToString(),
        (_, _) => 1, (s, e, _) => points.Where(p => p.Tick >= s && p.Tick < e));

    [Fact]
    public void SameTickFormalReorderInvalidatesTheRasterEvenWhenSpatialHashIsUnchanged()
    {
        string owner = Guid.NewGuid().ToString();
        TimelineStepPoint[] points = [new(100, 10, .2), new(100, 20, .8), new(200, 30, .5)];
        var first = new TimelineStepSignalSource(owner, (_, _) => 42, Query, id => id, 1);
        var reordered = new TimelineStepSignalSource(owner, (_, _) => 42, Query, id => -id, 2);
        ulong oldHash = TimelineStepSignalRasterizer.Fingerprint(first, 1, 0, 1);
        ulong newHash = TimelineStepSignalRasterizer.Fingerprint(reordered, 1, 0, 1);
        Assert.NotEqual(oldHash, newHash);
        Assert.Equal(.8, first.Summarize(100, 101).Last.Value);
        Assert.Equal(.2, reordered.Summarize(100, 101).Last.Value);
        IEnumerable<TimelineStepPoint> Query(long s, long e, CancellationToken _) => points.Where(p => p.Tick >= s && p.Tick < e);
    }

    [Fact]
    public void ExtremalTickAndInvalidScaleAreHandledExplicitly()
    {
        var source = Source([new(long.MaxValue - 5, 1, .5)]);
        Assert.True(source.TryGetBefore(long.MaxValue, out var previous));
        Assert.Equal(long.MaxValue - 5, previous.Tick);
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineStepSignalRasterizer.Range(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineStepSignalRasterizer.Range(double.NaN, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineStepSignalRasterizer.Rasterize(source, 1, double.PositiveInfinity, 0, 0, 1, 1, Colors.Blue));
    }

    [Fact]
    public void SummaryRetentionIsBoundedEvenWhenPanningThroughManyEmptyRanges()
    {
        var source = Source([]);
        for (int i = 0; i < TimelineStepSignalSource.SharedSummaryCapacity + 100; i++)
            source.Summarize(i, i + 1);
        var flags = System.Reflection.BindingFlags.NonPublic;
        var local = (System.Collections.IDictionary)typeof(TimelineStepSignalSource)
            .GetField("_local", flags | System.Reflection.BindingFlags.Instance)!.GetValue(source)!;
        Assert.InRange(local.Count, 1, 1024);
        var shared = (System.Collections.IDictionary)typeof(TimelineStepSignalSource)
            .GetField("Shared", flags | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.InRange(shared.Count, 1, TimelineStepSignalSource.SharedSummaryCapacity);
    }
}
