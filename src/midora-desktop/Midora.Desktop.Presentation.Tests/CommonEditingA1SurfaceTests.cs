using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class CommonEditingA1SurfaceTests
{
    [Fact]
    public void PitchBendRulerUsesTheActualNeutralTickWithoutChangingOtherValueAxes()
    {
        var lower = TimelineValueAxisTick.At(-8192, 8191, 0, 1, 0, true);
        var neutral = TimelineValueAxisTick.At(-8192, 8191, 0, 1, .5, true);
        var upper = TimelineValueAxisTick.At(-8192, 8191, 0, 1, 1, true);
        Assert.Equal(new(0, -8192), lower);
        Assert.Equal(new(8192d / 16383, 0), neutral);
        Assert.Equal(new(1, 8191), upper);
        Assert.Equal(0, -8192 + neutral.Normalized * 16383, 9);
        var zoomed = TimelineValueAxisTick.At(-8192, 8191, .25, .75, .5, true);
        Assert.Equal(neutral, zoomed);
        Assert.Equal(new(.5, -.5), TimelineValueAxisTick.At(-8192, 8191, 0, .5, 1, true)); // zero is outside viewport
        Assert.Equal(new(.5, -.5), TimelineValueAxisTick.At(-8192, 8191, 0, 1, .5, false)); // continuous axis
        Assert.Equal(new(.5, 63.5), TimelineValueAxisTick.At(0, 127, 0, 1, .5, true));
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 4, false)]
    [InlineData(true, 1, true)]
    public void SubVoiceCreationSamplesAcrossVisualTemplateEndWithoutChangingOtherHosts(bool extend, long step, bool reverse)
    {
        OnSta(() =>
        {
            TimelineSurface surface = new() { SurfaceMode = TimelineSurfaceMode.EventLanes, RangeStartTick = 0,
                RangeEndTick = 48, CanExtendEventCreationRange = extend, OperationStepTicks = step, TickSpan = 100 };
            surface.Measure(new Size(452, 280)); surface.Arrange(new Rect(0, 0, 452, 280));
            try
            {
                var trace = (List<Point>)Field(surface, "_eventPointTracePoints")!;
                // 52 header, 400 content pixels: tick 40 -> x 212, tick 56 -> x 276.
                trace.Add(new(reverse ? 276 : 212, 100)); trace.Add(new(reverse ? 212 : 276, 100));
                var viewport = new TimelineViewport(0, 100, 0, 1, 400, 256, 128);
                Invoke(surface, "BuildEventPointEditsFromTrace", viewport);
                var sampled = (Dictionary<long, double>)Field(surface, "_eventPointEdits")!;
                Assert.Equal(extend ? 56 : 48 - step, sampled.Keys.Max());
                Assert.Equal(48, surface.RangeEndTick); // visual range is not mutated by preview
                Assert.All(sampled.Keys, tick => Assert.Equal(0, tick % step));
            }
            finally { surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        });
    }

    [Theory]
    [InlineData(TimelineItemKind.DirectMidiEvent, 0, 16383, true)]
    [InlineData(TimelineItemKind.DirectMidiEvent, -8192, 8191, true)] // signed display, same 14-bit delta
    [InlineData(TimelineItemKind.LogicalParameterPoint, -8192, 8191, true)] // SubVoice PB projection
    [InlineData(TimelineItemKind.LogicalParameterPoint, 0, 127, true)]
    [InlineData(TimelineItemKind.LogicalParameterPoint, -3, 9, false)]
    [InlineData(TimelineItemKind.LogicalParameterPoint, 0, 127, true, false)]
    public void MouseUpPublishesTheExactSaturatedPreviewDelta(TimelineItemKind kind, double minimum, double maximum, bool integral, bool canDrag = true)
    {
        OnSta(() =>
        {
            double range = maximum - minimum;
            double low = integral ? 10 / range : .1, high = integral ? 1 - 7 / range : .9;
            var item = new TimelineRenderItem(new(1), kind, 100, 101, 0, low, 0, TimelineItemState.None);
            TimelineSurface surface = new() { SurfaceMode = TimelineSurfaceMode.EventLanes,
                Snapshot = new(1, "a1-value-delta", [item]), TickSpan = 1000,
                ValueAxisMinimum = minimum, ValueAxisMaximum = maximum, ValueAxisIntegral = integral, CanDragEventValue = canDrag };
            surface.Measure(new Size(452, 280)); surface.Arrange(new Rect(0, 0, 452, 280));
            try
            {
                Set(surface, "_dragItem", item); Set(surface, "_dragKind", TimelineItemEditKind.Move);
                Set(surface, "_dragOrigin", new Point(100, 260)); Set(surface, "_dragActivated", true);
                Set(surface, "_hoverPoint", new Point(100, -100));
                Set(surface, "_dragPreviewMinimumValue", low); Set(surface, "_dragPreviewMaximumValue", high);
                Set(surface, "_replayingConductorPosition", new Point(100, -100));
                object preview = Invoke(surface, "GetDragPreviewTransform", item)!;
                double previewDelta = (double)preview.GetType().GetProperty("ValueDelta")!.GetValue(preview)!;
                Assert.Equal(canDrag ? 1 - high : 0, previewDelta, 10);
                TimelineItemEditEventArgs? result = null;
                surface.ItemEditCompleted += (_, args) => result = args;
                Invoke(surface, "OnMouseUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseUpEvent });
                Assert.NotNull(result); Assert.Equal(previewDelta, result.ValueDelta, 10);
            }
            finally { surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        });
    }

    private static object? Field(object o, string name) => typeof(TimelineSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
    private static void Set(object o, string name, object? value) => typeof(TimelineSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);
    private static object? Invoke(object o, string name, params object[] args)
    {
        try { return typeof(TimelineSurface).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args); }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
