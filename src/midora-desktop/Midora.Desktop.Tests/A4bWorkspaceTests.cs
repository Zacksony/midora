using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Midora.Compiler;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class A4bWorkspaceTests
{
    [Fact]
    public void AllTracksFitsOnceUsingMeasuredContentAndKeepsEditsAcrossViewRecreation() => Sta(() =>
    {
        using var p = new MidoraProject(480);
        using var vm = new AllTracksWorkspaceViewModel(_ => { }); vm.Rebuild(p, 0);
        using var host = new HwndSource(new HwndSourceParameters("A4b hidden layout")
        { Width = 1200, Height = 700, WindowStyle = unchecked((int)0x80000000) });
        var view = new AllTracksView { DataContext = vm }; host.RootVisual = view;
        Layout(view, 700); Pump();
        Assert.True(view.Timeline.TryGetPianoVerticalFit(out double expected));
        Assert.Equal(expected, vm.LaneHeight); Assert.Equal(0, vm.FirstLane);
        Assert.False(vm.TryInitializeVerticalView(90));
        vm.FirstLane = 34; vm.LaneHeight = 11;
        Layout(view, 900); Pump();
        Assert.Equal(34, vm.FirstLane); Assert.Equal(11, vm.LaneHeight);
        host.RootVisual = null;
        var replacement = new AllTracksView { DataContext = vm }; host.RootVisual = replacement;
        Layout(replacement, 500); Pump();
        Assert.Equal(34, vm.FirstLane); Assert.Equal(11, vm.LaneHeight);
        host.RootVisual = null;

        using var restored = new AllTracksWorkspaceViewModel(_ => { });
        restored.RestoreVerticalView(50, 8);
        Assert.False(restored.TryInitializeVerticalView(3));
        Assert.Equal(50, restored.FirstLane); Assert.Equal(8, restored.LaneHeight);
    });

    [Fact]
    public void CompileButtonDescribesPhasePercentAndClearsWhenNotCompiling()
    {
        Assert.Equal("Compile", DesktopSessionController.FormatCompilationProgress(null));
        Assert.Equal("Compile · Validating", DesktopSessionController.FormatCompilationProgress(new(CompilationPhase.Validating, null)));
        Assert.Equal("Compile · Logical instances 37%", DesktopSessionController.FormatCompilationProgress(new(CompilationPhase.LogicalInstances, 37)));
    }

    private static void Layout(FrameworkElement view, double height)
    { view.Measure(new(1200, height)); view.Arrange(new Rect(0, 0, 1200, height)); view.UpdateLayout(); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
