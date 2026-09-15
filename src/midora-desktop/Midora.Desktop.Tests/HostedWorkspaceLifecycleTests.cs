using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit;

namespace Midora.Desktop.Tests;

/// <summary>
/// Runs on the theme test's single Application/STA. No window is shown and no
/// desktop input is synthesized: the native host deliberately omits WS_VISIBLE.
/// Logical focus is tested separately from foreground OS keyboard activation.
/// </summary>
public static class HostedWorkspaceLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void VerifyLoadedTemplatesAndModalReturnTargets()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { VerifyOnDispatcher(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyOnDispatcher()
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts",
            "hosted-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        MainWindow window = new();
        typeof(MainWindow).GetField("_preferenceStore", Private)!.SetValue(window,
            new ApplicationPreferencesStore(Path.Combine(directory, "preferences.json")));
        var session = (DesktopSessionController)typeof(MainWindow).GetField("_session", Private)!.GetValue(window)!;
        var playbackTimer = (DispatcherTimer)typeof(MainWindow).GetField("_playbackTimer", Private)!.GetValue(window)!;
        playbackTimer.Stop();
        FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        HwndSource? host = null;
        try
        {
            MidoraProject project = new(192);
            EventInstrument instrument = EventInstrumentLibrary.Create(project, "Hosted lifecycle");
            ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id).Prepare(project).Apply(project);
            Segment first = new(project) { LengthTicks = 192 };
            Segment second = new(project) { ProjectStartTick = 192, LengthTicks = 192 };
            first.Notes.Add(new(project) { StartTick = 0, LengthTicks = 96, Note = 60, Velocity = 100 });
            second.Notes.Add(new(project) { StartTick = 12, LengthTicks = 96, Note = 64, Velocity = 90 });
            project.Tracks[0].Segments.AddRange([first, second]);
            ActivateWithoutAudio(session, project);
            window.Content = null;
            content.Resources.MergedDictionaries.Add(window.Resources);
            content.DataContext = session;
            FocusManager.SetIsFocusScope(content, true);
            host = CreateHost("Midora hosted Workspace lifecycle");
            host.RootVisual = content;
            Layout(content);
            Assert.True(content.IsLoaded);
            Assert.False(window.IsVisible);

            TimelineWorkspaceViewModel firstWorkspace = session.OpenSegment(first.Id);
            Layout(content);
            TimelineSurface firstSurface = Piano(content, firstWorkspace);
            Assert.True(firstSurface.IsLoaded);
            TimelineCommandTarget previousTarget = new();
            previousTarget.Set(firstSurface);
            SetCommandHint(window, firstSurface);

            // Same Segment DataTemplate, different Workspace: the old target
            // must be rejected even if WPF keeps the same visual instance.
            TimelineWorkspaceViewModel secondWorkspace = session.OpenSegment(second.Id);
            Layout(content);
            TimelineSurface secondSurface = Piano(content, secondWorkspace);
            Assert.True(secondSurface.IsLoaded);
            Assert.Same(secondWorkspace, secondSurface.DataContext);
            Assert.True(firstWorkspace.IsPresentationSuspended);
            Assert.Null(previousTarget.Resolve(secondWorkspace));
            Assert.Null(previousTarget.Resolve(firstWorkspace));
            Assert.Null(GetCommandHint(window));

            VerifyExplicitTemplateReuse(window, session, host, content, firstWorkspace, secondWorkspace);
            secondSurface = Piano(content, secondWorkspace);
            int unloads = 0;
            int loads = 0;
            secondSurface.Unloaded += (_, _) => unloads++;
            secondSurface.Loaded += (_, _) => loads++;
            host.RootVisual = null;
            PumpUntil(() => !content.IsLoaded && !secondSurface.IsLoaded);
            Assert.True(unloads > 0);
            host.RootVisual = content;
            Layout(content);
            PumpUntil(() => content.IsLoaded && secondSurface.IsLoaded);
            Assert.True(loads > 0);
            Assert.Same(secondWorkspace, session.ActiveWorkspace);

            secondWorkspace.Selection.Replace(second.Notes[0].Id);
            session.RefreshWorkspaceSelection(secondWorkspace);
            long revision = session.Document!.PublicationRevision;
            int history = session.Document.History.Count;
            VerifyDialogReturn(new ObjectPropertiesDialog(session, secondWorkspace),
                window, session, host, content, secondWorkspace);
            VerifyDialogReturn(new BatchEditDialog(BatchEditPresetKind.Note),
                window, session, host, content, secondWorkspace);
            Assert.Equal(revision, session.Document.PublicationRevision);
            Assert.Equal(history, session.Document.History.Count);
            Assert.Equal(second.Notes[0].Id, secondWorkspace.Selection.Primary);

            VerifyAllTracksTabFocus(session, content, secondWorkspace);

            secondSurface = Piano(content, secondWorkspace);
            TimelineCommandTarget closingTarget = new();
            closingTarget.Set(secondSurface);
            SetCommandHint(window, secondSurface);
            // Close while a modal-return callback is still pending. The reused
            // visual may become another Tab's Surface; it is not the old target.
            Invoke(window, "RestoreModalCommandFocus", secondWorkspace, secondSurface);
            session.CloseWorkspace(secondWorkspace);
            Layout(content);
            Assert.True(secondWorkspace.IsDisposed);
            Assert.NotSame(secondWorkspace, session.ActiveWorkspace);
            Assert.Null(GetCommandHint(window));
            Assert.Null(closingTarget.Resolve(session.ActiveWorkspace));
            Assert.False(closingTarget.RestoreFocus(session.ActiveWorkspace));
            Assert.Equal(revision, session.Document.PublicationRevision);
            Assert.Equal(history, session.Document.History.Count);
        }
        finally
        {
            if (host is not null) { host.RootVisual = null; Pump(); host.Dispose(); }
            content.DataContext = null;
            Complete(session.DisposeAsync().AsTask());
            playbackTimer.Stop();
            typeof(MainWindow).GetField("_closeApproved", Private)!.SetValue(window, true);
            window.Close();
            Pump();
            string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts")) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test cleanup target escaped its owned artifact folder.");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyAllTracksTabFocus(DesktopSessionController session,
        FrameworkElement shell, TimelineWorkspaceViewModel returnWorkspace)
    {
        long revision = session.Document!.PublicationRevision;
        int history = session.Document.History.Count;
        MidoraId? primary = returnWorkspace.Selection.Primary;
        var all = session.OpenAllTracks();
        Layout(shell);
        AssertAllTracksFocus();

        // Selecting an existing Tab, including Ctrl+Tab's ActiveWorkspace path,
        // must restore the safe Surface, not its first focusable toolbar ComboBox.
        for (int i = 0; i < 3; i++)
        {
            session.ActiveWorkspace = returnWorkspace;
            Layout(shell);
            Piano(shell, returnWorkspace).Focus();
            session.ActiveWorkspace = all;
            Layout(shell);
            AssertAllTracksFocus();
        }

        var view = Assert.Single(Descendants<AllTracksView>(shell));
        var mode = Assert.Single(Descendants<ComboBox>(view));
        mode.Focus();
        mode.SelectedIndex = 1 - mode.SelectedIndex;
        Pump();
        AssertAllTracksFocus();

        // Background rebuilds must not continuously force focus away from a
        // toolbar control that the user deliberately chose after activation.
        mode.Focus();
        var scope = FocusManager.GetFocusScope(mode);
        Assert.Same(mode, FocusManager.GetFocusedElement(scope));
        all.RetryBuild();
        Pump();
        Assert.Same(mode, FocusManager.GetFocusedElement(scope));

        // Exercise the real ComboBox open/close state without creating a visible
        // Popup window: this test-only template deliberately has no PART_Popup.
        ControlTemplate template = mode.Template;
        mode.Template = new ControlTemplate(typeof(ComboBox));
        mode.ApplyTemplate();
        mode.IsDropDownOpen = true;
        Assert.True(mode.IsDropDownOpen);
        mode.Focus();
        mode.SelectedIndex = 1 - mode.SelectedIndex;
        Pump();
        Assert.Same(mode, FocusManager.GetFocusedElement(scope));
        mode.IsDropDownOpen = false;
        // WPF raises this notification from PART_Popup.Closed; our headless
        // template has no native popup, so deliver that control notification.
        Invoke(mode, "OnDropDownClosed", EventArgs.Empty);
        Pump();
        AssertAllTracksFocus();
        mode.Template = template;

        // Re-showing an already loaded view must hand focus back as well.
        view.Visibility = Visibility.Collapsed;
        Pump();
        view.Visibility = Visibility.Visible;
        Layout(shell);
        AssertAllTracksFocus();

        // A pending mode-return operation cannot focus an old hidden/closed Tab.
        mode.SelectedIndex = 1 - mode.SelectedIndex;
        session.ActiveWorkspace = returnWorkspace;
        Layout(shell);
        var surface = Piano(shell, returnWorkspace);
        surface.Focus();
        Pump();
        Assert.Same(surface, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(surface)));
        session.CloseWorkspace(all);
        Pump();
        Assert.Same(surface, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(surface)));
        Assert.Equal(revision, session.Document.PublicationRevision);
        Assert.Equal(history, session.Document.History.Count);
        Assert.Equal(primary, returnWorkspace.Selection.Primary);

        void AssertAllTracksFocus()
        {
            TimelineSurface timeline = Piano(shell, all);
            Assert.Same(timeline, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(timeline)));
            Assert.False(timeline.CanEdit);
            Assert.False(timeline.IsTimeRangeSelectionEnabled);
        }
    }

    private static void VerifyExplicitTemplateReuse(MainWindow window, DesktopSessionController session,
        HwndSource host, FrameworkElement shell, TimelineWorkspaceViewModel first, TimelineWorkspaceViewModel second)
    {
        session.ActiveWorkspace = first;
        host.RootVisual = null;
        Pump();
        ContentControl presenter = new()
        {
            Content = first,
            ContentTemplate = (DataTemplate)window.FindResource(new DataTemplateKey(typeof(TimelineWorkspaceViewModel)))
        };
        presenter.Resources.MergedDictionaries.Add(window.Resources);
        host.RootVisual = presenter;
        Layout(presenter);
        TimelineSurface surface = Piano(presenter, first);
        Assert.True(surface.IsLoaded);
        int contextChanges = 0;
        surface.DataContextChanged += (_, _) => contextChanges++;
        TimelineCommandTarget target = new(); target.Set(surface);
        session.ActiveWorkspace = second;
        presenter.Content = second;
        Layout(presenter);
        Assert.Same(surface, Piano(presenter, second));
        Assert.True(contextChanges > 0);
        Assert.Null(target.Resolve(second));
        Assert.True(surface.IsLoaded);
        host.RootVisual = null;
        PumpUntil(() => !presenter.IsLoaded && !surface.IsLoaded);
        presenter.Content = null;
        presenter.ContentTemplate = null;
        host.RootVisual = shell;
        Layout(shell);
    }

    private static void VerifyDialogReturn(Window dialog, MainWindow window, DesktopSessionController session,
        HwndSource host, FrameworkElement shell, WorkspaceViewModel workspace)
    {
        TimelineSurface source = Piano(shell, workspace);
        SetCommandHint(window, source);
        TimelineCommandTarget target = new(); target.Set(source);
        FrameworkElement draft = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
        draft.DataContext = dialog.DataContext;
        dialog.Content = null;
        draft.Resources.MergedDictionaries.Add(dialog.Resources);
        host.RootVisual = draft;
        Layout(draft);
        Assert.True(draft.IsLoaded);
        Assert.False(dialog.IsVisible);
        dialog.Close(); // Cancel/close the actual draft; never ShowDialog or submit.
        host.RootVisual = null;
        PumpUntil(() => !draft.IsLoaded);
        host.RootVisual = shell;
        Layout(shell);
        Assert.True(source.IsLoaded);
        Assert.Same(source, target.Resolve(session.ActiveWorkspace));
        Assert.Same(source, GetCommandHint(window));
        FocusManager.SetFocusedElement(shell, null);
        Invoke(window, "RestoreModalCommandFocus", workspace, source);
        Pump();
        // Invisible hosts cannot claim foreground keyboard activation. WPF's
        // logical focus still records the exact live source chosen by Focus().
        Assert.Same(source, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(source)));
    }

    private static HwndSource CreateHost(string name) => new(new HwndSourceParameters(name)
    { Width = 1600, Height = 1050, WindowStyle = unchecked((int)0x80000000), ParentWindow = IntPtr.Zero });

    private static void ActivateWithoutAudio(DesktopSessionController session, MidoraProject project)
    {
        ProjectCompilationSession compilation = new(project, executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromHours(1));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, new MidoraProjectPackageV1(MidoraSoftwareVersion.InformationalVersion));
        Type contextType = typeof(DesktopSessionController).GetNestedType("ProjectContext", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType, Private, binder: null,
            args: [new ProjectOwner(project), compilation, document, persistence, null, null, "No audio in hosted lifecycle tests"], culture: null)!;
        Complete((Task)Invoke(session, "ActivateAsync", context)!);
        Assert.False(session.CanPlayback);
    }

    private static TimelineSurface Piano(DependencyObject content, WorkspaceViewModel workspace) =>
        Assert.Single(Descendants<TimelineSurface>(content), surface => surface.IsVisible
            && ReferenceEquals(surface.DataContext, workspace) && surface.SurfaceMode == TimelineSurfaceMode.PianoRoll);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void SetCommandHint(MainWindow window, TimelineSurface source) =>
        typeof(MainWindow).GetProperty("_lastTimelineCommandSurface", Private)!.SetValue(window, source);
    private static object? GetCommandHint(MainWindow window) =>
        typeof(MainWindow).GetProperty("_lastTimelineCommandSurface", Private)!.GetValue(window);
    private static object? Invoke(object instance, string name, params object?[] args)
    {
        try { return instance.GetType().GetMethod(name, Private)!.Invoke(instance, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void Layout(FrameworkElement content)
    { content.Measure(new(1600, 1050)); content.Arrange(new Rect(0, 0, 1600, 1050)); content.UpdateLayout(); Pump(); }
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) { Pump(); Thread.Yield(); }
        Assert.True(condition(), "The hidden WPF host did not reach the expected lifecycle state.");
    }
    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private sealed class ProjectOwner(MidoraProject project) : IAsyncDisposable
    { public ValueTask DisposeAsync() { project.Dispose(); return ValueTask.CompletedTask; } }
}
