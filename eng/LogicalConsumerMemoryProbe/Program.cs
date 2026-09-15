using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Midora.Audio;
using Midora.Compiler;
using Midora.Compiler.Tests;
using Midora.Playback;

try
{
    if (args.Length > 0 && args[0] == "guard") return MemoryGuard.Run(args[1..]);
    if (args.Length < 2 || args[0] != "run") throw new ArgumentException("run OUTPUT [COUNT TEMPLATE_NOTES LOOPS VOICES]");
    string output = Path.GetFullPath(args[1]);
    int count = args.Length > 2 ? int.Parse(args[2]) : 8;
    int notes = args.Length > 3 ? int.Parse(args[3]) : 256;
    int loops = args.Length > 4 ? int.Parse(args[4]) : 16;
    int voices = args.Length > 5 ? int.Parse(args[5]) : 4;
    Directory.CreateDirectory(output);
    using StreamWriter log = new(new FileStream(Path.Combine(output, "consumer.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
    void Write(object value) { string json = JsonSerializer.Serialize(value); log.WriteLine(json); Console.WriteLine(json); }
    Write(new { phase = "configuration", count, notes, loops, voices,
        compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId,
        playbackMvid = typeof(MidiRenderPlanAdapter).Assembly.ManifestModule.ModuleVersionId,
        budgetDefaults = typeof(MidoraCompiler).Assembly.GetType("Midora.Compiler.CompilerStorageBudget")!
            .GetConstructors().Single().GetParameters().Select(p => new { p.Name, p.DefaultValue }).ToArray() });
    ConsumerProbe.Measure(count, notes, loops, voices, Write);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Write(new { phase = "closed-controlled-gc", memory = ConsumerProbe.Memory() });
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

internal static class ConsumerProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Measure(int count, int notes, int loops, int voices, Action<object> write)
    {
        using var fixture = LogicalCompiledMemoryOracle.Create("expansion", count, notes, loops, voices);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult canonical = Timed("compile", () => compiler.CompileFull(fixture.Project));
        if (!canonical.IsConsumable || !canonical.HasPagedLogicalEvents) throw new InvalidDataException("Expected successful paged Logical result.");
        write(new { phase = "canonical", canonical.TotalEventCount, canonical.TotalNoteOnEventCount, allocations = canonical.Allocations.Length });
        write(new { phase = "compiler-storage", compiler.LastLogicalStorageTelemetry });
        CanonicalAudioUnitProjection cold = Timed("metadata-cold", () => CanonicalAudioUnitProjection.Create(canonical));
        CanonicalAudioUnitProjection hot = Timed("metadata-hot", () => CanonicalAudioUnitProjection.Create(canonical));
        if (!ReferenceEquals(cold, hot)) throw new InvalidDataException("Metadata was not reused.");
        MidiRenderPlan plan = Timed("adapter-metadata-hot", () => MidiRenderPlanAdapter.CreateRealtime(canonical, 48_000));
        MidiRenderPlan repeat = Timed("adapter-repeat", () => MidiRenderPlanAdapter.CreateRealtime(canonical, 48_000));
        long window = Math.Min(4800, plan.TotalFrameCount);
        long firstCount = Timed("first-window", () => plan.EventPageProvider!.Query(0, window).LongCount());
        long lastCount = Timed("last-window", () => plan.EventPageProvider!.Query(plan.TotalFrameCount - window, plan.TotalFrameCount).LongCount());
        write(new { phase = "consumer-shape", fragments = plan.UnitFragments.Length,
            residentScheduled = plan.Ports.ToArray().Sum(p => (long)p.Events.Length),
            fragmentScheduled = plan.UnitFragments.ToArray().Sum(f => (long)f.Events.Length), firstCount, lastCount });
        GC.KeepAlive(repeat);

        T Timed<T>(string phase, Func<T> action)
        {
            long allocated = GC.GetTotalAllocatedBytes(true);
            Stopwatch watch = Stopwatch.StartNew(); T value = action(); watch.Stop();
            write(new { phase, elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, memory = Memory() });
            return value;
        }
    }

    public static object Memory()
    {
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        return new { managed = GC.GetTotalMemory(false), process.WorkingSet64, process.PrivateMemorySize64 };
    }
}
