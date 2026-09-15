using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineRasterAggregateCancellationTests
{
    [Theory]
    [InlineData("piano")]
    [InlineData("velocity")]
    [InlineData("event")]
    [InlineData("onion")]
    public async Task EachRasterLayerPropagatesCancellationIntoDenseAggregation(string layer)
    {
        using ProbeAggregateSource source = new(blockUntilCanceled: true);
        using CancellationTokenSource cancellation = new();
        Task<TimelineRasterBuffer> task = Task.Run(() => Render(layer, source, cancellation.Token));
        try
        {
            Assert.True(source.Started.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(task.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(source.Visited >= 256);
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData("piano")]
    [InlineData("velocity")]
    [InlineData("event")]
    [InlineData("onion")]
    public void DefaultAndLiveCancellationTokensProduceIdenticalPixels(string layer)
    {
        using ProbeAggregateSource source = new(blockUntilCanceled: false);
        using CancellationTokenSource cancellation = new();
        TimelineRasterBuffer baseline = Render(layer, source, default);
        TimelineRasterBuffer current = Render(layer, source, cancellation.Token);
        Assert.Equal(baseline.CandidateCount, current.CandidateCount);
        Assert.Equal(baseline.Width, current.Width);
        Assert.Equal(baseline.Height, current.Height);
        Assert.Equal(baseline.Pixels, current.Pixels);
        Assert.Contains(current.Pixels, static value => value != 0);
    }

    private static TimelineRasterBuffer Render(string layer, ProbeAggregateSource source, CancellationToken token)
    {
        TimelineRenderSnapshot snapshot = new(1, "aggregate-token", [], itemSource: source);
        return layer switch
        {
            "piano" => TimelinePianoTileRasterizer.Rasterize(snapshot, .0625, 1,
                0, 0, Colors.Red, Colors.Yellow, cancellationToken: token),
            "velocity" => TimelineVelocityTileRasterizer.Rasterize(snapshot, null, -8,
                0, Colors.Red, Colors.Yellow, Colors.White, token),
            "event" => TimelineEventPointTileRasterizer.Rasterize(snapshot, null, .0625, 256,
                0, 0, 1, 1, Colors.Red, Colors.Yellow, Colors.White, cancellationToken: token),
            "onion" => TimelineOnionRasterizer.Render(new("aggregate-token",
                [new(new(1), 0xffff0000, [new(source, 0, 100_000, 0)])], .5),
                0, 0, 0, .0625, 1, token),
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
    }

    private sealed class ProbeAggregateSource(bool blockUntilCanceled) :
        ITimelineRenderItemSource, ITimelineRasterAggregateSource, IDisposable
    {
        public long Count => 1_000_000;
        public long MaximumEndTick => 100_000;
        public ulong ContentFingerprint => 1;
        public ManualResetEventSlim Started { get; } = new(false);
        public long Visited { get; private set; }
        public bool TryAccumulateRasterColumns(TimelineRasterAggregateKind kind,
            TimelineRasterColumnProjection projection, int firstLane, int lastLaneExclusive,
            Span<TimelineRasterColumnSummary> destination, out int sourceWorkCount,
            CancellationToken cancellationToken = default)
        {
            sourceWorkCount = 0;
            cancellationToken.ThrowIfCancellationRequested();
            if (blockUntilCanceled)
            {
                long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                for (long record = 0; ; record++)
                {
                    if ((record & 255) != 0) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(15))
                        throw new TimeoutException("The raster layer did not propagate cancellation.");
                    Visited = record;
                    if (record >= 256) Started.Set();
                    Thread.Yield();
                }
            }
            destination.Clear();
            for (int column = 0; column < destination.Length; column += 3)
                destination[column].Include(1, 0, .25, .75, 1);
            sourceWorkCount = 100;
            return true;
        }
        public void QueryInto(long startTick, long endTick, int firstLane, int lastLaneExclusive,
            List<TimelineRenderItem> destination) => throw new InvalidOperationException("Expected aggregate path.");
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => [];
        public void Dispose() => Started.Dispose();
    }
}
