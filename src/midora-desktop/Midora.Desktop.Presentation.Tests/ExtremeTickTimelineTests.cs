using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Reflection;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Common;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop.Presentation.Tests;

public sealed class ExtremeTickTimelineTests
{
    [Fact]
    public void ViewportKeepsRelativePrecisionAtInt64End()
    {
        TimelineViewport viewport = new(long.MaxValue - 1000, long.MaxValue, 0, 128, 1000, 400, 15);
        for (int x = 0; x <= 1000; x++)
        {
            Assert.Equal(long.MaxValue - 1000 + x, viewport.XToTick(x));
            Assert.Equal(long.MaxValue - 1000 + Math.Min(x, 999), viewport.XToContainingTick(x));
        }
    }

    [Theory]
    [InlineData(TimelineSurfaceMode.General, false)]
    [InlineData(TimelineSurfaceMode.Arrangement, true)]
    [InlineData(TimelineSurfaceMode.PianoRoll, true)]
    [InlineData(TimelineSurfaceMode.EventLanes, true)]
    [InlineData(TimelineSurfaceMode.Velocity, true)]
    [InlineData(TimelineSurfaceMode.Conductor, true)]
    public void ActualRenderAtLastPartialBarAndReturnToOrdinaryRange(TimelineSurfaceMode mode, bool bars)
    {
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = mode, DisplayGridUsesBars = bars, GridStepTicks = 192,
                TimeSignatureMap = new ProjectTimeSignatureMap(new MidoraProject(32767)),
                StartTick = long.MaxValue - 1000, TickSpan = 1000, LaneHeight = 15,
                CanEdit = false
            };
            Render(surface);
            surface.StartTick = 0;
            Render(surface);
            surface.StartTick = long.MaxValue - 400;
            surface.TickSpan = 600;
            Render(surface);
            surface.StartTick = 0;
            Render(surface);
        });
    }

    private static void Render(TimelineSurface surface)
    {
        surface.Measure(new Size(852, 400));
        surface.Arrange(new Rect(0, 0, 852, 400));
        surface.UpdateLayout();
        RenderTargetBitmap bitmap = new(852, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
    }

    [Theory]
    [InlineData(TimelineSurfaceMode.PianoRoll, TimelineItemKind.DirectMidiNote)]
    [InlineData(TimelineSurfaceMode.PianoRoll, TimelineItemKind.LogicalNote)]
    [InlineData(TimelineSurfaceMode.PianoRoll, TimelineItemKind.TemplateNote)]
    [InlineData(TimelineSurfaceMode.EventLanes, TimelineItemKind.DirectMidiEvent)]
    [InlineData(TimelineSurfaceMode.EventLanes, TimelineItemKind.TemplateEvent)]
    [InlineData(TimelineSurfaceMode.Velocity, TimelineItemKind.Velocity)]
    public void NonemptyContentAndHoverAtInt64End(TimelineSurfaceMode mode, TimelineItemKind kind)
    {
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = mode, DisplayGridUsesBars = true, OperationStepTicks = 192,
                TimeSignatureMap = new ProjectTimeSignatureMap(new MidoraProject(32767)),
                StartTick = long.MaxValue - 1000, TickSpan = 1000, LaneHeight = 15,
                Snapshot = new TimelineRenderSnapshot(1, "extreme", [new(new MidoraId(1), kind,
                    long.MaxValue - 500, long.MaxValue - 1, 0, .5, 0, TimelineItemState.None)])
            };
            typeof(TimelineSurface).GetField("_hoverPoint", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.SetValue(surface, new Point(800, 60));
            Render(surface);
            surface.TickSpan = 16;
            surface.StartTick = long.MaxValue - 16;
            Render(surface);
            surface.TickSpan = long.MaxValue;
            surface.StartTick = 0;
            Render(surface);
            surface.TickSpan = 3072;
            Render(surface);
        });
    }

    [Fact]
    public void TraceRetainsAdjacentTicksAndValuesAtInt64End()
    {
        Dictionary<long, double> values = [];
        TimelineValueTracePoint[] trace = [new(long.MaxValue - 10, 0), new(long.MaxValue, 1)];
        TimelineValueTraceSampler.SampleInto(trace, values, 1, false, null);
        Assert.Equal(11, values.Count);
        Assert.Equal(.5, values[long.MaxValue - 5]);
        Assert.Equal(1, values[long.MaxValue]);
        Assert.All(TimelineValueTraceSampler.EnumerateSamples(trace, 1, false, null),
            point => Assert.Equal(values[point.Tick], point.NormalizedValue));
    }

    [Fact]
    public void OutOfRangeCreationCancelsWithoutPublishingAndNextOrdinaryEditWorks()
    {
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll, ToolMode = TimelineToolMode.Draw,
                StartTick = long.MaxValue - 1000, TickSpan = 1000,
                OperationStepTicks = 1, DefaultCreationLengthTicks = 768
            };
            Render(surface);
            int failures = 0, published = 0;
            surface.TickRangeExceeded += (_, _) => failures++;
            surface.NotePlacementCompleted += (_, _) => published++;
            Set(surface, "_replayingConductorPosition", new Point(452, 80));
            Set(surface, "_replayingConductorModifiers", ModifierKeys.None);
            Invoke(surface, "OnMouseDown", Button(true));
            Assert.Equal(1, failures);
            Assert.Null(Field(surface, "_notePlacementStartTick"));
            Invoke(surface, "OnMouseUp", Button(false));
            Assert.Equal(0, published);
            surface.StartTick = 0;
            Render(surface);
            Invoke(surface, "OnMouseDown", Button(true));
            Invoke(surface, "OnMouseUp", Button(false));
            Assert.Equal(1, published);
            Assert.Equal(1, failures);
        });
    }

    [Fact]
    public void GridIterationIsBoundedAndOnlyEmitsRealInDomainGridPoints()
    {
        ProjectTimeSignatureMap map = new(1, [new ProjectTimeSignaturePoint(new(1), 0, 1, 4)]);
        List<TimelineGridLine> lines = [];
        TimelineGridPresentation.BuildBarGridLines(0, long.MaxValue, map, lines, long.MaxValue / 1024);
        Assert.InRange(lines.Count, 1, 1025);
        Assert.All(lines, x => Assert.Equal(0, x.Tick % (long.MaxValue / 1024)));
        TimelineGridPresentation.BuildBarGridLines(0, 2000, map, lines, 1, long.MaxValue - 1000);
        Assert.Equal(1001, lines.Count);
        Assert.Equal(1000, lines[^1].Tick);
        TimelineGridPresentation.BuildBarGridLines(long.MaxValue - 1000, long.MaxValue,
            map, lines, 1, long.MaxValue);
        Assert.Empty(lines);
        TimelineGridPresentation.BuildFixedGridLines(0, long.MaxValue, 1, lines, long.MaxValue / 1024);
        Assert.InRange(lines.Count, 1, 1025);
        TimelineGridPresentation.BuildFixedGridLines(long.MaxValue - 2, long.MaxValue, 1, lines);
        Assert.Equal(new long[] { long.MaxValue - 2, long.MaxValue - 1 }, lines.Select(x => x.Tick));
    }

    [Fact]
    public void OrdinaryBarAndBeatCoordinatesAgreeWithExactProjectMap()
    {
        ProjectTimeSignatureMap map = new(480, [new ProjectTimeSignaturePoint(new(1), 0, 4, 4),
            new(new(2), 1000, 3, 8), new(new(3), 2440, 7, 16)]);
        foreach (long offset in new long[] { -3000, 0, 999, 10000 })
        {
            List<TimelineGridLine> actual = [];
            TimelineGridPresentation.BuildBarGridLines(0, 5000, map, actual, 1, offset);
            List<TimelineGridLine> expected = [];
            for (long tick = 0; tick < 5000; tick++)
            {
                long projectTick = tick + offset;
                if (projectTick < 0 || map.GetBeatGridTickAtOrBefore(projectTick) != projectTick) continue;
                expected.Add(new(tick, map.GetBarBounds(projectTick).StartTick == projectTick
                    ? TimelineGridLineKind.Bar : TimelineGridLineKind.Beat));
            }
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void NavigationAndInclusiveTileIterationNeverWrap()
    {
        Assert.Equal(long.MaxValue - 1, TimelineTickMath.Pan(long.MaxValue - 2, 100));
        Assert.Equal(0, TimelineTickMath.Pan(5, long.MinValue));
        Assert.Equal(1L << 50, TimelineTickMath.ScaleSpan(long.MaxValue, 1.25));
        Assert.Equal(long.MaxValue, TimelineTickMath.ProjectFraction(0, long.MaxValue, 1));
        List<long> tiles = [];
        foreach (long index in TimelineTickMath.InclusiveIndices(long.MaxValue - 1, long.MaxValue)) tiles.Add(index);
        Assert.Equal(new[] { long.MaxValue - 1, long.MaxValue }, tiles);
    }

    [Theory]
    [InlineData(TimelineItemEditKind.Move)]
    [InlineData(TimelineItemEditKind.ResizeEnd)]
    public void DragPastInt64EndCancelsBeforeRenderOrCommandPublication(TimelineItemEditKind kind)
    {
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll, ToolMode = TimelineToolMode.Draw,
                StartTick = long.MaxValue - 1000, TickSpan = 1000, OperationStepTicks = 1
            };
            Render(surface);
            int failures = 0, commands = 0;
            surface.TickRangeExceeded += (_, _) => failures++;
            surface.ItemEditCompleted += (_, _) => commands++;
            Set(surface, "_dragItem", new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote,
                long.MaxValue - 800, long.MaxValue - 1, 0, .5, 0, TimelineItemState.None));
            Set(surface, "_dragKind", kind);
            Set(surface, "_dragOriginTick", long.MaxValue - 600);
            Set(surface, "_replayingConductorPosition", new Point(452, 80));
            Set(surface, "_replayingConductorModifiers", ModifierKeys.None);
            Set(surface, "_replayingConductorMove", true);
            Invoke(surface, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Equal(1, failures);
            Assert.Null(Field(surface, "_dragItem"));
            Render(surface);
            Invoke(surface, "OnMouseUp", Button(false));
            Assert.Equal(0, commands);
        });
    }

    [Fact]
    public void SnappedZeroDeltaAndPlainRulerClickAtMaximumRemainValid()
    {
        Sta(() =>
        {
            TimelineSurface surface = new() { OperationStepTicks = 192 };
            Set(surface, "_dragKind", TimelineItemEditKind.ResizeEnd);
            Set(surface, "_dragOriginTick", long.MaxValue - 20);
            Set(surface, "_dragCurrentTick", long.MaxValue - 19);
            _ = Invoke(surface, "GetDragPreviewTransform", new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote,
                long.MaxValue - 10, long.MaxValue, 0, .5, 0, TimelineItemState.None));
            surface.OperationStepTicks = 1;
            Set(surface, "_rulerDragOrigin", new Point(0, 0));
            Set(surface, "_rulerDragStartTick", long.MaxValue);
            Set(surface, "_rulerDragCurrentTick", long.MaxValue);
            long? requested = null;
            surface.RulerClicked += (_, e) => requested = e.Tick;
            Invoke(surface, "OnMouseUp", Button(false));
            Assert.Equal(long.MaxValue, requested);
        });
    }

    [Fact]
    public void InvalidTraceSnapDoesNotPartiallyReplaceDraft()
    {
        Dictionary<long, double> values = new() { [42] = .5 };
        Assert.Throws<OverflowException>(() => TimelineValueTraceSampler.SampleInto(
            [new(0, 0), new(long.MaxValue, 1)], values, 192, false, null));
        Assert.Equal(.5, Assert.Single(values).Value);
    }

    [Fact]
    public void HugeArrangementSegmentFallbackAndDetailWidthStayFinite()
    {
        Assert.Equal(long.MaxValue, TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(long.MaxValue, 1));
        int lod = TimelineSegmentPreviewRasterizer.SelectWarmupLod(long.MaxValue, 1);
        long width = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(long.MaxValue, 1, lod);
        Assert.InRange(width, 1, TimelineSegmentPreviewRasterizer.FixedPreviewTileSize
            * TimelineSegmentPreviewRasterizer.MaximumFallbackTilesPerSegment);
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.Arrangement, StartTick = long.MaxValue - 1000,
                TickSpan = 1000, PreviewTicksPerQuarterNote = 1,
                Snapshot = new(1, "huge-segment", [new(new(2), TimelineItemKind.Segment, 0,
                    long.MaxValue, 0, 0, 0, TimelineItemState.None)],
                    segmentPreviews: new Dictionary<MidoraId, TimelineSegmentPreview>
                    { [new(2)] = new(new(2), [new(0, 1, 60)]) })
            };
            Render(surface);
            surface.TickSpan = 16;
            Render(surface);
            surface.StartTick = 0;
            surface.TickSpan = long.MaxValue;
            Render(surface);
        });
    }

    [Theory]
    [InlineData(TimelineItemKind.DirectMidiNote)]
    [InlineData(TimelineItemKind.LogicalNote)]
    [InlineData(TimelineItemKind.TemplateNote)]
    public void LongNotesAreClippedByTheRasterNotByAnInt32PixelCast(TimelineItemKind kind)
    {
        TimelineRenderSnapshot snapshot = new(1, "long-note", [new(new(1), kind, 0,
            long.MaxValue, 0, .5, 0, TimelineItemState.None)]);
        foreach (double scale in new[] { .25, 1.0, 32.0 })
        {
            TimelineRasterBuffer result = TimelinePianoTileRasterizer.Rasterize(snapshot,
                scale, 15.0, 2, 0, Colors.Gray, Colors.Yellow);
            Assert.Contains(result.Pixels, x => x != 0);
        }
    }

    private static MouseButtonEventArgs Button(bool down) => new(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent };
    private static object? Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Extreme Tick rendering did not terminate.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
