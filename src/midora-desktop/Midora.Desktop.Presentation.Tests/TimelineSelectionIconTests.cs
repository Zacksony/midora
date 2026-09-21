using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Xunit.Abstractions;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineSelectionIconTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void ToolbarGlyphPreservesWpfAntialiasingOnTheAliasedTimeline(double scale) => Sta(() =>
    {
        var cache = new TimelineSelectionIconCache(() => { });
        try
        {
            var icons = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/FluentSystemIcons.xaml", UriKind.Relative));
            var geometry = (Geometry)icons["Fluent.Pin16Regular"];
            var dpi = new DpiScale(scale, scale);
            var image = cache.Get("pin", geometry, Brushes.White, dpi);
            var cell = new Rect(4.37, 3.83, 27, 27);
            var destination = TimelineSelectionIconCache.Destination(image, cell, dpi);
            Assert.Equal(Math.Round(destination.X * scale), destination.X * scale, 8);
            Assert.Equal(Math.Round(destination.Y * scale), destination.Y * scale, 8);
            // Pixel snapping may displace the ink by at most half a device pixel,
            // but never changes its logical size or biases it toward an edge.
            Assert.InRange(Math.Abs((destination.X + 1 / scale + 7.5 - (cell.X + 13.5)) * scale), 0, .500001);
            Assert.InRange(Math.Abs((destination.Y + 1 / scale + 7.5 - (cell.Y + 13.5)) * scale), 0, .500001);

            var aliased = new IconHost(dc => dc.DrawImage(image, destination));
            var painted = Raster(aliased, scale);
            int partial = PartialAlpha(painted);
            Assert.True(partial > 10, $"{scale}: only {partial} antialiased pixels");
            Assert.Equal(PartialAlpha(image), partial); // No nearest-neighbour loss or second resampling.

            var direct = new IconHost(dc => TimelineSurface.DrawSelectionToolGeometry(dc, geometry, cell, Brushes.White));
            Assert.Equal(0, PartialAlpha(Raster(direct, scale))); // Reproduces the former rough path.
        }
        finally { cache.Clear(); }
    });

    [Fact]
    public void GlyphCacheReplacesDpiAndThemeEntriesAndReleasesResourceListeners() => Sta(() =>
    {
        int invalidations = 0;
        var cache = new TimelineSelectionIconCache(() => invalidations++);
        var brush = new SolidColorBrush(Colors.White);
        var geometry = Geometry.Parse("M1 1L15 5L5 15Z").Clone();
        var dpi = new DpiScale(1, 1);
        try
        {
            var first = cache.Get("icon", geometry, brush, dpi);
            Assert.Same(first, cache.Get("icon", geometry, brush, dpi));
            brush.Color = Colors.Red;
            Assert.True(invalidations > 0);
            var red = cache.Get("icon", geometry, brush, dpi);
            Assert.NotSame(first, red);
            geometry.Transform = new RotateTransform(15);
            Assert.NotSame(red, cache.Get("icon", geometry, brush, dpi));
            var large = cache.Get("icon", geometry, brush, new DpiScale(2, 2));
            Assert.Equal(32, large.PixelWidth);
            Assert.Equal(1, cache.Count);
            var replacement = brush.Clone();
            Assert.NotSame(large, cache.Get("icon", geometry, replacement, new DpiScale(2, 2)));
            int before = invalidations;
            brush.Color = Colors.Green;
            Assert.Equal(before, invalidations); // Detached when replaced.
            cache.Clear();
            replacement.Color = Colors.Blue; geometry.Transform = Transform.Identity;
            Assert.Equal(before, invalidations);
            Assert.Equal(0, cache.Count); Assert.Equal(0, cache.PixelBytes);
        }
        finally { cache.Clear(); }
    });

    [Fact]
    public void GlyphCacheIsBoundedAcrossDpiChangesAndWarmReadsDoNotRasterizeOrAllocate() => Sta(() =>
    {
        var cache = new TimelineSelectionIconCache(() => { });
        var geometry = Geometry.Parse("M1 1L15 5L5 15Z"); geometry.Freeze();
        var keys = Enumerable.Range(0, 18).Select(i => "icon-" + i).ToArray();
        try
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0, 1.0 })
            {
                foreach (var key in keys) cache.Get(key, geometry, Brushes.White, new(scale, scale));
                Assert.Equal(18, cache.Count);
                Assert.InRange(cache.PixelBytes, 1, 128 * 1024);
            }
            var dpi = new DpiScale(1, 1);
            var expected = keys.Select(key => cache.Get(key, geometry, Brushes.White, dpi)).ToArray();
            for (int repeat = 0; repeat < 1000; repeat++)
                foreach (var key in keys) cache.Get(key, geometry, Brushes.White, dpi);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp(); bool same = true;
            for (int repeat = 0; repeat < 1000; repeat++)
                for (int i = 0; i < keys.Length; i++)
                    same &= ReferenceEquals(expected[i], cache.Get(keys[i], geometry, Brushes.White, dpi));
            long ended = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var elapsed = Stopwatch.GetElapsedTime(started, ended);
            Assert.True(same); Assert.InRange(allocated, 0, 1024);
            Assert.True(elapsed < TimeSpan.FromSeconds(1));
            output.WriteLine($"18,000 warm glyph reads: {elapsed.TotalMilliseconds:F2} ms, {allocated} bytes; pixels: {cache.PixelBytes} bytes");
            cache.Get("extra", geometry, Brushes.White, dpi);
            Assert.InRange(cache.Count, 1, TimelineSelectionIconCache.MaximumEntries);
        }
        finally { cache.Clear(); }
    });

    private static RenderTargetBitmap Raster(Visual visual, double dpi)
    {
        if (visual is FrameworkElement element)
        { element.Measure(new Size(40, 40)); element.Arrange(new Rect(0, 0, 40, 40)); element.UpdateLayout(); }
        var bitmap = new RenderTargetBitmap((int)(40 * dpi), (int)(40 * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        var parent = new ContainerVisual(); parent.Children.Add(visual);
        try { bitmap.Render(parent); } finally { parent.Children.Remove(visual); }
        return bitmap;
    }

    // UIElement implements the RenderOptions property callbacks used by
    // TimelineSurface. Bare DrawingVisual does not, so it is not an aliasing oracle.
    private sealed class IconHost : FrameworkElement
    {
        private readonly Action<DrawingContext> _draw;
        internal IconHost(Action<DrawingContext> draw)
        {
            _draw = draw;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }
        protected override void OnRender(DrawingContext dc) => _draw(dc);
    }

    private static int PartialAlpha(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int count = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] is > 0 and < 255) count++;
        return count;
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
