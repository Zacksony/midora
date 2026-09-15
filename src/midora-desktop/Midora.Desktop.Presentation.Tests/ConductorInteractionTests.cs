using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

/// <summary>
/// Exercises the actual WPF control handlers and dispatcher continuation. Coordinates
/// are supplied through the same replay position used by deferred physical input;
/// no desktop cursor, native input injection, or visible application is required.
/// </summary>
public sealed class ConductorInteractionTests
{
    [Fact]
    public void WarmTempoClickInvokesThePointExactlyOnce()
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            TempoChange tempo = new(project, 100, 120);
            project.Conductor.Tempos.Add(tempo);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            List<MidoraId> invoked = [];
            int traces = 0;
            surface.ItemInvoked += (_, args) => invoked.Add(args.Item.Id);
            surface.EventPointTraceCompleted += (_, _) => traces++;
            Point point = new(152, 152);

            Assert.False(Defer(surface, point));
            At(surface, point, () => Invoke(surface, "OnMouseDown", Button(true)));
            At(surface, point, () => Invoke(surface, "OnMouseUp", Button(false)));

            Assert.Equal([tempo.Id], invoked);
            Assert.Equal(0, traces);
            Cleanup(surface);
        });
    }

    [Fact]
    public void ColdTempoClickReleasedBeforeReadCompletesIsReplayedExactlyOnce()
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            TempoChange tempo = new(project, 100, 120);
            using GatedSource<TempoChange> source = new([project.Conductor.Tempos[0], tempo]);
            project.Conductor.Tempos.AdoptSource(source);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            List<MidoraId> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args.Item.Id);
            surface.EventPointTraceCompleted += (_, _) => throw new InvalidOperationException("A point click must not draw a new Tempo.");
            Point point = new(152, 152);
            source.BlockReads();
            try
            {
                Assert.True(Defer(surface, point));
                At(surface, point, () => Invoke(surface, "OnMouseUp", Button(false)));
                Assert.Empty(invoked);
                source.ReleaseReads();
                PumpUntil(() => invoked.Count == 1);
                PumpDispatcher();
                Assert.Equal([tempo.Id], invoked);
                Assert.Null(Field(surface, "_pendingConductorPress"));
            }
            finally { source.ReleaseReads(); Cleanup(surface); }
        });
    }

    [Fact]
    public void ColdEmptyPointDragAndReleasePreserveTraceAndStreamingSnapSamples()
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            using GatedSource<TempoChange> source = new([project.Conductor.Tempos[0], new(project, 250, 120)]);
            project.Conductor.Tempos.AdoptSource(source);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            surface.OperationStepTicks = 4;
            TimelineEventPointTraceEventArgs? trace = null;
            int count = 0;
            surface.EventPointTraceCompleted += (_, args) => { count++; trace = args; };
            Point start = new(152, 216); // Tick 100, normalized .25.
            Point end = new(172, 88); // Tick 120, normalized .75.
            source.BlockReads();
            try
            {
                Assert.True(Defer(surface, start));
                At(surface, new Point(160, 56), () => Invoke(surface, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0)
                { RoutedEvent = Mouse.MouseMoveEvent })); // Tick 108, .875: a real freehand bend, not a straight trace.
                At(surface, end, () => Invoke(surface, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0)
                { RoutedEvent = Mouse.MouseMoveEvent }));
                At(surface, end, () => Invoke(surface, "OnMouseUp", Button(false)));
                Assert.Null(trace);
                source.ReleaseReads();
                PumpUntil(() => trace is not null);
                Assert.Equal(1, count);
                Assert.NotNull(trace);
                Dictionary<long, double> samples = [];
                foreach (TimelineValueTracePoint sample in trace.Sample()) samples[(long)sample.Tick] = sample.NormalizedValue;
                Assert.Equal([100L, 104, 108, 112, 116, 120], samples.Keys.Order());
                Assert.Equal(.25, samples[100], 6);
                Assert.Equal(.875, samples[108], 6);
                Assert.Equal(.75, samples[120], 6);
                Assert.Null(Field(surface, "_eventPointOrigin"));
                Assert.Null(Field(surface, "_pendingConductorPress"));
            }
            finally { source.ReleaseReads(); Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondColdPressSupersedesTheFirstPointAndReplaysOnlyItsOwnHit(bool samePoint)
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            TempoChange first = new(project, 100, 120);
            TempoChange second = new(project, 200, 60);
            using GatedSource<TempoChange> source = new([project.Conductor.Tempos[0], first, second]);
            project.Conductor.Tempos.AdoptSource(source);
            TimelineSurface surface = Surface(new ConductorTimelineProjection(project.Conductor, 1, 0, 240).TempoSnapshot);
            List<MidoraId> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args.Item.Id);
            source.BlockReads();
            try
            {
                Point firstPoint = new(152, 152);
                Point secondPoint = samePoint ? firstPoint : new(252, 216);
                At(surface, firstPoint, () => Invoke(surface, "OnMouseDown", Button(true)));
                At(surface, firstPoint, () => Invoke(surface, "OnMouseUp", Button(false)));
                Assert.NotNull(Field(surface, "_pendingConductorPress"));
                At(surface, secondPoint, () => Invoke(surface, "OnMouseDown", Button(true)));
                At(surface, secondPoint, () => Invoke(surface, "OnMouseUp", Button(false)));
                Assert.NotNull(Field(surface, "_pendingConductorPress"));
                Assert.Empty(invoked);
                source.ReleaseReads();
                PumpUntil(() => invoked.Count != 0);
                PumpDispatcher();
                Assert.Equal([samePoint ? first.Id : second.Id], invoked);
                Assert.Null(Field(surface, "_pendingConductorPress"));
            }
            finally { source.ReleaseReads(); Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("tool")]
    [InlineData("readonly")]
    [InlineData("unloaded")]
    [InlineData("viewport")]
    [InlineData("axis")]
    [InlineData("selection")]
    [InlineData("escape")]
    [InlineData("capture")]
    public void InvalidatedPendingInputNeverReplaysIntoAnotherEditingContext(string invalidation)
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            using GatedSource<TempoChange> source = new([project.Conductor.Tempos[0], new(project, 250, 120)]);
            project.Conductor.Tempos.AdoptSource(source);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            int edits = 0;
            surface.ItemInvoked += (_, _) => edits++;
            surface.EventPointTraceCompleted += (_, _) => edits++;
            Point point = new(152, 216);
            source.BlockReads();
            try
            {
                Assert.True(Defer(surface, point));
                At(surface, point, () => Invoke(surface, "OnMouseUp", Button(false)));
                switch (invalidation)
                {
                    case "snapshot": surface.Snapshot = new ConductorTimelineProjection(project.Conductor, 2, 0, 240).TempoSnapshot; break;
                    case "tool": surface.ToolMode = TimelineToolMode.Select; break;
                    case "readonly": surface.CanEdit = false; break;
                    case "unloaded": surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                    case "viewport": surface.StartTick = 100; break;
                    case "axis": surface.ValueAxisMaximum = 480; break;
                    case "selection": surface.SelectionSnapshot = new(1, [], null); break;
                    case "escape": Invoke(surface, "OnKeyDown", new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                        { RoutedEvent = Keyboard.KeyDownEvent }); break;
                    case "capture": surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                        { RoutedEvent = Mouse.LostMouseCaptureEvent }); break;
                }
                source.ReleaseReads();
                PumpUntil(() => Field(surface, "_pendingConductorPress") is null);
                PumpDispatcher();
                Assert.Equal(0, edits);
                Assert.Null(Field(surface, "_eventPointOrigin"));
            }
            finally { source.ReleaseReads(); Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MetadataPointClicksUseTheFourDistinctSemanticLanes(int lane)
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            TimeSignatureChange signature = new(project, 100, 3, 4);
            KeySignatureChange key = new(project, 100, -2, true);
            ProjectMarker marker = new(project, 100, "A");
            ProjectEndMarker end = new(project, 100);
            project.Conductor.TimeSignatures.Add(signature);
            project.Conductor.KeySignatures.Add(key);
            project.Conductor.Markers.Add(marker);
            project.Conductor.EndMarker = end;
            MidoraId[] ids = [signature.Id, key.Id, marker.Id, end.Id];
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.MetaSnapshot, meta: true);
            List<MidoraId> invoked = [];
            surface.ItemInvoked += (_, args) => invoked.Add(args.Item.Id);
            Point point = new(230, 24 + (lane + .5) * 44);

            Assert.False(Defer(surface, point));
            At(surface, point, () => Invoke(surface, "OnMouseDown", Button(true)));
            At(surface, point, () => Invoke(surface, "OnMouseUp", Button(false)));

            Assert.Equal([ids[lane]], invoked);
            Cleanup(surface);
        });
    }

    [Fact]
    public void PlaybackReadOnlySurfaceDoesNotBeginTempoDrawing()
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            TimelineSurface surface = Surface(new ConductorTimelineProjection(project.Conductor, 1, 0, 240).TempoSnapshot);
            surface.CanEdit = false;
            int edits = 0;
            surface.EventPointTraceCompleted += (_, _) => edits++;
            Point point = new(152, 216);
            Assert.False(Defer(surface, point));
            At(surface, point, () => Invoke(surface, "OnMouseDown", Button(true)));
            At(surface, point, () => Invoke(surface, "OnMouseUp", Button(false)));
            Assert.Equal(0, edits);
            Assert.Null(Field(surface, "_eventPointOrigin"));
            Cleanup(surface);
        });
    }

    [Fact]
    public void TempoSurfaceUsesGreenForUnselectedPointsAndHeldLines()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            project.Conductor.Tempos.Add(new(project, 100, 120));
            TimelineSurface surface = Surface(new ConductorTimelineProjection(project.Conductor, 1, 0, 240).TempoSnapshot);
            try
            {
                PumpUntil(() =>
                {
                    surface.UpdateLayout();
                    RenderTargetBitmap bitmap = new(564, 280, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    byte[] point = new byte[4], line = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(152, 152, 1, 1), point, 4, 0);
                    bitmap.CopyPixels(new Int32Rect(202, 152, 1, 1), line, 4, 0);
                    return IsGreen(point) && IsGreen(line);
                });
            }
            finally { Cleanup(surface); }
            static bool IsGreen(byte[] bgra) => bgra[1] > bgra[0] + 20 && bgra[1] > bgra[2] + 20;
        });
    }

    [Theory]
    [InlineData("RestartPendingRasterWork")]
    [InlineData("ResetRasterRequests")]
    public void RasterEpochResetDoesNotCancelCurrentConductorHover(string resetMethod)
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            for (int tick = 1; tick < 1000; tick++) project.Conductor.Tempos.Add(new(project, tick, 120));
            TimelineSurface surface = Surface(new ConductorTimelineProjection(project.Conductor, 1, 0, 240).TempoSnapshot);
            surface.TickSpan = 51200;
            typeof(TimelineSurface).GetField("_hoverPoint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(surface, new Point(58, 152));
            try
            {
                Invoke(surface, "RefreshHoverIntent");
                var cancellation = Assert.IsAssignableFrom<CancellationTokenSource>(Field(surface, "_conductorHitCancellation"));
                if (resetMethod == "ResetRasterRequests") Invoke(surface, resetMethod, false);
                else Invoke(surface, resetMethod);
                Assert.False(cancellation.IsCancellationRequested);
                PumpUntil(() => Field(surface, "_conductorHitReady") is true);
                Assert.False(Field(surface, "_exactQueryPending") is true);
                Assert.Equal(Cursors.SizeNS, surface.Cursor);
            }
            finally { Cleanup(surface); }
        });
    }

    [Fact]
    public void ConductorRulerSurvivesRasterResetButSourceReplacementCancelsIt()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
            TimelineSurface surface = Surface(projection.TempoSnapshot);
            surface.RulerSnapshot = projection.RulerSnapshot;
            try
            {
                object?[] arguments = [null];
                Assert.True((bool)Invoke(surface, "TryCreateViewport", arguments)!);
                _ = Invoke(surface, "TryPrepareConductorRuler", projection.RulerSnapshot, arguments[0]);
                var cancellation = Assert.IsAssignableFrom<CancellationTokenSource>(Field(surface, "_conductorRulerCancellation"));
                Invoke(surface, "ResetRasterRequests", false);
                Assert.False(cancellation.IsCancellationRequested);
                PumpUntil(() => Field(surface, "_conductorRulerReady") is true);
                surface.RulerSnapshot = null;
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Null(Field(surface, "_conductorRulerKey"));
                Assert.False(Field(surface, "_conductorRulerReady") is true);
            }
            finally { Cleanup(surface); }
        });
    }

    [Theory]
    [InlineData(88, false)]
    [InlineData(152, true)]
    [InlineData(216, false)]
    public void DenseHoverRestoresCursorWhenTheQueryFinishesWithoutAnotherMouseMove(double y, bool hit)
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            for (int tick = 1; tick < 1000; tick++) project.Conductor.Tempos.Add(new(project, tick, 120));
            TimelineSurface surface = Surface(new ConductorTimelineProjection(project.Conductor, 1, 0, 240).TempoSnapshot);
            surface.TickSpan = 51200; // 100 ticks per pixel; exceeds the bounded UI query budget.
            Point point = new(58, y);
            typeof(TimelineSurface).GetField("_hoverPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, point);
            try
            {
                Invoke(surface, "RefreshHoverIntent");
                Assert.Equal(Cursors.Wait, surface.Cursor);
                PumpUntil(() => Field(surface, "_conductorHitReady") is true);
                Assert.False(Field(surface, "_exactQueryPending") is true);
                Assert.Equal(hit, Field(surface, "_conductorHitResult") is not null);
                Assert.NotEqual(Cursors.Wait, surface.Cursor);
                Assert.Equal(hit ? Cursors.SizeNS : Cursors.Arrow, surface.Cursor);
                Assert.Null(Field(surface, "_pendingConductorPress"));
            }
            finally { Cleanup(surface); }
        });
    }

    private static TimelineSurface Surface(TimelineRenderSnapshot snapshot, bool meta = false)
    {
        TimelineSurface surface = new()
        {
            SurfaceMode = meta ? TimelineSurfaceMode.Conductor : TimelineSurfaceMode.EventLanes,
            ToolMode = TimelineToolMode.Draw,
            TickSpan = 512,
            LaneHeight = 44,
            OperationStepTicks = 1,
            Snapshot = snapshot,
            GridVisible = false
        };
        Size size = new(meta ? 642 : 564, 280);
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        return surface;
    }

    private static bool Defer(TimelineSurface surface, Point point)
    {
        object?[] arguments = [null];
        Assert.True((bool)Invoke(surface, "TryCreateViewport", arguments)!);
        bool result = false;
        At(surface, point, () => result = (bool)Invoke(surface, "TryDeferConductorPress", Button(true), point, arguments[0])!);
        return result;
    }
    private static MouseButtonEventArgs Button(bool down) => new(Mouse.PrimaryDevice, 0, MouseButton.Left)
    { RoutedEvent = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent };
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static object? Invoke(object instance, string name, params object?[] arguments)
    {
        try { return instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void At(TimelineSurface surface, Point point, Action action)
    {
        FieldInfo field = typeof(TimelineSurface).GetField("_replayingConductorPosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo modifiers = typeof(TimelineSurface).GetField("_replayingConductorModifiers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? previousModifiers = modifiers.GetValue(surface);
        field.SetValue(surface, point);
        modifiers.SetValue(surface, ModifierKeys.None); // Synthetic input must not inherit the user's physical Shift/Alt/Ctrl keys.
        try { action(); }
        finally { field.SetValue(surface, null); modifiers.SetValue(surface, previousModifiers); }
    }
    private static void Cleanup(TimelineSurface surface) => surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    private static void PumpUntil(Func<bool> completed)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!completed() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            PumpDispatcher();
            Thread.Yield();
        }
        Assert.True(completed(), "The Conductor background query did not reach its dispatcher continuation.");
    }
    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
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
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Conductor interaction test exceeded its timeout.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class GatedSource<T>(T[] values) : IImmutableTimelineValueSource<T>, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(true);
        public int Count => values.Length;
        public int PageCapacity => 128;
        public T this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public void BlockReads() => _gate.Reset();
        public void ReleaseReads() => _gate.Set();
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            if (!_gate.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Conductor test page read was not released.");
            int start = pageIndex * PageCapacity;
            return values.AsMemory(start, Math.Min(PageCapacity, Count - start));
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        // Background cancellation may still unwind after the assertion. The lightweight
        // event is not disposed underneath an in-flight reader; no OS wait handle is used.
        public void Dispose() => _gate.Set();
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
