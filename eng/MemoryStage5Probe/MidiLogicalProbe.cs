using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Midora.Application;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

internal static class MidiLogicalProbe
{
    public static int WriteFixture(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("midi-fixture new-file.mid");
        using MemoryStream track = new();
        void Vlq(int value)
        {
            Span<byte> bytes = stackalloc byte[4]; int i = 3; bytes[i] = (byte)(value & 127);
            while ((value >>= 7) != 0) bytes[--i] = (byte)((value & 127) | 128);
            track.Write(bytes[i..]);
        }
        byte[] name = System.Text.Encoding.UTF8.GetBytes("MIDI Out #23");
        track.Write([0, 0xFF, 3, (byte)name.Length]); track.Write(name);
        int lastTick = 0;
        for (int i = 0; i < 20; i++)
        {
            int start = 168_960 + i * 6; Vlq(start - lastTick); track.Write([0x90, (byte)(60 + i % 12), 100]);
            Vlq(2); track.Write([0x80, (byte)(60 + i % 12), 0]); lastTick = start + 2;
        }
        Vlq(193_536 - lastTick); track.Write([0xFF, 0x2F, 0]);
        using FileStream output = new(Path.GetFullPath(args[0]), FileMode.CreateNew);
        output.Write("MThd"u8); output.Write([0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0]); output.Write("MTrk"u8);
        Span<byte> length = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, checked((int)track.Length));
        output.Write(length); track.Position = 0; track.CopyTo(output); return 0;
    }

    public static int Run(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException("midi-logical output MIDI-file maximum-selection template-note(0|1) revisions count-only|compile|background");
        string output = Path.GetFullPath(args[0]), input = Path.GetFullPath(args[1]);
        int maximum = int.Parse(args[2]), templateNotes = int.Parse(args[3]), revisions = int.Parse(args[4]);
        bool countOnly = args[5] == "count-only";
        bool background = args[5] == "background";
        if (maximum < 1 || maximum > 2_000_000 || templateNotes is < 0 or > 1 || revisions is < 0 or > 20
            || args[5] is not ("compile" or "count-only" or "background")) throw new ArgumentException("Invalid bounded real fixture.");
        Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "midi-logical-phases.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        List<object> phases = [];
        MidoraProject? project = null;
        ProjectCompilationSession? session = null;
        ProjectDocumentSession? document = null;
        ProjectObjectClipboardPayload? payload = null;
        MidoraCompiler? compiler = null;
        object? selectionSummary = null;
        bool sourcePairsExceedInt32 = false;
        try
        {
            Phase("import", () => project = MidiProjectImportService.ImportFile(input, Path.GetFileNameWithoutExtension(input)).Project);
            PureMidiTrack sourceTrack = project!.PureMidiTracks.Single(t => t.Name == "MIDI Out #23");
            MidiSegment source = sourceTrack.Segments.Single();
            long localStart = checked(168_960 - source.ProjectStartTick + source.ContentOffsetTick);
            List<SelectedNote> notes = [];
            Phase("select-and-count-pairs", () =>
            {
                foreach (var note in source.Notes.QueryValues(Math.Max(0, localStart), source.ContentEndTick))
                {
                    // This fixture freezes start-in-range selection (not any-intersection selection).
                    if (note.StartTick < localStart) continue;
                    notes.Add(new(note.Id, note.StartTick, checked(note.StartTick + note.LengthTicks), note.Key));
                    if (notes.Count == maximum) break;
                }
                notes.Sort(static (a, b) => { int order = a.Start.CompareTo(b.Start); return order != 0 ? order : a.Id.CompareTo(b.Id); });
                PriorityQueue<long, long>[] active = Enumerable.Range(0, 128).Select(_ => new PriorityQueue<long, long>()).ToArray();
                long pairs = 0, maximumActivePitch = 0, previousStart = -1;
                foreach (SelectedNote note in notes)
                {
                    if (note.End <= note.Start) continue;
                    if (note.Start < previousStart) throw new InvalidDataException("Unsorted diagnostic count fixture.");
                    previousStart = note.Start; PriorityQueue<long, long> queue = active[note.Key];
                    while (queue.TryPeek(out long end, out _) && end <= note.Start) queue.Dequeue();
                    pairs = checked(pairs + queue.Count); queue.Enqueue(note.End, note.End);
                    maximumActivePitch = Math.Max(maximumActivePitch, queue.Count);
                }
                using Stream inputStream = File.OpenRead(input);
                sourcePairsExceedInt32 = pairs > int.MaxValue;
                selectionSummary = new { track = sourceTrack.Name, trackId = sourceTrack.Id.Value, segmentId = source.Id.Value,
                    requestedProjectStartTick = 168_960, localStartTick = localStart, endTickExclusive = source.ContentEndTick,
                    maximumSelection = maximum, selectedCount = notes.Count, samePitchHalfOpenPairs = pairs,
                    pairsExceedInt32 = pairs > int.MaxValue, maximumActivePitch,
                    sampleBytes = inputStream.Length, sampleSha256 = Convert.ToHexStringLower(SHA256.HashData(inputStream)),
                    note = "Pair count uses original copied gates before destination exact-duplicate reduction/hard clipping; it is a conservative source-pair oracle for default no-pre-roll/no-release instrument, not canonical diagnostics count. Fixture sorting holds only <=2M compact selected metadata, not the imported object graph." };
            });
            log.WriteLine(JsonSerializer.Serialize(selectionSummary));
            if (!countOnly && sourcePairsExceedInt32)
                throw new InvalidOperationException("Conservative source-pair oracle exceeds existing Int32 diagnostic Count representation. Stopping before paste/compilation; do not truncate diagnostics to make the probe pass.");
            if (!countOnly)
            {
                EventInstrument instrument = EventInstrumentLibrary.Create(project);
                if (templateNotes == 1) instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, project.TicksPerQuarterNote, 60, 100));
                EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
                project.EventInstrumentUsages.Add(usage);
                LogicalTrack targetTrack = new(project) { Name = "Stage 5 converted Logical", EventInstrumentUsageId = usage.Id };
                Segment target = new(project) { ProjectStartTick = 168_960, LengthTicks = 24_576 };
                targetTrack.Segments.Add(target); project.Tracks.Add(targetTrack);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, targetTrack.Id));
                Phase("document-before-paste", () =>
                {
                    session = new(project, executionMode: ProjectCompilationExecutionMode.Background,
                        backgroundDebounce: background ? null : TimeSpan.FromHours(1));
                    document = new(session);
                });
                MidoraId[] ids = notes.Select(n => n.Id).ToArray(); notes.Clear(); notes.TrimExcess();
                Phase("copy-direct-notes", () => payload = ProjectObjectClipboard.CopyDirectMidiNotes(document!, source.Id, ids));
                ids = [];
                Phase("paste-as-logical", () => document!.Execute(ProjectObjectClipboard.CreatePasteNotesCommand(
                    document, payload!, target.Id, 0, targetIsDirectMidi: false)));
                int publishedCount = project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single().Notes.Count;
                log.WriteLine(JsonSerializer.Serialize(new { phase = "published-content", publishedCount, payload!.ObjectCount,
                    collapsedExactDuplicates = payload.ObjectCount - publishedCount, history = document!.History.Count,
                    targetProjectStartTick = target.ProjectStartTick, targetInitialLengthTicks = target.LengthTicks,
                    targetPublishedLengthTicks = project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single().LengthTicks }));
                Phase("clipboard-replaced", () => { payload.Dispose(); payload = null; });
                if (!background) compiler = new MidoraCompiler();
                CanonicalCompiledResult? current = null;
                CanonicalCompiledResult? previousSuccessful = session!.LastSuccessfulResult;
                Phase("full", () => current = background ? AwaitNaturalCompilation("paste") : compiler!.CompileFull(project));
                List<string> identities = [Sample("full", current!)];
                for (int i = 1; i <= revisions; i++)
                {
                    int revision = i;
                    Phase($"create-note-{i}", () =>
                    {
                        Segment actual = project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single();
                        var snapshot = actual.Notes.CreateQuerySnapshot();
                        long tick = 97L * revision + 1;
                        bool found = false;
                        for (int attempt = 0; attempt < 256 && tick + 1 < actual.LengthTicks; attempt++, tick++)
                            if (!snapshot.QueryValues(tick, tick + 1, 60, 60).Any(n => n.StartTick == tick)) { found = true; break; }
                        if (!found) throw new InvalidDataException("No empty exact Note start/key in bounded repro search.");
                        ProjectEditExecution edit = document!.Execute(ProjectDomainEditCommands.CreateLogicalNote(target.Id, tick, 1, 60, 80 + revision));
                        if (!edit.Changed || project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single().Notes.Count != publishedCount + revision)
                            throw new InvalidDataException("Formal CreateLogicalNote did not append one independent Note.");
                        log.WriteLine(JsonSerializer.Serialize(new { phase = "created-note", revision, relativeTick = tick, key = 60, gate = 1,
                            history = document.History.Count }));
                    });
                    Phase($"incremental-{i}", () =>
                    {
                        ProjectChangeSet changes = new(); changes.TrackIds.Add(targetTrack.Id);
                        current = background ? AwaitNaturalCompilation($"create-{revision}") : compiler!.CompileIncremental(project, changes);
                    });
                    identities.Add(Sample($"incremental-{i}", current!));
                }
                Phase("full-oracle-sampled", () =>
                {
                    using MidoraCompiler oracle = new();
                    if (Sample("full-oracle", oracle.CompileFull(project)) != identities[^1])
                        throw new InvalidDataException("Real fixture Full/Incremental sampled identity mismatch.");
                });
                for (int i = revisions; i > 0; i--)
                {
                    Phase($"undo-created-note-{i}", () => document!.Undo());
                    Phase($"undo-incremental-{i}", () =>
                    {
                        ProjectChangeSet changes = new(); changes.TrackIds.Add(targetTrack.Id);
                        current = background ? AwaitNaturalCompilation($"undo-create-{i}") : compiler!.CompileIncremental(project, changes);
                    });
                    if (Sample($"undo-incremental-{i}", current!) != identities[i - 1])
                        throw new InvalidDataException("Undo CreateLogicalNote changed prior sampled compilation identity.");
                }
                // Huge diagnostics are sampled, not all enumerated into a digest. Small synthetic
                // fixtures provide exact ordering/Full-Incremental checks with the same instrument defaults.
                current = null; compiler?.Dispose(); compiler = null;
                Phase("paste-undo", () => document!.Undo());
                if (project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single().Notes.Count != 0)
                    throw new InvalidDataException("Undo did not restore empty Logical target.");
                if (background) Phase("paste-undo-background", () => Sample("paste-undo-background", AwaitNaturalCompilation("paste-undo")));
                Phase("paste-redo", () => document!.Redo());
                if (project.Tracks.Single(t => t.Id == targetTrack.Id).Segments.Single().Notes.Count != publishedCount)
                    throw new InvalidDataException("Redo changed converted count.");
                if (background) Phase("paste-redo-background", () =>
                {
                    if (Sample("paste-redo-background", AwaitNaturalCompilation("paste-redo")) != identities[0])
                        throw new InvalidDataException("Redo Paste changed sampled compilation identity.");
                });
                previousSuccessful = null;

                CanonicalCompiledResult AwaitNaturalCompilation(string change)
                {
                    // Do not call EnsureCurrentCompilationAsync before the natural attempt has finished:
                    // that API intentionally bypasses debounce by requesting immediate scheduling.
                    long revision = session.SourceRevision;
                    Stopwatch timeout = Stopwatch.StartNew();
                    while (session.CompiledRevision != revision || session.CompilationState is not
                        (ProjectCompilationState.Succeeded or ProjectCompilationState.Failed))
                    {
                        if (timeout.Elapsed > TimeSpan.FromMinutes(15)) throw new TimeoutException("Natural background compilation exceeded probe's 15-minute wait budget.");
                        // This is an out-of-product measurement wait, not MIDI timing. Polling avoids
                        // a disposed notification target racing the worker's completion event.
                        Thread.Sleep(100);
                    }
                    if (session.SourceRevision != revision) throw new InvalidDataException("Unexpected concurrent source edit in serialized background fixture.");
                    if (!session.IsCompilationCurrent)
                        _ = session.EnsureCurrentCompilationAsync().GetAwaiter().GetResult(); // Surface an already completed internal failure.
                    CanonicalCompiledResult attempt = session.LastAttempt;
                    bool successfulRetained = ReferenceEquals(previousSuccessful, session.LastSuccessfulResult);
                    if (!attempt.IsConsumable && !successfulRetained)
                        throw new InvalidDataException("Failed background attempt replaced last successful result.");
                    var state = new { phase = "background-publication", change, sourceRevision = revision,
                        compiledRevision = session.CompiledRevision, state = session.CompilationState.ToString(),
                        naturalDebounceMilliseconds = 75, attempt.IsConsumable, attempt.FailureStage,
                        lastSuccessfulPresent = session.LastSuccessfulResult is not null, successfulRetained,
                        lastSuccessfulFingerprint = session.LastSuccessfulResult?.Fingerprint,
                        lastSuccessfulEventCount = session.LastSuccessfulResult?.TotalEventCount,
                        telemetry = session.LastCompilationTelemetry };
                    phases.Add(state); log.WriteLine(JsonSerializer.Serialize(state));
                    previousSuccessful = session.LastSuccessfulResult;
                    return attempt;
                }
            }
            notes.Clear(); notes.TrimExcess(); sourceTrack = null!; source = null!;
            Phase("close-document-project", () =>
            {
                payload?.Dispose(); payload = null; compiler?.Dispose(); compiler = null;
                document?.Dispose(); document = null; session?.Dispose(); session = null;
                project?.Dispose(); project = null;
            });
            Phase("release-controlled-gc", () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            Phase("release-idle-3s-no-gc", () => Thread.Sleep(3000));
            File.WriteAllText(Path.Combine(output, "midi-logical-result.json"), JsonSerializer.Serialize(new
            {
                schema = 2, input, countOnly, mode = args[5], templateNotes, revisions, selectionSummary, phases,
                applicationMvid = typeof(ProjectDocumentSession).Assembly.ManifestModule.ModuleVersionId,
                compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { payload?.Dispose(); compiler?.Dispose(); document?.Dispose(); session?.Dispose(); project?.Dispose(); }

        void Phase(string phase, Action action)
        {
            log.WriteLine(JsonSerializer.Serialize(new { phase, state = "begin", utc = DateTimeOffset.UtcNow }));
            Stopwatch watch = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes(true);
            action(); watch.Stop(); using Process process = Process.GetCurrentProcess(); process.Refresh();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            var result = new { phase, state = "complete", elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, managedBytes = GC.GetTotalMemory(false),
                gcCommittedBytes = gc.TotalCommittedBytes, gcHeapBytes = gc.HeapSizeBytes,
                gc.Index, gc.Generation, gc.Concurrent,
                privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64 };
            phases.Add(result); log.WriteLine(JsonSerializer.Serialize(result)); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        string Sample(string phase, CanonicalCompiledResult result)
        {
            int n = result.Diagnostics.Count;
            int[] ordinals = Enumerable.Range(0, Math.Min(n, 257)).Select(i => n <= 257 ? i : (int)((long)i * (n - 1) / 256)).Distinct().ToArray();
            var identity = new { result.IsConsumable, result.FailureStage, result.Fingerprint,
                result.TotalEventCount, result.TotalNoteOnEventCount, diagnosticCount = n,
                samples = ordinals.Select(index => new { index, diagnostic = result.Diagnostics[index] }).ToArray() };
            var record = new { phase = phase + "-sample", identity };
            phases.Add(record); log.WriteLine(JsonSerializer.Serialize(record));
            return JsonSerializer.Serialize(identity);
        }
    }
    private readonly record struct SelectedNote(MidoraId Id, long Start, long End, int Key);
}
