using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Midora.Compiler;
using Midora.Domain;

try
{
    if (args.Length > 0 && args[0] == "guard") return MemoryGuard.Run(args[1..]);
    if (args.Length > 0 && args[0] == "guard-fixture") return GuardFixture.Run(args[1..]);
    if (args.Length > 0 && args[0] == "history") return HistoryProbe.Run(args[1..]);
    if (args.Length > 0 && args[0] == "midi-logical") return MidiLogicalProbe.Run(args[1..]);
    if (args.Length > 0 && args[0] == "midi-fixture") return MidiLogicalProbe.WriteFixture(args[1..]);
    return Probe.Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    if (args.Length > 1 && args[0] == "compile")
        File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "failure.txt"), error.ToString());
    return 1;
}

internal static class GuardFixture
{
    public static int Run(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int mebibytes) || mebibytes < 1 || mebibytes > 256)
            throw new ArgumentException("guard-fixture <1..256 MiB>, guarded safety test only");
        List<byte[]> retained = [];
        for (int i = 0; i < mebibytes; i++)
        {
            byte[] block = new byte[1048576];
            for (int offset = 0; offset < block.Length; offset += 4096) block[offset] = 1;
            retained.Add(block); Thread.Sleep(25);
        }
        GC.KeepAlive(retained); return 0;
    }
}

