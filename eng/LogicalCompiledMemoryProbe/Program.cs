using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Midora.Compiler;
using Midora.Compiler.Tests;
using Midora.Domain;
using Midora.Common;

try
{
    if (args.Length > 0 && args[0] == "guard") return MemoryGuard.Run(args[1..]);
    if (args.Length > 0 && args[0] == "compression") return CompressionMicrobenchmark.Run(args);
    return LogicalMemoryProbe.Run(args);
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

internal static class LogicalMemoryProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 3 || args[0] is not ("run" or "cold"))
            throw new ArgumentException("run|cold OUTPUT SCENARIO [COUNT [TEMPLATE_NOTES LOOPS VOICES]]");
        string output = Path.GetFullPath(args[1]);
        string scenario = args[2];
        int count = args.Length > 3 ? int.Parse(args[3]) : 8;
        int templateNotes = args.Length > 4 ? int.Parse(args[4]) : 8;
        int loops = args.Length > 5 ? int.Parse(args[5]) : 4;
        int voices = args.Length > 6 ? int.Parse(args[6]) : 4;
        Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "result.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        void Write(object value) { string text = JsonSerializer.Serialize(value); log.WriteLine(text); Console.WriteLine(text); }
        Write(new { schema = 1, scenario, count, templateNotes, loops, voices, runtime = Environment.Version.ToString(),
            compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId });
        WeakReference[] owners = args[0] == "cold"
            ? ExecuteCold(scenario, count, templateNotes, loops, voices, Write)
            : Execute(scenario, count, templateNotes, loops, voices, output, Write);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        int alive = owners.Count(reference => reference.IsAlive);
        Write(new { phase = "closed-controlled-gc", alive, owners = owners.Length, memory = Memory() });
        if (alive != 0) throw new InvalidOperationException("Closed compiler/result ownership leaked past scenario stack.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Execute(string scenario, int count, int templateNotes, int loops, int voices, string output, Action<object> write)
    {
        using LogicalCompiledMemoryOracle.Fixture fixture = CreateFixture(scenario, count, templateNotes, loops, voices);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult first = Compile("full", () => compiler.CompileFull(fixture.Project));
        if (first.IsConsumable && first.EndTick > first.StartTick)
        {
            MeasureQuery("first-window", first.StartTick, Math.Min(first.StartTick + 512, first.EndTick));
            MeasureQuery("last-window", Math.Max(first.StartTick, first.EndTick - 512), first.EndTick);
        }
        string firstDigest = Digest("full", first);
        if (scenario == "mixed") InspectConsumer("full", first);
        if (scenario != "invalid" && !first.IsConsumable) throw new InvalidDataException("Fixture must compile successfully.");
        if (scenario is "complex" or "mixed")
        {
            CanonicalCompiledResult range = Compile("range", () => compiler.CompileFull(fixture.Project, LogicalCompiledMemoryOracle.Request(true)));
            Digest("range", range);
            if (scenario == "mixed") InspectConsumer("range", range);
        }
        fixture.Segment.Notes[0].StartTick++;
        CanonicalCompiledResult edited = Compile("incremental", () => compiler.CompileIncremental(fixture.Project,
            new ProjectChangeSet { TrackIds = { fixture.Track.Id } }));
        string editedDigest = Digest("incremental", edited);
        if (scenario != "invalid" && editedDigest == firstDigest) throw new InvalidDataException("Fixture edit must alter the output.");
        using MidoraCompiler oracle = new();
        CanonicalCompiledResult full = Compile("fresh-full", () => oracle.CompileFull(fixture.Project), oracle);
        if (Digest("fresh-full", full) != editedDigest) throw new InvalidDataException("Full/Incremental formal digest differs.");
        compiler.ClearCache(); oracle.ClearCache();
        if (Digest("retained-old", first) != firstDigest) throw new InvalidDataException("Old immutable result changed.");
        RetainedStorageCollector combined = new();
        object Storage(CanonicalCompiledResult value)
        {
            RetainedStorageCollector own = new(); value.CollectRetainedStorage(own);
            value.CollectRetainedStorage(combined);
            RetainedStoragePart[] parts = own.ToArray();
            return new { bytes = parts.Sum(p => p.Bytes), parts = parts.Length };
        }
        object firstStorage = Storage(first), editedStorage = Storage(edited), freshStorage = Storage(full);
        write(new { phase = "retained-storage", first = firstStorage, edited = editedStorage, fresh = freshStorage,
            combinedBytes = combined.ToArray().Sum(p => p.Bytes) });
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        write(new { phase = "three-results-controlled-gc", memory = Memory() });
        GC.KeepAlive(first); GC.KeepAlive(edited); GC.KeepAlive(full); GC.KeepAlive(combined);
        return [new(fixture.Project), new(compiler), new(first), new(edited), new(full)];

        CanonicalCompiledResult Compile(string phase, Func<CanonicalCompiledResult> action, MidoraCompiler? owner = null)
        {
            IoCounters beforeIo = ReadIo();
            double beforeCpuMs = CpuMilliseconds();
            long before = GC.GetTotalAllocatedBytes(true); Stopwatch watch = Stopwatch.StartNew();
            CanonicalCompiledResult value = action(); watch.Stop();
            IoCounters afterIo = ReadIo();
            write(new { phase, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - before,
                cpuMs = CpuMilliseconds() - beforeCpuMs,
                value.IsConsumable, value.TotalEventCount, value.TotalNoteOnEventCount, memory = Memory(),
                logicalStorage = typeof(MidoraCompiler).GetProperty("LastLogicalStorageTelemetry")?.GetValue(owner ?? compiler),
                io = new { readBytes = afterIo.ReadBytes - beforeIo.ReadBytes, writeBytes = afterIo.WriteBytes - beforeIo.WriteBytes,
                    readCalls = afterIo.ReadCalls - beforeIo.ReadCalls, writeCalls = afterIo.WriteCalls - beforeIo.WriteCalls } });
            return value;
        }
        string Digest(string phase, CanonicalCompiledResult value)
        {
            long before = GC.GetTotalAllocatedBytes(true); Stopwatch watch = Stopwatch.StartNew();
            string digest = LogicalCompiledMemoryOracle.Digest(value); watch.Stop();
            write(new { phase = phase + "-oracle", digest, value.Fingerprint, elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - before, memory = Memory() });
            return digest;
        }
        void MeasureQuery(string phase, long start, long end)
        {
            long before = GC.GetTotalAllocatedBytes(true); Stopwatch watch = Stopwatch.StartNew();
            var query = LogicalCompiledMemoryOracle.QueryDigest(first, start, end); watch.Stop();
            write(new { phase, start, end, query.Digest, query.EventCount, elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - before, memory = Memory() });
        }
        void InspectConsumer(string phase, CanonicalCompiledResult result)
        {
            using StreamWriter writer = new(new FileStream(Path.Combine(output, phase + "-consumer.jsonl"), FileMode.CreateNew));
            string digest = LogicalCompiledMemoryOracle.ConsumerDigest(result, writer.WriteLine);
            write(new { phase = phase + "-consumer-oracle", digest });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExecuteCold(string scenario, int count, int notes, int loops, int voices, Action<object> write)
    {
        using var fixture = CreateFixture(scenario, count, notes, loops, voices);
        using MidoraCompiler compiler = new();
        var io = ReadIo(); long before = GC.GetTotalAllocatedBytes(true);
        Stopwatch timer = Stopwatch.StartNew();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project); timer.Stop();
        var afterIo = ReadIo();
        if (!result.IsConsumable) throw new InvalidDataException("Expected consumable result.");
        write(new { phase = "full", elapsedMs = timer.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - before,
            result.TotalEventCount, result.TotalNoteOnEventCount, result.Fingerprint, memory = Memory(),
            logicalStorage = compiler.LastLogicalStorageTelemetry,
            readBytes = afterIo.ReadBytes - io.ReadBytes, writeBytes = afterIo.WriteBytes - io.WriteBytes });
        Storage("before-clear"); compiler.ClearCache(); Storage("after-clear");
        return [new(fixture.Project), new(compiler), new(result)];

        void Storage(string phase)
        {
            RetainedStorageCollector collector = new(); result.CollectRetainedStorage(collector);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object? paged = typeof(CanonicalCompiledResult).GetField("_logicalEventSource", flags)?.GetValue(result);
            object? values = paged?.GetType().GetField("_values", flags)?.GetValue(paged);
            object? sources = values?.GetType().GetField("Sources", flags)?.GetValue(values);
            object? sourceCount = sources?.GetType().GetProperty("Count", flags)?.GetValue(sources);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            write(new { phase, retainedBytes = collector.ToArray().Sum(p => p.Bytes), sourceCount, memory = Memory() });
            GC.KeepAlive(result); GC.KeepAlive(compiler); GC.KeepAlive(fixture);
        }
    }

    private static LogicalCompiledMemoryOracle.Fixture CreateFixture(string scenario, int count, int notes, int loops, int voices)
    {
        if (scenario != "segmented") return LogicalCompiledMemoryOracle.Create(scenario, count, notes, loops, voices);
        var baseFixture = LogicalCompiledMemoryOracle.Create("expansion", count, notes, loops, voices);
        baseFixture.Track.Segments.Clear();
        Segment? first = null;
        foreach (LogicalNote source in baseFixture.Segment.Notes)
        {
            Segment segment = new(baseFixture.Project) { ProjectStartTick = source.StartTick, LengthTicks = source.LengthTicks + 1 };
            segment.Notes.Add(new LogicalNote(baseFixture.Project) { StartTick = 0, LengthTicks = source.LengthTicks, Note = source.Note, Velocity = source.Velocity });
            baseFixture.Track.Segments.Add(segment); first ??= segment;
        }
        return new(baseFixture.Project, baseFixture.Track, first!);
    }

    private static object Memory()
    {
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new { process.PrivateMemorySize64, process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) } };
    }

    private static IoCounters ReadIo()
    {
        using Process process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out IoCounters counters))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return counters;
    }
    private static double CpuMilliseconds()
    {
        using Process process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.TotalMilliseconds;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadCalls, WriteCalls, OtherCalls, ReadBytes, WriteBytes, OtherBytes;
    }
}
