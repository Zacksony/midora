using System.Diagnostics;
using System.Text.Json;
using Midora.Application;
using Midora.Domain;
using Midora.Playback;

internal static class HistoryProbe
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("history output-directory note-count command-count");
        string output = Path.GetFullPath(args[0]);
        int count = int.Parse(args[1]), commands = int.Parse(args[2]);
        if (count < 1 || count > 1_000_000 || commands < 1 || commands > 100)
            throw new ArgumentException("History fixture safety bounds exceeded, not product limits.");
        Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "history-phases.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        List<object> phases = [];
        using MidoraProject project = new(480, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        EventInstrument instrument = EventInstrumentLibrary.Create(project);
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project) { Name = "History Logical", EventInstrumentUsageId = usage.Id };
        Segment source = new(project) { LengthTicks = count * 4L + 4 };
        track.Segments.Add(source); project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        // Empty initial compile avoids making compiler expansion part of this attribution probe.
        // All later content edits use the real document commands; deferred compile is explicit.
        using ProjectCompilationSession compilation = new(project, executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromHours(1));
        Phase("construct", () =>
        {
            for (int i = 0; i < count; i++)
                source.Notes.Add(new LogicalNote(project) { StartTick = i * 4L, LengthTicks = 3, Note = i % 128, Velocity = 100 });
        });
        using ProjectDocumentSession document = new(compilation);
        ProjectObjectClipboardPayload? payload = null;
        object[] capturedStores = [];
        try
        {
            Phase("copy", () => payload = ProjectObjectClipboard.CopySegments(document, [source.Id], source.Id));
            object storage = typeof(ProjectObjectClipboardPayload).GetProperty("StorageOwner",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(payload)!;
            capturedStores = ((IEnumerable<IDisposable>)storage.GetType().GetField("_resources",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(storage)!).Cast<object>()
                .Where(value => value.GetType().GetProperty("ResidentBytes") is not null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
            Ownership("copy");
            for (int i = 1; i <= commands; i++)
            {
                int iteration = i;
                Phase($"paste-{i}", () => document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
                    document, payload!, track.Id, source.LengthTicks * iteration)));
                Assert(project.Tracks.Single().Segments.Count == i + 1, "Paste segment count.");
                Assert(project.Tracks.Single().Segments[^1].Notes.Count == count, "Paste source note count.");
                Ownership($"paste-{i}");
            }
            Assert(document.History.Count == commands, "One history entry per paste.");
            Phase("clipboard-replacement", () =>
            {
                using ProjectObjectClipboardPayload replacement = ProjectObjectClipboard.CopySegments(document, [source.Id], source.Id);
                payload!.Dispose(); payload = null;
            });
            Ownership("clipboard-replacement");
            Phase("undo-all", () => { for (int i = 0; i < commands; i++) document.Undo(); });
            Assert(project.Tracks.Single().Segments.Count == 1 && document.CanRedo, "Deep undo retains redo.");
            Phase("redo-all", () => { for (int i = 0; i < commands; i++) document.Redo(); });
            Assert(project.Tracks.Single().Segments.Count == commands + 1, "Deep redo retains content.");
            Phase("branch", () =>
            {
                document.Undo();
                using ProjectObjectClipboardPayload replacement = ProjectObjectClipboard.CopySegments(document, [source.Id], source.Id);
                document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(document, replacement, track.Id,
                    source.LengthTicks * (commands + 2)));
            });
            Assert(!document.CanRedo && document.History.Count == commands, "New branch discards only obsolete redo.");
            Phase("history-dispose", document.Dispose);
            Ownership("history-dispose");
            Phase("compilation-dispose", compilation.Dispose);
            Phase("project-dispose", project.Dispose);
            Phase("controlled-gc", () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            File.WriteAllText(Path.Combine(output, "history-result.json"), JsonSerializer.Serialize(new
            {
                schema = 1, count, commands, compilation = "background-deferred-one-hour-for-attribution",
                applicationMvid = typeof(ProjectDocumentSession).Assembly.ManifestModule.ModuleVersionId,
                domainMvid = typeof(MidoraProject).Assembly.ManifestModule.ModuleVersionId, phases
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { payload?.Dispose(); }

        void Phase(string phase, Action action)
        {
            log.WriteLine(JsonSerializer.Serialize(new { phase, state = "begin" }));
            long allocated = GC.GetTotalAllocatedBytes(true); Stopwatch watch = Stopwatch.StartNew();
            action(); watch.Stop(); using Process process = Process.GetCurrentProcess(); process.Refresh();
            var record = new { phase, state = "complete", elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, naturalManagedBytes = GC.GetTotalMemory(false),
                privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64 };
            phases.Add(record); log.WriteLine(JsonSerializer.Serialize(record)); Console.WriteLine(JsonSerializer.Serialize(record));
        }
        void Ownership(string phase)
        {
            long Resident(object store) => Convert.ToInt64(store.GetType().GetProperty("ResidentBytes")!.GetValue(store));
            long Spill(object store) => Convert.ToInt64(store.GetType().GetProperty("SpillBytes")!.GetValue(store));
            var record = new { phase = phase + "-source-clipboard-storage", uniqueStores = capturedStores.Length,
                residentPayloadBytes = capturedStores.Sum(Resident), spillBytes = capturedStores.Sum(Spill),
                note = "Only the original clipboard stores, reference-deduplicated. Not total History/project metadata or working reservations." };
            phases.Add(record); log.WriteLine(JsonSerializer.Serialize(record));
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
