using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineStepSignalSurfaceTests
{
    [Fact]
    public void TogglingLinesCancelsOnlyAuxiliaryWorkAndCompletionUsesOwnerAndTargetIdentity()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = new TimelineStepSignalSource("voice/CC10", (_, _) => 1, (_, _, _) => []);
                TimelineRenderSnapshot snapshot = new(1, "reused-surface", [], stepSignalSource: source);
                var surface = new TimelineSurface { Snapshot = snapshot, SurfaceMode = TimelineSurfaceMode.EventLanes };
                try
                {
                    var points = (CancellationToken)Invoke(surface, "GetRasterRequestToken", TimelineRasterLayer.EventPoints)!;
                    var lines = (CancellationToken)Invoke(surface, "GetRasterRequestToken", TimelineRasterLayer.EventSignal)!;
                    var key = new TimelineRasterCacheKey(TimelineRasterLayer.EventSignal,
                        "reused-surface:step:voice/CC10", 1, 1, 1, 0, 0, 0, 0, 0, 1024, 1024);
                    Assert.True((bool)Invoke(surface, "MatchesRasterProjection", snapshot, key)!);
                    Assert.False((bool)Invoke(surface, "MatchesRasterProjection", snapshot, key with { ProjectionKey = "reused-surface:step:voice/CC11" })!);
                    surface.ShowStepSignal = false;
                    Assert.True(lines.IsCancellationRequested); Assert.False(points.IsCancellationRequested);
                    Assert.False((bool)Invoke(surface, "MatchesRasterProjection", snapshot, key)!);
                    surface.ShowStepSignal = true;
                    var fresh = (CancellationToken)Invoke(surface, "GetRasterRequestToken", TimelineRasterLayer.EventSignal)!;
                    Assert.False(fresh.IsCancellationRequested);
                    Invoke(surface, "RestartPendingRasterWork");
                    Assert.True(fresh.IsCancellationRequested); Assert.True(points.IsCancellationRequested);
                }
                finally { surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
            }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static object? Invoke(object instance, string name, params object[] arguments) =>
        typeof(TimelineSurface).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
}
