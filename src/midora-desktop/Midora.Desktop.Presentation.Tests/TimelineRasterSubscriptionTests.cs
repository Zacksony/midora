using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Xunit.Abstractions;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineRasterSubscriptionTests(ITestOutputHelper output)
{
    [Fact]
    public void TenThousandFramesKeepOnlyOneSubscriptionPerConsumerKeyAndGeneration()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using ManualResetEventSlim release = new(false);
            using CancellationTokenSource first = new();
            using CancellationTokenSource second = new();
            long firstId = TimelineRasterCache.CreateConsumerId();
            long secondId = TimelineRasterCache.CreateConsumerId();
            int computations = 0;
            int callbacks = 0;
            Func<CancellationToken, TimelineRasterBuffer> factory = token =>
            {
                Interlocked.Increment(ref computations);
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
                return EmptyRaster();
            };
            Stopwatch watch = Stopwatch.StartNew();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                for (int frame = 0; frame < 10_000; frame++)
                {
                    for (int tile = 0; tile < 3; tile++)
                    {
                        Assert.True(cache.Request(Key(tile), factory, Dispatcher.CurrentDispatcher,
                            () => callbacks++, first.Token, TimelineRasterRequestPriority.Visible, firstId));
                        Assert.True(cache.Request(Key(tile), factory, Dispatcher.CurrentDispatcher,
                            () => callbacks++, second.Token, TimelineRasterRequestPriority.Visible, secondId));
                    }
                }
                Assert.Equal(3, cache.InFlightCount);
                Assert.Equal(6, cache.ActiveSubscriptionCount);
                output.WriteLine($"10,000 frames / 60,000 requests: {watch.Elapsed.TotalMilliseconds:F2} ms; "
                    + $"UI allocated {GC.GetAllocatedBytesForCurrentThread() - allocated:N0} bytes; "
                    + $"computations={computations}, subscriptions={cache.ActiveSubscriptionCount}.");
                first.Cancel();
                Assert.Equal(3, cache.ActiveSubscriptionCount);
                Assert.Equal(3, cache.InFlightCount);
                release.Set();
                WaitUntil(() => cache.RunningCount == 0 && cache.InFlightCount == 0
                    && cache.ActiveSubscriptionCount == 0, pump: true);
                Assert.Equal(3, computations);
                Assert.Equal(3, callbacks);
                Assert.Equal(3, cache.CompletedCount);
            }
            finally { release.Set(); cache.Clear(); }
        });
    }

    [Fact]
    public void CanceledFirstConsumerIsCollectibleWhileSharedFactoryStillRuns()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using ManualResetEventSlim started = new(false);
            using ManualResetEventSlim release = new(false);
            using CancellationTokenSource first = new();
            using CancellationTokenSource second = new();
            CancellationToken execution = default;
            Func<CancellationToken, TimelineRasterBuffer> factory = token =>
            {
                execution = token;
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
                return EmptyRaster();
            };
            WeakReference owner = SubscribeOwner(cache, factory, first.Token);
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            int completed = 0;
            Assert.True(cache.Request(Key(), factory, Dispatcher.CurrentDispatcher, () => completed++,
                second.Token, TimelineRasterRequestPriority.Visible, TimelineRasterCache.CreateConsumerId()));
            try
            {
                first.Cancel();
                Assert.Equal(1, cache.ActiveSubscriptionCount);
                Assert.False(execution.IsCancellationRequested);
                Collect();
                Assert.False(owner.IsAlive);
                release.Set();
                WaitUntil(() => completed == 1, pump: true);
                Assert.True(cache.TryGet(Key(), out _));
            }
            finally { release.Set(); cache.Clear(); }
        });
    }

    [Fact]
    public void CancellationAfterCompletionWasQueuedDropsOwnerBeforeDispatcherRuns()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using CancellationTokenSource generation = new();
            WeakReference owner = SubscribeOwner(cache, static _ => EmptyRaster(), generation.Token);
            WaitUntil(() => cache.QueuedCompletionCount == 1, pump: false);
            generation.Cancel();
            Assert.Equal(0, cache.ActiveSubscriptionCount);
            Assert.Equal(0, cache.QueuedCompletionCount);
            Collect();
            Assert.False(owner.IsAlive);
            Pump();
            Assert.True(cache.TryGet(Key(), out _));
        });
    }

    [Fact]
    public void NewGenerationOnSameConsumerAndKeySurvivesOldQueuedDelivery()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using CancellationTokenSource old = new();
            using CancellationTokenSource current = new();
            long consumer = TimelineRasterCache.CreateConsumerId();
            int oldCalls = 0;
            int currentCalls = 0;
            Assert.True(cache.Request(Key(), EmptyRaster, Dispatcher.CurrentDispatcher, () => oldCalls++,
                old.Token, consumerId: consumer));
            WaitUntil(() => cache.QueuedCompletionCount == 1, pump: false);
            old.Cancel();
            Assert.True(cache.Request(Key(), EmptyRaster, Dispatcher.CurrentDispatcher, () => currentCalls++,
                current.Token, consumerId: consumer));
            Pump();
            Assert.Equal(0, oldCalls);
            Assert.Equal(1, currentCalls);
            Assert.Equal(0, cache.ActiveSubscriptionCount);
        });
    }

    [Fact]
    public void LastConsumerCancellationStopsRunningScanAndDoesNotPublish()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using CancellationTokenSource first = new();
            using CancellationTokenSource second = new();
            using ManualResetEventSlim started = new(false);
            using ManualResetEventSlim canceled = new(false);
            Func<CancellationToken, TimelineRasterBuffer> factory = token =>
            {
                started.Set();
                try
                {
                    while (true) { token.ThrowIfCancellationRequested(); Thread.Yield(); }
                }
                catch (OperationCanceledException) { canceled.Set(); throw; }
            };
            Assert.True(cache.Request(Key(), factory, Dispatcher.CurrentDispatcher, static () => { }, first.Token));
            Assert.True(cache.Request(Key(), factory, Dispatcher.CurrentDispatcher, static () => { }, second.Token));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            first.Cancel();
            Assert.Equal(1, cache.ActiveSubscriptionCount);
            Assert.False(canceled.IsSet);
            second.Cancel();
            Assert.True(canceled.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, cache.InFlightCount);
            WaitUntil(() => cache.RunningCount == 0, pump: true);
            Assert.Equal(0, cache.ActiveSubscriptionCount);
            Assert.False(cache.TryGet(Key(), out _));
        });
    }

    [Fact]
    public void SessionClearCancelsRunningAndQueuedDeliveryAndAllowsSameKeyAgain()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using ManualResetEventSlim started = new(false);
            using ManualResetEventSlim release = new(false);
            CancellationToken oldExecution = default;
            int oldCalls = 0;
            int newCalls = 0;
            Assert.True(cache.Request(Key(1), token =>
            {
                oldExecution = token;
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return EmptyRaster();
            }, Dispatcher.CurrentDispatcher, () => oldCalls++, priority: TimelineRasterRequestPriority.Visible));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(cache.Request(Key(2), EmptyRaster, Dispatcher.CurrentDispatcher, () => oldCalls++,
                priority: TimelineRasterRequestPriority.Visible));
            WaitUntil(() => cache.QueuedCompletionCount == 1, pump: false);
            try
            {
                cache.Clear();
                Assert.True(oldExecution.IsCancellationRequested);
                Assert.Equal(0, cache.InFlightCount);
                Assert.Equal(1, cache.RunningCount);
                Assert.Equal(0, cache.ActiveSubscriptionCount);
                Assert.Equal(0, cache.CompletedCount);
                Assert.True(cache.Request(Key(1), EmptyRaster, Dispatcher.CurrentDispatcher, () => newCalls++,
                    priority: TimelineRasterRequestPriority.Visible));
                release.Set();
                WaitUntil(() => newCalls == 1 && cache.RunningCount == 0, pump: true);
                Assert.Equal(0, oldCalls);
                Assert.True(cache.TryGet(Key(1), out _));
                Assert.False(cache.TryGet(Key(2), out _));
            }
            finally { release.Set(); cache.Clear(); }
        });
    }

    [Fact]
    public void RepeatedCompletedGenerationsAbortRevokedDispatcherOperations()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Assert.True(cache.Request(Key(), EmptyRaster, dispatcher, static () => { }));
            WaitUntil(() => cache.ActiveSubscriptionCount == 0, pump: true);
            int posted = 0;
            int aborted = 0;
            int invoked = 0;
            DispatcherHookEventHandler onPosted = (_, args) =>
            { if (args.Operation.Priority == DispatcherPriority.Render) posted++; };
            DispatcherHookEventHandler onAborted = (_, args) =>
            { if (args.Operation.Priority == DispatcherPriority.Render) aborted++; };
            dispatcher.Hooks.OperationPosted += onPosted;
            dispatcher.Hooks.OperationAborted += onAborted;
            try
            {
                long consumer = TimelineRasterCache.CreateConsumerId();
                for (int generation = 0; generation < 1_000; generation++)
                {
                    using CancellationTokenSource cancellation = new();
                    Assert.True(cache.Request(Key(), EmptyRaster, dispatcher, () => invoked++,
                        cancellation.Token, consumerId: consumer));
                    Assert.Equal(1, cache.QueuedCompletionCount);
                    cancellation.Cancel();
                    Assert.Equal(0, cache.ActiveSubscriptionCount);
                }
                Assert.Equal(1_000, posted);
                Assert.Equal(posted, aborted);
                Pump();
                Assert.Equal(0, invoked);
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= onPosted;
                dispatcher.Hooks.OperationAborted -= onAborted;
                cache.Clear();
            }
        });
    }

    [Fact]
    public void TenThousandCanceledQueuedGenerationsKeepSchedulingStorageBounded()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using CountdownEvent started = new(TimelineRasterCache.WorkerCount);
            using ManualResetEventSlim release = new(false);
            for (int index = 0; index < TimelineRasterCache.WorkerCount; index++)
                Assert.True(cache.Request(Key(index), token =>
                {
                    started.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
                    return EmptyRaster();
                }, Dispatcher.CurrentDispatcher, static () => { }, priority: TimelineRasterRequestPriority.Visible));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            try
            {
                long consumer = TimelineRasterCache.CreateConsumerId();
                for (int generation = 0; generation < 10_000; generation++)
                {
                    using CancellationTokenSource cancellation = new();
                    Assert.True(cache.Request(Key(10_000 + generation), EmptyRaster,
                        Dispatcher.CurrentDispatcher, static () => { }, cancellation.Token,
                        TimelineRasterRequestPriority.Visible, consumer));
                    cancellation.Cancel();
                    Assert.InRange(cache.QueuedKeyCount, 0, TimelineRasterCache.MaximumInFlight * 2);
                }
                Assert.Equal(TimelineRasterCache.WorkerCount, cache.ActiveSubscriptionCount);
                Assert.Equal(TimelineRasterCache.WorkerCount, cache.InFlightCount);
                release.Set();
                WaitUntil(() => cache.RunningCount == 0 && cache.ActiveSubscriptionCount == 0, pump: true);
            }
            finally { release.Set(); cache.Clear(); }
        });
    }

    [Fact]
    public void SurfaceUnloadCancelsSelectionFamilyAndResumeRetainsViewport()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new() { StartTick = 900, TickSpan = 1800, LaneHeight = 13 };
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var field = typeof(TimelineSurface).GetField("_selectionRasterRequestCancellation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var cancellation = (CancellationTokenSource)field.GetValue(surface)!;
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.True(cancellation.IsCancellationRequested);
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.False(((CancellationTokenSource)field.GetValue(surface)!).IsCancellationRequested);
            Assert.Equal(900, surface.StartTick);
            Assert.Equal(1800, surface.TickSpan);
            Assert.Equal(13, surface.LaneHeight);
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeOwner(TimelineRasterCache cache,
        Func<CancellationToken, TimelineRasterBuffer> factory, CancellationToken cancellation)
    {
        CallbackOwner owner = new();
        Assert.True(cache.Request(Key(), factory, Dispatcher.CurrentDispatcher, owner.Complete,
            cancellation, TimelineRasterRequestPriority.Visible, TimelineRasterCache.CreateConsumerId()));
        return new(owner);
    }

    private sealed class CallbackOwner
    {
        private readonly byte[] _payload = new byte[1024 * 1024];
        public void Complete() => _payload[0]++;
    }

    private static TimelineRasterBuffer EmptyRaster() => new(1, 1, new byte[4]);
    private static TimelineRasterCacheKey Key(long tile = 0) => new(
        TimelineRasterLayer.PianoNotes, "subscriptions", 1, 1, 1, tile, 0, 0, 0, 0, 96, 96);

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private static void WaitUntil(Func<bool> condition, bool pump)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (pump) Pump();
            Thread.Sleep(1);
        }
        Assert.True(condition(), "Raster subscriptions did not reach the expected state within 10 seconds.");
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "STA test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
