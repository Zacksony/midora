using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class PresentationDispatchLifetimeTests
{
    [Fact]
    public void UndrainedCoalescedSignalsDoNotRetainUnloadedSurfaceOrSource()
    {
        Sta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            List<DispatcherOperation> operations = [];
            DispatcherHookEventHandler capture = (_, args) => operations.Add(args.Operation);
            dispatcher.Hooks.OperationPosted += capture;
            try
            {
                (WeakReference surface, WeakReference snapshot, WeakReference project) =
                    QueueCoalescedSignalsAndUnload();
                Assert.Contains(operations, operation => operation.Priority == DispatcherPriority.Background);
                Assert.Contains(operations, operation => operation.Priority == DispatcherPriority.Render);
                Assert.Contains(operations, operation => operation.Priority == DispatcherPriority.ApplicationIdle);
                Collect();
                Assert.False(surface.IsAlive);
                Assert.False(snapshot.IsAlive);
                Assert.False(project.IsAlive);
                Assert.All(operations, operation => Assert.Equal(DispatcherOperationStatus.Pending, operation.Status));
                GC.KeepAlive(operations);
            }
            finally { dispatcher.Hooks.OperationPosted -= capture; }
        });
    }

    [Fact]
    public void CanceledQueuedDeliveryReleasesOwnerEvenWhenOperationIsRetained()
    {
        Sta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using CancellationTokenSource cancellation = new();
            DispatcherOperation? operation = null;
            DispatcherHookEventHandler posted = (_, args) => operation = args.Operation;
            dispatcher.Hooks.OperationPosted += posted;
            try
            {
                WeakReference owner = QueueOwner(dispatcher, cancellation.Token);
                Assert.NotNull(operation);
                Collect();
                Assert.True(owner.IsAlive);
                cancellation.Cancel();
                Assert.Equal(DispatcherOperationStatus.Aborted, operation.Status);
                Collect();
                Assert.False(owner.IsAlive);
                GC.KeepAlive(operation);
            }
            finally { dispatcher.Hooks.OperationPosted -= posted; }
        });
    }

    [Fact]
    public void CancellationDuringPostingCannotRetainCallbackOrCancelNewDelivery()
    {
        Sta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using CancellationTokenSource old = new();
            using CancellationTokenSource current = new();
            int oldCalls = 0;
            int currentCalls = 0;
            DispatcherHookEventHandler cancelDuringPosting = (_, _) => old.Cancel();
            dispatcher.Hooks.OperationPosted += cancelDuringPosting;
            try
            {
                CancelablePresentationDispatch.Post(dispatcher, old.Token, () => oldCalls++);
            }
            finally { dispatcher.Hooks.OperationPosted -= cancelDuringPosting; }
            CancelablePresentationDispatch.Post(dispatcher, current.Token, () => currentCalls++);
            Pump();
            Assert.Equal(0, oldCalls);
            Assert.Equal(1, currentCalls);
        });
    }

    [Fact]
    public void ConductorLabelCompletionReleasesSurfaceAndSourceWithoutPumpingAfterUnload()
    {
        Sta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using ManualResetEventSlim posted = new(false);
            DispatcherOperation? operation = null;
            DispatcherHookEventHandler capture = (_, args) =>
            {
                if (args.Operation.Priority != DispatcherPriority.Background) return;
                operation = args.Operation;
                posted.Set();
            };
            dispatcher.Hooks.OperationPosted += capture;
            try
            {
                (WeakReference surface, WeakReference snapshot, WeakReference project) =
                    QueueConductorLabelAndUnload(posted);
                Assert.NotNull(operation);
                Stopwatch deadline = Stopwatch.StartNew();
                do
                {
                    Collect();
                    if (!surface.IsAlive && !snapshot.IsAlive && !project.IsAlive) break;
                    Thread.Yield();
                } while (deadline.Elapsed < TimeSpan.FromSeconds(10));
                // No Dispatcher frame was pumped. Even a retained aborted WPF
                // operation must no longer root the callback's source graph.
                Assert.Equal(DispatcherOperationStatus.Aborted, operation.Status);
                Assert.False(surface.IsAlive);
                Assert.False(snapshot.IsAlive);
                Assert.False(project.IsAlive);
                GC.KeepAlive(operation);
            }
            finally { dispatcher.Hooks.OperationPosted -= capture; }
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Surface, WeakReference Snapshot, WeakReference Project)
        QueueCoalescedSignalsAndUnload()
    {
        MidoraProject project = new(192);
        ConductorTimelineProjection projection = new(project.Conductor, 1, 0, 240);
        TimelineRenderSnapshot snapshot = projection.MetaSnapshot;
        TimelineSurface surface = new() { SurfaceMode = TimelineSurfaceMode.Conductor, Snapshot = snapshot };
        typeof(TimelineSurface).GetMethod("QueueRasterInvalidation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(surface, null);
        long generation = (long)typeof(TimelineSurface)
            .GetField("_segmentPreviewWarmupGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        typeof(TimelineSurface).GetMethod("ScheduleSegmentPreviewWarmupRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(surface, [generation]);
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        return (new(surface), new(snapshot), new(project));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueOwner(Dispatcher dispatcher, CancellationToken token)
    {
        CallbackOwner owner = new();
        CancelablePresentationDispatch.Post(dispatcher, token, owner.Complete);
        return new(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Surface, WeakReference Snapshot, WeakReference Project)
        QueueConductorLabelAndUnload(ManualResetEventSlim posted)
    {
        MidoraProject project = new(192);
        ConductorTimelineProjection projection = new(project.Conductor, 1, 0, 240);
        TimelineRenderSnapshot snapshot = projection.MetaSnapshot;
        TimelineSurface surface = new() { SurfaceMode = TimelineSurfaceMode.Conductor, Snapshot = snapshot };
        // Drain only initialization/viewport coalescing before the work under
        // test starts. No messages are pumped after the label completion posts.
        Pump();
        posted.Reset();
        TimelineRasterCacheKey key = new(TimelineRasterLayer.ConductorMeta,
            snapshot.ProjectionKey, 1, 1, 1, 0, 0, 0, 0, 0, 96, 96);
        Func<CancellationToken, TimelineRasterBuffer> build = TimelineRasterFactory.Create(
            snapshot,
            static (source, token) =>
            {
                token.ThrowIfCancellationRequested();
                GC.KeepAlive(source);
                return new(1, 1, new byte[4]);
            });
        typeof(TimelineSurface).GetMethod("RequestConductorLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(surface, [key, build, snapshot]);
        Assert.True(posted.Wait(TimeSpan.FromSeconds(10)));
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var requests = (HashSet<TimelineRasterCacheKey>)typeof(TimelineSurface)
            .GetField("_conductorLabelRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Empty(requests);
        return (new(surface), new(snapshot), new(project));
    }

    private sealed class CallbackOwner
    {
        private readonly byte[] _payload = new byte[1024 * 1024];
        public void Complete() => _payload[0]++;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
