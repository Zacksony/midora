using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineSemanticOverlayTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void AllPanelsUseTheSameDevicePixelAlignedBoundaries(double dpi)
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            {
                StartTick = 37, TickSpan = 1000, PreRollTicks = 193,
                LoopStartTick = 241, LoopEndTick = 813
            };
            var piano = overlay.GetLayout(853, 400, dpi, dpi);
            var velocity = overlay.GetLayout(853, 160, dpi, dpi);
            var events = overlay.GetLayout(853, 233, dpi, dpi);
            Assert.Equal(piano.LoopStartX, velocity.LoopStartX);
            Assert.Equal(piano.LoopEndX, events.LoopEndX);
            Assert.Equal(piano.PreRollShade.Width, events.PreRollShade.Width);
            foreach (double x in new[] { piano.LoopStartX!.Value, piano.LoopEndX!.Value, piano.PreRollEndX!.Value })
                Assert.Equal(Math.Round(x * dpi), x * dpi, 9);
            Assert.Equal(24, piano.Content.Top);
            Assert.Equal(376, piano.Content.Height);
            Assert.Equal(136, velocity.Content.Height);
            Assert.Equal(209, events.Content.Height);
            Assert.Equal(piano.LoopStartX, piano.LoopBand.Left);
            Assert.Equal(piano.LoopEndX, piano.LoopBand.Right);
        });
    }

    [Fact]
    public void SingleOrInvalidLoopEndpointsNeverCreateAFalseRange()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new() { TickSpan = 100, LoopStartTick = 20 };
            var startOnly = overlay.GetLayout(252, 100, 1, 1);
            Assert.NotNull(startOnly.LoopStartX);
            Assert.Null(startOnly.LoopEndX);
            Assert.True(startOnly.LoopBand.IsEmpty);
            overlay.LoopStartTick = null;
            overlay.LoopEndTick = 80;
            var endOnly = overlay.GetLayout(252, 100, 1, 1);
            Assert.Null(endOnly.LoopStartX);
            Assert.NotNull(endOnly.LoopEndX);
            Assert.True(endOnly.LoopBand.IsEmpty);
            overlay.LoopStartTick = 90;
            Assert.True(overlay.GetLayout(252, 100, 1, 1).LoopBand.IsEmpty);
        });
    }

    [Fact]
    public void ShadeAndLoopRangeAreClippedButEndpointsUseHalfOpenVisibility()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            { StartTick = 100, TickSpan = 100, PreRollTicks = 140, LoopStartTick = 40, LoopEndTick = 200 };
            var layout = overlay.GetLayout(252, 124, 1, 1);
            Assert.Equal(new Rect(52, 24, 80, 100), layout.PreRollShade);
            Assert.Equal(200, layout.LoopBand.Width);
            Assert.Null(layout.LoopStartX);
            Assert.Null(layout.LoopEndX);
            overlay.StartTick = 140;
            Assert.True(overlay.GetLayout(252, 124, 1, 1).PreRollShade.IsEmpty);
            overlay.PreRollTicks = 500;
            Assert.Equal(200, overlay.GetLayout(252, 124, 1, 1).PreRollShade.Width);
        });
    }

    [Fact]
    public void ExtremeTicksAndTransientLayoutInputsAreSafe()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            { StartTick = long.MaxValue - 100, TickSpan = long.MaxValue, LoopStartTick = long.MaxValue - 50 };
            Assert.Equal(152, overlay.GetLayout(252, 124, 2, 2).LoopStartX);
            overlay.StartTick = long.MaxValue;
            Assert.True(overlay.GetLayout(252, 124, 1, 1).Content.IsEmpty);
            overlay.StartTick = 0;
            Assert.True(overlay.GetLayout(0, 0, 1, 1).Content.IsEmpty);
            Assert.True(overlay.GetLayout(252, 124, double.NaN, 1).Content.IsEmpty);
            overlay.TickSpan = 0;
            Assert.True(overlay.GetLayout(252, 124, 1, 1).Content.IsEmpty);
        });
    }

    [Fact]
    public void DecorationCannotInterceptInputOrInvalidateTheTimelineModel()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new() { IsHitTestVisible = true, Focusable = true };
            Assert.False(overlay.IsHitTestVisible);
            Assert.False(overlay.Focusable);
            foreach (DependencyProperty property in new[]
                { TimelineSemanticOverlay.PreRollTicksProperty, TimelineSemanticOverlay.LoopStartTickProperty,
                    TimelineSemanticOverlay.LoopEndTickProperty, TimelineSemanticOverlay.TemplateEndTickProperty })
            {
                var metadata = Assert.IsType<FrameworkPropertyMetadata>(property.GetMetadata(typeof(TimelineSemanticOverlay)));
                Assert.True(metadata.AffectsRender);
                Assert.False(metadata.AffectsMeasure);
                Assert.False(metadata.AffectsArrange);
                Assert.False(metadata.AffectsParentArrange);
                Assert.False(metadata.AffectsParentMeasure);
            }
            Assert.Null(typeof(TimelineSemanticOverlay).GetProperty("Snapshot"));
        });
    }

    [Fact]
    public void ActualWpfDrawingShowsOnePixelLinesAcrossThePanelAndNoHeaderTint()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            { TickSpan = 100, PreRollTicks = 10, LoopStartTick = 20, LoopEndTick = 80, ShowRulerLabels = false };
            overlay.Resources["Brush.Warning"] = Brushes.Yellow;
            overlay.Measure(new Size(252, 124));
            overlay.Arrange(new Rect(0, 0, 252, 124));
            RenderTargetBitmap bitmap = new(252, 124, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);
            byte[] pixels = new byte[252 * 124 * 4];
            bitmap.CopyPixels(pixels, 252 * 4, 0);
            Assert.Equal(0, Alpha(40, 80));
            Assert.Equal(0, Alpha(100, 10));
            Assert.InRange(Alpha(55, 80), (byte)180, (byte)185);
            Assert.Equal(255, Alpha(92, 24));
            Assert.Equal(255, Alpha(92, 123));
            Assert.Equal(0, Alpha(91, 80));
            Assert.Equal(0, Alpha(93, 80));
            Assert.Equal(255, Alpha(212, 80));
            byte Alpha(int x, int y) => pixels[(y * 252 + x) * 4 + 3];
        });
    }

    [Fact]
    public void StandaloneWpfOverlayCanRenderItsTickLabelsWithoutApplicationStartup()
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            { TickSpan = 100, PreRollTicks = 10, LoopStartTick = 20, LoopEndTick = 80 };
            overlay.Measure(new Size(852, 124));
            overlay.Arrange(new Rect(0, 0, 852, 124));
            RenderTargetBitmap bitmap = new(852, 124, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);
            byte[] pixels = new byte[852 * 124 * 4];
            bitmap.CopyPixels(pixels, 852 * 4, 0);
            Assert.Contains(Enumerable.Range(0, 24 * 852), pixel => pixels[pixel * 4 + 3] != 0);
        });
    }

    [Theory]
    [InlineData(100, 10, 40, 80)]
    [InlineData(1000, 20, 15, 21)]
    [InlineData(10000, 100, 100, 101)]
    public void TemplateAndPreRollKeepTheirColorsWithoutRecoloringSeparateOrMergedLoopLabels(
        long span, long preRoll, long loopStart, long loopEnd)
    {
        OnSta(() =>
        {
            TimelineSemanticOverlay overlay = new()
            { TickSpan = span, PreRollTicks = preRoll, LoopStartTick = loopStart, LoopEndTick = loopEnd, TemplateEndTick = loopEnd };
            SolidColorBrush purple = new(Color.FromRgb(199, 138, 255));
            overlay.Resources["Brush.Timeline.PreRoll"] = purple;
            overlay.Resources["Brush.Warning"] = Brushes.Yellow;
            overlay.Resources["Brush.Info"] = Brushes.CornflowerBlue;
            overlay.Measure(new Size(852, 124));
            overlay.Arrange(new Rect(0, 0, 852, 124));
            RenderTargetBitmap bitmap = new(852, 124, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);

            GlyphRunDrawing[] runs = Glyphs(VisualTreeHelper.GetDrawing(overlay)).ToArray();
            Assert.Equal($"Pre-Roll {preRoll}", Text(purple.Color));
            Assert.Equal($"Template {loopEnd}", Text(Colors.CornflowerBlue));
            string loopText = Text(Colors.Yellow);
            Assert.Contains($"Start {loopStart}", loopText);
            Assert.Contains($"End {loopEnd}", loopText);
            Assert.DoesNotContain("Pre-Roll", loopText);
            Assert.DoesNotContain("Template", loopText);

            string Text(Color color) => string.Concat(runs
                .Where(run => run.ForegroundBrush is SolidColorBrush brush && brush.Color == color)
                .Select(run => new string(run.GlyphRun.Characters.ToArray())));
        });

        static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph;
            if (drawing is DrawingGroup group)
                foreach (Drawing child in group.Children)
                    foreach (GlyphRunDrawing nested in Glyphs(child)) yield return nested;
        }
    }

    [Fact]
    public void TemplateCaptionTracksValueAndViewportWithoutAddingInputOrLowerRulerLabels() => OnSta(() =>
    {
        TimelineSemanticOverlay overlay = new() { TickSpan = 192, TemplateEndTick = 192 };
        overlay.Resources["Brush.Info"] = Brushes.CornflowerBlue;
        overlay.Measure(new Size(852, 124)); overlay.Arrange(new Rect(0, 0, 852, 124));
        Assert.Equal(852, overlay.GetLayout(852, 124, 1, 1).TemplateEndX);
        AssertCaption("Template 192"); // The cap is still visible at the exact right viewport edge.
        overlay.TemplateEndTick = 96;
        AssertCaption("Template 96");
        overlay.TemplateEndTick = 193;
        AssertCaption("");
        overlay.TemplateEndTick = 192; overlay.StartTick = 193;
        AssertCaption("");
        overlay.StartTick = 0; overlay.ShowRulerLabels = false;
        AssertCaption("");
        Assert.False(overlay.IsHitTestVisible);

        void AssertCaption(string expected)
        {
            overlay.UpdateLayout();
            var bitmap = new RenderTargetBitmap(852, 124, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);
            Assert.Equal(expected, string.Concat(Glyphs(VisualTreeHelper.GetDrawing(overlay))
                .Where(run => run.ForegroundBrush is SolidColorBrush brush && brush.Color == Colors.CornflowerBlue)
                .Select(run => new string(run.GlyphRun.Characters.ToArray()))));
        }
        static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph;
            if (drawing is DrawingGroup group)
                foreach (Drawing child in group.Children)
                    foreach (GlyphRunDrawing nested in Glyphs(child)) yield return nested;
        }
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
