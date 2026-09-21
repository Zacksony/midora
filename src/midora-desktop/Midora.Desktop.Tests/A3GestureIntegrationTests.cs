using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public static partial class TimelineObjectListIntegrationTests
{
    private static void VerifyA3Commands(MainWindow window, DesktopSessionController session,
        FrameworkElement content, WorkspaceViewModel workspace, TimelineSurface piano, bool mixed)
    {
        CurrentTestStage = $"A3 {workspace.GetType().Name} mixed={mixed}: capabilities";
        long revision = session.Document!.PublicationRevision;
        var ids = workspace.Selection.SharedIds;
        var available = Complete((Task<IReadOnlySet<TimelineSelectionAction>>)Invoke(window,
            "ReadSelectionActionAvailabilityAsync", piano, CancellationToken.None)!);
        foreach (var descriptor in TimelineSelectionActions.All)
        {
            Assert.Contains(descriptor.Action, available);
            Assert.IsAssignableFrom<Geometry>(piano.FindResource("Fluent." + descriptor.Icon));
        }
        Assert.Same(ids, workspace.Selection.SharedIds);
        Assert.Equal(revision, session.Document.PublicationRevision);

        var menu = new ContextMenu { PlacementTarget = piano, Tag = TimelineSelectionAction.Scale };
        if (mixed) Complete((Task)Invoke(window, "PopulateObjectListMenuAsync", menu, workspace, null, false, CancellationToken.None)!);
        else Invoke(window, "OnTimelineContextMenuOpened", menu, new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        MainWindow.FilterSelectionActionMenu(menu.Items, "Scale");
        var targets = menu.Items.OfType<MenuItem>().ToArray();
        Assert.NotEmpty(targets);
        if (mixed)
        {
            Assert.Equal(2, targets.Length);
            Assert.All(targets, target =>
            {
                Assert.StartsWith("For ", Assert.IsType<string>(target.Header));
                Assert.StartsWith("Scale", Assert.IsType<string>(Assert.Single(target.Items.OfType<MenuItem>()).Header));
            });
        }
        else Assert.StartsWith("Scale", Assert.IsType<string>(Assert.Single(targets).Header));

        // A blank hit preserves selection and offers the same full commands.
        // Mixed selections keep explicit type submenus rather than dropping types.
        Set(piano, "<ContextTargetPosition>k__BackingField", new Point(piano.LaneHeaderWidth + 20, piano.TimelineRulerHeight + 20));
        Set(piano, "<ContextTargetHasObject>k__BackingField", false);
        try
        {
            CurrentTestStage = $"A3 mixed={mixed}: blank menu open";
            menu = new() { PlacementTarget = piano, Placement = PlacementMode.RelativePoint };
            menu.Opened += (sender, args) => Invoke(window, "OnTimelineContextMenuOpened", sender, args);
            menu.IsOpen = true;
            CurrentTestStage = $"A3 mixed={mixed}: blank menu wait";
            PumpUntil(() => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Deselect All")));
            string[] headers = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.Contains("Paste", headers);
            Assert.Contains("Copy", headers); Assert.Contains("Cut", headers);
            if (mixed)
            {
                Assert.Contains("For Notes", headers); Assert.Contains("For Events", headers);
                Assert.Contains("Delete All Selected", headers);
                Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy")).IsEnabled);
            }
            else
            {
                Assert.Contains(headers, h => h.StartsWith("Delete"));
                Assert.Contains(headers, h => h.StartsWith("Scale"));
                Assert.Contains(headers, h => h.StartsWith("Properties"));
            }
            var noteCommands = mixed
                ? menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "For Notes")).Items : menu.Items;
            foreach (string command in new[] { "Copy", "Cut", "Flip Horizontal", "Flip Vertical", "Scale", "Transpose", "Batch Edit", "Humanize", "Split", "Join", "Quantize", "Properties" })
                Assert.Contains(noteCommands.OfType<MenuItem>(), item => ((string)item.Header).StartsWith(command) && item.IsEnabled);
            Assert.Same(ids, workspace.Selection.SharedIds);
        }
        finally
        {
            CurrentTestStage = $"A3 mixed={mixed}: blank menu close";
            menu.IsOpen = false; Pump();
            Set(piano, "<ContextTargetPosition>k__BackingField", null);
            Set(piano, "<ContextTargetHasObject>k__BackingField", null);
        }
        if (!mixed)
        {
            CurrentTestStage = "A3 note selection: blank sibling event surface";
            var host = Descendants<LaneTabHost>(content).Single(control => control.IsVisible && ReferenceEquals(control.DataContext, workspace));
            var header = host.Header!;
            var state = (LaneTabSession)Field(header, "_state")!;
            var originalLane = state.Active;
            state.Show(state.Descriptors.First(lane => lane.Key.Type >= 2).Key);
            header.Refresh(); Layout(content); // The inactive content is deliberately not realized.
            var events = Descendants<TimelineSurface>(content).First(surface => ReferenceEquals(surface.DataContext, workspace)
                && surface.SurfaceMode == TimelineSurfaceMode.EventLanes);
            Set(events, "<ContextTargetPosition>k__BackingField", new Point(events.LaneHeaderWidth + 20, events.TimelineRulerHeight + 20));
            Set(events, "<ContextTargetHasObject>k__BackingField", false);
            menu = new() { PlacementTarget = events, Placement = PlacementMode.RelativePoint };
            menu.Opened += (sender, args) => Invoke(window, "OnTimelineContextMenuOpened", sender, args);
            try
            {
                menu.IsOpen = true;
                PumpUntil(() => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Deselect All")));
                foreach (string command in new[] { "Copy", "Scale…", "Transpose…", "Humanize…", "Properties…" })
                    Assert.True(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, command)).IsEnabled);
                Assert.Same(ids, workspace.Selection.SharedIds);
            }
            finally
            {
                menu.IsOpen = false; Pump();
                Set(events, "<ContextTargetPosition>k__BackingField", null);
                Set(events, "<ContextTargetHasObject>k__BackingField", null);
                state.Show(originalLane); header.Refresh(); Layout(content);
            }
        }
        var source = workspace.Selection.HomogeneousTimelineSource;
        CurrentTestStage = $"A3 mixed={mixed}: deselect";
        int history = session.Document.History.Count;
        Invoke(window, "OnSelectionActionRequested", piano,
            new TimelineSelectionActionEventArgs(TimelineSelectionAction.DeselectAll, new Point())
            { RoutedEvent = TimelineSurface.SelectionActionRequestedEvent, Source = piano });
        Assert.Empty(workspace.Selection.Ids);
        Assert.Equal(history, session.Document.History.Count);
        Assert.Equal(revision, session.Document.PublicationRevision);
        // With nothing selected, a blank content target still has no editing commands.
        Set(piano, "<ContextTargetHasObject>k__BackingField", false);
        Set(piano, "<ContextTargetPosition>k__BackingField", new Point(piano.LaneHeaderWidth + 20, piano.TimelineRulerHeight + 20));
        try
        {
            menu = new() { PlacementTarget = piano };
            Invoke(window, "OnTimelineContextMenuOpened", menu, new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Paste"));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => ((string)item.Header).StartsWith("Scale") || Equals(item.Header, "Copy"));
        }
        finally
        {
            Set(piano, "<ContextTargetHasObject>k__BackingField", null);
            Set(piano, "<ContextTargetPosition>k__BackingField", null);
        }
        if (source is { } homogeneous) workspace.Selection.ApplyRange(ids, WorkspaceSelectionRangeMode.Replace, homogeneous);
        else workspace.Selection.AdoptMaterialized(ids);
        session.RefreshWorkspaceSelection(workspace);
        CurrentTestStage = $"A3 mixed={mixed}: restore layout";
        Layout(content);
        CurrentTestStage = $"A3 mixed={mixed}: complete";
    }

    private static void VerifyInstrumentToolbarLayout(InstrumentChangeLane lane, WorkspaceViewModel workspace)
    {
        var oldAnchor = Field(lane, "_floatingAnchor");
        var oldRevision = Field(lane, "_floatingSelectionRevision");
        try
        {
            Set(lane, "_floatingAnchor", new InstrumentChangeValue(new(1), 1000, 0, 0, 0));
            Set(lane, "_floatingSelectionRevision", workspace.Selection.Revision);
            Invoke(lane, "UpdateInstrumentToolbar");
            var toolbar = (Border)Field(lane, "_instrumentToolbar")!;
            toolbar.Measure(new Size(toolbar.Width, toolbar.MaxHeight));
            toolbar.Arrange(new Rect(new Point(), toolbar.DesiredSize)); toolbar.UpdateLayout();
            var groups = (StackPanel)Field(lane, "_instrumentToolbarGroups")!;
            Assert.Equal(3, groups.Children.Count);
            var buttons = (Dictionary<Button, string>)Field(lane, "_instrumentToolbarButtons")!;
            Assert.Equal(16, buttons.Count);
            Assert.Equal(TimelineSelectionAction.DeselectAll, ((WrapPanel)groups.Children[0]).Children.OfType<Button>().Last().Tag);
            var caption = (TextBlock)Field(lane, "_instrumentToolbarName")!;
            var scroll = Descendants<ScrollViewer>(toolbar).Single();
            foreach (var pair in buttons)
            {
                Assert.Null(pair.Key.ToolTip);
                Assert.Equal(27, pair.Key.ActualWidth); Assert.Equal(27, pair.Key.ActualHeight);
                var icon = Assert.IsType<FluentIcon>(pair.Key.Content);
                Assert.Equal(15, icon.ActualWidth); Assert.Equal(15, icon.ActualHeight);
                Point iconOrigin = icon.TranslatePoint(new Point(), pair.Key);
                Assert.InRange(Math.Abs(iconOrigin.X + 7.5 - 13.5), 0, .51);
                Assert.InRange(Math.Abs(iconOrigin.Y + 7.5 - 13.5), 0, .51);
                Assert.NotNull(icon.Data);
                if (pair.Key.Tag is TimelineSelectionAction action)
                    Assert.Same(lane.FindResource("Fluent." + TimelineSelectionActions.All[(int)action].ToolbarIcon), icon.Data);
                Point position = pair.Key.TranslatePoint(new Point(14, 14), groups);
                scroll.ScrollToVerticalOffset(position.Y - 14); toolbar.UpdateLayout();
                Invoke(lane, "UpdateInstrumentToolbarHover", pair.Key.TranslatePoint(new Point(14, 14), toolbar));
                Assert.Equal(pair.Value, caption.Text); // Includes disabled Humanize, Split, etc.
            }
        }
        finally
        {
            Set(lane, "_floatingAnchor", oldAnchor); Set(lane, "_floatingSelectionRevision", oldRevision);
            Invoke(lane, "UpdateInstrumentToolbar");
        }
    }

    private static void VerifyInstrumentBlankSelectionMenu(FrameworkElement content, WorkspaceViewModel workspace, InstrumentChangeLane lane)
    {
        var originalIds = workspace.Selection.SharedIds;
        var originalSource = workspace.Selection.HomogeneousTimelineSource;
        var originalProjection = Field(lane, "_latest")!;
        // A detached projection fixture: no musical data, history or project
        // commands are changed just to test whether a blank click enables tools.
        var group = new InstrumentChange(new(100001), new(100002), null, new(100003));
        var root = InstrumentChangeSet.Empty.Add(group, directMidi: false);
        try
        {
            foreach (bool selected in new[] { true, false })
            {
                workspace.Selection.AdoptMaterialized(selected ? group.MemberIds.ToArray() : []);
                var ids = workspace.Selection.SharedIds;
                var projection = originalProjection.GetType().GetMethod("<Clone>$")!.Invoke(originalProjection, null)!;
                projection.GetType().GetProperty("Root")!.SetValue(projection, root);
                Set(lane, "_latest", projection);
                Invoke(lane, "ResetInteractionLifetime"); Invoke(lane, "ResetGesture");
                Set(lane, "_pressButton", MouseButton.Right); Set(lane, "_pressPoint", new Point(210, 30));
                Set(lane, "_pressSelectionRevision", workspace.Selection.Revision); Set(lane, "_pressedHit", null);
                Set(lane, "_pressWork", Task.CompletedTask); Set(lane, "_rightPressError", null);
                Invoke(lane, "OnContextClick", lane, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = Mouse.MouseUpEvent });
                PumpUntil(() => lane.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Scale…")) == true);
                foreach (string command in new[] { "Copy", "Cut", "Delete", "Flip Horizontal", "Scale…", "Quantize…", "Properties…" })
                    Assert.Equal(selected, lane.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, command)).IsEnabled);
                Assert.Same(ids, workspace.Selection.SharedIds);
                lane.ContextMenu.IsOpen = false; Pump();
            }
        }
        finally
        {
            if (lane.ContextMenu is { } menu) menu.IsOpen = false;
            Invoke(lane, "ResetInteractionLifetime"); Invoke(lane, "ResetGesture");
            if (originalSource is { } source) workspace.Selection.ApplyRange(originalIds, WorkspaceSelectionRangeMode.Replace, source);
            else workspace.Selection.AdoptMaterialized(originalIds);
            Set(lane, "_latest", originalProjection);
            Layout(content);
        }
    }

    private static void VerifyA3ShapeBindings(FrameworkElement content, WorkspaceViewModel workspace)
    {
        var shape = Descendants<ValueTraceShapeSelector>(content).First(s => s.IsVisible && ReferenceEquals(s.DataContext, workspace));
        var before = workspace.ValueTraceShape;
        var tool = workspace is TimelineWorkspaceViewModel t ? t.ToolMode : ((InstrumentWorkspaceViewModel)workspace).ToolMode;
        try
        {
            foreach (var selected in Enum.GetValues<TimelineValueTraceShape>())
            {
                SetTool(TimelineToolMode.Draw);
                var button = Assert.IsType<ToggleButton>(shape.FindName(selected.ToString()));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                Layout(content);
                Assert.Equal(selected, workspace.ValueTraceShape);
                Assert.Equal(selected, shape.Shape); Assert.True(shape.IsEnabled); Assert.True(button.IsChecked);
                var surface = Descendants<TimelineSurface>(content).Single(s => s.IsVisible && ReferenceEquals(s.DataContext, workspace)
                    && s.SurfaceMode == TimelineSurfaceMode.Velocity);
                Assert.Equal(selected, surface.ValueTraceShape);
                SetTool(TimelineToolMode.Select); Layout(content);
                Assert.False(shape.IsEnabled);
                SetTool(TimelineToolMode.Draw); Layout(content);
                Assert.Equal(selected, shape.Shape); Assert.True(shape.IsEnabled);
            }
        }
        finally { workspace.ValueTraceShape = before; SetTool(tool); Layout(content); }
        void SetTool(TimelineToolMode value)
        { if (workspace is TimelineWorkspaceViewModel timeline) timeline.ToolMode = value; else ((InstrumentWorkspaceViewModel)workspace).ToolMode = value; }
    }

    private static void VerifyCopyPitchDesktopAdapters(MainWindow window, DesktopSessionController session, FrameworkElement content)
    {
        var project = session.Project!;
        var logical = project.Tracks[0].Segments[0];
        var midi = project.PureMidiTracks[0].Segments[0];
        var instrument = project.EventInstruments[0];
        foreach (int kind in new[] { 0, 1, 2 })
        {
            WorkspaceViewModel workspace = kind switch
            {
                0 => session.OpenSegment(midi.Id),
                1 => session.OpenSegment(logical.Id),
                _ => session.OpenInstrument(instrument.Id)
            };
            if (workspace is InstrumentWorkspaceViewModel iv) { iv.ActiveSectionIndex = 1; iv.EditorSettings.SnapEnabled = false; }
            else ((TimelineWorkspaceViewModel)workspace).EditorSettings.SnapEnabled = false;
            Layout(content);
            var piano = Descendants<TimelineSurface>(content).Single(s => s.IsVisible && ReferenceEquals(s.DataContext, workspace)
                && s.SurfaceMode == TimelineSurfaceMode.PianoRoll);
            typeof(MainWindow).GetProperty("_lastTimelineCommandSurface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, piano);
            var before = Read();
            var chosen = new[] { before.First(v => v.Key == 60), before.First(v => v.Key == 71) };
            var ids = CompressedMidoraIdSet.Create(chosen.Select(v => v.Id));
            var source = kind switch
            {
                0 => new WorkspaceTimelineSelectionSource(WorkspaceTimelineSelectionKind.DirectMidiNote, midi.Id),
                1 => new WorkspaceTimelineSelectionSource(WorkspaceTimelineSelectionKind.LogicalNote, logical.Id),
                _ => new WorkspaceTimelineSelectionSource(WorkspaceTimelineSelectionKind.TemplateNote, instrument.Id, instrument.SubVoices[0].Id)
            };
            foreach (int delta in new[] { 60, -64, 128, -128 })
            foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Alt })
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine($"A3 desktop copy begin kind={kind}, delta={delta}, modifiers={modifiers}");
                workspace.Selection.ApplyRange(ids, WorkspaceSelectionRangeMode.Replace, source);
                session.RefreshWorkspaceSelection(workspace); Layout(content);
                long firstId = project.NextStableId;
                var item = new TimelineRenderItem(chosen[0].Id, kind switch
                { 0 => TimelineItemKind.DirectMidiNote, 1 => TimelineItemKind.LogicalNote, _ => TimelineItemKind.TemplateNote },
                    chosen[0].Tick, chosen[0].Tick + 4, 127 - chosen[0].Key, 100d / 127, 0, TimelineItemState.None);
                var edit = new TimelineItemEditEventArgs(item, TimelineItemEditKind.Move, 8192, -delta, 0, modifiers, true);
                Task task = kind switch
                {
                    0 => (Task)Invoke(window, "EditDirectMidiNotes", midi.Id, edit, ids, 8192L)!,
                    1 => (Task)Invoke(window, "EditLogicalNotes", logical.Id, edit, ids, 8192L)!,
                    _ => (Task)Invoke(window, "EditTemplateEvent", workspace, edit)!
                };
                Complete(task); Layout(content);
                var after = Read();
                var copies = after.Where(v => v.Id.Value >= firstId).ToArray();
                var expected = chosen.Where(v => v.Key + delta is >= 0 and <= 127)
                    .Select(v => (Tick: v.Tick + 8192, Key: v.Key + delta)).ToArray();
                Assert.Equal(expected, copies.Select(v => (v.Tick, v.Key)));
                Assert.Equal(before, after.Where(v => v.Id.Value < firstId));
                Assert.True(workspace.Selection.SharedIds.SetEquals(copies.Select(v => v.Id)));
                if (copies.Length != 0) { session.Undo(); Layout(content); Assert.Equal(before, Read()); }
                Console.WriteLine($"A3 desktop copy completed kind={kind}, delta={delta}, copies={copies.Length}, {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            }
            (MidoraId Id, long Tick, int Key)[] Read() => kind switch
            {
                0 => project.PureMidiTracks[0].Segments[0].Notes.Select(v => (v.Id, v.StartTick, v.Key)).ToArray(),
                1 => project.Tracks[0].Segments[0].Notes.Select(v => (v.Id, v.StartTick, v.Note)).ToArray(),
                _ => instrument.SubVoices[0].Events.Where(v => v.Kind == TemplateEventKind.Note).Select(v => (v.Id, v.Tick, v.Number)).ToArray()
            };
        }
    }
}
