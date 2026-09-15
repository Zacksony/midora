using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Midora.Compiler;
using Midora.Domain;

try
{
    if (args.Length > 0 && args[0] == "guard") return MemoryGuard.Run(args[1..]);
    return RetentionProbe.Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal static class RetentionProbe
{
    public static int Run(string[] args)
    {
        if (args.Length != 6 || args[0] != "retention")
            throw new ArgumentException("retention OUTPUT TRIGGERS VOICES TEMPLATE_NOTES CYCLES");
        string output = Path.GetFullPath(args[1]);
        int count = int.Parse(args[2]), voices = int.Parse(args[3]), notes = int.Parse(args[4]), cycles = int.Parse(args[5]);
        if (count is < 1 or > 50_000 || voices is < 1 or > 8 || notes is < 1 or > 32 || cycles is < 1 or > 10
            || (long)count * voices * notes * 4 > 1_500_000)
            throw new ArgumentException("Probe safety bounds exceeded; not product limits.");
        Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "retention.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        void Write(object value) { string json = JsonSerializer.Serialize(value); log.WriteLine(json); Console.WriteLine(json); }
        Write(new { schema = 1, count, voices, notes, cycles, runtime = Environment.Version.ToString(),
            compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId,
            domainMvid = typeof(MidoraProject).Assembly.ManifestModule.ModuleVersionId,
            gcServer = System.Runtime.GCSettings.IsServerGC });
        Write(Sample("start"));
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            // No strong Project/compiler/result escapes the non-inlined frame.
            WeakReference[] references = CompileAndClose(count, voices, notes, Write);
            Write(Sample($"cycle-{cycle}-returned"));
            for (int pass = 0; pass < 3; pass++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
            }
            Write(Sample($"cycle-{cycle}-controlled-gc"));
            int alive = references.Count(reference => reference.IsAlive);
            Write(new { phase = $"cycle-{cycle}-weak-references", alive, total = references.Length });
            if (alive != 0) throw new InvalidOperationException("Closed compilation owners remain reachable after the scope returned.");
            Thread.Sleep(3000);
            Write(Sample($"cycle-{cycle}-idle-3s"));
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileAndClose(int count, int voices, int notes, Action<object> write)
    {
        using MidoraProject project = new(480, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        EventInstrument instrument = EventInstrumentLibrary.Create(project);
        instrument.RequiresChannelIsolation = true;
        instrument.OverlapPolicy = OverlapPolicy.Reject;
        instrument.TemplateLengthTicks = 480;
        instrument.LoopStartTick = 0;
        instrument.LoopEndTick = 480;
        for (int i = 1; i < voices; i++) instrument.SubVoices.Add(new SubVoice(project));
        foreach (SubVoice voice in instrument.SubVoices)
            for (int i = 0; i < notes; i++) voice.Events.Add(TemplateEvent.Note(project, i * 4L, 2, 60, 100));
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project) { Name = "Retention", EventInstrumentUsageId = usage.Id };
        Segment segment = new(project) { LengthTicks = count * 2048L + 1921 };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        for (int i = 0; i < count; i++)
            segment.Notes.Add(new LogicalNote(project) { StartTick = i * 2048L, LengthTicks = 1920,
                Note = 48 + i % 24, Velocity = 64 + i % 63 });
        using MidoraCompiler compiler = new();
        long allocation = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        CanonicalCompiledResult first = compiler.CompileFull(project);
        watch.Stop();
        Validate(first, count, voices, notes);
        write(new { phase = "full", elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation,
            first.TotalEventCount, first.TotalNoteOnEventCount });
        write(Sample("full-memory"));
        segment.Notes[0].Velocity = 101;
        ProjectChangeSet changes = new(); changes.TrackIds.Add(track.Id);
        allocation = GC.GetTotalAllocatedBytes(true); watch.Restart();
        CanonicalCompiledResult current = compiler.CompileIncremental(project, changes);
        watch.Stop(); Validate(current, count, voices, notes);
        write(new { phase = "incremental", elapsedMs = watch.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation });
        write(Sample("incremental-memory"));
        using MidoraCompiler oracleCompiler = new();
        CanonicalCompiledResult oracle = oracleCompiler.CompileFull(project);
        if (oracle.Fingerprint != current.Fingerprint || !oracle.Events.SequenceEqual(current.Events)
            || !oracle.Diagnostics.SequenceEqual(current.Diagnostics))
            throw new InvalidDataException("Full/Incremental exact event/source/diagnostic mismatch.");
        write(Sample("oracle-memory"));
        WeakReference[] references = [new(project), new(segment), new(instrument), new(compiler), new(first), new(current), new(oracle)];
        // Disposal plus actual stack exit are both required before testing reachability.
        compiler.ClearCache(); oracleCompiler.ClearCache();
        return references;
    }

    private static void Validate(CanonicalCompiledResult result, int count, int voices, int notes)
    {
        if (!result.IsConsumable || result.TotalNoteOnEventCount != (long)count * voices * notes * 4)
            throw new InvalidDataException("Unexpected canonical result for the fixed loop fixture.");
    }

    private static object Sample(string phase)
    {
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new { phase, process.PrivateMemorySize64, process.WorkingSet64,
            managedBytes = GC.GetTotalMemory(false), gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            gc.Generation, gc.Index, collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            map = ProcessMemoryMap.Capture() };
    }
}
