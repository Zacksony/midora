using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Midora.Audio;
using Midora.Application;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;
using Midora.Persistence;

// Identical harness against frozen old/new assemblies. No UI or audio device.
// Usage: plans <range-count> <note-count> <report.json> [midi-path]
//        packs <segment-count> <report.json> [notes-per-segment=1] [budget-MiB=64]
//        ids <id-count> <dense|sparse> <report.json>
//        opaque <event-count> <payload-MiB> <report.json>
internal static class Program
{
    private static readonly List<object> Measurements = [];
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int Main(string[] args)
    {
        try
        {
            string report;
            object result;
            if (args[0] == "plans")
            {
                report = args[3];
                result = Plans(int.Parse(args[1]), int.Parse(args[2]), args.Length > 4 ? args[4] : null);
            }
            else if (args[0] == "opaque")
            {
                report = args[3];
                result = OpaqueProbe.Run(int.Parse(args[1]), int.Parse(args[2]));
            }
            else if (args[0] == "ids")
            {
                report = args[3];
                result = Ids(int.Parse(args[1]), args[2] == "dense",
                    Path.GetDirectoryName(Path.GetFullPath(report))!);
            }
            else
            {
                report = args[2];
                result = Packs(int.Parse(args[1]), Path.GetDirectoryName(Path.GetFullPath(report))!,
                    args.Length > 3 ? int.Parse(args[3]) : 1,
                    args.Length > 4 ? long.Parse(args[4]) * 1024 * 1024 : 64L * 1024 * 1024);
            }
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Plans(int count, int noteCount, string? midi)
    {
        using MidoraProject project = midi is null ? CreateProject(noteCount)
            : MidiProjectImportService.ImportFile(midi, Path.GetFileNameWithoutExtension(midi)).Project;
        using ProjectCompilationSession session = new(project);
        if (!session.LastAttempt.IsConsumable) throw new InvalidOperationException(string.Join(";", session.LastAttempt.Diagnostics));
        HashSet<MidoraId> audible = project.TracksInArrangementOrder().Select(value => value.TrackId).ToHashSet();
        Collect(); Check("compiled", session);
        List<double> cold = [], hot = [];
        int revisitIndex = count / 4;
        long revisitFingerprint = 0, revisitFrames = 0;
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            long tick = midi is null ? i : i * 96L;
            Stopwatch watch = Stopwatch.StartNew();
            CanonicalCompiledResult result = session.CompileForPlayback(tick, null);
            MidiRenderPlan plan = session.GetOrCreateRealtimeRenderPlan(result, 48000, audible);
            cold.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            CanonicalCompiledResult repeat = session.CompileForPlayback(tick, null);
            MidiRenderPlan repeatPlan = session.GetOrCreateRealtimeRenderPlan(repeat, 48000, audible);
            hot.Add(watch.Elapsed.TotalMilliseconds);
            if (result.Fingerprint != repeat.Fingerprint || plan.TotalFrameCount != repeatPlan.TotalFrameCount)
                throw new InvalidOperationException("Cache changed canonical/sample results.");
            if (i == revisitIndex)
            {
                revisitFingerprint = result.Fingerprint;
                revisitFrames = plan.TotalFrameCount;
            }
            if ((i + 1) % 32 == 0 || i == count - 1)
            {
                Collect(); Check($"ranges-{i + 1}", session);
            }
        }
        // The real-MIDI mode measures paged realtime preparation. Do not silently
        // materialize a giant offline event plan a hundred times in this probe.
        if (midi is null)
        {
            for (int sampleRate = 8000; sampleRate < 9000; sampleRate += 10)
                _ = session.GetOrCreateRenderPlan(sampleRate);
            Collect(); Check("sample-rates-100", session);
        }
        long revisitTick = midi is null ? revisitIndex : revisitIndex * 96L;
        Stopwatch revisitWatch = Stopwatch.StartNew();
        CanonicalCompiledResult revisited = session.CompileForPlayback(revisitTick, null);
        MidiRenderPlan revisitedPlan = session.GetOrCreateRealtimeRenderPlan(revisited, 48000, audible);
        double revisitMs = revisitWatch.Elapsed.TotalMilliseconds;
        if (revisited.Fingerprint != revisitFingerprint || revisitedPlan.TotalFrameCount != revisitFrames)
            throw new InvalidOperationException("Eviction/revisit changed canonical or sample output.");
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        double seconds = elapsed.Elapsed.TotalSeconds;
        session.Dispose(); Collect(); Check("session-disposed", session);
        cold.Sort(); hot.Sort();
        return new { mode = "plans", count, noteCount, midi, assembly = typeof(ProjectCompilationSession).Assembly.Location,
            elapsedSeconds = seconds, allocatedBytes = allocated, revisitTick, revisitMs, revisitFingerprint, revisitFrames,
            coldMedianMs = cold[cold.Count / 2], coldP95Ms = cold[(int)(cold.Count * .95)],
            hotMedianMs = hot[hot.Count / 2], hotP95Ms = hot[(int)(hot.Count * .95)], measurements = Measurements };
    }

    private static object Packs(int count, string directory, int notesPerSegment, long budget)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"probe-{count}-{Guid.NewGuid():N}.mpk");
        try
        {
            Collect(); long before = GC.GetTotalMemory(false);
            long allocations = GC.GetTotalAllocatedBytes(true);
            Stopwatch watch = Stopwatch.StartNew();
            ConstructorInfo constructor = typeof(PureMidiContentPackWriter).GetConstructors(Flags)
                .Single(c => c.GetParameters().Length >= 4);
            object?[] parameters = new object?[constructor.GetParameters().Length];
            parameters[0] = path; parameters[1] = CancellationToken.None;
            parameters[2] = 2; parameters[3] = budget;
            using var writer = (PureMidiContentPackWriter)constructor.Invoke(parameters);
            byte[] payload = Enumerable.Range(0, 500).Select(i => (byte)(i % 128)).ToArray();
            for (int pass = 0; pass < 2; pass++)
            for (int i = count - 1; i >= 0; i--)
            {
                MidoraId segment = new(i + 1);
                for (int note = pass * notesPerSegment / 2; note < (pass + 1) * notesPerSegment / 2; note++)
                    writer.AddNote(segment, new(new(100_000L + (long)i * notesPerSegment + note),
                        1_000_000 - note * 3, 12 + note % 45, note % 128, 100, note % 128,
                        note * 2L, note * 2L + 1));
                if (notesPerSegment == 1) continue;
                writer.AddChannelEvent(segment, new(new(100_000_000L + i * 10 + pass),
                    pass * 200, DirectMidiChannelEventKind.ControlChange, 11, pass * 37, pass));
                writer.AddOpaqueEvent(segment, new(new(200_000_000L + i * 10 + pass),
                    pass * 100, OpaqueMidiEventKind.Meta, 0x7F, payload, pass));
            }
            double addMs = watch.Elapsed.TotalMilliseconds;
            Collect(); long partial = GC.GetTotalMemory(false) - before;
            using Process process = Process.GetCurrentProcess();
            long partialWorkingSetBytes = process.WorkingSet64;
            object? active = writer.GetType().GetProperty("ActiveBuilderCapacityBytes", Flags)?.GetValue(writer);
            object? spoolBytes = writer.GetType().GetProperty("BuilderSpoolLength", Flags)?.GetValue(writer);
            watch.Restart();
            using PureMidiContentPack pack = writer.Complete();
            double completeMs = watch.Elapsed.TotalMilliseconds;
            string hash;
            using (FileStream input = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(input));
            process.Refresh();
            return new { mode = "packs", count, notesPerSegment, budget, hash, fileBytes = new FileInfo(path).Length,
                partialManagedBytes = partial, activeCapacity = active,
                partialWorkingSetBytes, workingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
                peakCapacity = writer.GetType().GetProperty("PeakReservedBytes", Flags)?.GetValue(writer),
                peakActualCapacity = writer.GetType().GetProperty("PeakActualBufferBytes", Flags)?.GetValue(writer),
                activeAfterComplete = writer.GetType().GetProperty("ActiveBuilderCapacityBytes", Flags)?.GetValue(writer),
                spills = writer.GetType().GetProperty("BuilderSpillCount", Flags)?.GetValue(writer), spoolBytes,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocations, addMs, completeMs,
                assembly = typeof(PureMidiContentPackWriter).Assembly.Location };
        }
        finally { File.Delete(path); }
    }

    private static object Ids(int count, bool dense, string directory)
    {
        Directory.CreateDirectory(directory);
        Type package = typeof(MidoraProjectPackageV1);
        Type? current = package.Assembly.GetType("Midora.Persistence.StableIdValidatorV1");
        Type validatorType = current ?? package.GetNestedType("StableIdSetV1", BindingFlags.NonPublic)!;
        List<object> iterations = [];
        List<double> elapsed = [];
        for (int i = 0; i < 3; i++)
        {
            Collect();
            iterations.Add(IdIteration(validatorType, current is not null, count, dense, directory, out double ms));
            elapsed.Add(ms);
        }
        elapsed.Sort();
        Collect();
        return new { mode = "ids", count, dense, implementation = validatorType.FullName,
            medianMs = elapsed[1], assembly = package.Assembly.Location, iterations };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object IdIteration(Type type, bool current, int count, bool dense, string directory, out double ms)
    {
        long managedBefore = GC.GetTotalMemory(false);
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        object validator = current
            ? type.GetConstructors(Flags).Single().Invoke([CancellationToken.None, 8L * 1024 * 1024, directory, 32_768, 65_536])
            : Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            if (current)
            {
                var add = type.GetMethod("Add", Flags)!.CreateDelegate<Action<MidoraId, long, string>>(validator);
                for (int i = 0; i < count; i++)
                    add(new(dense ? i + 1L : i * (1L << 20) + 1), long.MaxValue, "Pure MIDI Track object");
                type.GetMethod("Complete", Flags)!.Invoke(validator, null);
            }
            else
            {
                var add = type.GetMethod("Add", Flags)!.CreateDelegate<Func<MidoraId, bool>>(validator);
                for (int i = 0; i < count; i++)
                {
                    MidoraId id = new(dense ? i + 1L : i * (1L << 20) + 1);
                    if (id.Value <= 0 || id.Value >= long.MaxValue || !add(id))
                        throw new InvalidDataException("Unexpected duplicate or invalid ID in benchmark.");
                }
            }
            ms = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Collect();
            long retained = GC.GetTotalMemory(false) - managedBefore;
            object? Property(string name) => type.GetProperty(name, Flags)?.GetValue(validator);
            GC.KeepAlive(validator);
            return new { elapsedMs = ms, allocatedBytes = allocated, retainedManagedBytes = retained,
                accountedResidentBytes = Property("AccountedResidentBytes"), peakResidentBytes = Property("PeakResidentBytes"),
                liveSpillBytes = Property("LiveSpillBytes"), peakSpillBytes = Property("PeakSpillBytes"),
                totalSpillBytesWritten = Property("TotalSpillBytesWritten"), spilled = Property("HasSpilled") };
        }
        finally { (validator as IDisposable)?.Dispose(); }
    }

    private static void Check(string name, ProjectCompilationSession session)
    {
        using Process process = Process.GetCurrentProcess();
        Measurements.Add(new { name, managedBytes = GC.GetTotalMemory(false), workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            cache = session.GetType().GetProperty("PreparationStorage")?.GetValue(session) });
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static MidoraProject CreateProject(int count)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Piano", TemplateLengthTicks = 120 };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 60, 60, 100));
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = count * 120L };
        for (int i = 0; i < count; i++) segment.Notes.Add(new LogicalNote(project)
            { StartTick = i * 120L, LengthTicks = 60, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        return project;
    }
}
