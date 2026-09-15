using System.Diagnostics;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineOnionTests(ITestOutputHelper output)
{
    [Fact]
    public void ZeroLengthCanonicalPairHasOnePixelWithoutExtendingItsTickDuration()
    {
        var source = new Source([new(new(1), TimelineItemKind.DirectMidiNote, 20, 20, 67, 0, 0, TimelineItemState.None)]);
        var snapshot = new TimelineOnionSnapshot("zero-length", [new(new(2), 0xff00ff00, [new(source, 0, 100, 0)])], .5);
        var tile = TimelineOnionRasterizer.Render(snapshot, 0, 0, 0, 1, 1);
        Assert.NotEqual(0, Alpha(tile, 20, 67));
        Assert.Equal(0, Alpha(tile, 19, 67)); Assert.Equal(0, Alpha(tile, 21, 67));
        var clipped = new TimelineOnionSnapshot("zero-clipped", [new(new(2), 0xff00ff00, [new(source, 0, 20, 0)])], .5);
        Assert.All(TimelineOnionRasterizer.Render(clipped, 0, 0, 0, 1, 1).Pixels, p => Assert.Equal(0, p));
    }
    [Fact]
    public void DenseGhostsUseBoundedHierarchyWithShiftAndExposedClip()
    {
        var source = new AggregateSource();
        var snapshot = new TimelineOnionSnapshot("dense", [new(new(1), 0xff00ff00,
            [new(source, 100, 150, 200)])], .5);
        var tile = TimelineOnionRasterizer.Render(snapshot, 0, 0, 0, 1, 1);
        Assert.True(source.Used);
        Assert.Equal(0, Alpha(tile, 299, 67)); Assert.NotEqual(0, Alpha(tile, 300, 67));
        Assert.NotEqual(0, Alpha(tile, 349, 67)); Assert.Equal(0, Alpha(tile, 350, 67));
    }
    [Fact]
    public void ClipsHiddenContentAndMapsAbsoluteTimeWithoutSelectionState()
    {
        var source = new Source([new(new(1), TimelineItemKind.LogicalNote, 50, 500, 67, 0, 0, TimelineItemState.Selected)]);
        var snapshot = new TimelineOnionSnapshot("clip", [new(new(2), 0xff00ff00, [new(source, 100, 150, 200)])], .5);
        var tile = TimelineOnionRasterizer.Render(snapshot, 0, 0, 0, 1, 1);
        Assert.Equal(0, Alpha(tile, 299, 67)); Assert.InRange(Alpha(tile, 300, 67), 127, 128);
        Assert.InRange(Alpha(tile, 349, 67), 127, 128); Assert.Equal(0, Alpha(tile, 350, 67));
        Assert.Equal(0, Alpha(tile, 300, 66));
    }
    [Fact]
    public void SourceOrderCompositesLastTrackOnTopAndThinNotesKeepOnePixel()
    {
        var source = new Source([new(new(1), TimelineItemKind.DirectMidiNote, 1, 2, 30, 0, 0, TimelineItemState.None)]);
        var snapshot = new TimelineOnionSnapshot("order", [new(new(2), 0xffff0000, [new(source, 0, 100, 0)]),
            new(new(3), 0xff00ff00, [new(source, 0, 100, 0)])], .5);
        var tile = TimelineOnionRasterizer.Render(snapshot, 0, 0, 0, .001, 1);
        int pixel = (30 * tile.Width) * 4;
        Assert.True(tile.Pixels[pixel + 1] > tile.Pixels[pixel + 2]);
        Assert.True(Alpha(tile, 0, 30) > 0);
    }
    [Fact]
    public void BlockFingerprintIsIndependentAndFarRangesDoNotReadSources()
    {
        var source = new Source([new(new(1), TimelineItemKind.LogicalNote, 10, 20, 60, 0, 0, TimelineItemState.None)]);
        var tracks = Enumerable.Range(1, 16).Select(i => new TimelineOnionTrack(new(i), 0xffabcdef, [new(source, 0, 100, 0)])).ToArray();
        var first = new TimelineOnionSnapshot("blocks", tracks, .3);
        tracks[0] = tracks[0] with { Color = 0xff123456 };
        var second = new TimelineOnionSnapshot("blocks", tracks, .3);
        Assert.NotEqual(first.Fingerprint(0, 0, 100, 0, 128), second.Fingerprint(0, 0, 100, 0, 128));
        Assert.Equal(first.Fingerprint(1, 0, 100, 0, 128), second.Fingerprint(1, 0, 100, 0, 128));
        Assert.Equal(0, source.Reads);
        var empty = TimelineOnionRasterizer.Render(first, 0, 1000, 0, 1, 1);
        Assert.Equal(0, source.Reads); Assert.All(empty.Pixels, p => Assert.Equal(0, p));
    }
    [Theory]
    [InlineData(100)] [InlineData(200)] [InlineData(400)]
    public void ManyTracksUseBoundedVisibleBlocksAndDeviceBuffers(int count)
    {
        var watch = Stopwatch.StartNew();
        var source = new Source(Enumerable.Range(1, 1000).Select(i => new TimelineRenderItem(new(i),
            TimelineItemKind.DirectMidiNote, i, i + 2, i % 128, 0, 0, TimelineItemState.None)).ToArray());
        var snapshot = new TimelineOnionSnapshot("many", Enumerable.Range(1, count)
            .Select(i => new TimelineOnionTrack(new(i), 0xff607080, [new(source, 0, 2000, 0)])), .3)
            .FitVisibleCacheBudget(4);
        Assert.InRange(snapshot.Blocks.Count, 1, 4);
        Assert.Equal(count, snapshot.Blocks.Sum(b => b.Count));
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < snapshot.Blocks.Count; i++)
        {
            var tile = TimelineOnionRasterizer.Render(snapshot, i, 0, 0, .5, 1);
            Assert.Equal(512 * 128 * 4, tile.ByteSize);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.True(allocated < 8 * 1024 * 1024, $"{allocated} allocated bytes");
        output.WriteLine($"{count} tracks, {source.Reads:N0} visits: {watch.Elapsed.TotalMilliseconds:F1} ms; allocated {allocated / 1048576d:F2} MiB");
    }
    [Fact]
    public void CancellationIsObservedInsideDenseRange()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source(Enumerable.Range(1, 4000).Select(i => new TimelineRenderItem(new(i),
            TimelineItemKind.DirectMidiNote, 0, 300, 60, 0, 0, TimelineItemState.None)).ToArray())
        { AfterRead = n => { if (n == 1200) cancellation.Cancel(); } };
        var snapshot = new TimelineOnionSnapshot("cancel", [new(new(1), 0xffffffff, [new(source, 0, 400, 0)])], .3);
        Assert.Throws<OperationCanceledException>(() => TimelineOnionRasterizer.Render(snapshot, 0, 0, 0, 1, 1, cancellation.Token));
        Assert.InRange(source.Reads, 1200, 2200);
    }
    private static byte Alpha(TimelineRasterBuffer buffer, int x, int y) => buffer.Pixels[(y * buffer.Width + x) * 4 + 3];
    private sealed class AggregateSource : INonBlockingTimelineFingerprintSource, ITimelineRasterAggregateSource
    {
        public bool Used;
        public long Count => 18_000_000;
        public long MaximumEndTick => 1000;
        public ulong ContentFingerprint => 1;
        public bool TryAccumulateRasterColumns(TimelineRasterAggregateKind kind, TimelineRasterColumnProjection projection,
            int first, int last, Span<TimelineRasterColumnSummary> destination, out int work,
            CancellationToken cancellationToken = default)
        {
            Used = true; work = 1;
            Assert.Equal(TimelineRasterAggregateKind.PianoNotes, kind);
            Assert.Equal((100L, 150L), (projection.StartTick, projection.EndTick));
            Assert.True(projection.TryGetColumns(0, 1000, out int a, out int b));
            for (int i = a; i < b; i++) destination[i].Include(0, 1UL << 3, 0, 0, 1);
            return true;
        }
        public void VisitInto(long a, long b, int first, int last, Action<TimelineRenderItem> visit) => throw new InvalidOperationException("Dense hierarchy must not enumerate notes.");
        public void QueryInto(long a, long b, int first, int last, List<TimelineRenderItem> destination) => throw new NotSupportedException();
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => throw new NotSupportedException();
    }
    private sealed class Source(TimelineRenderItem[] items) : INonBlockingTimelineFingerprintSource
    {
        public int Reads; public Action<int>? AfterRead;
        public long Count => items.Length;
        public long MaximumEndTick => items.Select(i => i.EndTick).DefaultIfEmpty().Max();
        public ulong ContentFingerprint => 1;
        public void VisitInto(long a, long b, int first, int last, Action<TimelineRenderItem> visit)
        { foreach (var item in items) if (item.StartTick < b && (item.EndTick > a || item.StartTick == item.EndTick && item.StartTick >= a) && item.Lane >= first && item.Lane < last) { AfterRead?.Invoke(++Reads); visit(item); } }
        public void QueryInto(long a, long b, int first, int last, List<TimelineRenderItem> destination) => VisitInto(a, b, first, last, destination.Add);
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => items;
    }
}
