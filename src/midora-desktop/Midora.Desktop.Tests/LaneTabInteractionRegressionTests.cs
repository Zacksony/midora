using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Xunit;

namespace Midora.Desktop.Tests;

public static partial class TimelineObjectListIntegrationTests
{
    private static void VerifyLaneHeaderDrag(MainWindow window, FrameworkElement content, WorkspaceViewModel workspace,
        LaneTabHeader header, ListBox tabs, LaneTabSession state)
    {
        var beforeSelection = workspace.Selection.SharedIds;
        var real = state.Descriptors;
        var ordinary = Enumerable.Range(0, 5).Select(i => new LaneTabDescriptor(new(3, Number: i), $"Test target {i}", 1)).ToArray();
        state.Sync(real.Where(r => r.Key.Type < 2).Concat(ordinary).ToArray());
        Invoke(header, "RenderHeader"); Layout(content);
        var source = ordinary[0]; var target = ordinary[2];
        // Selecting a target no longer discards every header container.
        var itemsSource = tabs.ItemsSource;
        state.Show(source.Key); Invoke(header, "RenderHeader");
        Assert.Same(itemsSource, tabs.ItemsSource);
        state.Show(LaneTabKey.Velocity); Invoke(header, "RenderHeader");
        var item = Assert.IsType<ListBoxItem>(tabs.ItemContainerGenerator.ContainerFromItem(target));
        var label = Descendants<TextBlock>(item).First();
        var payload = LaneDragPayload(header, state, source.Key);
        // Use the real routed event and the original MainWindow handler above
        // this header. Before the fix that ancestor replaces Move with None.
        foreach (var eventId in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent })
        foreach (double fraction in new[] { .05, .25, .5, .75, .95 })
        {
            var args = LaneDragEvent(payload, label, new Point(label.ActualWidth * fraction, label.ActualHeight / 2), eventId);
            label.RaiseEvent(args);
            Assert.True(args.Handled); Assert.Equal(DragDropEffects.Move, args.Effects);
        }
        var originalOrder = state.Visible().Select(r => r.Key).ToArray();
        var drop = LaneDragEvent(payload, item, new Point(item.ActualWidth * .75, item.ActualHeight / 2), DragDrop.DropEvent);
        item.RaiseEvent(drop); Layout(content);
        Assert.True(drop.Handled); Assert.Equal(DragDropEffects.Move, drop.Effects);
        Assert.Equal(new[] { ordinary[1].Key, ordinary[2].Key, ordinary[0].Key, ordinary[3].Key, ordinary[4].Key },
            state.Visible().Where(r => r.Key.Type >= 2).Select(r => r.Key));
        Assert.Equal(originalOrder.Where(k => k.Type < 2), state.Visible().Where(r => r.Key.Type < 2).Select(r => r.Key));
        Assert.Same(beforeSelection, workspace.Selection.SharedIds);

        item = Assert.IsType<ListBoxItem>(tabs.ItemContainerGenerator.ContainerFromItem(ordinary[1]));
        foreach (var invalid in new[] {
            LaneDragPayload(header, state, LaneTabKey.Velocity),
            LaneDragPayload(new LaneTabHeader(), state, source.Key),
            LaneDragPayload(header, new LaneTabSession(), source.Key),
            new DataObject("Midora.WorkspaceTab", workspace) })
        {
            var args = LaneDragEvent(invalid, item, new Point(10, 10), DragDrop.DragOverEvent);
            item.RaiseEvent(args);
            Assert.True(args.Handled); Assert.Equal(DragDropEffects.None, args.Effects);
        }
        var fixedItem = Assert.IsType<ListBoxItem>(tabs.ItemContainerGenerator.ContainerFromIndex(0));
        var fixedDrop = LaneDragEvent(payload, fixedItem, new Point(10, 10), DragDrop.DropEvent);
        fixedItem.RaiseEvent(fixedDrop); Assert.Equal(DragDropEffects.None, fixedDrop.Effects);
        // A bare gap is also a target; it does not require a label child hit.
        var trailing = LaneDragEvent(payload, tabs, new Point(tabs.ActualWidth - 24, tabs.ActualHeight / 2), DragDrop.DragOverEvent);
        tabs.RaiseEvent(trailing); Assert.Equal(DragDropEffects.Move, trailing.Effects);

