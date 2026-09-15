using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit;

namespace Midora.Desktop.Tests;

/// <summary>
/// Invoked from the theme regression's one Application/STA. This deliberately loads
/// MainWindow BAML, its real DataTemplates and code-behind connectors. The source
/// is a hidden (never WS_VISIBLE) WPF presentation host, not desktop automation.
/// </summary>
public static partial class TimelineObjectListIntegrationTests
{
    public static void VerifyActualMainWindowTemplatesAndHandlers()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { VerifyOnDispatcher(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyOnDispatcher()
    {
        using PreferenceDirectory preferences = new();
        MainWindow window = new();
        Set(window, "_preferenceStore", new ApplicationPreferencesStore(preferences.FilePath));
        var session = (DesktopSessionController)Field(window, "_session")!;
        ((DispatcherTimer)Field(window, "_playbackTimer")!).Stop();
        var fixture = CreateFixture();
        bool activated = false;
        FrameworkElement? content = null;
        HwndSource? host = null;
        try
        {
            ActivateWithoutAudio(session, fixture.Project);
            activated = true;
            content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            window.Content = null;
            // The same template resource instances and binding root accompany
            // the original content when it is moved to the invisible test host.
            content.Resources.MergedDictionaries.Add(window.Resources);
            content.DataContext = session;
            host = new HwndSource(new HwndSourceParameters("Midora object-list offscreen test")
            { Width = 1600, Height = 1050, WindowStyle = unchecked((int)0x80000000), ParentWindow = IntPtr.Zero });
            host.RootVisual = content;
            Layout(content);

            var logical = session.OpenSegment(fixture.Logical.Id);
            VerifyWorkspace(window, session, content, logical, TimelineObjectListOwnerKind.LogicalSegment);
            TimelineObjectListSource logicalSource = logical.ObjectList.Source!;
            var midi = session.OpenSegment(fixture.Midi.Id);
            Layout(content);
            // Both Segment workspaces use the same DataTemplate. WPF may reuse
            // its presenter and swap DataContext without an Unloaded event.
            Assert.Null(logical.ObjectList.Source);
            Assert.True(logicalSource.IsDisposed);
            VerifyWorkspace(window, session, content, midi, TimelineObjectListOwnerKind.MidiSegment);
            TimelineObjectListSource midiSource = midi.ObjectList.Source!;
            session.ActiveWorkspace = logical;
            Layout(content);
            TimelineObjectListPane reusedPane = VisibleWorkspacePane(content, logical);
            PumpUntil(() => reusedPane.Source is { IsDisposed: false });
            Assert.Equal(TimelineObjectListOwnerKind.LogicalSegment, reusedPane.Source!.OwnerKind);
            Assert.Null(midi.ObjectList.Source);
            Assert.True(midiSource.IsDisposed);
            Assert.Equal(37, reusedPane.FirstRow);
            Assert.InRange(reusedPane.ActualWidth, 389, 391);
            var instrument = session.OpenInstrument(fixture.Instrument.Id);
            instrument.ActiveSectionIndex = 1; // Configurations is deliberately the first section.
            VerifyWorkspace(window, session, content, instrument, TimelineObjectListOwnerKind.SubVoice);
            VerifySubVoiceOverlays(content, instrument, fixture.Instrument);
            Assert.Equal(300, fixture.Logical.Notes.Count);
            Assert.Equal(300, fixture.Midi.Notes.Count);
            Assert.Equal(301, fixture.Instrument.SubVoices[0].Events.Count);
            Assert.Empty(session.Document!.History);
            Assert.False(session.Document.IsModified);
            VerifyBlankPreRollInput(window, session, content, instrument);
        }
        finally
        {
            foreach (WorkspaceViewModel workspace in session.Workspaces)
                workspace.CancelBackgroundPresentationWork();
            if (host is not null) { host.RootVisual = null; host.Dispose(); }
            if (content is not null) content.DataContext = null;
            // All real window-owned resources are released. The window's
            // preferences store has been redirected to this owned test folder.
            Complete(session.DisposeAsync().AsTask());
            ((DispatcherTimer)Field(window, "_playbackTimer")!).Stop();
            Set(window, "_closeApproved", true);
            window.Close();
            if (!activated) fixture.Project.Dispose();
            Pump();
        }
    }

    private static void VerifyWorkspace(MainWindow window, DesktopSessionController session,
        FrameworkElement content, WorkspaceViewModel workspace, TimelineObjectListOwnerKind expectedKind)
    {
        Layout(content);
        VerifyLaneHeader(window, content, workspace, expectedKind);
        TimelineSurface piano = Descendants<TimelineSurface>(content).Single(surface => surface.IsVisible
            && ReferenceEquals(surface.DataContext, workspace) && surface.SurfaceMode == TimelineSurfaceMode.PianoRoll);
        Assert.Equal(expectedKind != TimelineObjectListOwnerKind.SubVoice, piano.IsTimeRangeSelectionEnabled);
        TimelineObjectListPane pane = VisibleWorkspacePane(content, workspace);
        Assert.False(workspace.ObjectList.IsVisible);
        Assert.Equal(Visibility.Collapsed, pane.Visibility);
        Assert.Null(workspace.ObjectList.Source);
        Assert.Equal(0, workspace.ObjectList.ColumnWidth.Value);
        Assert.False(pane.IsActive);

        int factoryCalls = 0;
        var factory = (Func<TimelineObjectListSource?>)Field(workspace.ObjectList, "_factory")!;
        workspace.ObjectList.SetFactory(() => { factoryCalls++; return factory(); });
        Layout(content);
        Assert.Equal(0, factoryCalls); // Idle/hidden layout does not even capture a source.

        ToggleButton toggle = Descendants<ToggleButton>(content).Single(button =>
            ReferenceEquals(button.DataContext, workspace) && Equals(button.Content, "List") && button.IsVisible);
        Invoke(toggle, "OnClick");
        Layout(content);
        PumpUntil(() => pane.IsLoaded && pane.IsActive && pane.Source is not null);
        Assert.True(workspace.ObjectList.IsVisible);
        Assert.Equal(expectedKind, pane.Source!.OwnerKind);
        Assert.True(factoryCalls > 0);
        Assert.True(pane.ActualWidth >= 240);
        PumpUntil(() => ((TimelineObjectListRow[])Field(pane, "_visible")!).Length > 2);
        Assert.InRange(((TimelineObjectListRow[])Field(pane, "_visible")!).Length, 3, 256);
        var rows = Complete(pane.Source.ReadRowsAsync(0, 5));
        Assert.False(rows[0].IsNote);
        Assert.True(rows[1].IsNote);

        long revision = session.Document!.PublicationRevision;
        int history = session.Document.History.Count;
        Select(1, 1, System.Windows.Input.ModifierKeys.None, rows[1]);
        Assert.Equal([rows[1].Id], workspace.Selection.Ids);
        Assert.Same(workspace.SelectionSnapshot, pane.Selection);
        Select(2, 2, System.Windows.Input.ModifierKeys.Control, rows[2]);
        Assert.True(workspace.Selection.IdSet.SetEquals([rows[1].Id, rows[2].Id]));
        Select(1, 1, System.Windows.Input.ModifierKeys.None, rows[1]);
        Assert.Equal([rows[1].Id], workspace.Selection.Ids);
        Select(2, 2, System.Windows.Input.ModifierKeys.Control, rows[2]);
        Select(2, 2, System.Windows.Input.ModifierKeys.Control, rows[2]);
        Assert.Equal([rows[1].Id], workspace.Selection.Ids);
        Select(0, 2, System.Windows.Input.ModifierKeys.Shift, rows[2]);
        Assert.True(workspace.Selection.IdSet.SetEquals(rows.Take(3).Select(row => row.Id)));

        var mixedTask = (Task<TimelineObjectSelection?>)Invoke(window, "ReadObjectListSelectionAsync", workspace, CancellationToken.None)!;
        var mixed = Assert.IsType<TimelineObjectSelection>(Complete(mixedTask));
        Assert.True(mixed.IsMixed);
        Assert.False(mixed.CanCopyOrCut);
        Assert.True(mixed.CanDelete);
        Assert.Same(workspace.SelectionSnapshot, pane.Selection);

        var selectedBeforeProperties = workspace.Selection.SharedIds;
        var propertyContext = ObjectPropertiesSelectionContext.Capture(workspace) with
        { Ids = mixed.Notes, Source = mixed.NoteSource };
        ObjectPropertiesViewModel properties = ObjectPropertiesProjection.ReadMultiSelection(
            session.Project!, propertyContext, CancellationToken.None, progress: null);
        Assert.NotEmpty(properties.Fields);
        ObjectPropertiesDialog dialog = new(properties, _ => true);
        dialog.Close(); // Not shown; reading/cancelling the draft never commits.
        Assert.Same(selectedBeforeProperties, workspace.Selection.SharedIds);
        Assert.Equal(history, session.Document.History.Count);
        Assert.Equal(revision, session.Document.PublicationRevision);

        // Locate is the real MainWindow route: lower editor opens and switches
        // the formal lane but must retain the mixed selection unchanged.
        if (workspace is TimelineWorkspaceViewModel timeline) timeline.IsLowerEditorVisible = false;
        else ((InstrumentWorkspaceViewModel)workspace).IsLowerEditorVisible = false;
        Invoke(window, "LocateObjectListRow", pane, rows[0]);
        Layout(content);
        Assert.Same(selectedBeforeProperties, workspace.Selection.SharedIds);
        Assert.True(workspace is TimelineWorkspaceViewModel track ? track.IsLowerEditorVisible
            : ((InstrumentWorkspaceViewModel)workspace).IsLowerEditorVisible);
        Assert.Equal(revision, session.Document.PublicationRevision);

        workspace.ObjectList.ColumnWidth = new(390);
        workspace.ObjectList.FirstRow = 37;
        Layout(content);
        Assert.InRange(pane.ActualWidth, 389, 391);
        Assert.Equal(37, pane.FirstRow);
        TimelineObjectListSource oldSource = pane.Source!;
        Invoke(toggle, "OnClick");
        Layout(content);
        Assert.False(pane.IsActive);
        Assert.Null(pane.Source);
        Assert.True(oldSource.IsDisposed);
        Assert.Equal(37, workspace.ObjectList.FirstRow);
        Invoke(toggle, "OnClick");
        Layout(content);
        PumpUntil(() => pane.IsActive && pane.Source is { IsDisposed: false });
        Assert.NotSame(oldSource, pane.Source);
        Assert.InRange(pane.ActualWidth, 389, 391);
        Assert.Equal(37, pane.FirstRow);
        Assert.True(workspace.Selection.IdSet.SetEquals(rows.Take(3).Select(row => row.Id)));

        // Switching the actual Workspace Tab away/back suspends reads while
        // preserving the per-workspace list width and row offset.
        TimelineObjectListSource beforeTabSwitch = pane.Source!;
        session.OpenArrangement();
        Layout(content);
        Assert.True(beforeTabSwitch.IsDisposed);
        Assert.Null(workspace.ObjectList.Source);
        session.ActiveWorkspace = workspace;
        Layout(content);
        pane = VisibleWorkspacePane(content, workspace);
        PumpUntil(() => pane.IsLoaded && pane.IsActive && pane.Source is not null);
        Assert.Equal(37, pane.FirstRow);
        Assert.InRange(pane.ActualWidth, 389, 391);

        void Select(int first, int last, System.Windows.Input.ModifierKeys modifiers, TimelineObjectListRow primary)
        {
            Complete((Task)Invoke(window, "SelectObjectListRangeAsync", pane,
                new TimelineObjectListSelectionEventArgs(first, last, modifiers, primary))!);
            Layout(content);
        }
    }

    private static void VerifySubVoiceOverlays(FrameworkElement content, InstrumentWorkspaceViewModel workspace,
        EventInstrument instrument)
    {
        workspace.TimelineStartTick = 37;
        workspace.TimelineTickSpan = 1000;
        workspace.IsLowerEditorVisible = true;
        foreach (int lowerTab in new[] { 0, 2 })
        {
            workspace.ActiveLowerEditorIndex = lowerTab;
            Layout(content);
            TimelineSurface piano = Descendants<TimelineSurface>(content).Single(surface => surface.IsVisible && Equals(surface.Tag, "SubVoiceNotes"));
            TimelineSurface lower = Descendants<TimelineSurface>(content).Single(surface => surface.IsVisible
                && Equals(surface.Tag, lowerTab == 0 ? "SubVoiceVelocity" : "SubVoiceEvents"));
            Assert.Equal(piano.ActualWidth, lower.ActualWidth, precision: 2);
            Assert.Equal(piano.TranslatePoint(new(52, 0), content).X, lower.TranslatePoint(new(52, 0), content).X, precision: 2);
            foreach (var surface in new[] { piano, lower })
            {
                Assert.False(surface.IsTimeRangeSelectionEnabled);
                var overlay = Assert.Single(((Panel)VisualTreeHelper.GetParent(surface)).Children.OfType<TimelineSemanticOverlay>());
                Assert.Equal(surface.ActualWidth, overlay.ActualWidth, precision: 2);
                Assert.Equal(surface.ActualHeight, overlay.ActualHeight, precision: 2);
                Assert.Equal(surface.TranslatePoint(new(), content), overlay.TranslatePoint(new(), content));
                Assert.Equal(surface.StartTick, overlay.StartTick);
                Assert.Equal(surface.TickSpan, overlay.TickSpan);
                Assert.Equal(instrument.PreRollTicks, overlay.PreRollTicks);
                Assert.Equal(instrument.LoopStartTick, overlay.LoopStartTick);
                Assert.Equal(instrument.LoopEndTick, overlay.LoopEndTick);
                Assert.Equal(ReferenceEquals(surface, piano), overlay.ShowRulerLabels);
                Assert.False(overlay.IsHitTestVisible);
            }
        }
    }

    private static void VerifyBlankPreRollInput(MainWindow window, DesktopSessionController session,
        FrameworkElement content, InstrumentWorkspaceViewModel workspace)
    {
        workspace.ActiveSectionIndex = 0;
        Layout(content);
        TextBox textBox = Descendants<TextBox>(content).Single(box => box.IsVisible
            && ReferenceEquals(box.DataContext, workspace) && Equals(box.Tag, "PreRollTicks"));
        Assert.Equal("96", textBox.Text);
        long originalRevision = session.Document!.PublicationRevision;
        string? statusBefore = session.StatusMessage;

        Enter("");
        Assert.Equal(0, session.Project!.EventInstruments.Single().PreRollTicks);
        Assert.Equal("0", workspace.InstrumentPreRollTicksText);
        Assert.Equal("0", textBox.Text);
        Assert.Equal(originalRevision + 1, session.Document.PublicationRevision);
        Assert.Single(session.Document.History);
        Assert.Equal(statusBefore, session.StatusMessage);
        Assert.False(session.StatusMessageIsError);

        session.Undo();
        Layout(content);
        Assert.Equal(96, session.Project.EventInstruments.Single().PreRollTicks);
        Assert.Equal("96", textBox.Text);
        session.Redo();
        Layout(content);
        Assert.Equal(0, session.Project.EventInstruments.Single().PreRollTicks);
        Assert.Equal("0", textBox.Text);

        long zeroRevision = session.Document.PublicationRevision;
        foreach (string blank in new[] { "", "   " })
        {
            Enter(blank);
            Assert.Equal("0", workspace.InstrumentPreRollTicksText);
            Assert.Equal("0", textBox.Text);
            Assert.Equal(zeroRevision, session.Document.PublicationRevision);
            Assert.Single(session.Document.History);
            Assert.False(session.StatusMessageIsError);
        }

        session.Undo();
        Layout(content);
        Enter("   ");
        Assert.Equal(0, session.Project.EventInstruments.Single().PreRollTicks);
        Assert.Equal("0", textBox.Text);
        Assert.Single(session.Document.History);
        Assert.False(session.StatusMessageIsError);

        // Empty is an input alias for zero, not permission to clamp malformed
        // or out-of-range nonempty values.
        foreach (string invalid in new[] { "-1", "4097", "abc" })
        {
            long revision = session.Document.PublicationRevision;
            Enter(invalid);
            Assert.Equal(0, session.Project.EventInstruments.Single().PreRollTicks);
            Assert.Equal("0", textBox.Text);
            Assert.Equal(revision, session.Document.PublicationRevision);
            Assert.Single(session.Document.History);
            Assert.True(session.StatusMessageIsError);
        }
        session.SetStatusMessage(null);

        void Enter(string text)
        {
            textBox.Text = text;
            textBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Invoke(window, "OnInstrumentConfigurationLostFocus", textBox,
                new System.Windows.Input.KeyboardFocusChangedEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, 0, textBox, null)
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Layout(content);
        }
    }

    private static (MidoraProject Project, Segment Logical, MidiSegment Midi, EventInstrument Instrument) CreateFixture()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "List integration");
        instrument.TemplateLengthTicks = 4096; instrument.PreRollTicks = 96;
        instrument.LoopStartTick = 192; instrument.LoopEndTick = 576;
        LogicalParameterDefinition parameter = new(project) { Name = "Expression", Maximum = 127, DisplayMaximum = 127 };
        instrument.LogicalParameters.Add(parameter);
        ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id).Prepare(project).Apply(project);
        Segment logical = new(project) { LengthTicks = 4096 };
        project.Tracks[0].Segments.Add(logical);
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(project, 64, 90)); logical.ParameterLanes.Add(lane);
        MidiChannelRoot root = new(project) { Name = "MIDI Root" };
        PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        MidiSegment midi = new(project) { LengthTicks = 4096 }; track.Segments.Add(midi);
        midi.ChannelEvents.Add(new(project) { Tick = 64, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 90 });
        SubVoice voice = instrument.SubVoices[0];
        voice.Events.Add(new(project) { Tick = 64, Kind = TemplateEventKind.ControlChange, Number = 11, Value = 90 });
        for (int i = 0; i < 300; i++)
        {
            long tick = 128 + i * 8;
            logical.Notes.Add(new(project) { StartTick = tick, LengthTicks = 4, Note = 60 + i % 12, Velocity = 100 });
            midi.Notes.Add(new(project) { StartTick = tick, LengthTicks = 4, Key = 60 + i % 12, NoteOnVelocity = 100 });
            voice.Events.Add(new(project) { Tick = tick, Kind = TemplateEventKind.Note, LengthTicks = 4, Number = 60 + i % 12, Value = 100 });
        }
        return (project, logical, midi, instrument);
    }

    private static void ActivateWithoutAudio(DesktopSessionController session, MidoraProject project)
    {
        ProjectCompilationSession compilation = new(project, executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromHours(1));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, new MidoraProjectPackageV1(MidoraSoftwareVersion.InformationalVersion));
        Type contextType = typeof(DesktopSessionController).GetNestedType("ProjectContext", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [new ProjectOwner(project), compilation, document, persistence, null, null, "No audio in the offscreen integration test"], culture: null)!;
        Complete((Task)Invoke(session, "ActivateAsync", context)!);
        Assert.False(session.CanPlayback);
        Assert.False(session.CanPreview);
    }

    private static TimelineObjectListPane VisibleWorkspacePane(FrameworkElement content, WorkspaceViewModel workspace) =>
        Assert.Single(Descendants<TimelineObjectListPane>(content), pane => ReferenceEquals(pane.DataContext, workspace));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Layout(FrameworkElement content)
    {
        content.Measure(new(1600, 1050)); content.Arrange(new Rect(0, 0, 1600, 1050)); content.UpdateLayout(); Pump();
    }
    private static T Complete<T>(Task<T> task) { Complete((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Yield(); }
        Assert.True(ready(), "The real WPF object-list binding/handler did not complete.");
    }
    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static FieldInfo FindField(object instance, string name)
    {
        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) return field;
        throw new MissingFieldException(name);
    }
    private static object? Field(object instance, string name) => FindField(instance, name).GetValue(instance);
    private static void Set(object instance, string name, object? value) => FindField(instance, name).SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object?[] arguments)
    {
        MethodInfo? method = null;
        for (Type? type = instance.GetType(); type is not null && method is null; type = type.BaseType)
            method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        try { return method!.Invoke(instance, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private sealed class ProjectOwner(MidoraProject project) : IAsyncDisposable
    { public ValueTask DisposeAsync() { project.Dispose(); return ValueTask.CompletedTask; } }
    private sealed class PreferenceDirectory : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "test-artifacts", "object-list-" + Guid.NewGuid().ToString("N")));
        public string FilePath => Path.Combine(_directory, "preferences.json");
        public PreferenceDirectory() => Directory.CreateDirectory(_directory);
        public void Dispose()
        {
            string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts"))
                + Path.DirectorySeparatorChar;
            if (!_directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test cleanup target left its owned artifact folder.");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
