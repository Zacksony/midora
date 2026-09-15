using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class TimelineCommandTargetTests
{
    [Fact]
    public void TargetRequiresTheSameLiveWorkspaceAndSurfaceDataContext()
    {
        Sta(() =>
        {
            using var first = new TestWorkspace();
            using var second = new TestWorkspace(); // Same workspace key is not sufficient.
            var surface = new TimelineSurface { DataContext = first };
            TimelineCommandTarget target = new();
            target.Set(surface);
            Assert.Same(surface, target.Resolve(first));
            Assert.Null(target.Resolve(second));
            Assert.Null(target.Resolve(null));
            surface.DataContext = second;
            Assert.Null(target.Resolve(first));
            target.Set(surface);
            Assert.Same(surface, target.Resolve(second));
            second.SuspendPresentation();
            Assert.Null(target.Resolve(second));
            second.ResumePresentation();
            Assert.Same(surface, target.Resolve(second));
            second.Dispose();
            Assert.Null(target.Resolve(second));
            Assert.False(target.RestoreFocus(second));
        });
    }

    [Fact]
    public void ClearingOrCapturingANonWorkspaceDiscardsTheOldTarget()
    {
        Sta(() =>
        {
            using var workspace = new TestWorkspace();
            var surface = new TimelineSurface { DataContext = workspace };
            TimelineCommandTarget target = new();
            target.Set(surface);
            target.Clear();
            Assert.Null(target.Resolve(workspace));
            target.Set(surface);
            target.Set(new TimelineSurface());
            Assert.Null(target.Resolve(workspace));
        });
    }

    [Fact]
    public void RetainedNavigationHintsDoNotRetainOneHundredOldSurfacesOrWorkspaces()
    {
        Sta(() =>
        {
            var refs = Enumerable.Range(0, 100).Select(_ => CreateReleasedTarget()).ToArray();
            Collect();
            Assert.All(refs, item =>
            {
                Assert.False(item.Surface.IsAlive);
                Assert.False(item.Workspace.IsAlive);
                GC.KeepAlive(item.Target);
            });
        });
    }

    [Fact]
    public void QueuedFocusRestorationDoesNotRetainAnAbandonedSurface()
    {
        Sta(() =>
        {
            var refs = CreateQueuedTarget();
            // The queued action exists, but only owns the weak navigation hint.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(refs.Surface.IsAlive);
            Assert.False(refs.Workspace.IsAlive);
            Drain();
            Assert.True(refs.Completed());
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TimelineCommandTarget Target, WeakReference Surface, WeakReference Workspace) CreateReleasedTarget()
    {
        var workspace = new TestWorkspace();
        var surface = new TimelineSurface { DataContext = workspace };
        TimelineCommandTarget target = new(); target.Set(surface);
        workspace.Dispose();
        return (target, new(surface), new(workspace));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Surface, WeakReference Workspace, Func<bool> Completed) CreateQueuedTarget()
    {
        var (target, surface, workspace) = CreateReleasedTarget();
        bool completed = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Assert.False(target.RestoreFocus(null));
            completed = true;
        }));
        return (surface, workspace, () => completed);
    }

    private static void Collect()
    {
        Drain();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Drain();
    }

    private static void Drain()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF target lifetime test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestWorkspace() : WorkspaceViewModel(WorkspaceKey.ForType(WorkspaceKind.Arrangement), "Test")
    {
        public override void Rebuild(MidoraProject project, long revision) { }
    }
}