        // Hundreds of long headers still realize only a small visible window;
        // wheel/edge scrolling never enumerates source music objects.
        state.Sync(real.Where(r => r.Key.Type < 2).Concat(Enumerable.Range(0, 500)
            .Select(i => new LaneTabDescriptor(new(3, Number: i), $"Long target {i} " + new string('W', 40), 1))).ToArray());
        Invoke(header, "RenderHeader"); Layout(content);
        var scroll = Descendants<ScrollViewer>(tabs).First();
        Assert.True(scroll.ScrollableWidth > 0);
        double offset = scroll.HorizontalOffset;
        tabs.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        Layout(content); Assert.True(scroll.HorizontalOffset > offset);
        Assert.InRange(Descendants<ListBoxItem>(tabs).Count(), 1, 30);
        Invoke(header, "ResetTabDrag");
        state.Sync(real); header.Refresh(); Layout(content);
    }

    private static DataObject LaneDragPayload(LaneTabHeader header, LaneTabSession state, LaneTabKey key)
    {
        var type = typeof(LaneTabHeader).GetNestedType("LaneTabDragData", BindingFlags.NonPublic)!;
        return new("Midora.LaneTab", Activator.CreateInstance(type, header, state, key)!);
    }

    private static DragEventArgs LaneDragEvent(IDataObject data, UIElement target, Point position, RoutedEvent routedEvent)
    {
        // WPF keeps construction internal. This runs its normal GetPosition and
        // routing against the hidden HwndSource, without desktop input injection.
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, position]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static void VerifyInstrumentLaneGestures(MainWindow window, FrameworkElement content, WorkspaceViewModel workspace,
        LaneTabHost host, LaneTabHeader header, LaneTabSession state)
    {
        state.Show(LaneTabKey.Instrument); header.Refresh(); Layout(content);
        var lane = Descendants<InstrumentChangeLane>(host).Single(l => l.IsVisible);
        lane.CommandHost = window;
        var points = Assert.IsType<InstrumentChangeLaneVisual>(lane.FindName("Points"));
        var backdrop = Assert.IsType<TimelineSurface>(lane.FindName("Backdrop"));
        var overlay = Assert.IsType<InstrumentChangeGestureVisual>(lane.FindName("GestureOverlay"));
        Assert.False(overlay.IsHitTestVisible);
        var selection = workspace.Selection.SharedIds;
        var settings = lane.Settings!;
        bool snap = settings.SnapEnabled;
        TimelineToolMode tool = workspace is TimelineWorkspaceViewModel t ? t.ToolMode : ((InstrumentWorkspaceViewModel)workspace).ToolMode;
        long savedStart = backdrop.StartTick;
        void SetTool(TimelineToolMode mode)
        { if (workspace is TimelineWorkspaceViewModel timeline) timeline.ToolMode = mode; else ((InstrumentWorkspaceViewModel)workspace).ToolMode = mode; }
        long Start() => workspace is TimelineWorkspaceViewModel timeline ? timeline.StartTick : ((InstrumentWorkspaceViewModel)workspace).TimelineStartTick;
        SetTool(TimelineToolMode.Draw);
        foreach (bool enabled in new[] { false, true })
        {
            settings.SnapEnabled = enabled;
            Set(lane, "_hoverPoint", new Point(213, 30));
            var preview = lane.CreationPreviewPoint!.Value;
            Assert.Equal(24 + (points.ActualHeight - 24) * .5, preview.Y, 6);
            Assert.Equal(lane.TickX(lane.PointTick(213)), preview.X, 6);
            Set(lane, "_hoverPoint", new Point(213, points.ActualHeight - 1));
            Assert.Equal(preview, lane.CreationPreviewPoint);
        }
        foreach (var outside in new[] { new Point(63, 30), new Point(213, 23), new Point(points.ActualWidth + 1, 30) })
        { Set(lane, "_hoverPoint", outside); Assert.Null(lane.CreationPreviewPoint); }
        SetTool(TimelineToolMode.Select); Set(lane, "_hoverPoint", new Point(213, 30));
        Assert.Null(lane.CreationPreviewPoint);
        Set(lane, "_dragging", true); Set(lane, "_pressPoint", new Point(120, 30));
        Set(lane, "_pressTick", lane.PointTick(120, false)); Set(lane, "_current", new Point(220, 80));
        DrawingGroup drawing = new();
        using (var dc = drawing.Open()) lane.DrawGesture(dc, new Typeface("Arial"), 1);
        var expectedColor = ((SolidColorBrush)lane.FindResource("Brush.Info")).Color;
        var outline = FlattenDrawing(drawing).OfType<GeometryDrawing>().Single(d => d.Pen is not null);
        Assert.Equal(expectedColor, ((SolidColorBrush)outline.Pen.Brush).Color);
        Assert.Equal(DashStyles.Dash.Dashes, outline.Pen.DashStyle.Dashes);
        Invoke(lane, "ResetGesture");

        // Capture-independent model math exercises the same Begin/Update path
        // as middle input, including outside release and the Int64 frontier.
        if (workspace is TimelineWorkspaceViewModel timeline) timeline.StartTick = 1024;
        else ((InstrumentWorkspaceViewModel)workspace).TimelineStartTick = 1024;
        Layout(content);
        double pixelsPerTick = (points.ActualWidth - 64) / backdrop.TickSpan;
        Invoke(lane, "BeginPan", new Point(240, 50));
        Invoke(lane, "UpdatePan", new Point(240 - 100 * pixelsPerTick, -200));
        Assert.Equal(1124, Start());
        Invoke(lane, "UpdatePan", new Point(240 + 2048 * pixelsPerTick, 5000));
        Assert.Equal(0, Start());
        Invoke(lane, "ResetGesture");
        Invoke(lane, "UpdatePan", new Point(-5000, 60)); Assert.Equal(0, Start());
        if (workspace is TimelineWorkspaceViewModel extremeTimeline) extremeTimeline.StartTick = long.MaxValue - 50;
        else ((InstrumentWorkspaceViewModel)workspace).TimelineStartTick = long.MaxValue - 50;
        Layout(content);
        Invoke(lane, "BeginPan", new Point(240, 50));
        Invoke(lane, "UpdatePan", new Point(240 - 100 * pixelsPerTick, 50));
        Assert.Equal(long.MaxValue - 1, Start());
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(points)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Assert.True(lane.HandleShortcut(escape)); Assert.True(escape.Handled);
        Assert.Null(Field(lane, "_panOrigin"));
        Invoke(lane, "UpdatePan", new Point(10000, 50)); Assert.Equal(long.MaxValue - 1, Start());
        if (workspace is TimelineWorkspaceViewModel finalTimeline) finalTimeline.StartTick = savedStart;
        else ((InstrumentWorkspaceViewModel)workspace).TimelineStartTick = savedStart;
        settings.SnapEnabled = snap; SetTool(tool); Set(lane, "_hoverPoint", null);
        Assert.Same(selection, workspace.Selection.SharedIds);
        state.Show(LaneTabKey.Velocity); header.Refresh(); Layout(content);
        lane.CommandHost = null;
    }

    private static IEnumerable<Drawing> FlattenDrawing(Drawing root)
    {
        yield return root;
        if (root is DrawingGroup group)
            foreach (var item in group.Children.SelectMany(FlattenDrawing)) yield return item;
    }
}

public sealed partial class WpfInteractionRegressionTests
{
    [Fact]
    public void LaneHeaderPlusAlwaysUsesTheSharedAddLaneCommand()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "midora-desktop", "Midora.Desktop", "LaneTabHeader.xaml.cs"));
        Assert.Contains("OnAddLane(object sender, RoutedEventArgs e) => Host?.AddLaneFromHeader(this);", source);
        Assert.DoesNotContain("AddAtCursor", source);
    }
}
