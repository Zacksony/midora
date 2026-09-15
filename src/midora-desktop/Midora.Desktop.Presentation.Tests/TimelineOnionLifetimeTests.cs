using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineOnionLifetimeTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("transparent")]
    [InlineData("empty")]
    [InlineData("unloaded")]
    public void UndrawnLayerDropsItsDerivedSnapshotsAndCanRenderAgain(string transition)
    {
        Sta(() =>
        {
            var snapshot = new TimelineOnionSnapshot(Guid.NewGuid().ToString("N"),
                [new(new(1), 0xff507080, [new(new Source(), 0, 100, 0)])], .5);
            var surface = new TimelineSurface
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                Snapshot = new(0, "empty", [], Enumerable.Range(0, 128).Select(i => i.ToString()).ToArray()),
                OnionSnapshot = snapshot, TickSpan = 100, FirstLane = 0, LaneHeight = 8
            };
            surface.Measure(new(564, 300)); surface.Arrange(new Rect(0, 0, 564, 300));
            Render(surface);
            Assert.Same(snapshot, Field(surface, "_onionSourceSnapshot"));
            Assert.NotNull(Field(surface, "_onionDrawSnapshot"));
            switch (transition)
            {
                case "disabled": surface.OnionSnapshot = null; break;
                case "transparent": surface.OnionSnapshot = new(snapshot.Identity, snapshot.Blocks.SelectMany(b => b), 0); break;
                case "empty": surface.OnionSnapshot = new(snapshot.Identity, [], .5); break;
                case "unloaded": surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
            }
            Assert.Null(Field(surface, "_onionSourceSnapshot"));
            Assert.Null(Field(surface, "_onionDrawSnapshot"));
            Assert.Equal(0, surface.OnionMissingTileCount);
            if (transition == "unloaded")
            {
                var canceled = Assert.IsType<CancellationTokenSource>(Field(surface, "_onionCancellation"));
                Assert.True(canceled.IsCancellationRequested);
                Assert.Equal(0, SubscriptionCount(surface));
                for (int index = 0; index < 10; index++)
                {
                    surface.InvalidateVisual(); Render(surface);
                    Assert.Same(canceled, Field(surface, "_onionCancellation"));
                    Assert.True(canceled.IsCancellationRequested);
                    Assert.Null(Field(surface, "_onionSourceSnapshot"));
                    Assert.Null(Field(surface, "_onionDrawSnapshot"));
                    Assert.Equal(0, surface.OnionMissingTileCount);
                    Assert.Equal(0, SubscriptionCount(surface));
                }
            }
            surface.OnionSnapshot = snapshot;
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            surface.InvalidateVisual(); Render(surface);
            Assert.Same(snapshot, Field(surface, "_onionSourceSnapshot"));
            Assert.NotNull(Field(surface, "_onionDrawSnapshot"));
            Assert.False(Assert.IsType<CancellationTokenSource>(Field(surface, "_onionCancellation")).IsCancellationRequested);
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            surface.OnionSnapshot = null;
        });
    }
    private static void Render(TimelineSurface surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(564, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
    }
    private static object? Field(TimelineSurface surface, string name) => typeof(TimelineSurface)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface);
    private static int SubscriptionCount(TimelineSurface surface)
    {
        long consumerId = Assert.IsType<long>(Field(surface, "_rasterConsumerId"));
        var cache = TimelineRasterCache.Shared;
        object gate = typeof(TimelineRasterCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        lock (gate)
        {
            var subscriptions = (IDictionary)typeof(TimelineRasterCache)
                .GetField("_subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
            return subscriptions.Keys.Cast<object>().Count(key =>
                (long)key.GetType().GetProperty("ConsumerId")!.GetValue(key)! == consumerId);
        }
    }
    private sealed class Source : INonBlockingTimelineFingerprintSource
    {
        public long Count => 1;
        public long MaximumEndTick => 100;
        public ulong ContentFingerprint => 1;
        public bool HasHitTestableItems => false;
        public void VisitInto(long start, long end, int first, int last, Action<TimelineRenderItem> visitor)
            => visitor(new(new(1), TimelineItemKind.LogicalNote, 1, 50, 1, 0, 0, TimelineItemState.HitTestDisabled));
        public void QueryInto(long start, long end, int first, int last, List<TimelineRenderItem> destination)
            => VisitInto(start, end, first, last, destination.Add);
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => throw new NotSupportedException();
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
