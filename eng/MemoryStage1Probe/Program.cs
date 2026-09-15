using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Cryptography;
using Midora.Application;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

internal static class Program
{
    private static Meter meter = null!;
    private static bool forceGc;
    private static MidoraProject? current;
    private static readonly List<WeakReference> probes = [];
    private static readonly CancellationTokenSource cancellation = new();

    public static int Main(string[] args)
    {
        if (args.Length < 3 || args[0] is not ("roundtrip" or "edited" or "save" or "open"))
        {
            Console.Error.WriteLine("Usage: probe <roundtrip|edited|save|open> <input> <new-output-directory> [--gc]");
            return 2;
        }
        string mode = args[0], input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory for every measurement.");
        // Resolve serializer dependencies before spending minutes on a large
        // baseline import, particularly when comparing isolated reference builds.
        _ = Assembly.Load("Google.Protobuf");
        Directory.CreateDirectory(output);
        forceGc = args.Contains("--gc");
        using (meter = new Meter(Path.Combine(output, "memory.csv"), cancellation))
        {
            Check("baseline");
            Console.WriteLine(JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), serverGC = System.Runtime.GCSettings.IsServerGC,
                processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString(), mode, input,
                sampleBytes = new FileInfo(input).Length, sampleSha256 = HashFile(input),
                assemblies = new[] { typeof(MidoraProject), typeof(MidoraProjectPackageV1), typeof(ProjectDocumentSession) }
                    .Select(t => new { name = t.Assembly.GetName().Name, path = t.Assembly.Location, sha256 = HashFile(t.Assembly.Location) }).ToArray() }));
            try { RunIsolated(mode, input, output); }
            catch (Exception e) { Console.WriteLine(e); return 1; }
            finally { current = null; }
            Check("released-before-GC");
            Collect(); Check("released-after-GC");
            Thread.Sleep(1000);
            Collect(); Check("idle-after-GC");
            Console.WriteLine(JsonSerializer.Serialize(new { weakProjectAlive = probes.Select(p => p.IsAlive).ToArray() }));
            Check("exit");
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunIsolated(string mode, string input, string output) => Run(mode, input, output).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Run(string mode, string input, string output)
    {
        var packages = new MidoraProjectPackageV1("1.0.0-dev", null, new Hooks());
        if (mode == "open")
        {
            Check("open-start");
            using var opened = await packages.OpenAsync(input, cancellation.Token);
            probes.Add(new WeakReference(opened.Project));
            current = opened.Project; Check("open-return");
            Collect(); Check("open-live-after-GC");
            Verify(current); current = null; return;
        }
        Check("import-start");
        var imported = MidiProjectImportService.ImportFile(input, Path.GetFileNameWithoutExtension(input));
        using var project = imported.Project;
        probes.Add(new WeakReference(project));
        current = project;
        Check("import-return");
        if (forceGc) { Collect(); Check("import-live-after-GC"); }
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromHours(1));
        using var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Unsaved);
        var persistence = new ProjectPersistenceCoordinator(document, packages);
        Check("document-ready");
        if (mode is "roundtrip" or "edited")
        {
            var segment = project.PureMidiTracks.SelectMany(t => t.Segments).MaxBy(s => s.Notes.Count)!;
            var selected = segment.Notes.QueryValues(segment.ContentOffsetTick, segment.ContentEndTick).Take(60_000).ToArray();
            var ids = selected.Select(n => n.Id).ToArray();
            foreach (var command in new[] { ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(segment.Id, ids, 0, 1),
                ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 1, 0) })
            {
                Check("edit-prepare");
                using var prepared = document.PrepareEdit(command, cancellation.Token);
                Check("edit-prepared"); document.ExecutePrepared(prepared); Check("edit-committed");
                document.Undo(); Check("undo-first-return"); document.Redo(); Check("redo-return");
                document.Undo(); Check("undo-repeat-return");
                Check("edit-history-done");
            }
            using var cancel = new CancellationTokenSource();
            try { using var ignored = document.PrepareEdit(ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 2, 0),
                cancel.Token, new CancelAfterWork(cancel)); }
            catch (OperationCanceledException) { }
            var track = project.PureMidiTracks.First(t => t.Segments.Contains(segment));
            var source = project.PureMidiTracks.First(t => t.Id != track.Id);
            persistence.Presentation.Replace(new(ProjectPresentationAllTracksModeV3.Compiled,
                [new(track.Id, true, .3, [source.Id])], []));
            if (mode == "edited")
            {
                using var prepared = document.PrepareEdit(ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 1, 0), cancellation.Token);
                document.ExecutePrepared(prepared);
                Check("dirty-edit-committed");
            }
            Check("edit-and-presentation-done");
        }
        var expected = Counts(project);
        long expectedNextId = project.NextStableId;
        string saved = Path.Combine(output, "saved.midora"), copy = Path.Combine(output, "copy.midora");
        Check("save-start");
        await persistence.SaveProjectAsync(saved, cancellationToken: cancellation.Token);
        Check("save-return");
        if (forceGc) { await Task.Delay(200); Collect(); Check("save-live-after-GC"); }
        if (mode != "save")
        {
            Check("copy-start");
            await persistence.SaveCopyAsync(copy, cancellationToken: cancellation.Token);
            Check("copy-return");
            if (forceGc) { await Task.Delay(200); Collect(); Check("copy-live-after-GC"); }
            Check("reopen-start");
            using var opened = await packages.OpenAsync(copy, cancellation.Token);
            probes.Add(new WeakReference(opened.Project));
            Check("reopen-return");
            Census(opened.Project, "reopened-census"); Verify(opened.Project);
            if (Counts(opened.Project) != expected || opened.Project.NextStableId != expectedNextId
                || opened.Project.DamagedPureMidiTracks.Count != 0 || opened.Diagnostics.Count != 0)
                throw new InvalidDataException("Round-trip counts, identities, or diagnostics changed.");
            if (forceGc) { await Task.Delay(200); Collect(); Check("reopen-both-live-after-GC"); }
        }
        Verify(project); current = null;
    }

    private static string HashFile(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static (long Notes, long Events, long Opaque, int Tracks, int Segments) Counts(MidoraProject project) =>
        (project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.Notes.Count)),
         project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.ChannelEvents.Count)),
         project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.OpaqueEvents.Count)),
         project.PureMidiTracks.Count, project.PureMidiTracks.Sum(t => t.Segments.Count));

    private static void Verify(MidoraProject project)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { verifyNotes = project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.Notes.Count)),
            damagedTracks = project.DamagedPureMidiTracks.Count, tracks = project.PureMidiTracks.Count }));
    }
    private static void Check(string phase)
    {
        meter.Phase = phase; meter.Write(phase, console: true);
        if (current is not null) Census(current, phase);
    }
    private static void Census(MidoraProject project, string phase)
    {
        string[] names = ["_materializedSourceIds", "_materializedSourceValues", "_sourceIndices", "_materializedSourceItems", "_added"];
        Dictionary<string, long> counts = [];
        foreach (var segment in project.PureMidiTracks.SelectMany(t => t.Segments))
            foreach (object collection in new object[] { segment.Notes, segment.ChannelEvents, segment.OpaqueEvents })
            foreach (string name in names)
            {
                var value = collection.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(collection);
                if (value is null) continue;
                var count = value.GetType().GetProperty("Count")?.GetValue(value);
                if (count is not null) counts[collection.GetType().Name + "." + name] = counts.GetValueOrDefault(collection.GetType().Name + "." + name) + Convert.ToInt64(count);
            }
        Console.WriteLine(JsonSerializer.Serialize(new { phase, census = counts }));
    }
    private static void Collect()
    { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true); }

    private sealed class Hooks : IMidoraPackageFaultInjectorV1
    {
        public void ThrowIfRequested(MidoraPackageFaultPointV1 point, string path)
        {
            Check(point.ToString());
            if (forceGc) { Collect(); Check(point + "-after-GC"); }
            cancellation.Token.ThrowIfCancellationRequested();
        }
    }
    private sealed class CancelAfterWork(CancellationTokenSource source) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress p) { if (p.Completed > 0) source.Cancel(); } }

    private sealed class Meter : IDisposable
    {
        private readonly StreamWriter log;
        private readonly Timer timer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource cancel;
        private readonly object sync = new();
        public volatile string Phase = "startup";
        public Meter(string path, CancellationTokenSource cancel)
        {
            this.cancel = cancel;
            log = new StreamWriter(path) { AutoFlush = true };
            log.WriteLine("seconds,phase,wsMiB,privateMiB,peakWsMiB,managedMiB,heapMiB,fragmentedMiB,committedMiB,allocatedMiB,gen0,gen1,gen2");
            timer = new Timer(_ => Write(Phase, false), null, 0, 100);
        }
        public void Write(string phase, bool console)
        {
            lock (sync)
            {
                using var p = Process.GetCurrentProcess(); var gc = GC.GetGCMemoryInfo(); const double M = 1048576d;
                string line = FormattableString.Invariant($"{clock.Elapsed.TotalSeconds:F3},{phase},{p.WorkingSet64/M:F2},{p.PrivateMemorySize64/M:F2},{p.PeakWorkingSet64/M:F2},{GC.GetTotalMemory(false)/M:F2},{gc.HeapSizeBytes/M:F2},{gc.FragmentedBytes/M:F2},{gc.TotalCommittedBytes/M:F2},{GC.GetTotalAllocatedBytes(false)/M:F2},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}");
                log.WriteLine(line);
                if (console) Console.WriteLine(line);
                if (p.PrivateMemorySize64 > 14L*1024*1024*1024) cancel.Cancel();
            }
        }
        public void Dispose() { using var ended = new ManualResetEvent(false); timer.Dispose(ended); ended.WaitOne(); lock(sync) log.Dispose(); }
    }
}
