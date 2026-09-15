using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Compiler;
using Midora.Common;
using Midora.Desktop;
using Midora.Domain;
using Midora.Persistence;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("OUTPUT_DIRECTORY");
            string output = Path.GetFullPath(args[0]);
            if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
            Directory.CreateDirectory(output);
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow();
            var session = (DesktopSessionController)typeof(MainWindow)
                .GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            if (session.HasEnabledSoundFonts) throw new InvalidOperationException("Use an isolated executable directory with no SoundFonts.");
            using var project = CreateProject();
            using var compiler = new MidoraCompiler();
            var watch = Stopwatch.StartNew();
            var compiled = compiler.CompileFull(project);
            double compileMilliseconds = watch.Elapsed.TotalMilliseconds;
            Check(compiled.Diagnostics.Count == 2_147_516_416, "Full diagnostic count");
            using var workspace = new DiagnosticsWorkspaceViewModel();
            workspace.Replace(new VirtualDiagnosticRows(compiled.Diagnostics, true));
            session.Workspaces.Add(workspace); session.ActiveWorkspace = workspace;
            var host = new ContentControl
            {
                Content = workspace, Resources = window.Resources,
                ContentTemplate = (DataTemplate)window.FindResource(new DataTemplateKey(workspace.GetType()))
            };
            Layout(host);
            var list = Descendants(host).OfType<ListBox>().Single(x => x.Name == "DiagnosticList");
            var next = Descendants(host).OfType<Button>().Single(x => Equals(x.Content, "Next"));
            var previous = Descendants(host).OfType<Button>().Single(x => Equals(x.Content, "Previous"));
            var go = Descendants(host).OfType<Button>().Single(x => Equals(x.Content, "Go"));
            var input = Descendants(host).OfType<TextBox>().Single(x => x.Name == "DiagnosticPageInput");
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "initial-controls", next.IsVisible,
                next.Visibility, next.ActualWidth, previous.IsEnabled, count = list.Items.Count,
                workspace.IsDiagnosticPagingVisible, workspace.Summary }));
            Check(IsShownInTemplate(next) && !previous.IsEnabled && list.Items.Count == 4096, "Initial page controls");
            int realized = Descendants(list).OfType<ListBoxItem>().Count();
            Check(realized is > 0 and < 256, "Virtualized actual WPF containers");
            VerifyDiagnosticWheel(host, list, workspace);
            session.SelectedDiagnostic = workspace.Diagnostics[0];
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(host);
            Check(workspace.DiagnosticPageText == "2" && session.SelectedDiagnostic is null, "Click next clears retired row");
            var pageBeforeSuspend = workspace.Diagnostics;
            workspace.SuspendPresentation(); workspace.SetScope(null); workspace.ResumePresentation(); Layout(host);
            Check(ReferenceEquals(pageBeforeSuspend, workspace.Diagnostics), "Tab suspension retains current page");
            input.Text = "524289"; go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(host);
            Check(((VirtualDiagnosticRows)workspace.Diagnostics).StartOrdinal == 2_147_483_648, "Actual bound page input beyond Int32");
            input.Text = "not a number"; go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(host);
            Check(workspace.DiagnosticPageError.Length > 0 && workspace.Diagnostics.Count == 4096, "Invalid input keeps page");
            input.Text = ((VirtualDiagnosticRows)workspace.Diagnostics).PageCount.ToString();
            go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(host);
            Check(!next.IsEnabled && workspace.Diagnostics[^1].SourceReference.LogicalNoteId == project.Tracks[0].Segments[0].Notes[^1].Id,
                "Last row maps to its real source");
            workspace.SearchText = "No such diagnostic message";
            PumpUntil(() => !workspace.IsFiltering); Layout(host);
            Check(workspace.Diagnostics.Count == 0 && !IsShownInTemplate(next), "Whole-source empty filter");
            watch.Restart();
            workspace.SearchText = "MIDORA2201";
            PumpUntil(() => !workspace.IsFiltering); Layout(host);
            double filterMilliseconds = watch.Elapsed.TotalMilliseconds;
            Check(((VirtualDiagnosticRows)workspace.Diagnostics).TotalCount == compiled.Diagnostics.Count && IsShownInTemplate(next),
                "Whole-source filter returns every pair, not one page");
            Check(Descendants(host).OfType<Button>().Any(x => Equals(x.Content, "Go to Source")), "Details navigation retained");
            var bitmap = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "diagnostic-page.png"))) encoder.Save(stream);
            workspace.SearchText = string.Empty;
            workspace.Replace(new VirtualDiagnosticRows(Enumerable.Range(0, 500)
                .Select(index => new CompilerDiagnostic($"ORDINARY{index}", DiagnosticSeverity.Info,
                    "A normal diagnostic", new(Tick: index))).ToArray(), true));
            Layout(host);
            Check(!IsShownInTemplate(next), "Ordinary scrollable list has no pagination");
            VerifyDiagnosticWheel(host, list, workspace);
            workspace.Replace(new VirtualDiagnosticRows(new CompilerDiagnostic[]
                { new("SMALL", DiagnosticSeverity.Info, "A normal diagnostic", new()) }, true));
            Layout(host);
            Check(!IsShownInTemplate(next) && list.Items.Count == 1, "Normal list has no pagination");
            VerifyDiagnosticWheel(host, list, workspace);
            workspace.Replace(Array.Empty<DiagnosticRow>());
            Layout(host);
            VerifyDiagnosticWheel(host, list, workspace);
            var packages = new MidoraProjectPackageV1(MidoraSoftwareVersion.InformationalVersion);
            string capacityFixture = Path.Combine(output, "diagnostics-int64.midora");
            Task saved = packages.SaveCopyAsync(project, capacityFixture);
            PumpUntil(() => saved.IsCompleted); saved.GetAwaiter().GetResult();
            using var warningProject = CreateProject(51, OverlapPolicy.Warn);
            string readmeFixture = Path.Combine(output, "diagnostics-readme-1275.midora");
            saved = packages.SaveCopyAsync(warningProject, readmeFixture);
            PumpUntil(() => saved.IsCompleted); saved.GetAwaiter().GetResult();
            Check(compiler.CompileFull(warningProject).Diagnostics.Count == 1275, "README acceptance fixture diagnostic count");
            RetainedStorageCollector storage = new();
            ((CompilerDiagnosticList)compiled.Diagnostics).CollectRetainedStorage(storage);
            var result = new
            {
                passed = true, wheelPassed = true, compiled.Diagnostics.Count, realized, compileMilliseconds, filterMilliseconds,
                capacityFixture, readmeFixture,
                diagnosticStorageBytes = storage.ToArray().Sum(part => part.Bytes),
                managedBytes = GC.GetTotalMemory(false), privateBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                desktopMvid = typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId,
                compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId,
                note = "Actual offscreen WPF templates, bindings and routed button/wheel handlers; no computer-use, native audio or human visual/focus acceptance."
            };
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            host.Content = null; host.ContentTemplate = null; Layout(host);
            session.Workspaces.Remove(workspace); session.ActiveWorkspace = null;
            Task cleanup = session.DisposeAsync().AsTask(); PumpUntil(() => cleanup.IsCompleted); cleanup.GetAwaiter().GetResult();
            GC.KeepAlive(window); GC.KeepAlive(app);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VerifyDiagnosticWheel(FrameworkElement host, ListBox list, DiagnosticsWorkspaceViewModel workspace)
    {
        var viewer = Descendants(list).OfType<ScrollViewer>().First();
        Check(viewer.CanContentScroll && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Item,
            "Diagnostic scrolling is measured in items");
        string summary = workspace.Summary;
        var rows = workspace.Diagnostics;
        list.SelectedItem = rows.Count > 0 ? rows[0] : null;
        object? selected = list.SelectedItem;
        Layout(host);
        viewer.ScrollToTop(); Layout(host);
        foreach (int delta in new[] { -120, -120, 120, -360, 120, 0 })
        {
            double expected = Math.Clamp(viewer.VerticalOffset - delta / 120d, 0, viewer.ScrollableHeight);
            var row = Descendants(list).OfType<ListBoxItem>().FirstOrDefault();
            UIElement source = row is null ? list : Descendants(row).OfType<TextBlock>().First();
            RaiseWheel(source, delta, expected);
        }
        viewer.ScrollToTop(); Layout(host); RaiseWheel(list, 120, 0);
        viewer.ScrollToBottom(); Layout(host); RaiseWheel(list, -120, viewer.ScrollableHeight);
        Check(ReferenceEquals(selected, list.SelectedItem) && ReferenceEquals(rows, workspace.Diagnostics)
            && workspace.Summary == summary, "Wheel keeps diagnostic selection, source, page and counts");
        Check(Descendants(list).OfType<ListBoxItem>().Count() < 256, "Wheel retains bounded virtualized rows");
        list.SelectedItem = null;
        viewer.ScrollToTop(); Layout(host);

        void RaiseWheel(UIElement source, int delta, double expected)
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = source };
            source.RaiseEvent(wheel); Layout(host);
            Check(wheel.Handled, "Diagnostic list consumes wheel including boundaries/empty content");
            Check(viewer.VerticalOffset == expected,
                $"Diagnostic wheel delta {delta}: expected row offset {expected}, actual {viewer.VerticalOffset}");
        }
    }

    private static MidoraProject CreateProject(int count = 65_537, OverlapPolicy policy = OverlapPolicy.Reject)
    {
        var project = new MidoraProject(480);
        project.Metadata.ProjectName = "Diagnostics acceptance fixture";
        var instrument = new EventInstrument(project) { Name = "Overlap fixture", RootNote = 60,
            TemplateLengthTicks = 480, OverlapPolicy = policy, OverlapScope = OverlapScope.SamePitch };
        var voice = new SubVoice(project) { Name = "Piano" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        var usage = new EventInstrumentUsage(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        var track = new LogicalTrack(project) { Name = "Diagnostic fixture", EventInstrumentUsageId = usage.Id };
        var segment = new Segment(project) { LengthTicks = count * 2L };
        for (int index = 0; index < count; index++) segment.Notes.Add(new LogicalNote(project)
            { StartTick = index, LengthTicks = count, Note = 60, Velocity = 100 });
        track.Segments.Add(segment); project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        return project;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    // Offscreen trees have no visible HWND, so IsVisible cannot test a template's
    // collapsed ancestors. Inspect the actual template visibility chain instead.
    private static bool IsShownInTemplate(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static void Layout(FrameworkElement element)
    { element.Measure(new(1200, 760)); element.Arrange(new Rect(0, 0, 1200, 760)); element.UpdateLayout(); Pump(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(); Pump(); Thread.Yield(); }
        Pump();
    }
}
