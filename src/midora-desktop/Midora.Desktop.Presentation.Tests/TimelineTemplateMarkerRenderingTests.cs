using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineTemplateMarkerRenderingTests
{
    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void RoundedCapsKeepAntialiasedPixelsAcrossFractionalPositions(double scale) => Sta(() =>
    {
        var cache = new TimelineTemplateMarkerCapCache(() => { });
        var dpi = new DpiScale(scale, scale);
        try
        {
            foreach (var marker in Enum.GetValues<TemplateTimelineMarker>())
            {
                var image = cache.Get(marker, new Size(8, 10), Brushes.White, dpi);
                int[] expected = AlphaHistogram(image);
                Assert.True(expected.Skip(1).Take(254).Sum() > 4, "Round corners must contain partial coverage.");
                foreach (double phase in new[] { 0d, .1, .37, .66, .99 })
                {
                    var cap = new Rect(6 + phase, 7 + phase, 8, 10);
                    Rect destination = TimelineTemplateMarkerCapCache.Destination(image, cap, dpi);
                    Assert.Equal(Math.Round(destination.X * scale), destination.X * scale, 8);
                    Assert.Equal(Math.Round(destination.Y * scale), destination.Y * scale, 8);
                    Assert.InRange(Math.Abs((destination.X + 1 / scale - cap.X) * scale), 0, .500001);
                    var host = new CapHost(dc => dc.DrawImage(image, destination));
                    int[] actual = AlphaHistogram(Raster(host, scale));
                    // Ignore transparent background area, compare every covered
                    // alpha bucket: no corners vanish when the cap moves.
                    Assert.Equal(expected.Skip(1), actual.Skip(1));
                }
            }
            var oldPath = new CapHost(dc => dc.DrawRoundedRectangle(Brushes.White, null,
                new Rect(6.37, 7.37, 8, 10), 2, 2));
            Assert.Equal(0, AlphaHistogram(Raster(oldPath, scale)).Skip(1).Take(254).Sum());
        }
        finally { cache.Clear(); }
    });

    [Fact]
    public void CapCacheIsBoundedAndReplacesDpiSizeAndBrushWithoutRetainingListeners() => Sta(() =>
    {
        int invalidations = 0;
        var cache = new TimelineTemplateMarkerCapCache(() => invalidations++);
        var brush = new SolidColorBrush(Colors.CornflowerBlue);
        var size = new Size(8, 10);
        try
        {
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2, 1 })
            {
                foreach (var marker in Enum.GetValues<TemplateTimelineMarker>())
                    cache.Get(marker, size, brush, new DpiScale(scale, scale));
                Assert.Equal(4, cache.Count);
                Assert.InRange(cache.PixelBytes, 1, 8 * 1024);
            }
            const TemplateTimelineMarker key = TemplateTimelineMarker.TemplateEnd;
            var dpi = new DpiScale(1, 1);
            var image = cache.Get(key, size, brush, dpi);
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool same = true;
            for (int i = 0; i < 10000; i++) same &= ReferenceEquals(image, cache.Get(key, size, brush, dpi));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(same);
            Assert.InRange(allocated, 0, 1024);
            brush.Color = Colors.Red;
            Assert.True(invalidations > 0);
            var recolored = cache.Get(key, size, brush, dpi);
            Assert.NotSame(image, recolored);
            Assert.NotSame(recolored, cache.Get(key, new Size(9, 10), brush, dpi));
            var replacement = brush.Clone();
            cache.Clear();
            cache.Get(key, size, replacement, dpi);
            int count = invalidations;
            brush.Color = Colors.Green;
            Assert.Equal(count, invalidations);
            cache.Clear();
            replacement.Color = Colors.Blue;
            Assert.Equal(count, invalidations);
            Assert.Equal(0, cache.Count); Assert.Equal(0, cache.PixelBytes);
        }
        finally { cache.Clear(); }
    });

    [Fact]
    public void RealSurfaceUsesTheCapCacheAndUnloadingReleasesItWithoutChangingHitBounds() => Sta(() =>
    {
        var surface = new TimelineSurface
        {
            SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = new(1, "template", []),
            TickSpan = 800, RangeEndTick = 400, MinimumTemplateLength = 1,
            TemplateLoopStartTick = 200, TemplateLoopEndTick = 400, TemplatePreRollTicks = 100,
            GridVisible = false
        };
        surface.Measure(new Size(852, 124)); surface.Arrange(new Rect(0, 0, 852, 124));
        var before = new TemplateMarkerHandle[4];
        Assert.Equal(4, surface.GetTemplateMarkerHandles(before));
        var bitmap = new RenderTargetBitmap(852, 124, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var cache = Assert.IsType<TimelineTemplateMarkerCapCache>(typeof(TimelineSurface)
            .GetField("_templateMarkerCaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        Assert.Equal(4, cache.Count);
        Assert.Equal(EdgeMode.Aliased, RenderOptions.GetEdgeMode(surface));
        var after = new TemplateMarkerHandle[4];
        Assert.Equal(4, surface.GetTemplateMarkerHandles(after));
        Assert.Equal(before, after);
        foreach (var handle in after)
            Assert.Equal(handle.Marker, surface.HitTemplateMarker(new Point(handle.Bounds.X + 5, handle.Bounds.Y + 5)));
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(0, cache.Count); Assert.Equal(0, cache.PixelBytes);
    });

    private sealed class CapHost(Action<DrawingContext> draw) : FrameworkElement
    {
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }
        protected override void OnRender(DrawingContext dc) => draw(dc);
    }

    private static RenderTargetBitmap Raster(FrameworkElement element, double scale)
    {
        element.Measure(new Size(40, 40)); element.Arrange(new Rect(0, 0, 40, 40)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(40 * scale), (int)(40 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var parent = new ContainerVisual(); parent.Children.Add(element);
        try { bitmap.Render(parent); } finally { parent.Children.Remove(element); }
        return bitmap;
    }

    private static int[] AlphaHistogram(BitmapSource image)
    {
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int[] histogram = new int[256];
        for (int i = 3; i < pixels.Length; i += 4) histogram[pixels[i]]++;
        return histogram;
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