internal static class Probe
{
    public static int Run(string[] args)
    {
        if (args.Length < 8)
            throw new ArgumentException("compile output count voices templateNotes loops revisions Reject|LetOverlap [isolation=shared|isolated] [spacing=ticks] [varied=true|false]");
        if (args[0] != "compile") throw new ArgumentException("Unknown workload.");
        string output = Path.GetFullPath(args[1]);
        int count = int.Parse(args[2]), voices = int.Parse(args[3]), templateNotes = int.Parse(args[4]);
        int loops = int.Parse(args[5]), revisions = int.Parse(args[6]);
        OverlapPolicy overlap = Enum.Parse<OverlapPolicy>(args[7]);
        Dictionary<string, string> options = args[8..].Select(a => a.Split('=', 2)).ToDictionary(a => a[0], a => a[1], StringComparer.Ordinal);
        foreach (string key in options.Keys)
            if (key is not ("isolation" or "spacing" or "varied")) throw new ArgumentException($"Unknown option {key}.");
        bool isolation = options.TryGetValue("isolation", out string? isolationValue)
            ? isolationValue switch { "shared" => false, "isolated" => true, _ => throw new ArgumentException("Bad isolation option.") }
            : overlap == OverlapPolicy.LetOverlap;
        long spacing = options.TryGetValue("spacing", out string? spacingValue) ? long.Parse(spacingValue) : 4;
        bool varied = options.TryGetValue("varied", out string? variedValue) && bool.Parse(variedValue);
        if (count < 1 || count > 2_000_000 || voices < 1 || voices > 32 || templateNotes < 0 || templateNotes > 512
            || loops < 1 || loops > 64 || revisions < 0 || revisions > 100 || spacing < 1 || spacing > 1_000_000)
            throw new ArgumentException("Probe fixture safety bounds exceeded; these are not product limits.");
        Directory.CreateDirectory(output);
        using StreamWriter phases = new(new FileStream(Path.Combine(output, "phases.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        List<object> records = [];
        Stopwatch total = Stopwatch.StartNew();
        long initialAllocation = GC.GetTotalAllocatedBytes(true);
        MidoraProject? project = null;
        LogicalTrack? track = null;
        Segment? segment = null;
        MidoraCompiler? compiler = null;
        CanonicalCompiledResult? current = null;
        try
        {
            Phase("construct", () =>
            {
                project = new MidoraProject(480, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
                EventInstrument instrument = EventInstrumentLibrary.Create(project);
                instrument.OverlapPolicy = overlap;
                instrument.RequiresChannelIsolation = isolation;
                for (int v = 1; v < voices; v++) instrument.SubVoices.Add(new SubVoice(project));
                foreach (SubVoice voice in instrument.SubVoices)
                    for (int i = 0; i < templateNotes; i++)
                        voice.Events.Add(TemplateEvent.Note(project, i * 4L, 2, 60, 100));
                instrument.TemplateLengthTicks = Math.Max(480, templateNotes * 4L);
                if (loops > 1) { instrument.LoopStartTick = 0; instrument.LoopEndTick = instrument.TemplateLengthTicks; }
                EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
                project.EventInstrumentUsages.Add(usage);
                track = new LogicalTrack(project) { Name = "Stage 5 Logical", EventInstrumentUsageId = usage.Id };
                long gate = instrument.TemplateLengthTicks * loops;
                segment = new Segment(project) { LengthTicks = checked(count * spacing + gate + 1) };
                for (int i = 0; i < count; i++)
                    segment.Notes.Add(new LogicalNote(project) { StartTick = i * spacing, LengthTicks = gate,
                        Note = varied ? 48 + i % 24 : 60, Velocity = varied ? 64 + i % 63 : 100 });
                track.Segments.Add(segment);
                project.Tracks.Add(track);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
                compiler = new MidoraCompiler();
            });
            Phase("full", () => current = compiler!.CompileFull(project!));
            RecordCanonical("full", current!);
            for (int revision = 1; revision <= revisions; revision++)
            {
                int captured = revision;
                Phase($"incremental-{revision}", () =>
                {
                    segment!.Notes[0].Velocity = 100 + captured % 20;
                    ProjectChangeSet changes = new(); changes.TrackIds.Add(track!.Id);
                    current = compiler!.CompileIncremental(project!, changes);
                });
                RecordCanonical($"incremental-{revision}", current!);
            }
            // This oracle is intentionally a separate measured phase. No event array copy is required.
            Phase("full-oracle", () =>
            {
                using MidoraCompiler oracle = new();
                CanonicalCompiledResult reference = oracle.CompileFull(project!);
                if (Digest(reference) != Digest(current!)) throw new InvalidDataException("Full/Incremental formal digest mismatch.");
            });
            current = null;
            Phase("clear-cache", () => compiler!.ClearCache());
            compiler!.Dispose(); compiler = null;
            project!.Dispose(); project = null; track = null; segment = null;
            Phase("release-controlled-gc", () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            var result = new
            {
                schema = 2, fixture = new { count, voices, templateNotes, loops, revisions, overlap = overlap.ToString(),
                    requiresChannelIsolation = isolation, triggerSpacingTicks = spacing, varied },
                runtime = Environment.Version.ToString(), domainMvid = typeof(MidoraProject).Assembly.ManifestModule.ModuleVersionId,
                compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId,
                totalMilliseconds = total.Elapsed.TotalMilliseconds,
                cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(true) - initialAllocation, records
            };
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { compiler?.Dispose(); project?.Dispose(); }

        void Phase(string name, Action action)
        {
            using Process p = Process.GetCurrentProcess(); p.Refresh();
            if (p.PrivateMemorySize64 >= 8L * 1024 * 1024 * 1024) throw new InvalidOperationException("Probe self-guard reached 8 GiB.");
            phases.WriteLine(JsonSerializer.Serialize(new { phase = name, state = "begin", utc = DateTimeOffset.UtcNow }));
            long allocated = GC.GetTotalAllocatedBytes(true); Stopwatch watch = Stopwatch.StartNew();
            action(); watch.Stop(); p.Refresh(); GCMemoryInfo gc = GC.GetGCMemoryInfo();
            var record = new { phase = name, state = "complete", elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, naturalManagedBytes = GC.GetTotalMemory(false),
                gcHeapBytes = gc.HeapSizeBytes, gcCommittedBytes = gc.TotalCommittedBytes, gcFragmentedBytes = gc.FragmentedBytes,
                privateBytes = p.PrivateMemorySize64, workingSetBytes = p.WorkingSet64, peakWorkingSetBytes = p.PeakWorkingSet64 };
            records.Add(record); phases.WriteLine(JsonSerializer.Serialize(record));
            Console.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds:F1} ms; Private={p.PrivateMemorySize64 / 1048576.0:F1} MiB; WS={p.WorkingSet64 / 1048576.0:F1} MiB");
        }
        void RecordCanonical(string name, CanonicalCompiledResult value)
        {
            Dictionary<string, int> diagnostics = new(StringComparer.Ordinal);
            foreach (CompilerDiagnostic diagnostic in value.Diagnostics)
                diagnostics[diagnostic.Code] = checked(diagnostics.GetValueOrDefault(diagnostic.Code) + 1);
            var record = new { phase = name + "-canonical", value.IsConsumable, value.FailureStage, value.Fingerprint,
                value.TotalEventCount, value.TotalNoteOnEventCount, statistics = Statistics(value.Statistics), digest = Digest(value),
                diagnostics,
                inMemoryCanonicalEventValueBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<CanonicalMidiEvent>(),
                inMemoryCanonicalEventPayloadBytes = (long)value.Events.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<CanonicalMidiEvent>(),
                telemetry = compiler!.LastTelemetry, raw = RawCensus(compiler) };
            records.Add(record); phases.WriteLine(JsonSerializer.Serialize(record));
        }
    }

    private static string Digest(CanonicalCompiledResult value)
    {
        using HashStream sink = new(); using Utf8JsonWriter writer = new(sink);
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer, new { value.IsConsumable, value.IsPartial, value.FailureStage, value.Fingerprint,
            value.TicksPerQuarterNote, statistics = Statistics(value.Statistics), context = new { value.Context.Purpose, value.Context.StartTick,
                value.Context.RequestedEndTick, value.Context.EndTick, value.Context.EndTickSource,
                value.Context.IncludesAllTracks, value.Context.IncludesAllSubVoices, value.Context.TreatWarningsAsErrors,
                value.Context.CollectDebugDiagnostics, includedTrackIds = value.Context.IncludedTrackIds.ToArray(),
                includedSubVoiceIds = value.Context.IncludedSubVoiceIds.ToArray() } });
        foreach (var item in value.Events) { JsonSerializer.Serialize(writer, item); writer.Flush(); }
        foreach (var item in value.Allocations) JsonSerializer.Serialize(writer, item);
        // Diagnostics can outnumber notes by two orders of magnitude. Hash their complete formal
        // sequence through one bounded binary buffer instead of transient JSON strings per source.
        writer.Flush();
        {
            // The buffer is fully flushed here; HashStream remains owned by this Digest call.
            BufferedStream buffered = new(sink, 65_536);
            using BinaryWriter binary = new(buffered, System.Text.Encoding.UTF8, leaveOpen: true);
            binary.Write(value.Diagnostics.Count);
            foreach (var item in value.Diagnostics)
            {
                binary.Write(item.Code); binary.Write((int)item.Severity); binary.Write(item.Message);
                SourceReference s = item.Source;
                binary.Write(s.TrackId.Value); binary.Write(s.SegmentId.Value); binary.Write(s.LogicalNoteId.Value);
                binary.Write(s.EventInstrumentId.Value); binary.Write(s.SubVoiceId.Value); binary.Write(s.SourceEventId.Value);
                binary.Write(s.Tick); binary.Write(s.LogicalParameterId.Value); binary.Write(s.LogicalParameterMappingId.Value);
                binary.Write(s.MappingStepId.Value); binary.Write(s.MappingFunctionId.Value); binary.Write(s.ValueCurveId.Value);
                binary.Write(s.EnvelopeId.Value); binary.Write((int)s.Origin); binary.Write(s.MidiChannelRootId.Value);
                binary.Write(s.PureMidiTrackId.Value); binary.Write(s.MidiSegmentId.Value); binary.Write(s.DirectMidiObjectId.Value);
                binary.Write(s.ExportTrackId.Value); binary.Write(s.EventInstrumentUsageId.Value);
            }
            binary.Flush(); buffered.Flush();
        }
        foreach (var item in value.SmfTracks) JsonSerializer.Serialize(writer, item);
        foreach (var item in value.Conductor.Tempos) JsonSerializer.Serialize(writer, item);
        foreach (var item in value.Conductor.TimeSignatures) JsonSerializer.Serialize(writer, item);
        foreach (var item in value.Conductor.KeySignatures) JsonSerializer.Serialize(writer, item);
        foreach (var item in value.Conductor.Markers) JsonSerializer.Serialize(writer, item);
        JsonSerializer.Serialize(writer, value.Conductor.EndMarker);
        writer.WriteEndArray(); writer.Flush(); return sink.Finish();
    }
    private static object Statistics(CompilationStatistics value) => new
    {
        value.SourceTrackCount, value.ExpandedInstanceCount, value.EventCount, value.PeakChannelUnitCount,
        value.ExpandedSegmentCount, value.ParticipatingEventInstrumentCount, value.ParticipatingSubVoiceCount,
        value.UsedPortCount, value.NoteOnEventCount, value.ReservedMidiRootUnitCount, value.AllocatedMidiRootUnitCount,
        value.LogicalPeakChannelUnitCount,
        shortage = value.ResourceShortage is not { } shortage ? null : new
        {
            shortage.Range, shortage.RequestedChannelUnitCount, shortage.AvailableChannelUnitCount,
            trackIds = shortage.TrackIds.ToArray(), segmentIds = shortage.SegmentIds.ToArray(),
            logicalNoteIds = shortage.LogicalNoteIds.ToArray(), eventInstrumentIds = shortage.EventInstrumentIds.ToArray(),
            subVoiceIds = shortage.SubVoiceIds.ToArray()
        }
    };
    private static object RawCensus(MidoraCompiler compiler)
    {
        System.Collections.IDictionary cache = (System.Collections.IDictionary)typeof(MidoraCompiler)
            .GetField("_trackCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(compiler)!;
        object? Read(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
        long segments = 0, instances = 0, voices = 0, events = 0;
        HashSet<object> eventStores = new(ReferenceEqualityComparer.Instance);
        HashSet<Array> eventArrays = new(ReferenceEqualityComparer.Instance);
        HashSet<object> patternPools = new(ReferenceEqualityComparer.Instance);
        foreach (object entry in cache.Values)
            foreach (object segment in (System.Collections.IEnumerable)Read(entry, "Segments")!)
            {
                segments++;
                foreach (object instance in (System.Collections.IEnumerable)Read(segment, "Instances")!)
                {
                    instances++;
                    foreach (object voice in (System.Collections.IEnumerable)Read(instance, "Voices")!)
                    {
                        voices++; object store = Read(voice, "Events")!; eventStores.Add(store);
                        events += store is Array array ? array.Length : Convert.ToInt64(Read(store, "Count"));
                        if (store is Array oldArray) eventArrays.Add(oldArray);
                        else
                        {
                            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            if (store.GetType().GetField("_events", fields)?.GetValue(store) is Array compactArray) eventArrays.Add(compactArray);
                            if (store.GetType().GetField("_pool", fields)?.GetValue(store) is object pool) patternPools.Add(pool);
                        }
                    }
                }
            }
        Dictionary<Type, int> sizes = [];
        int Size(Type type)
        {
            if (!sizes.TryGetValue(type, out int size)) sizes[type] = size = (int)typeof(System.Runtime.CompilerServices.Unsafe)
                .GetMethod(nameof(System.Runtime.CompilerServices.Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
            return size;
        }
        long ArrayPayload(Array array) => checked(array.LongLength * Size(array.GetType().GetElementType()!));
        long sourceTableCapacityPayloadBytes = 0;
        foreach (object pool in patternPools)
        {
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object? sourceTable = pool.GetType().GetField("_sources", fields)?.GetValue(pool);
            if (sourceTable?.GetType().GetField("_items", fields)?.GetValue(sourceTable) is Array sourceArray)
                sourceTableCapacityPayloadBytes += ArrayPayload(sourceArray);
        }
        Type? rawType = typeof(MidoraCompiler).GetNestedType("RawMidiEvent", System.Reflection.BindingFlags.NonPublic);
        return new { cacheTracks = cache.Count, segments, instances, voices, events, uniqueEventStores = eventStores.Count,
            uniqueEventArrays = eventArrays.Count, eventArrayPayloadBytes = eventArrays.Sum(ArrayPayload), patternPools = patternPools.Count,
            sourceTableCapacityPayloadBytes, sourceReferenceValueBytes = Size(typeof(SourceReference)),
            rawMidiEventValueBytes = rawType is null ? 0 : Size(rawType),
            note = "Reference-deduplicated array element capacity bytes only; excludes array/object headers, sequence/instance objects and collection directories. Reflection only after timing." };
    }
    private sealed class HashStream : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string Finish() => Convert.ToHexStringLower(hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}
