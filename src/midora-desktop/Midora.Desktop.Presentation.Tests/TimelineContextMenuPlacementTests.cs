using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineContextMenuPlacementTests
{
    [Theory]
    [InlineData(TimelineSurfaceMode.PianoRoll, 1)]
    [InlineData(TimelineSurfaceMode.EventLanes, 1)]
    [InlineData(TimelineSurfaceMode.Velocity, 1)]
    [InlineData(TimelineSurfaceMode.Arrangement, 1)]
    [InlineData(TimelineSurfaceMode.PianoRoll, 1.5)]
    [InlineData(TimelineSurfaceMode.EventLanes, 1.5)]
    public void NativeRulerMenuDoesNotReuseTheContentClicksLocalOffsets(TimelineSurfaceMode mode, double scale) => Sta(() =>
    {
        var menu = new ContextMenu { Width = 120, Height = 40 };
        menu.Items.Add(new MenuItem { Header = "Context" });
        var surface = new TimelineSurface { Width = 452, Height = 280, SurfaceMode = mode, ContextMenu = menu,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(36, 50, 0, 0), LayoutTransform = new ScaleTransform(scale, scale) };
        var root = new Grid(); root.Children.Add(surface);
        using var host = new HwndSource(new HwndSourceParameters("Timeline placement regression (hidden)")
        { Width = 760, Height = 600, PositionX = 40, PositionY = 40, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = root;
        PumpUntil(() => surface.IsLoaded);
        root.Measure(new Size(760, 600)); root.Arrange(new Rect(0, 0, 760, 600)); root.UpdateLayout();
        try
        {
            Point ruler = new(96, 8);
            // Use the real WPF context-menu service, with a deterministic local
            // anchor instead of moving/reading the user's physical mouse. This
            // also exercises owner-property coercion and native Popup placement.
            ContextMenuService.SetPlacement(surface, PlacementMode.RelativePoint);
            ContextMenuService.SetPlacementRectangle(surface, new Rect(ruler, new Size()));
            var service = typeof(FrameworkElement).GetProperty("PopupControlService", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var open = service.GetType().GetMethod("RaiseContextMenuOpeningEvent", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(IInputElement), typeof(double), typeof(double), typeof(bool)], null)!;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                // Exactly the prior content popup's local placement state.
                menu.PlacementTarget = surface; menu.Placement = PlacementMode.RelativePoint;
                menu.HorizontalOffset = 270; menu.VerticalOffset = 160;
                menu.IsOpen = true; PumpUntil(() => menu.IsVisible); Close(menu);

                open.Invoke(service, [surface, ruler.X, ruler.Y, false]);
                PumpUntil(() => menu.IsOpen && menu.IsVisible);
                Assert.Equal(0, menu.HorizontalOffset); Assert.Equal(0, menu.VerticalOffset);
                Assert.Equal(PlacementMode.MousePoint, menu.ReadLocalValue(ContextMenu.PlacementProperty));
                Assert.Equal(Rect.Empty, menu.ReadLocalValue(ContextMenu.PlacementRectangleProperty));
                Assert.Equal(ruler, surface.ContextTargetPosition);
                Point expected = surface.PointToScreen(ruler), actual = menu.PointToScreen(new Point());
                Assert.InRange(Math.Abs(actual.X - expected.X), 0, 2);
                Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 2);
                Close(menu);
            }
        }
        finally { if (menu.IsOpen) Close(menu); host.RootVisual = null; Pump(); }
    });

    private static void Close(ContextMenu menu)
    {
        bool closed = false;
        RoutedEventHandler handler = (_, _) => closed = true;
        menu.Closed += handler; menu.IsOpen = false;
        try { PumpUntil(() => closed); }
        finally { menu.Closed -= handler; }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        { if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("WPF popup did not reach the expected state."); Pump(); }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "WPF popup test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
