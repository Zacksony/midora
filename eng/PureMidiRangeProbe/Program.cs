using System.Diagnostics;
using System.Text.Json;
using Midora.Application;
using Midora.Compiler;
using Midora.Domain;

if (args.Length is < 2 or > 3) throw new ArgumentException("INPUT.mid OUTPUT-directory [raw|verify] (run under the stage-6 memory guard)");
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using StreamWriter log = new(new FileStream(Path.Combine(output, "phases.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
void Write(object value) { string text = JsonSerializer.Serialize(value); Console.WriteLine(text); log.WriteLine(text); }
Write(new { compiler = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId,
    domain = typeof(MidoraProject).Assembly.ManifestModule.ModuleVersionId, runtime = Environment.Version.ToString() });
T Phase<T>(string phase, Func<T> action)
{
    long before = GC.GetTotalAllocatedBytes(true);
    Stopwatch watch = Stopwatch.StartNew();
    T result = action(); watch.Stop();
    using Process process = Process.GetCurrentProcess(); process.Refresh();
    Write(new { phase, ms = watch.Elapsed.TotalMilliseconds, allocated = GC.GetTotalAllocatedBytes(true) - before,
        process.PrivateMemorySize64, process.WorkingSet64, managed = GC.GetTotalMemory(false) });
    return result;
}
using MidoraProject project = Phase("import", () => MidiProjectImportService.ImportFile(args[0], "range-perf").Project);
if (args.Length == 3 && args[2] == "raw")
{
    foreach (PureMidiTrack rawTrack in project.PureMidiTracks)
    foreach (MidiSegment rawSegment in rawTrack.Segments)
    {
        var raw = rawSegment.ChannelEvents.EnumerateValues().Where(e => e.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff).ToArray();
        if (raw.Length == 0) continue;
        Write(new { rawTrack.Name, root = rawTrack.MidiChannelRootId.Value, count = raw.Length,
            first = raw.Take(20).Select(e => new { e.Tick, e.Kind, e.Data1, e.Data2, e.Order,
                pairedActive = rawSegment.Notes.QueryActiveValues(e.Tick).LongCount(n => n.Key == e.Data1) }).ToArray() });
    }
    return;
}
using MidoraCompiler compiler = new();
CanonicalCompiledResult Full(string phase, CompilationRequest? request = null) => Phase(phase, () =>
{
    CanonicalCompiledResult result = compiler.CompileFull(project, request);
    if (!result.IsConsumable) throw new InvalidDataException(string.Join("\n", result.Diagnostics));
    Write(new { phase = phase + "-result", result.StartTick, result.EndTick, result.TotalEventCount, result.TotalNoteOnEventCount });
    return result;
});
void Query(string phase, CanonicalCompiledResult result, long start, long end) => Phase(phase, () =>
{
    long count = 0, ons = 0, offEnd = 0;
    ulong hash = 14695981039346656037;
    foreach (CanonicalMidiEventPage page in result.QueryEventPages(start, end, true))
        foreach (CanonicalMidiEvent value in page.Items)
        {
            count++;
            if (value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn && value.Message.Byte2 > 0) ons++;
            if (value.Tick == end && value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOff) offEnd++;
            hash = unchecked((hash ^ (ulong)value.Tick ^ value.Message.PackedValue ^ (ulong)value.Source.DirectMidiObjectId.Value) * 1099511628211);
        }
    Write(new { phase = phase + "-result", count, ons, offEnd, hash });
    if (args.Length == 3 && args[2] == "verify" && start == result.StartTick && end == result.EndTick
        && (count != result.TotalEventCount || ons != result.TotalNoteOnEventCount))
        throw new InvalidDataException("Declared range counts differ from actual canonical events.");
    return count;
});
CanonicalCompiledResult full = Full("full-cold");
full = Full("full-warm");
Query("opening-window", full, 0, 96);
Query("dense-window-cold", full, 168960, 169056);
Query("dense-window-warm", full, 168960, 169056);
CompilationRequest seek = new() { Purpose = CompilationPurpose.Playback, StartTick = 168960, EndTick = full.EndTick };
CanonicalCompiledResult fromMiddle = Full("seek-natural-end-cold", seek);
fromMiddle = Full("seek-natural-end-warm", seek);
Query("seek-opening-window", fromMiddle, seek.StartTick, seek.StartTick + 96);
CompilationRequest cut = new() { Purpose = CompilationPurpose.Playback, StartTick = 168960, EndTick = 169056 };
CanonicalCompiledResult clipped = Full("early-end-cold", cut);
Query("early-end-query-cold", clipped, cut.StartTick, cut.EndTick.Value);
clipped = Full("early-end-warm", cut);
Query("early-end-query-warm", clipped, cut.StartTick, cut.EndTick.Value);
CompilationRequest late = new() { Purpose = CompilationPurpose.Playback, StartTick = 181200, EndTick = 181400 };
CanonicalCompiledResult afterRaw = Full("raw-area-end-cold", late);
Query("raw-area-query-cold", afterRaw, late.StartTick, late.EndTick.Value);
afterRaw = Full("raw-area-end-warm", late);
Query("raw-area-query-warm", afterRaw, late.StartTick, late.EndTick.Value);
PureMidiTrack track = project.PureMidiTracks.Single(value => value.Name == "MIDI Out #23");
MidiSegment segment = track.Segments.Single();
segment.Notes.Add(new(project) { StartTick = 168970, LengthTicks = 80, Key = 60, NoteOnVelocity = 99 });
ProjectChangeSet changes = new(); changes.PureMidiTrackIds.Add(track.Id);
CanonicalCompiledResult edited = Phase("edited-incremental", () => compiler.CompileIncremental(project, changes, cut));
CanonicalCompiledResult oracle = Full("edited-full", cut);
// Only the short dense range is enumerated. Never materialize the 18M-note source.
Query("edited-query", edited, cut.StartTick, cut.EndTick.Value);
if (edited.TotalEventCount != oracle.TotalEventCount || edited.Fingerprint != oracle.Fingerprint)
    throw new InvalidDataException("Incremental metadata differs from Full.");
Phase("clear-cache", () => { compiler.ClearCache(); return 0; });
