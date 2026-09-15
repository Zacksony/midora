using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;

// Runs real WPF templates off-screen, without UI automation, audio, or user data writes.
// References only pre-existing APIs so precisely the same harness runs against stage 1.
internal static class Program
{
    private static readonly List<(string Kind, WeakReference Reference)> References = [];
    private static readonly List<double> Frames = [];
    private static readonly List<object> Measurements = [];
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            int cycles = args.Length > 0 ? int.Parse(args[0]) : 100;
            string? midi = args.Length > 1 ? args[1] : null;
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow();
            var session = (DesktopSessionController)typeof(MainWindow).GetField("_session", Private)!.GetValue(window)!;
            var elapsed = Stopwatch.StartNew();
            Measure("initialized", session);
            for (int projectIndex = 0; projectIndex < 2; projectIndex++)
            {
                RunProject(window, session, projectIndex, cycles, midi);
                Collect(); Measure($"project-{projectIndex}-closed", session);
            }
            Frames.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                desktopAssembly = typeof(MainWindow).Assembly.Location,
                cyclesPerProject = cycles, projects = 2, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                frameMedianMs = Frames[Frames.Count / 2], frameP95Ms = Frames[(int)(Frames.Count * .95)], frameMaxMs = Frames[^1],
                measurements = Measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            GC.KeepAlive(window); GC.KeepAlive(session);
            window.Close(); Pump();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunProject(MainWindow window, DesktopSessionController session, int projectIndex, int cycles, string? midi)
    {
        if (midi is not null) Wait(session.ImportMidiAsNewProjectAsync(midi));
        else Wait(session.CreateProjectAsync(new NewProjectCreationRequest
        { ProjectName = "Stage 2 lifetime probe", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved }));
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = session.Project!.EventInstruments.Last();
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
        var logical = session.Project.Tracks.Last();
        session.Execute(ProjectDomainEditCommands.CreateSegment(logical.Id, 0, 192));
        if (session.Project.PureMidiTracks.Count == 0)
        {
            session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            session.Execute(ProjectDomainEditCommands.CreateMidiSegment(session.Project.PureMidiTracks[0].Id, 0, 192));
        }
        var direct = session.Project.PureMidiTracks.OrderByDescending(t => t.Segments.Sum(s => (long)s.Notes.Count)).First(t => t.Segments.Count > 0);
        var midiSegment = direct.Segments.OrderByDescending(s => s.Notes.Count).First();
        Console.WriteLine($"project {projectIndex}: MIDI notes in target={midiSegment.Notes.Count:N0}; cycles={cycles}");
        Measure($"project-{projectIndex}-loaded", session);
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            Exercise(window, session, session.OpenSegment(logical.Segments[0].Id));
            Exercise(window, session, session.OpenSegment(midiSegment.Id));
            var voice = session.OpenInstrument(instrument.Id);
            session.ActivateSubVoiceEditor(voice, instrument.SubVoices[0].Id);
            Exercise(window, session, voice);
            Exercise(window, session, session.OpenWorkspace(session.ProjectTree.Single(n => n.Kind == ProjectTreeNodeKind.Conductor)));
            Exercise(window, session, session.OpenAllTracks());
            if ((cycle + 1) % 20 == 0) Measure($"project-{projectIndex}-cycle-{cycle + 1}", session);
        }
        Measure($"project-{projectIndex}-natural", session);
        Collect(); Measure($"project-{projectIndex}-controlled-gc", session);
        Remember("Project", session.Project);
        Wait(session.CloseProjectAsync());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exercise(MainWindow window, DesktopSessionController session, WorkspaceViewModel workspace)
    {
        // Use the application's real DataTemplate, styles, bindings and controls.
        var presenter = new ContentControl
        {
            Content = workspace,
            ContentTemplate = (DataTemplate)window.FindResource(new DataTemplateKey(workspace.GetType()))
        };
        presenter.Resources = window.Resources;
        var timer = Stopwatch.StartNew();
        presenter.Measure(new(1200, 760)); presenter.Arrange(new Rect(0, 0, 1200, 760)); presenter.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(presenter);
        Frames.Add(timer.Elapsed.TotalMilliseconds);
        var surfaces = Descendants(presenter).OfType<TimelineSurface>().ToArray();
        foreach (var surface in surfaces) { Remember("Surface", surface); Remember("Snapshot", surface.Snapshot); }
        Remember(workspace.GetType().Name, workspace);
        if (surfaces.Length > 0)
        {
            // Simulates the MainWindow's last command/focus hint without input automation.
            var property = typeof(MainWindow).GetProperty("_lastTimelineCommandSurface", Private);
            if (property is not null) property.SetValue(window, surfaces[0]);
            else typeof(MainWindow).GetField("_lastTimelineCommandSurface", Private)!.SetValue(window, surfaces[0]);
        }
        // Real WPF Unloaded handlers retire consumer subscriptions before releasing template.
        foreach (var surface in surfaces) surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        session.CloseWorkspace(workspace);
        presenter.Content = null; presenter.ContentTemplate = null; presenter.UpdateLayout();
        BindingOperations.ClearAllBindings(presenter);
        Pump();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = VisualTreeHelper.GetChild(value, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Remember(string kind, object? value) { if (value is not null) References.Add((kind, new(value))); }
    private static void Measure(string phase, DesktopSessionController session)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        GCMemoryInfo heap = GC.GetGCMemoryInfo();
        var live = References.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count(r => r.Reference.IsAlive));
        var record = new
        {
            phase, managedMiB = GC.GetTotalMemory(false) / 1048576d,
            workingMiB = process.WorkingSet64 / 1048576d, privateMiB = process.PrivateMemorySize64 / 1048576d,
            gcHeapMiB = heap.HeapSizeBytes / 1048576d, gcFragmentedMiB = heap.FragmentedBytes / 1048576d,
            gcCommittedMiB = heap.TotalCommittedBytes / 1048576d, peakWorkingMiB = process.PeakWorkingSet64 / 1048576d,
            allocatedMiB = GC.GetTotalAllocatedBytes(false) / 1048576d,
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            cpuSeconds = process.TotalProcessorTime.TotalSeconds,
            live, pianoSubscribers = CountHandlers(session.PianoRollEditorSettings), arrangementSubscribers = CountHandlers(session.ArrangementEditorSettings)
        };
        Measurements.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
    }
    private static int CountHandlers(TimelineEditorSettings settings) =>
        ((PropertyChangedEventHandler?)typeof(ObservableObject).GetField("PropertyChanged", Private)!.GetValue(settings))?
        .GetInvocationList().Count(h => h.Target is WorkspaceViewModel) ?? 0;
    private static void Collect()
    {
        Pump(); GC.Collect(); GC.WaitForPendingFinalizers();
        Pump(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }
    private static void Wait(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        { if (timer.Elapsed > TimeSpan.FromMinutes(10)) throw new TimeoutException("Probe operation timed out."); Pump(); }
        task.GetAwaiter().GetResult();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
