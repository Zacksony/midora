using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed partial class WpfInteractionRegressionTests
{
    private static void AssertA1DialogLayoutsAndInitialSettingsPage()
    {
        AssertA1ScanPresetsWheelUsesTheActualRowTemplate();
        AssertA1ScanPresetsSourceSelectionWheelIsOneItem();
        AssertA1PopupHoverDoesNotScrollBeforeTheScrollViewerHandlesTheRequest();
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        {
            NewProjectDialog dialog = new();
            var root = (FrameworkElement)dialog.Content;
            root.LayoutTransform = new ScaleTransform(scale, scale);
            root.Measure(new Size(dialog.MinWidth * scale, dialog.MinHeight * scale));
            root.Arrange(new Rect(0, 0, dialog.MinWidth * scale, dialog.MinHeight * scale));
            root.UpdateLayout();
            var browse = (Button)dialog.FindName("BrowseButton");
            Assert.True(browse.ActualWidth >= 104);
            Assert.True(browse.ActualHeight >= 30);
            var presenter = DescendantsA1(browse).OfType<ContentPresenter>().First();
            Assert.True(presenter.DesiredSize.Width <= presenter.ActualWidth + 1);
            Assert.True(presenter.DesiredSize.Height <= presenter.ActualHeight + 1);
            var confirm = DescendantsA1(root).OfType<Button>().Single(b => Equals(b.Content, "Create"));
            Assert.True(confirm.IsDefault);
            Assert.True(DescendantsA1(root).OfType<Button>().Single(b => Equals(b.Content, "Cancel")).IsCancel);
            dialog.Close();
        }
        foreach (var page in Enum.GetValues<ApplicationPreferencesPage>())
        {
            var dialog = new ApplicationPreferencesDialog(ApplicationPreferences.Default, InstrumentCatalogState.Default, null, page);
            Assert.Equal((int)page, ((TabControl)dialog.FindName("PreferencesTabs")).SelectedIndex);
            Assert.Null(dialog.Result);
            dialog.Close(); // never Loaded: no worker or device enumeration in this layout test
        }
    }

    private static void AssertA1ScanPresetsSourceSelectionWheelIsOneItem()
    {
        var fonts = Enumerable.Range(0, 50).Select(i => new ApplicationSoundFontPreference(
            System.IO.Path.Combine(FindRepositoryRoot(), "artifacts", $"test-{i}.sf2"), enabled: true)).ToArray();
        var dialog = InstrumentCatalogDialog.CreateSf2SelectionDialog(fonts);
        try
        {
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(460, 310)); root.Arrange(new Rect(0, 0, 460, 310)); root.UpdateLayout();
            var list = (ListBox)dialog.FindName("OptionsList");
            var viewer = DescendantsA1(list).OfType<ScrollViewer>().First();
            viewer.ScrollToTop(); DrainDispatcher();
            Wheel(-30); Wheel(-30); Wheel(-30); Assert.Equal(0, viewer.VerticalOffset);
            Wheel(-30); Assert.Equal(1, viewer.VerticalOffset);
            Wheel(-240); Assert.Equal(3, viewer.VerticalOffset);
            Wheel(120); Assert.Equal(2, viewer.VerticalOffset);
            viewer.ScrollToBottom(); DrainDispatcher();
            double bottom = viewer.VerticalOffset; Wheel(-120); Assert.Equal(bottom, viewer.VerticalOffset);
            Assert.Equal(0, list.SelectedIndex);
            Assert.True(DescendantsA1(list).OfType<ListBoxItem>().Count() < 30);
            void Wheel(int delta)
            {
                var origin = DescendantsA1((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex((int)viewer.VerticalOffset))
                    .OfType<TextBlock>().Last();
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                origin.RaiseEvent(args); DrainDispatcher(); Assert.True(args.Handled);
            }
        }
        finally { dialog.Close(); }
    }

    [Fact]
    public void A1CreatedLaneNavigationHandlesQueuedDesktopRefresh()
    {
        RunOnSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using HwndSource host = new(new HwndSourceParameters("A1 Lane bindings")
            { Width = 500, Height = 200, PositionX = -30000, PositionY = -30000,
                WindowStyle = unchecked((int)0x80000000) });
            var session = new DesktopSessionController();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                { ProjectName = "A1 Lane navigation", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                var instrument = Assert.Single(session.Project!.EventInstruments);
                var voice = Assert.Single(instrument.SubVoices);
                var old = TemplateEvent.ControlChange(session.Project, 0, 11, 50); voice.Events.Add(old);
                DrainDispatcher();
                var workspace = session.OpenInstrument(instrument.Id);
                workspace.Selection.Replace(old.Id); session.RefreshWorkspace(workspace);
                var laneCombo = new ComboBox { DataContext = workspace, ItemsSource = workspace.RenderLanes };
                laneCombo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,
                    new Binding(nameof(workspace.ActiveRenderLaneIndex)) { Mode = BindingMode.TwoWay });
                var eventSurface = new TimelineSurface { DataContext = workspace, SurfaceMode = TimelineSurfaceMode.EventLanes };
                var lowerTabs = new TabControl { DataContext = workspace };
                lowerTabs.Items.Add(new TabItem { Content = new Border() });
                lowerTabs.Items.Add(new TabItem { Content = new Border() }); // Inst.
                lowerTabs.Items.Add(new TabItem { Content = eventSurface });
                lowerTabs.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,
                    new Binding(nameof(workspace.ActiveLowerEditorIndex)) { Mode = BindingMode.TwoWay });
                host.RootVisual = lowerTabs;
                lowerTabs.Measure(new Size(500, 200)); lowerTabs.Arrange(new Rect(0, 0, 500, 200)); lowerTabs.UpdateLayout();
                var target = new MidiValueTarget(MidiValueKind.ControlChange, 7);
                session.Execute(ProjectDomainEditCommands.CreateSubVoiceEventLane(instrument.Id, voice.Id, target));
                Assert.DoesNotContain(workspace.RenderLanes, lane => lane.Target == target);
                Assert.True(session.ActivateCreatedSubVoiceEventLane(workspace, voice.Id, target));
                DrainDispatcher();
                Assert.Equal(target, workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)!.Target);
                Assert.Equal(target, ((InstrumentRenderLane)laneCombo.SelectedItem).Target);
                Assert.Equal(2, lowerTabs.SelectedIndex);
                lowerTabs.UpdateLayout(); Assert.True(eventSurface.IsVisible);
                Assert.Equal(old.Id, workspace.Selection.Primary);
                Assert.True(workspace.IsLowerEditorVisible); Assert.Equal(2, workspace.ActiveLowerEditorIndex);
                // The active projection must already be CC7, not the old selected CC11 point.
                List<TimelineRenderItem> points = [];
                workspace.SubVoiceEventSnapshot!.QueryInto(0, 480, 0, 1, points);
                Assert.Empty(points);
                var pianoSurface = new TimelineSurface { DataContext = workspace, SurfaceMode = TimelineSurfaceMode.PianoRoll };
                var eventSettings = MainWindow.GetWorkspaceEditorSettingsForSurface(workspace, eventSurface)!;
                Assert.Same(workspace.EventLaneEditorSettings, eventSettings);
                bool pianoSnap = workspace.EditorSettings.SnapEnabled;
                bool eventSnap = eventSettings.SnapEnabled;
                eventSettings.SnapEnabled = !eventSettings.SnapEnabled;
                Assert.Equal(!eventSnap, workspace.EventLaneEditorSettings.SnapEnabled);
                Assert.Equal(pianoSnap, MainWindow.GetWorkspaceEditorSettingsForSurface(workspace, pianoSurface)!.SnapEnabled);
                Assert.False(session.ActivateCreatedSubVoiceEventLane(workspace, voice.Id, new(MidiValueKind.ControlChange, 99)));
                Assert.Equal(target, workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)!.Target);
                session.ActiveWorkspace = session.Workspaces.First(value => value is TimelineWorkspaceViewModel);
                Assert.False(session.ActivateCreatedSubVoiceEventLane(workspace, voice.Id, target));
                Assert.Equal(old.Id, workspace.Selection.Primary);
            }
            finally { host.RootVisual = null; PumpUntil(session.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void A1MidiPitchBendDisplayIsSignedButRawValuesAndBatchRangeStayUnsigned()
    {
        RunOnSta(() =>
        {
            using var project = new MidoraProject(192);
            var root = new MidiChannelRoot(project) { Name = "Root" };
            var track = new PureMidiTrack(project) { Name = "Track" };
            var segment = new MidiSegment(project) { LengthTicks = 480 };
            track.Segments.Add(segment); ProjectGraphConstruction.AddPureMidiTrack(project, root, track, addRoot: true);
            int[] values = [0, 8192, 16383];
            foreach (int value in values)
                segment.ChannelEvents.Add(new(project) { Tick = value / 100, Kind = DirectMidiChannelEventKind.PitchBend,
                    Data1 = value & 127, Data2 = value >> 7 });
            using var workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI", TimelineWorkspaceMode.Segment);
            workspace.AddDirectMidiLaneTarget(new(DirectMidiChannelEventKind.PitchBend, 0));
            workspace.Rebuild(project, 1);
            var surface = new TimelineSurface { DataContext = workspace, SurfaceMode = TimelineSurfaceMode.EventLanes };
            surface.SetBinding(TimelineSurface.ValueAxisMinimumProperty, new Binding(nameof(workspace.ActiveValueAxisMinimum)));
            surface.SetBinding(TimelineSurface.ValueAxisMaximumProperty, new Binding(nameof(workspace.ActiveValueAxisMaximum)));
            DrainDispatcher();
            Assert.Equal(-8192, surface.ValueAxisMinimum); Assert.Equal(8191, surface.ValueAxisMaximum);
            Assert.Equal(0, workspace.ActiveValueMinimum); Assert.Equal(16383, workspace.ActiveValueMaximum);
            List<TimelineRenderItem> points = [];
            workspace.ParameterSnapshot!.QueryInto(0, 480, 0, 1, points);
            Assert.Equal(3, points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.Equal(values[i] / 16383d, points[i].Value, 12);
                Assert.Equal(values[i] - 8192, surface.ValueAxisMinimum + points[i].Value * 16383, 9);
                var encoded = ((int Data1, int Data2))typeof(MainWindow)
                    .GetMethod("DenormalizeDirectMidiEventValue", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.PitchBend, 0), points[i].Value])!;
                Assert.Equal(values[i], encoded.Data1 | encoded.Data2 << 7);
            }
            var cc = new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.ControlChange, 7);
            workspace.AddDirectMidiLaneTarget(cc); workspace.Rebuild(project, 2);
            workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList().FindIndex(lane => lane.DirectMidiTarget == cc);
            workspace.Rebuild(project, 2);
            DrainDispatcher();
            Assert.Equal(0, surface.ValueAxisMinimum); Assert.Equal(127, surface.ValueAxisMaximum);
            workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList().FindIndex(lane => lane.DirectMidiTarget?.Kind == DirectMidiChannelEventKind.PitchBend);
            workspace.Rebuild(project, 2);
            DrainDispatcher();
            Assert.Equal(-8192, surface.ValueAxisMinimum); Assert.Equal(8191, surface.ValueAxisMaximum);
            Assert.Equal(values, segment.ChannelEvents.Select(e => e.Data1 | e.Data2 << 7));
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A1SegmentAddedLaneFocusWinsOverModalPianoRestore(bool directMidi)
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            using HwndSource host = new(new HwndSourceParameters("A1 Segment lane focus")
            { Width = 500, Height = 400, PositionX = -30000, PositionY = -30000,
                WindowStyle = unchecked((int)0x80000000) });
            var session = new DesktopSessionController();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                { ProjectName = "Segment focus", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved }));
                MidoraId segmentId;
                MidoraId parameterId = default;
                if (directMidi)
                {
                    session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
                    var track = Assert.Single(session.Project!.PureMidiTracks);
                    session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
                    segmentId = Assert.Single(track.Segments).Id;
                }
                else
                {
                    session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                    var instrument = Assert.Single(session.Project!.EventInstruments);
                    session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
                    var track = Assert.Single(session.Project.Tracks);
                    session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
                    segmentId = Assert.Single(track.Segments).Id;
                    session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(instrument.Id,
                        "Expression", LogicalParameterType.Integer, 0, 127, 0, 127, 127));
                    session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(instrument.Id,
                        "Volume", LogicalParameterType.Integer, 0, 127, 0, 127, 100));
                    parameterId = instrument.LogicalParameters.Last().Id;
                }
                DrainDispatcher();
                var workspace = session.OpenSegment(segmentId);
                var piano = new TimelineSurface { Height = 150, DataContext = workspace,
                    Tag = "PrimaryTimeline", SurfaceMode = TimelineSurfaceMode.PianoRoll };
                var events = new TimelineSurface { Height = 150, DataContext = workspace,
                    Tag = "ParameterLanes", SurfaceMode = TimelineSurfaceMode.EventLanes };
                var combo = new ComboBox { DataContext = workspace, ItemsSource = workspace.ParameterLaneOptions };
                combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,
                    new Binding(nameof(workspace.ActiveParameterLaneIndex)) { Mode = BindingMode.TwoWay });
                var root = new StackPanel();
                FocusManager.SetIsFocusScope(root, true);
                root.Children.Add(piano); root.Children.Add(combo); root.Children.Add(events);
                host.RootVisual = root;
                DrainDispatcher();
                piano.Focus();
                Assert.Same(piano, FocusManager.GetFocusedElement(root));

                // ShowModalDialog queues the old piano target first. Add Lane
                // must queue its successful destination after that, not focus
                // synchronously and let the older queued restore override it.
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, piano);
                var target = new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.ControlChange, 7);
                if (directMidi)
                {
                    workspace.AddDirectMidiLaneTarget(target);
                    session.RefreshWorkspace(workspace);
                    workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList()
                        .FindIndex(lane => lane.DirectMidiTarget == target);
                    session.RefreshWorkspace(workspace);
                }
                else
                {
                    long firstId = session.Project!.NextStableId;
                    session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(segmentId, parameterId));
                    MainWindow.SelectCreatedWorkspaceObjects(session, workspace, firstId);
                }
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, events);
                var selection = workspace.Selection.SharedIds;
                long revision = session.Document!.PublicationRevision;
                DrainDispatcher();
                Assert.Same(events, FocusManager.GetFocusedElement(root));
                var option = Assert.IsType<ParameterLaneOption>(combo.SelectedItem);
                if (directMidi) Assert.Equal(target, option.DirectMidiTarget);
                else Assert.Equal(parameterId, option.ParameterId);
                Assert.Equal(selection, workspace.Selection.SharedIds);
                Assert.Equal(revision, session.Document.PublicationRevision);

                var focusedSurface = Assert.IsType<TimelineSurface>(FocusManager.GetFocusedElement(root));
                var settings = MainWindow.GetWorkspaceEditorSettingsForSurface(workspace, focusedSurface)!;
                Assert.Same(workspace.LaneEditorSettings, settings);
                bool pianoSnap = workspace.EditorSettings.SnapEnabled;
                bool eventSnap = settings.SnapEnabled;
                settings.SnapEnabled = !settings.SnapEnabled; // A's existing focus-sensitive command route
                Assert.Equal(!eventSnap, workspace.LaneEditorSettings.SnapEnabled);
                Assert.Equal(pianoSnap, workspace.EditorSettings.SnapEnabled);

                // Cancel/failure has only the original modal restore.
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, piano);
                DrainDispatcher(); Assert.Same(piano, FocusManager.GetFocusedElement(root));
                // A late callback must not target hidden or rebound editors,
                // or take focus after the user has changed Workspace.
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, events);
                events.Visibility = Visibility.Collapsed;
                DrainDispatcher(); Assert.Same(piano, FocusManager.GetFocusedElement(root));
                events.Visibility = Visibility.Visible; DrainDispatcher();
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, events);
                events.DataContext = null;
                DrainDispatcher(); Assert.Same(piano, FocusManager.GetFocusedElement(root));
                events.DataContext = workspace;
                MainWindow.QueueTimelineCommandFocus(dispatcher, session, workspace, events);
                session.ActiveWorkspace = session.Workspaces.First(value => value.Kind == WorkspaceKind.Arrangement);
                DrainDispatcher(); Assert.Same(piano, FocusManager.GetFocusedElement(root));
            }
            finally { host.RootVisual = null; PumpUntil(session.DisposeAsync().AsTask()); }
        });
    }

    private static void AssertA1ScanPresetsWheelUsesTheActualRowTemplate()
    {
        var dialog = new InstrumentCatalogDialog(InstrumentCatalogState.Default, []);
        try
        {
            var list = (ListBox)dialog.FindName("ScanBankList");
            list.ItemsSource = Enumerable.Range(0, 128).Select(i => new Sf2BankProjectionDraft((ushort)i, 128)).ToArray();
            ((Grid)dialog.FindName("ScanOverlay")).Visibility = Visibility.Visible;
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(1200, 800)); root.Arrange(new Rect(0, 0, 1200, 800)); root.UpdateLayout();
            var viewer = DescendantsA1(list).OfType<ScrollViewer>().First();
            Assert.True(viewer.CanContentScroll);
            Assert.True(ScrollViewerWheelRouter.GetUseSingleItemWheel(viewer));
            Assert.True(ScrollViewerWheelRouter.GetIsEnabled(viewer), "The real ListBox template must install the wheel router.");
            Assert.True(viewer.ScrollableHeight > 10);
            foreach (bool overTextBox in new[] { false, true })
            {
                viewer.ScrollToTop(); DrainDispatcher();
                Wheel(-30); Wheel(-30); Wheel(-30); Assert.Equal(0, viewer.VerticalOffset);
                Wheel(-30); Assert.Equal(1, viewer.VerticalOffset);
                Wheel(-120); Assert.Equal(2, viewer.VerticalOffset);
                Wheel(120); Assert.Equal(1, viewer.VerticalOffset);
                viewer.ScrollToBottom(); DrainDispatcher();
                double bottom = viewer.VerticalOffset; Wheel(-120); Assert.Equal(bottom, viewer.VerticalOffset);
                void Wheel(int delta)
                {
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex((int)viewer.VerticalOffset);
                    UIElement origin = overTextBox ? DescendantsA1(row).OfType<TextBox>().First()
                        : DescendantsA1(row).OfType<TextBlock>().First();
                    var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                    origin.RaiseEvent(args); DrainDispatcher(); Assert.True(args.Handled);
                }
            }
            Assert.True(DescendantsA1(list).OfType<ListBoxItem>().Count() < 30);
        }
        finally { dialog.Close(); }
    }

    private static void AssertA1PopupHoverDoesNotScrollBeforeTheScrollViewerHandlesTheRequest()
    {
        // A hidden native presentation source supplies real WPF Loaded/template
        // routing. No desktop input is injected or user's cursor/focus manipulated.
        using HwndSource source = new(new HwndSourceParameters("A1 popup routing")
        { Width = 350, Height = 180, PositionX = -30000, PositionY = -30000,
            WindowStyle = unchecked((int)0x80000000) });
        ComboBox combo = new() { Width = 250, MaxDropDownHeight = 120, SelectedIndex = 0,
            ItemsSource = Enumerable.Range(0, 50).Select(i => $"Item {i}").ToArray() };
        source.RootVisual = new Border { Child = combo };
        DrainDispatcher();
        Assert.True(combo.IsLoaded);
        combo.IsDropDownOpen = true; DrainDispatcher();
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        var viewer = DescendantsA1(popup.Child).OfType<ScrollViewer>().First();
        var owner = DescendantsA1(popup.Child).OfType<Grid>().Single(ComboBoxWheelSelectionGuard.GetUseSingleStepDropDownWheel);
        var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(8);
        Assert.NotNull(item); Assert.False(item.IsSelected);
        // IsMouseOver's CLR getter reads WPF's reverse-inheritance flag cache,
        // not GetValue(IsMouseOverProperty). Test only: set that cache, no OS input.
        var writeFlag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object hoverFlag = Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsMouseOverCache");
        void Hover(bool value) => writeFlag.Invoke(item, [hoverFlag, value]);
        try
        {
            viewer.ScrollToTop(); DrainDispatcher();
            // A hidden owner cannot keep native mouse capture. Re-open directly
            // before the synchronous routed request instead of changing the OS mouse.
            combo.IsDropDownOpen = true;
            Hover(true);
            Assert.True(combo.IsDropDownOpen, "Popup state"); Assert.True(item.IsMouseOver, "Synthetic hover state");
            Assert.Same(combo, ItemsControl.ItemsControlFromItemContainer(item));
            bool? handledAtItem = null;
            bool leftPressedAtItem = false;
            string? bringIntoViewState = null;
            var explicitNavigationProperty = (DependencyProperty)typeof(ComboBoxWheelSelectionGuard)
                .GetField("ExplicitNavigationProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            item.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) =>
                {
                    if (handledAtItem is not null) return;
                    handledAtItem = e.Handled;
                    leftPressedAtItem = Mouse.LeftButton == MouseButtonState.Pressed;
                    bringIntoViewState = $"Open={combo.IsDropDownOpen}; Hover={item.IsMouseOver}; "
                        + $"LeftButton={Mouse.LeftButton}; Explicit={owner.GetValue(explicitNavigationProperty)}";
                }), true);
            item.BringIntoView(); DrainDispatcher();
            // The hidden test host shares physical button state with the user.
            // A real held-left gesture is intentionally exempt from hover-only
            // suppression; do not make this test depend on the user releasing it.
            Assert.True(handledAtItem == !leftPressedAtItem, bringIntoViewState);
            if (!leftPressedAtItem) Assert.Equal(0, viewer.VerticalOffset);

            // Navigation requests must continue through to the real ScrollViewer.
            typeof(ComboBoxWheelSelectionGuard).GetMethod("AllowExplicitNavigation", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [owner]);
            combo.IsDropDownOpen = true;
            handledAtItem = null;
            item.BringIntoView(); DrainDispatcher();
            Assert.False(handledAtItem); Assert.True(viewer.VerticalOffset > 0);
            Hover(false);
            viewer.ScrollToTop(); DrainDispatcher();
            handledAtItem = null;
            item.BringIntoView(); DrainDispatcher();
            Assert.False(handledAtItem); Assert.True(viewer.VerticalOffset > 0);
        }
        finally { Hover(false); combo.IsDropDownOpen = false; source.RootVisual = null; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50000)]
    public void A1LogicalListWheelIsOneItemAccumulatesAndNeverMovesOuterForm(int count)
    {
        RunOnSta(() =>
        {
            ListBox list = new() { Height = 90, ItemsSource = Enumerable.Range(0, count).ToArray() };
            ScrollViewer.SetCanContentScroll(list, true);
            ScrollViewerWheelRouter.SetUseSingleItemWheel(list, true);
            StackPanel panel = new(); panel.Children.Add(list); panel.Children.Add(new Border { Height = 600 });
            ScrollViewer outer = new() { Content = panel, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ScrollViewerWheelRouter.SetIsEnabled(outer, true);
            outer.Measure(new Size(350, 180)); outer.Arrange(new Rect(0, 0, 350, 180)); outer.UpdateLayout();
            var inner = DescendantsA1(list).OfType<ScrollViewer>().First();
            ScrollViewerWheelRouter.SetIsEnabled(inner, true);
            Assert.True(ScrollViewerWheelRouter.GetUseSingleItemWheel(inner));
            Assert.True(inner.CanContentScroll);
            Wheel(-30); Assert.Equal(0, inner.VerticalOffset);
            Wheel(-30); Wheel(-30); Assert.Equal(0, inner.VerticalOffset);
            Wheel(-30); Assert.Equal(count == 1 ? 0 : 1, inner.VerticalOffset);
            Wheel(-240); Assert.Equal(count == 1 ? 0 : 3, inner.VerticalOffset);
            Wheel(120); Assert.Equal(count == 1 ? 0 : 2, inner.VerticalOffset);
            inner.ScrollToBottom(); DrainDispatcher();
            double bottom = inner.VerticalOffset; Wheel(-120); Assert.Equal(bottom, inner.VerticalOffset);
            Assert.Equal(0, outer.VerticalOffset); Assert.Equal(-1, list.SelectedIndex);
            Assert.True(DescendantsA1(list).OfType<ListBoxItem>().Count() < 30);
            void Wheel(int delta)
            {
                MouseWheelEventArgs args = new(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                inner.RaiseEvent(args); DrainDispatcher(); Assert.True(args.Handled);
            }
        });
    }

    [Fact]
    public void A1WheelRemaindersAreLocalAndReverseImmediately()
    {
        RunOnSta(() =>
        {
            DependencyObject a = new(), b = new();
            Assert.Equal(0, ScrollViewerWheelRouter.ConsumeWheelSteps(a, -90));
            Assert.Equal(0, ScrollViewerWheelRouter.ConsumeWheelSteps(b, -30));
            Assert.Equal(0, ScrollViewerWheelRouter.ConsumeWheelSteps(a, 30));
            Assert.Equal(1, ScrollViewerWheelRouter.ConsumeWheelSteps(a, 90));
            Assert.Equal(-1, ScrollViewerWheelRouter.ConsumeWheelSteps(b, -90));
        });
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void A1ComboSuppressesOnlyHoverFocusScroll(bool hover, bool pressed, bool navigating, bool expected)
        => Assert.Equal(expected, ComboBoxWheelSelectionGuard.SuppressHoverBringIntoView(true, hover, pressed, navigating));

    [Fact]
    public void A1ListsUse400DefaultWithoutResettingAdjustedSessionWidth()
    {
        var state = new TimelineObjectListState();
        Assert.Equal(0, state.ColumnWidth.Value); Assert.Null(state.Source);
        state.IsVisible = true; Assert.Equal(400, state.ColumnWidth.Value);
        state.ColumnWidth = new(521); state.IsVisible = false; state.IsVisible = true;
        Assert.Equal(521, state.ColumnWidth.Value);
        state.ColumnWidth = new(900); Assert.Equal(700, state.ColumnWidth.Value);
        state.ColumnWidth = new(1); Assert.Equal(240, state.ColumnWidth.Value);
        Assert.Null(state.Source);
    }

    [Fact]
    public void A1EventListValuesDoNotDuplicateTargetIdentifiers()
    {
        foreach (var kind in new[] { DirectMidiChannelEventKind.PitchBend, DirectMidiChannelEventKind.ChannelPressure,
                     DirectMidiChannelEventKind.ControlChange, DirectMidiChannelEventKind.PolyphonicKeyPressure })
        {
            var row = new TimelineObjectListRow(new(1), TimelineItemKind.DirectMidiEvent, 0, Value: 42, DirectKind: kind, Number: 11);
            Assert.Equal("42", TimelineObjectListSource.GetValueLabel(row));
            if (kind is DirectMidiChannelEventKind.ControlChange or DirectMidiChannelEventKind.PolyphonicKeyPressure)
                Assert.Contains("11", TimelineObjectListSource.GetTypeLabel(row));
        }
        foreach (var kind in new[] { TemplateEventKind.RegisteredParameter, TemplateEventKind.NonRegisteredParameter })
        {
            var row = new TimelineObjectListRow(new(1), TimelineItemKind.TemplateEvent, 0, Value: 8191, TemplateKind: kind, Number: 123);
            Assert.Equal("8191", TimelineObjectListSource.GetValueLabel(row));
            Assert.Contains("123", TimelineObjectListSource.GetTypeLabel(row));
        }
    }

    [Fact]
    public void A1AddedLaneNavigationWinsOverOldSelectedEventAndSurvivesRebuild()
    {
        RunOnSta(() =>
        {
            using var project = new MidoraProject(480);
            EventInstrument instrument = new(project) { Name = "A1", TemplateLengthTicks = 480 };
            project.EventInstruments.Add(instrument);
            SubVoice voice = new(project) { Name = "A1" }; instrument.SubVoices.Add(voice);
            var old = TemplateEvent.ControlChange(project, 0, 1, 50); voice.Events.Add(old);
            using var workspace = new InstrumentWorkspaceViewModel(instrument.Id, "A1");
            workspace.Rebuild(project, 1); workspace.Selection.Replace(old.Id); workspace.Rebuild(project, 1);
            var target = new MidiValueTarget(MidiValueKind.PitchBend);
            var edit = ProjectDomainEditCommands.CreateSubVoiceEventLane(instrument.Id, voice.Id, target).Prepare(project);
            edit.Apply(project); workspace.Rebuild(project, 2);
            Assert.True(workspace.TryActivateEventLane(target)); workspace.Rebuild(project, 2);
            Assert.Equal(target, workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)!.Target);
            Assert.Equal(2, workspace.ActiveLowerEditorIndex); Assert.True(workspace.IsLowerEditorVisible);
            Assert.False(workspace.TryActivateEventLane(new(MidiValueKind.ControlChange, 123)));
            Assert.Equal(target, workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)!.Target);
            edit.Undo(project); workspace.Rebuild(project, 3);
            // Existing UX retains an empty currently viewed Lane. Navigation
            // must not recreate its deleted formal Mapping owner.
            Assert.DoesNotContain(voice.EventMappings, mapping => mapping.Target == TemplateEventMidiTargets.ToMappingTarget(target));
        });
    }

    [Fact]
    public void A1EnumLaneDisablesRelativeVerticalDragAndNumericLaneRestoresIt()
    {
        RunOnSta(() =>
        {
            using var project = new MidoraProject(480);
            var instrument = new EventInstrument(project) { Name = "A1" }; project.EventInstruments.Add(instrument);
            var track = new LogicalTrack(project) { Name = "A1" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
            var segment = new Segment(project) { LengthTicks = 480 }; track.Segments.Add(segment);
            var parameter = new LogicalParameterDefinition(project) { Name = "A1", Type = LogicalParameterType.Enum,
                Minimum = 0, Maximum = 1, DisplayMinimum = 0, DisplayMaximum = 1 };
            instrument.LogicalParameters.Add(parameter);
            using var workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "A1", TimelineWorkspaceMode.Segment);
            workspace.Rebuild(project, 1); Assert.False(workspace.ActiveValueDragEnabled);
            parameter.Type = LogicalParameterType.Integer;
            workspace.Rebuild(project, 2); Assert.True(workspace.ActiveValueDragEnabled);
        });
    }

    private static IEnumerable<DependencyObject> DescendantsA1(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in DescendantsA1(child)) yield return nested;
        }
    }
}
