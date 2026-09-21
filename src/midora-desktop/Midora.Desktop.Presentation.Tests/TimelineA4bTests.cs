using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineA4bTests
{
    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void FitUsesIntegerDevicePixelsAndKeepsThreePixelMinimum(double dpi)
    {
        foreach (double height in new[] { 100d, 384, 520, 900 })
        {
            double result = TimelineSurface.CalculatePianoVerticalFit(height, dpi);
            Assert.Equal(Math.Max(3, Math.Floor(height * dpi / 128)), result * dpi, 8);
        }
    }

    [Fact]
    public void FitExcludesTheRulerAndWaitsForNonzeroLayout() => Sta(() =>
    {
        var surface = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll };
        Assert.False(surface.TryGetPianoVerticalFit(out _));
        surface.Measure(new(852, 520)); surface.Arrange(new Rect(0, 0, 852, 520));
        Assert.True(surface.TryGetPianoVerticalFit(out double height));
        Assert.Equal(3, height); // floor((520 - 24) / 128), not round(520 / 128).
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [Theory]
    [InlineData(49, 1, 0, 192, 49)]
    [InlineData(49, 1, 192, 192, 241)]
    [InlineData(817, 601, -192, 192, 625)]
    [InlineData(817, 601, -384, 192, 601)]
    [InlineData(long.MaxValue - 49, 1, 192, 192, long.MaxValue)]
    [InlineData(long.MaxValue, 200, long.MinValue, 192, 200)]
    public void TemplateLengthUsesDeltaSnapAndActualMinimum(long original, long minimum, long delta, long step, long expected)
        => Assert.Equal(expected, TimelineSurface.CalculateTemplateLength(original, minimum, delta, step));

    [Fact]
    public void HandleIsOnlyOnEnabledTemplateRulerAndDoesNotCoverTheNoteArea() => Sta(() =>
    {
        var s = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = new(1, "template", []),
            TickSpan = 800, RangeEndTick = 400, MinimumTemplateLength = 49, CanEdit = true, GridVisible = false };
        s.Measure(new(852, 500)); s.Arrange(new Rect(0, 0, 852, 500));
        Assert.Equal(TemplateTimelineMarker.TemplateEnd, s.HitTemplateMarker(new(452, 18)));
        Assert.Null(s.HitTemplateMarker(new(452, 50)));
        Assert.Null(s.HitTemplateMarker(new(430, 18)));
        s.CanEdit = false; // Right-click inspection is still possible while edit commands are locked.
        Assert.Equal(TemplateTimelineMarker.TemplateEnd, s.HitTemplateMarker(new(452, 18)));
        s.CanEdit = true; s.MinimumTemplateLength = null;
        Assert.Null(s.HitTemplateMarker(new(452, 18)));
        s.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [Fact]
    public void CapturedTemplateDragCommitsOnceAndCancellationNeverCommits() => Sta(() =>
    {
        var s = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = new(1, "template", []),
            TickSpan = 800, RangeEndTick = 401, MinimumTemplateLength = 201, CanEdit = true, GridVisible = false,
            OperationStepTicks = 96, TemplateEditRevision = 7, DataContext = new object() };
        using var host = new HwndSource(new HwndSourceParameters("A4b hidden capture")
        { Width = 852, Height = 500, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = s;
        s.Measure(new(852, 500)); s.Arrange(new Rect(0, 0, 852, 500)); s.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        List<TemplateMarkerEditEventArgs> commits = [];
        s.TemplateMarkerEditCompleted += (_, e) => commits.Add(e);
        Begin();
        Invoke("UpdateTemplateMarkerDrag", new Point(549, -200)); // 96 ticks, outside vertical bounds.
        Assert.Equal(497, s.TemplatePreviewEndTick);
        Assert.Equal(401, s.RangeEndTick); // The caption follows the draft, not a premature source edit.
        s.OperationStepTicks = 192; // Frozen Snap remains 96 throughout the gesture.
        Invoke("FinishTemplateMarkerDrag", new Point(549, -200));
        var committed = Assert.Single(commits);
        Assert.Equal(TemplateTimelineMarker.TemplateEnd, committed.Marker);
        Assert.Equal(401, committed.OldTick); Assert.Equal(497, committed.Tick); Assert.Equal(7, committed.Revision);
        Assert.False(s.IsMouseCaptured);
        Assert.Equal(401, s.TemplatePreviewEndTick);
        commits.Clear();
        Begin(); Invoke("UpdateTemplateMarkerDrag", new Point(-100, 100));
        s.TemplateEditRevision++;
        Invoke("FinishTemplateMarkerDrag", new Point(-100, 100)); Assert.Empty(commits);
        Assert.Equal(401, s.TemplatePreviewEndTick);
        Begin(); Invoke("ClearCapturedInteraction");
        Invoke("FinishTemplateMarkerDrag", new Point(600, 0)); Assert.Empty(commits);
        Begin(); s.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        Invoke("FinishTemplateMarkerDrag", new Point(600, 0)); Assert.Empty(commits);
        Begin(); s.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Invoke("FinishTemplateMarkerDrag", new Point(600, 0)); Assert.Empty(commits);
        host.RootVisual = null;
        s.RangeEndTick = 593; Assert.Equal(593, s.TemplatePreviewEndTick);
        s.RangeEndTick = 401; Assert.Equal(401, s.TemplatePreviewEndTick);

        void Begin()
        {
            typeof(TimelineSurface).GetField("_replayingConductorPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, new Point(453, 18));
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
            Invoke("HandleTimelineMouseDown", args);
            Assert.True(args.Handled); Assert.True(s.IsMouseCaptured);
        }
        object? Invoke(string name, params object[] args)
            => typeof(TimelineSurface).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s, args);
    });

    [Theory]
    [InlineData(800, 400, 0, 400, 400)]
    [InlineData(800, 800, 798, 800, 799)]
    [InlineData(1000000, 400, 0, 400, 1)]
    public void CoincidentMarkerCapsRemainIndividuallyTargetable(long span, long length, long start, long end, long preRoll) => Sta(() =>
    {
        var s = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = new(1, "template", []),
            TickSpan = span, RangeEndTick = length, MinimumTemplateLength = 1, GridVisible = false,
            TemplateLoopStartTick = start, TemplateLoopEndTick = end, TemplatePreRollTicks = preRoll };
        s.Measure(new(852, 500)); s.Arrange(new Rect(0, 0, 852, 500));
        var handles = new TemplateMarkerHandle[4];
        Assert.Equal(4, s.GetTemplateMarkerHandles(handles));
        Assert.Equal(4, handles.Select(h => h.Marker).Distinct().Count());
        foreach (var h in handles)
        {
            Assert.Equal(h.Marker, s.HitTemplateMarker(new(h.Bounds.X + h.Bounds.Width / 2, h.Bounds.Y + 5)));
            Assert.InRange(h.Bounds.X, 52, 842);
            Assert.Equal(52 + h.Tick * 800d / span, h.AnchorX, 6);
        }
        for (int i = 1; i < handles.Length; i++) Assert.True(handles[i].Bounds.Left >= handles[i - 1].Bounds.Right);
        s.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [Theory]
    [InlineData(TemplateTimelineMarker.LoopStart, 0, 399)]
    [InlineData(TemplateTimelineMarker.LoopEnd, 201, 800)]
    [InlineData(TemplateTimelineMarker.PreRoll, 0, 800)]
    [InlineData(TemplateTimelineMarker.TemplateEnd, 600, long.MaxValue)]
    public void MarkerDragBoundsFollowTheFormalTemplateRange(TemplateTimelineMarker marker, long minimum, long maximum)
    {
        Assert.True(TimelineSurface.TryGetTemplateMarkerBounds(marker, 800, 600, 200, 400, out long min, out long max));
        Assert.Equal(minimum, min); Assert.Equal(maximum, max);
    }

    [Fact]
    public void IncompleteAndExtremeLoopBoundsDoNotOverflow()
    {
        Assert.True(TimelineSurface.TryGetTemplateMarkerBounds(TemplateTimelineMarker.LoopStart, long.MaxValue, 1, null, null, out long a, out long b));
        Assert.Equal(0, a); Assert.Equal(long.MaxValue - 1, b);
        Assert.True(TimelineSurface.TryGetTemplateMarkerBounds(TemplateTimelineMarker.LoopEnd, long.MaxValue, 1, null, null, out a, out b));
        Assert.Equal(1, a); Assert.Equal(long.MaxValue, b);
        Assert.False(TimelineSurface.TryGetTemplateMarkerBounds(TemplateTimelineMarker.LoopEnd, long.MaxValue, 1, long.MaxValue, null, out _, out _));
        Assert.False(TimelineSurface.TryGetTemplateMarkerBounds(TemplateTimelineMarker.LoopStart, 800, 1, null, long.MinValue, out _, out _));
    }

    [Theory]
    [InlineData(TemplateTimelineMarker.LoopStart, 200, 296)]
    [InlineData(TemplateTimelineMarker.LoopEnd, 400, 496)]
    [InlineData(TemplateTimelineMarker.PreRoll, 96, 192)]
    public void MarkerDragUsesDraftPreviewAndCommitsOnce(TemplateTimelineMarker marker, long oldTick, long expected) => Sta(() =>
    {
        var s = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = new(1, "template", []),
            TickSpan = 800, RangeEndTick = 700, MinimumTemplateLength = 400, CanEdit = true, GridVisible = false,
            TemplateLoopStartTick = 200, TemplateLoopEndTick = 400, TemplatePreRollTicks = 96, TemplateLoopEditingEnabled = true,
            OperationStepTicks = 96, TemplateEditRevision = 9, DataContext = new object() };
        using var host = new HwndSource(new HwndSourceParameters("A4b marker hidden capture")
        { Width = 852, Height = 500, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = s;
        s.Measure(new(852, 500)); s.Arrange(new Rect(0, 0, 852, 500)); s.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        var handles = new TemplateMarkerHandle[4]; s.GetTemplateMarkerHandles(handles);
        Rect cap = handles.Single(h => h.Marker == marker).Bounds;
        Point down = new(cap.X + 5, cap.Y + 5), target = new(down.X + 96, -200);
        List<TemplateMarkerEditEventArgs> commits = [];
        s.TemplateMarkerEditCompleted += (_, e) => commits.Add(e);
        Begin();
        Invoke("UpdateTemplateMarkerDrag", target);
        Assert.Equal(expected, Preview()); Assert.Equal(oldTick, s.GetTemplateMarkerTick(marker)); Assert.Empty(commits);
        s.OperationStepTicks = 192;
        Invoke("FinishTemplateMarkerDrag", target);
        var edit = Assert.Single(commits); Assert.Equal(marker, edit.Marker); Assert.Equal(expected, edit.Tick); Assert.Equal(9, edit.Revision);
        Assert.Equal(oldTick, Preview()); Assert.False(s.IsMouseCaptured);
        commits.Clear();
        Begin(); Invoke("UpdateTemplateMarkerDrag", target); s.TemplateEditRevision++;
        Invoke("FinishTemplateMarkerDrag", target); Assert.Empty(commits); Assert.Equal(oldTick, Preview());
        if (marker != TemplateTimelineMarker.PreRoll)
        {
            s.TemplateLoopEditingEnabled = false;
            var args = Press(); Assert.True(args.Handled); Assert.False(s.IsMouseCaptured);
            Invoke("FinishTemplateMarkerDrag", target); Assert.Empty(commits);
        }
        s.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); host.RootVisual = null;
        void Begin() { Assert.True(Press().Handled); Assert.True(s.IsMouseCaptured); }
        MouseButtonEventArgs Press()
        {
            typeof(TimelineSurface).GetField("_replayingConductorPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, down);
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
            Invoke("HandleTimelineMouseDown", args); return args;
        }
        long? Preview() => marker switch
        {
            TemplateTimelineMarker.LoopStart => s.TemplatePreviewLoopStartTick,
            TemplateTimelineMarker.LoopEnd => s.TemplatePreviewLoopEndTick,
            _ => s.TemplatePreviewPreRollTicks
        };
        object? Invoke(string name, params object[] args) => typeof(TimelineSurface).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s, args);
    });

    [Theory]
    [InlineData(0)] [InlineData(777)] [InlineData(-221)]
    public void BarLabelsKeepGlobalPhaseAcrossPanAndSignatureChanges(long offset)
    {
        var p = new MidoraProject(480);
        p.Conductor.TimeSignatures.Add(new(p, 5000, 3, 4)); // Truncated bar is deliberate.
        var map = new ProjectTimeSignatureMap(p);
        List<TimelineGridLine> all = [], pan = [];
        TimelineGridPresentation.BuildBarRulerLines(0, 100000, map, all, 3400, offset);
        for (int start = 1; start < 20000; start += 317)
        {
            TimelineGridPresentation.BuildBarRulerLines(start, start + 60000, map, pan, 3400, offset);
            Assert.Equal(all.Where(v => v.Tick >= start && v.Tick < start + 60000), pan);
        }
    }

    [Fact]
    public void BarLabelsAreBoundedAtExtremeTicksAndManyTruncatedBars()
    {
        var p = new MidoraProject(480);
        for (int i = 1; i <= 10000; i++) p.Conductor.TimeSignatures.Add(new(p, i, 4, 4));
        var map = new ProjectTimeSignatureMap(p);
        List<TimelineGridLine> lines = [];
        TimelineGridPresentation.BuildBarRulerLines(0, long.MaxValue, map, lines, long.MaxValue / 1000);
        Assert.InRange(lines.Count, 1, 1002);
        TimelineGridPresentation.BuildBarRulerLines(long.MaxValue - 1000000, long.MaxValue, map, lines, 10000);
        Assert.InRange(lines.Count, 0, 102);
        Assert.All(lines, line => Assert.InRange(line.Tick, long.MaxValue - 1000000, long.MaxValue - 1));
    }

    [Fact]
    public void ShortPartialBarDoesNotMakeOrdinaryLabelsDisappearAndDenseChangesKeepPanPhase()
    {
        using var p = new MidoraProject(480);
        for (int i = 1; i <= 10000; i++) p.Conductor.TimeSignatures.Add(new(p, i, 4, 4));
        var map = new ProjectTimeSignatureMap(p);
        List<TimelineGridLine> all = [], pan = [];
        TimelineGridPresentation.BuildBarRulerLines(0, 100000, map, all, 3400);
        Assert.InRange(all.Count, 20, 31);
        for (int start = 1; start < 20000; start += 317)
        {
            TimelineGridPresentation.BuildBarRulerLines(start, start + 60000, map, pan, 3400);
            Assert.Equal(all.Where(v => v.Tick >= start && v.Tick < start + 60000), pan);
        }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
