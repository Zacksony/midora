using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineA3GestureTests(ITestOutputHelper output)
{
    private static string _stage = "start";
    [Theory]
    [InlineData(TimelineSurfaceMode.Arrangement)]
    [InlineData(TimelineSurfaceMode.PianoRoll)]
    [InlineData(TimelineSurfaceMode.EventLanes)]
    [InlineData(TimelineSurfaceMode.Velocity)]
    [InlineData(TimelineSurfaceMode.Conductor)]
    public void RightMarqueeKeepsThePrimaryModeAndFrozenModifiers(TimelineSurfaceMode mode) => Sta(() =>
    {
        foreach (var tool in Enum.GetValues<TimelineToolMode>())
        foreach (var modifier in new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Alt })
        {
            var surface = Surface(mode, tool);
            try
            {
                var start = new Point(surface.LaneHeaderWidth + 40, 80);
                Invoke(surface, "BeginPendingRightGesture", start, modifier);
                Invoke(surface, "BeginRightButtonMarquee", start, start + new Vector(100, 60));
                Assert.Equal(MouseButton.Right, Field(surface, "_marqueeButton"));
                Assert.Equal(modifier, Field(surface, "_marqueeModifiers"));
                Assert.Null(Field(surface, "_eventPointOrigin"));
                Assert.Null(Field(surface, "_velocityOrigin"));
                Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation"));
                Assert.Equal(tool, surface.ToolMode);
            }
            finally { Unload(surface); }
        }
    });

    [Theory]
    [InlineData(TimelineSurfaceMode.PianoRoll, false)]
    [InlineData(TimelineSurfaceMode.PianoRoll, true)]
    [InlineData(TimelineSurfaceMode.Arrangement, true)]
    [InlineData(TimelineSurfaceMode.EventLanes, false)]
    public void CtrlRulerClickSetsOnlyEditCursorAndNeverClearsSelection(TimelineSurfaceMode mode, bool rangeEnabled) => Sta(() =>
    {
        var surface = Surface(mode, TimelineToolMode.Draw);
        surface.IsTimeRangeSelectionEnabled = rangeEnabled;
        var selection = new TimelineSelectionSnapshot(1, [new MidoraId(1)], new MidoraId(1));
        surface.SelectionSnapshot = selection;
        List<TimelineRulerEventArgs> clicks = []; int ranges = 0, selectionChanges = 0;
        surface.RulerClicked += (_, args) => clicks.Add(args);
        surface.TimeRangeSelected += (_, _) => ranges++;
        surface.BackgroundInvoked += (_, _) => selectionChanges++;
        try
        {
            Set(surface, "_replayingConductorPosition", new Point(surface.LaneHeaderWidth + 100, 10));
            Set(surface, "_replayingConductorModifiers", ModifierKeys.Control);
            Invoke(surface, "HandleTimelineMouseDown", MouseArgs(MouseButton.Left));
            Invoke(surface, "HandleTimelineMouseUp", MouseArgs(MouseButton.Left));
            Assert.True(Assert.Single(clicks).IsEditCursor); Assert.Equal(0, ranges); Assert.Equal(0, selectionChanges);
            Assert.Same(selection, surface.SelectionSnapshot);
            clicks.Clear();
            Invoke(surface, "HandleTimelineMouseDown", MouseArgs(MouseButton.Left));
            Set(surface, "_replayingConductorPosition", new Point(surface.LaneHeaderWidth + 200, 10));
            Invoke(surface, "HandleTimelineMouseUp", MouseArgs(MouseButton.Left));
            Assert.Empty(clicks); Assert.Equal(0, ranges);
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(TimelineValueTraceShape.Free, false, 1)]
    [InlineData(TimelineValueTraceShape.Line, false, 1)]
    [InlineData(TimelineValueTraceShape.Horizontal, false, 1)]
    [InlineData(TimelineValueTraceShape.Line, false, 8)]
    [InlineData(TimelineValueTraceShape.Horizontal, false, 8)]
    [InlineData(TimelineValueTraceShape.Line, true, 1)]
    public void NumericShapesUseLeftDrawingAndPreserveSnapAndShift(TimelineValueTraceShape shape, bool timeLocked, long step) => Sta(() =>
    {
        var surface = Surface(TimelineSurfaceMode.EventLanes, TimelineToolMode.Draw);
        surface.RangeStartTick = 0; surface.RangeEndTick = 1000; surface.OperationStepTicks = step; surface.ValueTraceShape = shape;
        TimelineEventPointTraceEventArgs? trace = null;
        surface.EventPointTraceCompleted += (_, args) => trace = args;
        var origin = new Point(152, 248); var end = new Point(352, 56);
        try
        {
            Set(surface, "_replayingConductorPosition", origin);
            Set(surface, "_replayingConductorModifiers", ModifierKeys.Alt | (timeLocked ? ModifierKeys.Shift : ModifierKeys.None));
            Invoke(surface, "HandleTimelineMouseDown", MouseArgs(MouseButton.Left));
            Assert.Equal(shape, Field(surface, "_activeValueTraceShape"));
            Invoke(surface, "UpdateEventPointTrace", origin, end);
            Set(surface, "_replayingConductorPosition", end);
            Invoke(surface, "HandleTimelineMouseUp", MouseArgs(MouseButton.Left));
            Assert.NotNull(trace);
            var samples = trace.Sample().ToArray();
            if (timeLocked) Assert.Single(samples);
            else Assert.True(samples.Length >= 20);
            Assert.All(samples, sample => Assert.Equal(0, sample.Tick % step));
            if (shape == TimelineValueTraceShape.Horizontal) Assert.All(samples, sample => Assert.Equal(.125, sample.NormalizedValue, 6));
            else if (!timeLocked) Assert.True(samples[^1].NormalizedValue > samples[0].NormalizedValue);
            Assert.Equal(shape, surface.ValueTraceShape);
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(TimelineToolMode.Select)]
    [InlineData(TimelineToolMode.Draw)]
    [InlineData(TimelineToolMode.Split)]
    [InlineData(TimelineToolMode.Erase)]
    public void AllFourteenCommandsUseReadableGroupedRowsInAnyMode(TimelineToolMode tool) => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        var surface = Surface(TimelineSurfaceMode.PianoRoll, tool);
        surface.FirstLane = 48; surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]);
        surface.Measure(new Size(452, 320)); surface.Arrange(new Rect(0, 0, 452, 320));
        try
        {
            Render(surface);
            Rect bounds = (Rect)Field(surface, "_selectionToolBounds")!;
            Rect[] cells = (Rect[])Field(surface, "_selectionActionBounds")!;
            Assert.Equal(14, cells.Length); Assert.Equal(170, bounds.Width);
            Assert.All(cells, cell => Assert.Equal(27, cell.Width));
            Assert.Equal(3, cells.Select(cell => cell.Y).Distinct().Count());
            Rect deselect = cells[(int)TimelineSelectionAction.DeselectAll];
            Assert.Equal(cells.Min(cell => cell.Y), deselect.Y);
            Assert.Equal(cells.Max(cell => cell.Right), deselect.Right);
            foreach (string name in new[] { "_selectionToolPinBounds", "_selectionToolResizeStartBounds", "_selectionToolResizeEndBounds", "_selectionToolMoveBounds" })
                Assert.Equal(deselect.Y, ((Rect)Field(surface, name)!).Y);
            Assert.All(cells, cell => Assert.True(bounds.Contains(cell)));
            Assert.All(cells.SelectMany((cell, index) => cells.Skip(index + 1).Select(other => (cell, other))),
                pair => Assert.False(pair.cell.IntersectsWith(new Rect(pair.other.X + .1, pair.other.Y + .1, pair.other.Width - .2, pair.other.Height - .2))));
            Assert.Equal(TimelineSelectionActions.HeaderHeight + TimelineSelectionActions.MaximumBodyHeight, bounds.Height);
            Assert.InRange(bounds.Bottom, 0, 320);
        }
        finally { Unload(surface); }
    });

    [Fact]
    public void WorldCoordinatesContinueOutsideViewportAndSaturateOnlyAtTickLimits()
    {
        var view = new TimelineViewport(1000, 2000, 48, 60, 400, 256, 8);
        Assert.Equal(0, TimelineSurface.WorldEditTick(view, -800));
        Assert.Equal(2250, TimelineSurface.WorldEditTick(view, 500));
        var end = view with { StartTick = long.MaxValue - 1000, EndTick = long.MaxValue };
        Assert.Equal(long.MaxValue, TimelineSurface.WorldEditTick(end, 800));
    }

    [Theory]
    [InlineData(TimelineSurfaceMode.PianoRoll)]
    [InlineData(TimelineSurfaceMode.Arrangement)]
    public void RightClickBeyondRowsTargetsBackgroundWithoutChangingSelection(TimelineSurfaceMode mode) => Sta(() =>
    {
        var surface = Surface(mode, TimelineToolMode.Draw);
        surface.Measure(new Size(452, 1400)); surface.Arrange(new Rect(0, 0, 452, 1400));
        var selection = new TimelineSelectionSnapshot(1, [new MidoraId(1)], new MidoraId(1));
        surface.SelectionSnapshot = selection;
        Point position = new(surface.LaneHeaderWidth + 50, 1300);
        try
        {
            Set(surface, "_replayingConductorPosition", position);
            Invoke(surface, "HandleTimelineMouseDown", MouseArgs(MouseButton.Right));
            Assert.Equal(false, surface.ContextTargetHasObject);
            Assert.Equal(position, surface.ContextTargetPosition);
            Assert.Same(selection, surface.SelectionSnapshot);
            Assert.Null(Field(surface, "_pendingRightGestureOrigin"));
        }
        finally { Unload(surface); }
    });

    [Fact]
    public void EventValueDragUsesWorldValueBeyondZoomedViewport() => Sta(() =>
    {
        var surface = Surface(TimelineSurfaceMode.EventLanes, TimelineToolMode.Draw);
        try
        {
            Set(surface, "_valueViewMinimum", .25); Set(surface, "_valueViewMaximum", .75);
            Set(surface, "_dragOriginNormalizedValue", .5);
            double delta = (double)Invoke(surface, "GetDragNormalizedValueDelta", surface.TimelineRulerHeight - 128)!;
            Assert.Equal(.5, delta, 6); // world value 1, not the viewport maximum .75
        }
        finally { Unload(surface); }
    });

    [Fact]
    public void RevisionChangeCancelsFrozenDragWithoutPublishingAnEdit() => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Draw);
        surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]);
        int edits = 0; surface.ItemEditCompleted += (_, _) => edits++;
        try
        {
            Set(surface, "_dragItem", note); Set(surface, "_dragActivated", true); Invoke(surface, "FreezeEditGesture");
            surface.SelectionSnapshot = new(2, [], null);
            Invoke(surface, "HandleTimelineMouseUp", MouseArgs(MouseButton.Left));
            Assert.Equal(0, edits); Assert.Null(Field(surface, "_dragItem"));
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ColdRightClickShowsLocatingImmediatelyAndMenuCloseCancelsTheQuery(int iteration) => Sta(() =>
    {
        var source = new BlockedSource(); var surface = Surface(TimelineSurfaceMode.EventLanes, TimelineToolMode.Draw);
        _stage = "surface";
        surface.Snapshot = new(2 + iteration, "cold-a3", [], itemSource: source);
        using var host = new HwndSource(new HwndSourceParameters("A3 hidden test") { Width = 452, Height = 280, WindowStyle = unchecked((int)0x80000000) });
        _stage = "host";
        host.RootVisual = surface; PumpUntil(() => surface.IsLoaded);
        _stage = "loaded";
        int invoked = 0; surface.ItemInvoked += (_, _) => invoked++;
        try
        {
            source.Block = true; source.Started.Reset(); source.Completed.Reset();
            Invoke(surface, "BeginPendingRightGesture", new Point(152, 152), ModifierKeys.None);
            _stage = "query-started";
            Assert.True(source.Started.Wait(TimeSpan.FromSeconds(3)));
            Set(surface, "_pendingRightGestureOrigin", null); Set(surface, "_delayedContextMenuAwaitingMouseUp", false);
            Invoke(surface, "StartDelayedContextMenuTimer"); Invoke(surface, "TryOpenDelayedContextMenu");
            _stage = "menu-opened";
            Assert.True(surface.ContextMenu.IsOpen); Assert.True(surface.IsContextTargetPending);
            var item = Assert.IsType<MenuItem>(surface.ContextMenu.Items[0]); Assert.Equal("Locating…", item.Header); Assert.False(item.IsEnabled);
            bool closed = false;
            surface.ContextMenu.Closed += (_, _) => closed = true;
            surface.ContextMenu.IsOpen = false;
            Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation"));
            source.Release.Set();
            _stage = "closed";
            // IsOpen cancellation is immediate, while Closed/native popup
            // teardown may wait for animation. Drain that before STA shutdown.
            PumpUntil(() => closed && source.Completed.IsSet); Pump();
            Assert.Null(Field(surface, "_delayedContextMenuQueryCancellation")); Assert.Equal(0, invoked);
        }
        finally
        {
            _stage = "cleanup-release"; source.Release.Set();
            _stage = "cleanup-menu"; surface.ContextMenu.IsOpen = false;
            _stage = "cleanup-unload"; Unload(surface);
            _stage = "cleanup-root"; host.RootVisual = null;
            _stage = "cleanup-pump"; Pump();
            _stage = "cleanup-dispose"; host.Dispose();
            _stage = "cleanup-done";
        }
    });

    [Fact]
    public void SourceChangeClosesAnAlreadyResolvedContextMenu() => Sta(() =>
    {
        var surface = Surface(TimelineSurfaceMode.EventLanes, TimelineToolMode.Draw);
        surface.Snapshot = new(1, "ready-menu", [], itemSource: new BlockedSource());
        using var host = new HwndSource(new HwndSourceParameters("A3 ready menu")
            { Width = 452, Height = 280, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = surface; PumpUntil(() => surface.IsLoaded);
        bool closed = false; surface.ContextMenu.Closed += (_, _) => closed = true;
        try
        {
            Invoke(surface, "BeginPendingRightGesture", new Point(152, 152), ModifierKeys.None);
            Set(surface, "_pendingRightGestureOrigin", null); Set(surface, "_delayedContextMenuAwaitingMouseUp", false);
            Invoke(surface, "StartDelayedContextMenuTimer"); Invoke(surface, "TryOpenDelayedContextMenu");
            PumpUntil(() => surface.ContextMenu.IsOpen && !surface.IsContextTargetPending);
            Assert.Equal(true, surface.ContextTargetHasObject);
            surface.Snapshot = new(2, "changed-menu-source", []);
            Assert.False(surface.ContextMenu.IsOpen);
            PumpUntil(() => closed);
            Assert.Null(surface.ContextTargetPosition);
        }
        finally { surface.ContextMenu.IsOpen = false; Unload(surface); host.RootVisual = null; Pump(); }
    });

    [Theory]
    [InlineData(TimelineItemKind.DirectMidiNote, TimelineItemEditKind.Move, false)]
    [InlineData(TimelineItemKind.LogicalNote, TimelineItemEditKind.Move, true)]
    [InlineData(TimelineItemKind.TemplateNote, TimelineItemEditKind.ResizeStart, false)]
    [InlineData(TimelineItemKind.TemplateNote, TimelineItemEditKind.ResizeEnd, false)]
    public void CapturedStationaryPointerScrollsAndSelectionChangeStopsIt(
        TimelineItemKind kind, TimelineItemEditKind edit, bool vertical) => Sta(() =>
    {
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Draw);
        var note = new TimelineRenderItem(new(1), kind, 1100, 1200, 50, .5, 0, TimelineItemState.None);
        surface.StartTick = 1000; surface.FirstLane = 48;
        surface.Snapshot = new(2, "a3-auto-scroll", [note]);
        surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]);
        using var host = new HwndSource(new HwndSourceParameters("A3 captured hidden test")
            { Width = 452, Height = 280, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = surface; PumpUntil(() => surface.IsLoaded);
        try
        {
            Set(surface, "_dragItem", note); Set(surface, "_dragKind", edit);
            Set(surface, "_dragOrigin", new Point(152, 40)); Set(surface, "_dragOriginTick", 1100L);
            Set(surface, "_dragOriginLane", 50); Set(surface, "_dragActivated", true);
            Invoke(surface, "FreezeEditGesture");
            Assert.True(surface.CaptureMouse());
            Point pointer = vertical ? new Point(200, 480) : new Point(650, 100);
            long start = surface.StartTick; int lane = surface.FirstLane;
            Invoke(surface, "TrackEditAutoScroll", pointer);
            Assert.NotNull(Field(surface, "_editAutoScrollTimer"));
            PumpUntil(() => vertical ? surface.FirstLane > lane : surface.StartTick > start);
            Assert.True(vertical ? (int)Field(surface, "_dragCurrentLane")! > lane + 31
                : (long)Field(surface, "_dragCurrentTick")! > surface.StartTick + surface.TickSpan);
            surface.SelectionSnapshot = new(2, [], null);
            Assert.Null(Field(surface, "_editAutoScrollTimer")); Assert.Null(Field(surface, "_dragItem"));
            start = surface.StartTick; lane = surface.FirstLane;
            Invoke(surface, "OnEditAutoScrollTick", null, EventArgs.Empty); Pump();
            Assert.Equal(start, surface.StartTick); Assert.Equal(lane, surface.FirstLane);
        }
        finally { Unload(surface); host.RootVisual = null; Pump(); }
    });

    [Fact]
    public void ShortViewportScrollsEveryToolbarCommandIntoReach() => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Select);
        surface.FirstLane = 48; surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]);
        surface.Measure(new Size(154, 112)); surface.Arrange(new Rect(0, 0, 154, 112));
        try
        {
            Render(surface);
            double contentHeight = (double)Field(surface, "_selectionToolContentHeight")!;
            Rect bounds = (Rect)Field(surface, "_selectionToolBounds")!;
            Assert.True(contentHeight > bounds.Height);
            var reached = new HashSet<int>();
            for (double offset = 0; offset < contentHeight; offset += 4)
            {
                Set(surface, "_selectionToolScrollOffset", offset); Render(surface);
                bounds = (Rect)Field(surface, "_selectionToolBounds")!;
                Assert.InRange(bounds.Bottom, 0, 112);
                var cells = (Rect[])Field(surface, "_selectionActionBounds")!;
                Rect body = (Rect)Field(surface, "_selectionToolBodyBounds")!;
                for (int i = 0; i < cells.Length; i++) if (body.Contains(cells[i])) reached.Add(i);
            }
            Assert.Equal(14, reached.Count);
        }
        finally { Unload(surface); }
    });

    [Fact]
    public void HoverNamesAreImmediateIncludingDisabledActionsAndDeselectWorksReadOnly() => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Select);
        surface.FirstLane = 48; surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]); surface.CanEdit = false;
        try
        {
            Render(surface);
            Rect[] cells = (Rect[])Field(surface, "_selectionActionBounds")!;
            foreach (var descriptor in TimelineSelectionActions.All)
            {
                Rect cell = cells[(int)descriptor.Action];
                Invoke(surface, "UpdateSelectionToolTip", new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2));
                Assert.Equal(descriptor.Label, Field(surface, "_selectionToolHoverName"));
                Assert.Null(surface.ToolTip);
            }
            Assert.False((bool)Invoke(surface, "SelectionActionEnabled", (int)TimelineSelectionAction.Delete)!);
            Assert.True((bool)Invoke(surface, "SelectionActionEnabled", (int)TimelineSelectionAction.DeselectAll)!);
            int deselect = 0;
            surface.SelectionActionRequested += (_, args) => { Assert.Equal(TimelineSelectionAction.DeselectAll, args.Action); deselect++; };
            var target = cells[(int)TimelineSelectionAction.DeselectAll];
            var point = new Point(target.X + 14, target.Y + 14);
            Assert.True((bool)Invoke(surface, "TryPressSelectionAction", point)!);
            Set(surface, "_replayingConductorPosition", point);
            Invoke(surface, "HandleTimelineMouseUp", MouseArgs(MouseButton.Left));
            Assert.Equal(1, deselect);
            Invoke(surface, "UpdateSelectionToolTip", new Point(-1, -1));
            Assert.Equal("Selection", Field(surface, "_selectionToolHoverName"));
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(TimelineItemKind.DirectMidiNote)]
    [InlineData(TimelineItemKind.LogicalNote)]
    [InlineData(TimelineItemKind.TemplateNote)]
    public void CopyPreviewUsesUnclampedNotePitchDeltaAndOmitsOutOfRangeTargets(TimelineItemKind kind) => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), kind, 100, 200, 0, .5, 0, TimelineItemState.None);
        var safe = note with { Id = new(2), Lane = 16 };
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Select);
        surface.SelectionSnapshot = new(1, [note.Id, safe.Id], note.Id, [note, safe]);
        var view = new TimelineViewport(0, 400, 0, 128, 400, 256, 8);
        try
        {
            foreach (var modifier in new[] { ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Alt })
            {
                Render(surface);
                Rect move = (Rect)Field(surface, "_selectionToolMoveBounds")!;
                Set(surface, "_replayingConductorModifiers", modifier);
                Invoke(surface, "TryBeginSelectionFloatingToolGesture", new Point(move.X + 14, move.Y + 14), view);
                Assert.True((bool)Field(surface, "_dragCopyRequested")!);
                Invoke(surface, "PrepareDragPreviewSelection", note);
                Set(surface, "_dragCurrentLane", (int)Field(surface, "_dragOriginLane")! - 2);
                var transform = Invoke(surface, "GetDragPreviewTransform", note)!;
                Assert.Equal(-2, transform.GetType().GetProperty("LaneDelta")!.GetValue(transform));
                object?[] outside = [note, transform, view, surface.LaneHeaderWidth, surface.TimelineRulerHeight, null];
                object?[] inside = [safe, transform, view, surface.LaneHeaderWidth, surface.TimelineRulerHeight, null];
                Assert.False((bool)Invoke(surface, "TryGetDragPreviewBounds", outside)!);
                // The second note remains in range; no whole-group pitch clamp.
                Assert.True((bool)Invoke(surface, "TryGetDragPreviewBounds", inside)!);
                Invoke(surface, "ClearCapturedInteraction"); surface.ReleaseMouseCapture();
            }
        }
        finally { Unload(surface); }
    });

    [Fact]
    public void ToolbarIconsFitVisibleInkNotTheSvgArtboard() => Sta(() =>
    {
        Geometry geometry = Geometry.Parse("M3 3H17V17H3Z");
        Rect cell = new(40, 80, 27, 27);
        Rect ink = TimelineSurface.SelectionToolIconBounds(geometry, cell);
        Assert.Equal(15, ink.Width); Assert.Equal(15, ink.Height);
        Assert.Equal(cell.X + 13.5, ink.X + ink.Width / 2);
        Assert.Equal(cell.Y + 13.5, ink.Y + ink.Height / 2);
    });

    [Fact]
    public void GroupedToolbarUsesTheRealThemeAndProducesAnOffscreenVisual() => Sta(() =>
    {
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Select);
        surface.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Midora.Desktop.Presentation;component/Themes/FluentSystemIcons.xaml", UriKind.Relative)));
        surface.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Midora.Desktop.Presentation;component/Themes/Palette.xaml", UriKind.Relative)));
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        surface.SelectionSnapshot = new(1, [note.Id], note.Id, [note]);
        try
        {
            Render(surface);
            foreach (var action in TimelineSelectionActions.All)
                Assert.IsAssignableFrom<Geometry>(surface.FindResource("Fluent." + action.ToolbarIcon));
            foreach (var icon in new[] { TimelineSelectionActions.PinIcon, TimelineSelectionActions.ResizeStartIcon,
                TimelineSelectionActions.ResizeEndIcon, TimelineSelectionActions.MoveIcon })
                Assert.IsAssignableFrom<Geometry>(surface.FindResource("Fluent." + icon));
            Rect cell = (Rect)Field(surface, "_selectionToolMoveBounds")!;
            Set(surface, "_hoverPoint", new Point(cell.X + 14, cell.Y + 14));
            surface.InvalidateVisual(); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = System.IO.Path.Combine(AppContext.BaseDirectory, ".tmp", "a3-visual");
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "selection-toolbar.png");
            using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
            output.WriteLine(path);
            Assert.Equal("Move (Ctrl+drag: copy)", Field(surface, "_selectionToolHoverName"));
            Assert.Null(surface.ToolTip);
            Assert.Equal(EdgeMode.Aliased, RenderOptions.GetEdgeMode(surface));
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(surface));
            var cache = Assert.IsType<TimelineSelectionIconCache>(Field(surface, "_selectionToolIcons"));
            Assert.Equal(18, cache.Count);
            Unload(surface);
            Assert.Equal(0, cache.Count);
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(TimelineItemKind.DirectMidiEvent, 0, 127)]
    [InlineData(TimelineItemKind.LogicalParameterPoint, 0, 1)]
    [InlineData(TimelineItemKind.DirectMidiEvent, -8192, 8191)] // SubVoice event projection, not TemplateEvent notes.
    public void EventCopyPreviewKeepsTheExistingSharedValueClamp(TimelineItemKind kind, double minimum, double maximum) => Sta(() =>
    {
        var first = new TimelineRenderItem(new(1), kind, 100, 101, 0, .1, 0, TimelineItemState.None);
        var last = first with { Id = new(2), StartTick = 200, EndTick = 201, Value = .9 };
        var surface = Surface(TimelineSurfaceMode.EventLanes, TimelineToolMode.Draw);
        surface.ValueAxisMinimum = minimum; surface.ValueAxisMaximum = maximum;
        surface.CanDragEventValue = true; surface.SelectionSnapshot = new(1, [first.Id, last.Id], first.Id, [first, last]);
        try
        {
            Set(surface, "_dragKind", TimelineItemEditKind.Move); Set(surface, "_dragCopyRequested", true);
            Set(surface, "_dragOriginNormalizedValue", .5);
            Set(surface, "_hoverPoint", new Point(200, -200));
            Invoke(surface, "PrepareDragPreviewSelection", first);
            var transform = Invoke(surface, "GetDragPreviewTransform", first)!;
            Assert.Equal(.1, (double)transform.GetType().GetProperty("ValueDelta")!.GetValue(transform)!, 8);
        }
        finally { Unload(surface); }
    });

    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(400_000)] [InlineData(1_000_000)]
    public void ToolbarAndCapabilitiesUseFixedSizeMetricsAtEverySelectionScale(int count) => Sta(() =>
    {
        var note = new TimelineRenderItem(new(1), TimelineItemKind.DirectMidiNote, 100, 200, 60, .5, 0, TimelineItemState.None);
        var metrics = new Dictionary<TimelineItemKind, TimelineSelectionMetrics>
        { [note.Kind] = new(count, 100, 10000, 0, 127, .2, .8, note) };
        var surface = Surface(TimelineSurfaceMode.PianoRoll, TimelineToolMode.Draw);
        surface.SelectionSnapshot = new(1, Enumerable.Range(1, count).Select(i => new MidoraId(i)), note.Id, metrics);
        var viewport = new TimelineViewport(0, 400, 0, 128, 400, 256, 8);
        try
        {
            for (int i = 0; i < 5; i++) Layout();
            long before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) Layout();
            clock.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{count}: {clock.Elapsed}");
            Assert.InRange(allocated, 0, 8 * 1024 * 1024);
            output.WriteLine($"A3 toolbar {count:N0}: 1000 layouts {clock.Elapsed.TotalMilliseconds:F2} ms, {allocated:N0} allocated bytes");
        }
        finally { Unload(surface); }
        void Layout()
        {
            object?[] args = [viewport, null, null, null];
            Assert.True((bool)Invoke(surface, "TryGetSelectionFloatingToolLayout", args)!);
            foreach (var action in TimelineSelectionActions.All) Assert.True(TimelineSelectionActions.CanOffer(action.Action, surface.SelectionSnapshot, true));
        }
    });

    private static TimelineSurface Surface(TimelineSurfaceMode mode, TimelineToolMode tool)
    {
        var result = new TimelineSurface { SurfaceMode = mode, ToolMode = tool, TickSpan = 400, Snapshot = new(1, "a3", []),
            ContextMenu = new(), LaneHeight = 8, RangeStartTick = 0, RangeEndTick = 1000, OperationStepTicks = 1, GridVisible = false };
        result.Measure(new Size(452, 280)); result.Arrange(new Rect(0, 0, 452, 280)); return result;
    }
    private static MouseButtonEventArgs MouseArgs(MouseButton button) => new(Mouse.PrimaryDevice, 0, button) { RoutedEvent = UIElement.MouseUpEvent };
    private static object? Field(object o, string name) => typeof(TimelineSurface).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o);
    private static void Set(object o, string name, object? value) => typeof(TimelineSurface).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
    private static object? Invoke(object o, string name, params object?[] args)
    {
        try { return typeof(TimelineSurface).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args); }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void Render(TimelineSurface s) { s.InvalidateVisual(); s.UpdateLayout(); var b = new RenderTargetBitmap((int)s.ActualWidth, (int)s.ActualHeight, 96, 96, PixelFormats.Pbgra32); b.Render(s); }
    private static void Unload(TimelineSurface s) => s.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    private static void Pump()
    {
        // A visible Popup/render queue need not ever become ApplicationIdle.
        // Bound each pump instead of hanging the test on that unrelated state.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void PumpUntil(Func<bool> test) { var w = Stopwatch.StartNew(); while (!test() && w.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Yield(); } Assert.True(test()); }
    private static void Sta(Action action)
    {
        Exception? failure = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } finally { _stage = "dispatcher-shutdown"; Dispatcher.CurrentDispatcher.InvokeShutdown(); _stage = "dispatcher-stopped"; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), _stage);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class BlockedSource : ITimelineRenderItemSource
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Completed { get; } = new(false);
        public volatile bool Block;
        public long Count => 1; public long MaximumEndTick => 101; public ulong ContentFingerprint => 991;
        public void QueryInto(long s, long e, int f, int l, List<TimelineRenderItem> target) => VisitInto(s, e, f, l, target.Add);
        public void VisitInto(long s, long e, int f, int l, Action<TimelineRenderItem> visitor)
        {
            // Only the narrow exact context hit is cold. Viewport raster/prefetch
            // tasks must not all contend on the artificial test gate.
            bool coldHit = Block && s > 0 && e < 400;
            if (coldHit) Started.Set();
            try
            {
                if (coldHit) Release.Wait(TimeSpan.FromSeconds(3));
                if (s <= 100 && e > 100) visitor(new(new(1), TimelineItemKind.DirectMidiEvent, 100, 101, 0, .5, 0, TimelineItemState.None));
            }
            finally { if (coldHit) Completed.Set(); }
        }
        public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
        public IEnumerable<TimelineRenderItem> EnumerateAll() => [];
    }
}
