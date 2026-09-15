global using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<(string Kind, WeakReference Value)> References = [];
    private static readonly List<object> Measurements = [];
    private static StreamWriter Log = null!;
    private static string Output = "";
    private static WeakReference<DesktopSessionController>? SessionReference;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "guard") return MemoryGuard.Run(args[1..]);
        try
        {
            if (args.Length != 6 || args[0] != "run")
                throw new ArgumentException("run OUTPUT MIDI MAX_SELECTION EDITS VIEW_CYCLES");
            Output = Path.GetFullPath(args[1]);
            string input = Path.GetFullPath(args[2]);
            int maximum = int.Parse(args[3]), edits = int.Parse(args[4]), cycles = int.Parse(args[5]);
            if (maximum is < 1 or > 2_000_000 || edits is < 0 or > 20 || cycles is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(args), "Probe safety bounds exceeded.");
            if (Directory.Exists(Output)) throw new IOException("Output directory must be new.");
            Directory.CreateDirectory(Output);
            using var log = new StreamWriter(new FileStream(Path.Combine(Output, "wpf-phases.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
            Log = log;
            using (Stream source = File.OpenRead(input))
                Write(new { phase = "fixture", input, bytes = source.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(source)), maximum, edits, cycles,
                    desktopMvid = typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId,
                    applicationMvid = typeof(ProjectDocumentSession).Assembly.ManifestModule.ModuleVersionId,
                    playbackMvid = typeof(ProjectCompilationSession).Assembly.ManifestModule.ModuleVersionId,
                    compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId });
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow();
            var session = (DesktopSessionController)typeof(MainWindow).GetField("_session", Private)!.GetValue(window)!;
            SessionReference = new(session);
            if (session.HasEnabledSoundFonts) throw new InvalidOperationException("Probe executable directory must have no enabled SoundFonts.");
            Measure("initialized");
            var summary = RunImportedScenario(window, session, input, maximum, edits, cycles);
            VerifyClosed("imported", session);
            RunReopenedScenario(window, session, summary);
            VerifyClosed("reopened", session);
            RunReopenedScenario(window, session, summary with { Saved = summary.Copy });
            VerifyClosed("save-copy-reopened", session);
            var finalSummary = new { summary.Saved, summary.Copy, summary.TrackId, summary.SegmentId,
                summary.SourceTrackId, summary.Selected, summary.Published, summary.SourceCensus,
                identitySha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(summary.Identity))) };
            Write(new { phase = "complete", summary = finalSummary, passed = true });
            File.WriteAllText(Path.Combine(Output, "wpf-result.json"), JsonSerializer.Serialize(new
            {
                schema = 2, input, maximum, edits, cycles, summary = finalSummary, passed = true, measurements = Measurements,
                note = "Real offscreen WPF templates and natural background Desktop session; no audio or human input/visual acceptance. Weak references and VM region classes do not identify every native owner."
            }, new JsonSerializerOptions { WriteIndented = true }));
            GC.KeepAlive(window); GC.KeepAlive(session); GC.KeepAlive(app);
            Wait(session.DisposeAsync().AsTask());
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ScenarioSummary RunImportedScenario(MainWindow window, DesktopSessionController session,
        string input, int maximum, int edits, int cycles)
    {
        Phase("import", () => Wait(session.ImportMidiAsNewProjectAsync(input)));
        RememberSession(session);
        AwaitCompilation(session, "import");
        MidoraProject project = session.Project!;
        MidiCensus sourceCensus = Census(project);
        Write(new { phase = "imported-source-census", sourceCensus });
        PureMidiTrack sourceTrack = project.PureMidiTracks.Single(t => t.Name == "MIDI Out #23");
        MidiSegment source = sourceTrack.Segments.Single();
        long sourceStart = checked(168_960 - source.ProjectStartTick + source.ContentOffsetTick);
        CompressedMidoraIdSet selected = CompressedMidoraIdSet.Empty;
        Phase("select-start-in-range", () => selected = Wait(Task.Run(() => CompressedMidoraIdSet.Create(
            source.Notes.QueryValues(Math.Max(0, sourceStart), source.ContentEndTick)
                .Where(note => note.StartTick >= sourceStart).Take(maximum).Select(note => note.Id)))));
        if (selected.Count == 0) throw new InvalidDataException("Fixture contains no selected notes.");
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Stage 6 instrument"));
        EventInstrument instrument = project.EventInstruments.Last();
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Stage 6 converted Logical", instrument.Id));
        LogicalTrack targetTrack = project.Tracks.Last();
        session.Execute(ProjectDomainEditCommands.CreateSegment(targetTrack.Id, 168_960, 24_576));
        MidoraId targetId = targetTrack.Segments.Single().Id;
        TimelineWorkspaceViewModel sourceView = session.OpenSegment(source.Id);
        sourceView.StartTick = Math.Max(0, sourceStart); sourceView.TickSpan = 24_576;
        Render(window, session, sourceView, "direct-before-copy");
        TimelineWorkspaceViewModel targetView = session.OpenSegment(targetId);
        AwaitCompilation(session, "setup");
        ProjectObjectClipboardPayload? payload = null;
        Phase("copy", () => payload = Wait(Task.Run(() => ProjectObjectClipboard.CopyDirectMidiNotes(session.Document!, source.Id, selected))));
        selected = CompressedMidoraIdSet.Empty;
        Remember("Clipboard", payload);
        // Match MainWindow's session-local Clipboard ownership without touching the OS Clipboard.
        typeof(MainWindow).GetField("_projectClipboard", Private)!.SetValue(window, payload);
        typeof(MainWindow).GetField("_clipboardDocument", Private)!.SetValue(window, session.Document);
        long beforeCancel = session.Document!.Compilation.SourceRevision;
        Phase("cancel-paste-zero-publication", () =>
        {
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            try
            {
                using StagedProjectEdit unexpected = Wait(Task.Run(() => session.PrepareProjectEdit(
                    ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload!, targetId, 0, false),
                    targetView, cancellation.Token)));
                throw new InvalidDataException("Canceled preparation unexpectedly completed.");
            }
            catch (OperationCanceledException) { }
            if (session.Document!.Compilation.SourceRevision != beforeCancel || ActualTarget().Notes.Count != 0)
                throw new InvalidDataException("Canceled paste published data.");
        });
        Phase("cancel-paste-after-work-zero-publication", () =>
        {
            using CancellationTokenSource cancellation = new();
            var progress = new CancelAfterWork(cancellation);
            try
            {
                using StagedProjectEdit unexpected = Wait(Task.Run(() => session.PrepareProjectEdit(
                    ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload!, targetId, 0, false),
                    targetView, cancellation.Token, progress)));
                throw new InvalidDataException("In-flight canceled preparation unexpectedly completed.");
            }
            catch (OperationCanceledException) { }
            if (!progress.ObservedWork || session.Document!.Compilation.SourceRevision != beforeCancel || ActualTarget().Notes.Count != 0)
                throw new InvalidDataException("In-flight cancellation did not stop after work with zero publication.");
            Write(new { phase = "cancellation-observed", progress.LastPhase, progress.Completed });
        });
        Phase("paste-prepare-and-publish", () =>
        {
            using StagedProjectEdit prepared = Wait(Task.Run(() => session.PrepareProjectEdit(
                ProjectObjectClipboard.CreatePasteNotesCommand(session.Document!, payload!, targetId, 0, false), targetView)));
            if (!session.ExecutePreparedPreservingWorkspaceSelection(prepared, targetView).Changed)
                throw new InvalidDataException("Paste failed to publish.");
        });
        int selectedCount = payload!.ObjectCount, published = ActualTarget().Notes.Count;
        if (ActualTarget().LengthTicks != 24_576) throw new InvalidDataException("Paste changed fixed target length.");
        Write(new { phase = "published", sourceTrack = sourceTrack.Name, selectedCount, published,
            exactDuplicatesCollapsed = selectedCount - published, targetStartTick = 168_960, targetLengthTicks = 24_576,
            selectionCount = targetView.Selection.SharedIds.Count });
        List<string> identities = [AwaitCompilation(session, "paste")];
        // Keep actual templates present while compilation/history/save consumers coexist.
        using var targetHost = new ViewHost(window, targetView);
        targetHost.Render();
        session.SetCustomOnionSources(targetView, [sourceTrack.Id]);
        session.SetOnionOpacity(targetView, .5);
        var all = session.OpenAllTracks();
        var instrumentView = session.OpenInstrument(instrument.Id);
        session.ActivateSubVoiceEditor(instrumentView, instrument.SubVoices[0].Id);
        var conductor = session.OpenWorkspace(session.ProjectTree.Single(n => n.Kind == ProjectTreeNodeKind.Conductor));
        var arrangement = session.OpenArrangement();
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            sourceView.ObjectList.IsVisible = cycle % 2 == 0;
            targetView.ObjectList.IsVisible = cycle % 2 == 0;
            instrumentView.ObjectList.IsVisible = cycle % 2 == 0;
            sourceView.StartTick = Math.Max(0, sourceStart) + cycle % 2 * 12_288;
            sourceView.TickSpan = cycle % 2 == 0 ? 3_072 : 32_768;
            Render(window, session, sourceView, $"direct-{cycle}");
            targetView.StartTick = 21_504;
            targetView.TickSpan = cycle % 2 == 0 ? 3_072 : 3_073;
            Render(window, session, targetView, $"logical-onion-edge-{cycle}");
            Render(window, session, instrumentView, $"instrument-{cycle}");
            Render(window, session, conductor, $"conductor-{cycle}");
            Render(window, session, arrangement, $"arrangement-{cycle}");
            session.ActiveWorkspace = all; all.Mode = ProjectPresentationAllTracksModeV3.Raw;
            all.StartTick = 168_960; Render(window, session, all, $"all-raw-{cycle}");
            all.Mode = ProjectPresentationAllTracksModeV3.Compiled;
            PumpUntil(() => !all.IsBuilding, "compiled All Tracks");
            Render(window, session, all, $"all-compiled-{cycle}");
        }
        session.ActiveWorkspace = targetView;
        for (int i = 1; i <= edits; i++)
        {
            int revision = i;
            Phase($"create-{i}", () =>
            {
                var snapshot = ActualTarget().Notes.CreateQuerySnapshot();
                long tick = revision * 97L + 1;
                bool free = false;
                for (int attempt = 0; attempt < 256 && tick + 1 < 24_576; attempt++, tick++)
                    if (!snapshot.QueryValues(tick, tick + 1, 60, 60).Any(note => note.StartTick == tick)) { free = true; break; }
                if (!free || !session.Execute(ProjectDomainEditCommands.CreateLogicalNote(targetId, tick, 1, 60, 80 + revision)).Changed)
                    throw new InvalidDataException("Small edit failed to add a note.");
                if (ActualTarget().Notes.Count != published + revision) throw new InvalidDataException("Small edit count mismatch.");
            });
            identities.Add(AwaitCompilation(session, $"create-{i}")); targetHost.Render();
        }
        for (int i = edits; i > 0; i--)
        {
            Phase($"undo-create-{i}", session.Undo);
            if (AwaitCompilation(session, $"undo-create-{i}") != identities[i - 1])
                throw new InvalidDataException("Undo did not restore the prior canonical/diagnostic sample.");
            targetHost.Render();
        }
        Phase("undo-paste", session.Undo);
        if (ActualTarget().Notes.Count != 0) throw new InvalidDataException("Paste Undo did not empty target.");
        AwaitCompilation(session, "undo-paste");
        Phase("redo-paste", session.Redo);
        if (ActualTarget().Notes.Count != published || AwaitCompilation(session, "redo-paste") != identities[0])
            throw new InvalidDataException("Paste Redo changed content/result.");
        Phase("replace-clipboard", () =>
        {
            payload.Dispose(); payload = null;
            typeof(MainWindow).GetField("_projectClipboard", Private)!.SetValue(window, null);
            typeof(MainWindow).GetField("_clipboardDocument", Private)!.SetValue(window, null);
        });
        session.ActiveWorkspace = all; all.Mode = ProjectPresentationAllTracksModeV3.Compiled;
        PumpUntil(() => !all.IsBuilding, "All Tracks before save");
        using var allHost = new ViewHost(window, all); allHost.Render();
        string saved = Path.Combine(Output, "saved.midora"), copy = Path.Combine(Output, "copy.midora");
        Phase("save-with-history-and-views", () => Wait(session.SaveProjectAsync(saved)));
        Phase("save-copy-with-history-and-views", () => Wait(session.SaveCopyAsync(copy, false)));
        int historyCount = session.Document.History.Count;
        if (historyCount < 4) throw new InvalidDataException("Saving unexpectedly cleared history.");
        Write(new { phase = "history-after-save", count = historyCount, session.Document.CanUndo, session.Document.CanRedo });
        targetHost.Dispose(); allHost.Dispose();
        Remember("Canonical", session.Document.Compilation.LastAttempt);
        Remember("Canonical", session.Document.Compilation.LastSuccessfulResult);
        Phase("close-imported", () => Wait(session.CloseProjectAsync()));
        return new(saved, copy, targetTrack.Id, targetId, sourceTrack.Id, selectedCount, published, identities[0], sourceCensus);

        Segment ActualTarget() => session.Project!.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single(s => s.Id == targetId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunReopenedScenario(MainWindow window, DesktopSessionController session, ScenarioSummary expected)
    {
        Phase("reopen", () => Wait(session.OpenProjectAsync(expected.Saved)));
        RememberSession(session);
        MidiCensus reopenedCensus = Census(session.Project!);
        if (reopenedCensus != expected.SourceCensus) throw new InvalidDataException("Reopen changed source MIDI census.");
        Write(new { phase = "reopened-source-census", reopenedCensus });
        Segment target = session.Project!.Tracks.Single(t => t.Id == expected.TrackId).Segments.Single(s => s.Id == expected.SegmentId);
        if (target.Notes.Count != expected.Published || target.ProjectStartTick != 168_960 || target.LengthTicks != 24_576)
            throw new InvalidDataException("Reopen changed converted source.");
        if (session.Document!.History.Count != 0) throw new InvalidDataException("Undo history leaked into saved Project.");
        if (AwaitCompilation(session, "reopened") != expected.Identity)
            throw new InvalidDataException("Reopened canonical/diagnostics sample mismatch.");
        var view = session.OpenSegment(expected.SegmentId);
        var onion = session.GetOnionControls(view)!;
        if (!onion.Enabled || onion.Opacity != .5 || !onion.Sources.Contains(expected.SourceTrackId))
            throw new InvalidDataException("Presentation did not survive explicit save/reopen.");
        Render(window, session, view, "reopened-logical-onion");
        var all = session.OpenAllTracks();
        if (all.Mode != ProjectPresentationAllTracksModeV3.Compiled) throw new InvalidDataException("All Tracks mode not restored.");
        PumpUntil(() => !all.IsBuilding, "reopened All Tracks");
        Render(window, session, all, "reopened-all-compiled");
        Phase("close-reopened", () => Wait(session.CloseProjectAsync()));
    }

    private static string AwaitCompilation(DesktopSessionController session, string label)
    {
        string identity = "";
        Phase("background-" + label, () =>
        {
            ProjectCompilationSession compilation = session.Document!.Compilation;
            long revision = compilation.SourceRevision;
            CanonicalCompiledResult? previous = compilation.LastSuccessfulResult;
            PumpUntil(() => compilation.CompiledRevision == revision && compilation.CompilationState is
                ProjectCompilationState.Succeeded or ProjectCompilationState.Failed, "natural background compile");
            if (compilation.SourceRevision != revision) throw new InvalidDataException("Revision changed during serialized probe.");
            if (!compilation.IsCompilationCurrent) Wait(compilation.EnsureCurrentCompilationAsync());
            CanonicalCompiledResult result = compilation.LastAttempt;
            if (!result.IsConsumable && !ReferenceEquals(previous, compilation.LastSuccessfulResult))
                throw new InvalidDataException("Failed compilation replaced previous success.");
            int count = result.Diagnostics.Count;
            int[] ordinals = Enumerable.Range(0, Math.Min(count, 257))
                .Select(i => count <= 257 ? i : (int)((long)i * (count - 1) / 256)).Distinct().ToArray();
            identity = JsonSerializer.Serialize(new { result.IsConsumable, result.FailureStage, result.Fingerprint,
                result.TotalEventCount, result.TotalNoteOnEventCount, diagnosticCount = count,
                diagnostics = ordinals.Select(index => new { index, value = result.Diagnostics[index] }).ToArray() });
            Write(new { phase = "publication-" + label, revision, compilation.CompiledRevision,
                state = compilation.CompilationState.ToString(), result.IsConsumable, diagnosticCount = count,
                lastSuccessfulPresent = compilation.LastSuccessfulResult is not null,
                lastSuccessfulEventCount = compilation.LastSuccessfulResult?.TotalEventCount,
                identitySha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))),
                compilation.LastCompilationTelemetry });
            // Access only a bounded diagnostic window, matching the virtual row consumer.
            var rows = session.CompilerDiagnostics;
            for (int i = 0; i < Math.Min(rows.Count, 256); i++) GC.KeepAlive(rows[i]);
        });
        return identity;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Render(MainWindow window, DesktopSessionController session, WorkspaceViewModel workspace, string label)
    {
        session.ActiveWorkspace = workspace;
        Phase("render-" + label, () => { using var host = new ViewHost(window, workspace); host.Render(); });
    }

    private sealed class ViewHost : IDisposable
    {
        private readonly ContentControl presenter;
        private bool disposed;
        public ViewHost(MainWindow window, WorkspaceViewModel workspace)
        {
            presenter = new ContentControl { Content = workspace,
                ContentTemplate = (DataTemplate)window.FindResource(new DataTemplateKey(workspace.GetType())), Resources = window.Resources };
            Remember(workspace.GetType().Name, workspace);
        }
        public void Render()
        {
            presenter.Measure(new(1200, 760)); presenter.Arrange(new Rect(0, 0, 1200, 760)); presenter.UpdateLayout();
            foreach (FrameworkElement element in Descendants(presenter).OfType<FrameworkElement>())
                element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Pump();
            var bitmap = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(presenter);
            foreach (TimelineSurface surface in Descendants(presenter).OfType<TimelineSurface>())
            { Remember("Surface", surface); Remember("Snapshot", surface.Snapshot); }
            if (presenter.Content is WorkspaceViewModel workspace)
            {
                // A detached visual has no HWND visibility transition. Explicitly activate
                // the real provider and request only its visible row window; do not claim
                // this is a measurement of the pane's IsVisible-driven request scheduler.
                workspace.ObjectList.SetActive(workspace.ObjectList.IsVisible);
                if (workspace.ObjectList.Source is { } source)
                {
                    TimelineObjectListRow[] rows = Wait(source.ReadRowsAsync(0, Math.Min(32, source.Count)));
                    Write(new { phase = "object-list-window", ownerKind = source.OwnerKind.ToString(),
                        source.Count, returnedRows = rows.Length, source.IsActivated });
                }
                Remember("ObjectListSource", workspace.ObjectList.Source);
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (FrameworkElement element in Descendants(presenter).OfType<FrameworkElement>())
                element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            if (presenter.Content is WorkspaceViewModel workspace) workspace.ObjectList.SetActive(false);
            presenter.Content = null; presenter.ContentTemplate = null; presenter.UpdateLayout();
            BindingOperations.ClearAllBindings(presenter); Pump();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RememberSession(DesktopSessionController session)
    { Remember("Project", session.Project); Remember("Document", session.Document); Remember("Compilation", session.Document!.Compilation); }
    private static void Remember(string kind, object? value)
    { if (value is not null) References.Add((kind, new WeakReference(value))); }
    private static Dictionary<string, int> Live() => References.GroupBy(r => r.Kind)
        .ToDictionary(group => group.Key, group => group.Count(item => item.Value.IsAlive));

    private static void VerifyClosed(string label, DesktopSessionController session)
    {
        if (session.HasProject || session.Workspaces.Count != 0 || session.CompilerDiagnostics.Count != 0)
            throw new InvalidDataException("Close left an active Project/UI consumer.");
        Measure(label + "-closed-natural");
        Phase(label + "-raster-drain", () =>
        {
            PumpUntil(() => TimelineRasterCacheSession.InFlightCount == 0
                && TimelineRasterCacheSession.RunningCount == 0
                && TimelineRasterCacheSession.ActiveSubscriptionCount == 0
                && TimelineRasterCacheSession.QueuedCompletionCount == 0, "closed raster work", TimeSpan.FromSeconds(30));
            if (TimelineRasterCacheSession.CurrentBytes != 0 || TimelineRasterCacheSession.CompletedCount != 0)
                throw new InvalidDataException("Closed session retained raster cache entries.");
        });
        Collect(false); Measure(label + "-closed-controlled-gc");
        Wait(Task.Delay(3000)); Measure(label + "-closed-idle-3s");
        Collect(true); Measure(label + "-closed-compacting-gc");
        if (Live().Values.Any(count => count != 0))
            throw new InvalidDataException("Tracked closed-session objects are still alive: " + JsonSerializer.Serialize(Live()));
    }

    private static void Collect(bool compact)
    {
        Pump();
        if (compact) GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, compact); GC.WaitForPendingFinalizers();
        Pump(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, compact); GC.WaitForPendingFinalizers(); Pump();
    }

    private static void Phase(string phase, Action action)
    {
        Write(new { phase, state = "begin", utc = DateTimeOffset.UtcNow });
        var watch = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes(true);
        action(); watch.Stop(); Measure(phase, watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated);
    }
    private static void Measure(string phase, double? elapsedMs = null, long? allocatedBytes = null)
    {
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        var record = new { phase, state = "complete", elapsedMs, allocatedBytes,
            managedBytes = GC.GetTotalMemory(false), allocatedTotalBytes = GC.GetTotalAllocatedBytes(false),
            gcCommittedBytes = gc.TotalCommittedBytes, gcHeapBytes = gc.HeapSizeBytes, gcFragmentedBytes = gc.FragmentedBytes,
            gc.Index, gc.Generation, gc.Concurrent, privateBytes = process.PrivateMemorySize64,
            privateMinusGcCommittedBytes = process.PrivateMemorySize64 - gc.TotalCommittedBytes,
            workingSetBytes = process.WorkingSet64, cpuSeconds = process.TotalProcessorTime.TotalSeconds,
            handleCount = process.HandleCount, threadCount = process.Threads.Count,
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            live = Live(), memoryMap = ProcessMemoryMap.Capture(), preparationStorage = PreparationStorage(),
            raster = new { TimelineRasterCacheSession.CurrentBytes, TimelineRasterCacheSession.CompletedCount,
                TimelineRasterCacheSession.InFlightCount, TimelineRasterCacheSession.ActiveSubscriptionCount,
                TimelineRasterCacheSession.QueuedCompletionCount, TimelineRasterCacheSession.RunningCount } };
        Measurements.Add(record); Write(record);
    }
    private static void Write(object value)
    { string json = JsonSerializer.Serialize(value); Log.WriteLine(json); Console.WriteLine(json); }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    { PumpUntil(() => task.IsCompleted, "async operation"); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> completed, string operation, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > (timeout ?? TimeSpan.FromMinutes(15))) throw new TimeoutException(operation + " exceeded the probe wait limit.");
            Pump();
            // Probe-only backoff; never participates in MIDI/audio timing.
            Thread.Sleep(1);
        }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private sealed record ScenarioSummary(string Saved, string Copy, MidoraId TrackId, MidoraId SegmentId,
        MidoraId SourceTrackId, int Selected, int Published, string Identity, MidiCensus SourceCensus);

    private sealed record MidiCensus(int Roots, int Tracks, int Segments, long Notes, long ChannelEvents, long OpaqueEvents);
    private static MidiCensus Census(MidoraProject project) => new(project.MidiChannelRoots.Count,
        project.PureMidiTracks.Count, project.PureMidiTracks.Sum(track => track.Segments.Count),
        project.PureMidiTracks.Sum(track => track.Segments.Sum(segment => (long)segment.Notes.Count)),
        project.PureMidiTracks.Sum(track => track.Segments.Sum(segment => (long)segment.ChannelEvents.Count)),
        project.PureMidiTracks.Sum(track => track.Segments.Sum(segment => (long)segment.OpaqueEvents.Count)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PreparationStorageSnapshot? PreparationStorage()
    {
        if (SessionReference is not { } reference || !reference.TryGetTarget(out DesktopSessionController? session)) return null;
        return session.Document?.Compilation.PreparationStorage;
    }

    private sealed class CancelAfterWork(CancellationTokenSource cancellation) : IProgress<TimelineEditPreparationProgress>
    {
        public bool ObservedWork { get; private set; }
        public string? LastPhase { get; private set; }
        public long Completed { get; private set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            if (ObservedWork || value.Completed == 0 || value.Phase == TimelineEditPreparationPhase.Ready) return;
            ObservedWork = true; LastPhase = value.Phase.ToString(); Completed = value.Completed;
            cancellation.Cancel();
        }
    }
}
