using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineEventContextGestureTests
{
    [Theory]
    [InlineData("Logical Segment", TimelineItemKind.LogicalParameterPoint)]
    [InlineData("MIDI Segment", TimelineItemKind.DirectMidiEvent)]
    [InlineData("SubVoice", TimelineItemKind.LogicalParameterPoint)]
    [InlineData("Imported Meta / SysEx", TimelineItemKind.OpaqueMidiEvent)]
    public void LowValuePointUsesOneValueLaneAndEightDipHitGeometry(string workspace, TimelineItemKind kind)
    {
        OnSta(() =>
        {
            TimelineRenderItem point = new(new MidoraId(7), kind, 100, 101, 0, .125, 0, TimelineItemState.None);
            TimelineSurface surface = Surface(new(1, workspace, [point]));
            List<TimelineItemEventArgs> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args);
            try
            {
                // Content is 400 x 256. Point is (152,248): well below a nominal
                // LaneHeight row, and the pointer is seven DIP away on both axes.
                BeginAndFinishClick(surface, new Point(159, 255));
                TimelineItemEventArgs hit = Assert.Single(invoked);
                Assert.Equal(point.Id, hit.Item.Id);
                Assert.Equal(0, hit.Lane);
                Assert.False(hit.PreserveExistingSelection);
            }
            finally { Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData(152, 239)] // Nine DIP vertically: same tick does not mean same point.
    [InlineData(161, 248)] // Nine DIP horizontally: an exact point tolerance.
    public void EmptyValueSpaceDoesNotInvokeANearbyPoint(double x, double y)
    {
        OnSta(() =>
        {
            TimelineRenderItem point = new(new MidoraId(7), TimelineItemKind.DirectMidiEvent, 100, 101, 0, .125, 0, TimelineItemState.None);
            TimelineSurface surface = Surface(new(1, "empty-value-space", [point]));
            int invoked = 0;
            surface.ItemInvoked += (_, _) => invoked++;
            try { BeginAndFinishClick(surface, new Point(x, y)); Assert.Equal(0, invoked); }
            finally { Cleanup(surface); }
        });
    }

    [Fact]
    public void ColdPointQueryFreezesSelectionAndProjectionWithoutBlockingTheDispatcher()
    {
        OnSta(() =>
        {
            TimelineRenderItem point = new(new MidoraId(7), TimelineItemKind.DirectMidiEvent, 100, 101, 0, .125, 0, TimelineItemState.None);
            StreamingGate source = new(point);
            TimelineSurface surface = Surface(new(1, "cold-event-context", [], itemSource: source));
            surface.SelectionSnapshot = new(1, [point.Id, new MidoraId(8)], point.Id, [point]);
            List<TimelineItemEventArgs> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args);
            try
            {
                Invoke(surface, "BeginPendingRightGesture", new Point(159, 255), ModifierKeys.Control);
                Assert.True(source.Started.Wait(TimeSpan.FromSeconds(3)));
                Assert.NotEqual(Environment.CurrentManagedThreadId, source.ThreadId);
                Assert.Empty(invoked);
                // UI projection/selection changes while a source page is cold
                // must not reinterpret the already frozen physical right click.
                Set(surface, "_valueViewMinimum", .5);
                Set(surface, "_valueViewMaximum", 1d);
                source.Release.Set();
                PumpUntil(() => (bool)Field(surface, "_delayedContextMenuQueryReady")!);
                Assert.Empty(invoked);
                FinishClick(surface);
                TimelineItemEventArgs hit = Assert.Single(invoked);
                Assert.Equal(point.Id, hit.Item.Id);
                Assert.True(hit.PreserveExistingSelection);
                Assert.Equal(ModifierKeys.Control, hit.Modifiers);
                Assert.Equal(0, source.QueryIntoCalls);
            }
            finally { source.Release.Set(); Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingCaptureOrUnloadingCancelsAColdRightClick(bool unload)
    {
        OnSta(() =>
        {
            TimelineRenderItem point = new(new MidoraId(7), TimelineItemKind.DirectMidiEvent, 100, 101, 0, .125, 0, TimelineItemState.None);
            StreamingGate source = new(point);
            TimelineSurface surface = Surface(new(1, "cancel-event-context", [], itemSource: source));
            int invoked = 0;
            surface.ItemInvoked += (_, _) => invoked++;
            try
            {
                Invoke(surface, "BeginPendingRightGesture", new Point(152, 248), ModifierKeys.None);
                Assert.True(source.Started.Wait(TimeSpan.FromSeconds(3)));
                if (unload) Cleanup(surface);
                else Invoke(surface, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent });
                source.Release.Set();
                PumpUntil(() => source.Completed.IsSet);
                PumpDispatcher();
                Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation"));
                Assert.Null(Field(surface, "_pendingRightGestureOrigin"));
                Assert.Equal(0, invoked);
            }
            finally { source.Release.Set(); Cleanup(surface); }
        });
    }

    [Fact]
    public void RightDragCancelsThePendingMenuAndUsesAddMarqueeWithoutDrawing()
    {
        OnSta(() =>
        {
            TimelineSurface surface = Surface(new(1, "right-event-trace", []));
            TimelineEventPointTraceEventArgs? trace = null;
            int invoked = 0;
            surface.ItemInvoked += (_, _) => invoked++;
            surface.EventPointTraceCompleted += (_, args) => trace = args;
            try
            {
                Invoke(surface, "BeginPendingRightGesture", new Point(152, 248), ModifierKeys.Shift);
                // These are exactly the two actions of the committed drag branch
                // in OnMouseMove; no physical cursor or global input is injected.
                Invoke(surface, "BeginRightButtonMarquee", new Point(152, 248), new Point(172, 56));
                Invoke(surface, "OnMouseUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = Mouse.MouseUpEvent });
                Assert.Null(trace);
                Assert.Equal(ModifierKeys.Shift, Field(surface, "_marqueeModifiers"));
                Assert.Equal(0, invoked);
                Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation"));
            }
            finally { Cleanup(surface); }
        });
    }

    [Fact]
    public void EscapeCancelsAPendingPointMenuWithoutChangingTheSelection()
    {
        OnSta(() =>
        {
            TimelineRenderItem point = new(new MidoraId(7), TimelineItemKind.DirectMidiEvent, 100, 101, 0, .125, 0, TimelineItemState.None);
            TimelineSurface surface = Surface(new(1, "escape-event-context", [point]));
            TimelineSelectionSnapshot selection = new(1, [point.Id], point.Id, [point]);
            surface.SelectionSnapshot = selection;
            int invoked = 0;
            surface.ItemInvoked += (_, _) => invoked++;
            try
            {
                Invoke(surface, "BeginPendingRightGesture", new Point(152, 248), ModifierKeys.None);
                KeyEventArgs escape = new(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent };
                Invoke(surface, "OnKeyDown", escape);
                Assert.True(escape.Handled);
                Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation"));
                Assert.Null(Field(surface, "_pendingRightGestureOrigin"));
                Assert.Same(selection, surface.SelectionSnapshot);
                Assert.Equal(0, invoked);
            }
            finally { Cleanup(surface); }
        });
    }

    [Fact]
    public void TempoPointContextStillUsesExactValueCoordinates()
    {
        OnSta(() =>
        {
            MidoraProject project = new(192);
            TempoChange tempo = new(project, 100, 120);
            project.Conductor.Tempos.Add(tempo);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            List<TimelineItemEventArgs> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args);
            try { BeginAndFinishClick(surface, new Point(152, 152)); Assert.Equal(tempo.Id, Assert.Single(invoked).Item.Id); }
            finally { Cleanup(surface); }
        });
    }

    private static TimelineSurface Surface(TimelineRenderSnapshot snapshot)
    {
        TimelineSurface surface = new()
        {
            SurfaceMode = TimelineSurfaceMode.EventLanes, ToolMode = TimelineToolMode.Draw,
            Snapshot = snapshot, ContextMenu = new(), StartTick = 0, TickSpan = 400,
            FirstLane = 0, LaneHeight = 32, GridVisible = false
        };
        surface.Measure(new Size(452, 280));
        surface.Arrange(new Rect(0, 0, 452, 280));
        return surface;
    }

    private static void BeginAndFinishClick(TimelineSurface surface, Point pointer)
    {
        Invoke(surface, "BeginPendingRightGesture", pointer, ModifierKeys.None);
        PumpUntil(() => (bool)Field(surface, "_delayedContextMenuQueryReady")!);
        FinishClick(surface);
    }
    private static void FinishClick(TimelineSurface surface)
    {
        Set(surface, "_pendingRightGestureOrigin", null);
        Set(surface, "_delayedContextMenuAwaitingMouseUp", false);
        Set(surface, "_delayedContextMenuDelayElapsed", true);
        Invoke(surface, "TryOpenDelayedContextMenu");
    }
    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object?[] arguments)
    {
        try { return instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void Cleanup(TimelineSurface surface) => surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    private static void PumpUntil(Func<bool> ready)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(5)) { PumpDispatcher(); Thread.Yield(); }
        Assert.True(ready());
    }
    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class StreamingGate(TimelineRenderItem point) : ITimelineRenderItemSource
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Completed { get; } = new(false);
        public int ThreadId { get; private set; }
        public int QueryIntoCalls { get; private set; }
        public long Count => 1;
        public long MaximumEndTick => 101;
        public ulong ContentFingerprint => 81;
        public void QueryInto(long start, long end, int first, int last, List<TimelineRenderItem> result)
        { QueryIntoCalls++; throw new InvalidOperationException("Exact event targets must use the streaming query."); }
        public void VisitInto(long start, long end, int first, int last, Action<TimelineRenderItem> visitor)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Started.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Event source was not released.");
                if (point.StartTick >= start && point.StartTick < end && first <= point.Lane && point.Lane < last) visitor(point);
            }
            finally { Completed.Set(); }
        }
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = point; return id == point.Id; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => [point];
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
