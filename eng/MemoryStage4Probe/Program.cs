using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Midora.Domain;
using Midora.Persistence;

if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--warm"))
    throw new ArgumentException("kind(logical|subvoice|conductor|mixed) count output-directory [--warm]");
string kind = args[0];
int count = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
if (count < 1 || count > 1_000_000 || kind is not ("logical" or "subvoice" or "conductor" or "mixed"))
    throw new ArgumentException("Invalid bounded probe fixture.");
string output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Use a new empty probe directory.");
List<object> phases = [];
Stopwatch total = Stopwatch.StartNew();
DateTimeOffset epoch = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
async Task WarmUp()
{
    string warmDirectory = Path.Combine(output, "warmup");
    Directory.CreateDirectory(warmDirectory);
    using MidoraProject warm = CreateProject(kind, Math.Min(count, 100_000));
    MidoraProjectPackageV1 warmPackages = new("1.0.0-dev", new FixedClock(epoch));
    string warmPackage = Path.Combine(warmDirectory, "fixture.midora");
    if (kind is "logical" or "mixed")
    {
        string path = Path.Combine(warmDirectory, "logical.pb");
        WriteCodec(typeof(LogicalTrackProtobufCodecV1), warm.Tracks[0], path);
        using MidoraProject restored = new(480, epoch);
        _ = ReadCodec(typeof(LogicalTrackProtobufCodecV1), restored, path);
    }
    if (kind is "subvoice" or "mixed")
    {
        string path = Path.Combine(warmDirectory, "instrument.pb");
        WriteCodec(typeof(EventInstrumentProtobufCodecV2), warm.EventInstruments[0], path);
        using MidoraProject restored = new(480, epoch);
        _ = ReadCodec(typeof(EventInstrumentProtobufCodecV2), restored, path);
    }
    await warmPackages.SaveCopyAsync(warm, warmPackage);
    using MidoraProjectOpenResultV1 opened = await warmPackages.OpenAsync(warmPackage);
    await warmPackages.SaveCopyAsync(opened.Project, Path.Combine(warmDirectory, "resaved.midora"));
}
if (args.Length == 4)
{
    await WarmUp();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
total.Restart();
MidoraProject? source = null;
long baseline = GC.GetTotalAllocatedBytes(precise: true);
Measure("construct", () => source = CreateProject(kind, count));
if (kind is "logical" or "mixed")
    Measure("logical-first-snapshot", () =>
    {
        var snapshot = source!.Tracks[0].Segments[0].Notes.CreateQuerySnapshot();
        if (snapshot.EnumerateAll().Count() != count) throw new InvalidDataException("Snapshot count changed.");
    });
if (kind is "subvoice" or "mixed")
    Measure("subvoice-first-snapshot", () =>
    {
        var snapshot = source!.EventInstruments[0].SubVoices[0].Events.CreateQuerySnapshot();
        if (snapshot.EnumerateAll().Count() != count) throw new InvalidDataException("Snapshot count changed.");
    });
using (source!)
{
    if (kind is "logical" or "mixed")
    {
        LogicalTrack track = source!.Tracks[0];
        string path = Path.Combine(output, "logical.pb");
        Measure("logical-write", () => WriteCodec(typeof(LogicalTrackProtobufCodecV1), track, path));
        using MidoraProject restored = new(480, epoch);
        Measure("logical-read", () =>
        {
            LogicalTrack result = (LogicalTrack)ReadCodec(typeof(LogicalTrackProtobufCodecV1), restored, path);
            if (result.Segments[0].Notes.Count != count) throw new InvalidDataException("Logical count changed.");
            restored.Tracks.Add(result);
        });
    }
    if (kind is "subvoice" or "mixed")
    {
        string path = Path.Combine(output, "instrument.pb");
        Measure("subvoice-write", () => WriteCodec(typeof(EventInstrumentProtobufCodecV2), source!.EventInstruments[0], path));
        using MidoraProject restored = new(480, epoch);
        Measure("subvoice-read", () =>
        {
            EventInstrument result = (EventInstrument)ReadCodec(typeof(EventInstrumentProtobufCodecV2), restored, path);
            if (result.SubVoices[0].Events.Count != count) throw new InvalidDataException("SubVoice count changed.");
            restored.EventInstruments.Add(result);
        });
    }
    MidoraProjectPackageV1 packages = new("1.0.0-dev", new FixedClock(epoch));
    string package = Path.Combine(output, "fixture.midora");
    await MeasureAsync("save-copy", async () => { await packages.SaveCopyAsync(source!, package); });
    MidoraProjectOpenResultV1? reopened = null;
    await MeasureAsync("open", async () => reopened = await packages.OpenAsync(package));
    using (reopened!)
    {
        if (reopened!.Diagnostics.Count != 0 || reopened.IsModified) throw new InvalidDataException("Package recovery occurred.");
        var restored = reopened.Project;
        if (kind is "logical" or "mixed" && restored.Tracks[0].Segments[0].Notes.Count != count)
            throw new InvalidDataException("Package Logical count changed.");
        if (kind is "subvoice" or "mixed" && restored.EventInstruments[0].SubVoices[0].Events.Count != count)
            throw new InvalidDataException("Package SubVoice count changed.");
        if (kind is "conductor" or "mixed" && restored.Conductor.Tempos.Count != count + 1)
            throw new InvalidDataException("Package Conductor count changed.");
        await MeasureAsync("resave", async () => { await packages.SaveCopyAsync(restored, Path.Combine(output, "resaved.midora")); });
    }
}
using (Process current = Process.GetCurrentProcess())
{
    current.Refresh();
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    using ZipArchive archive = ZipFile.OpenRead(Path.Combine(output, "fixture.midora"));
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
        using Stream data = entry.Open();
        hashes.Add(entry.FullName, Convert.ToHexStringLower(SHA256.HashData(data)));
    }
    using ZipArchive resaved = ZipFile.OpenRead(Path.Combine(output, "resaved.midora"));
    if (resaved.Entries.Count != hashes.Count) throw new InvalidDataException("Resave changed entry count.");
    foreach (ZipArchiveEntry entry in resaved.Entries)
    {
        using Stream data = entry.Open();
        if (!hashes.TryGetValue(entry.FullName, out string? expected)
            || Convert.ToHexStringLower(SHA256.HashData(data)) != expected)
            throw new InvalidDataException($"Resave changed '{entry.FullName}'.");
    }
    string result = JsonSerializer.Serialize(new
    {
        kind, count, warmed = args.Length == 4, runtime = Environment.Version.ToString(),
        domain = typeof(MidoraProject).Assembly.ManifestModule.ModuleVersionId,
        persistence = typeof(MidoraProjectPackageV1).Assembly.ManifestModule.ModuleVersionId,
        totalMilliseconds = total.Elapsed.TotalMilliseconds,
        cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - baseline,
        peakWorkingSetBytes = current.PeakWorkingSet64, phases, hashes
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "result.json"), result);
    Console.WriteLine(result);
}

void Measure(string phase, Action action)
{
    long allocated = GC.GetTotalAllocatedBytes(precise: true);
    Stopwatch watch = Stopwatch.StartNew();
    action();
    watch.Stop();
    Record(phase, watch, allocated);
}
async Task MeasureAsync(string phase, Func<Task> action)
{
    long allocated = GC.GetTotalAllocatedBytes(precise: true);
    Stopwatch watch = Stopwatch.StartNew();
    await action();
    watch.Stop();
    Record(phase, watch, allocated);
}
void Record(string phase, Stopwatch watch, long allocated)
{
    long allocatedDelta = GC.GetTotalAllocatedBytes(precise: true) - allocated;
    long managed = GC.GetTotalMemory(forceFullCollection: true);
    using Process current = Process.GetCurrentProcess();
    current.Refresh();
    phases.Add(new { phase, milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = allocatedDelta,
        postGcManagedBytes = managed, workingSetBytes = current.WorkingSet64, privateBytes = current.PrivateMemorySize64 });
    Console.Error.WriteLine($"{phase}: {watch.Elapsed.TotalMilliseconds:F1} ms, managed={managed / 1048576.0:F2} MiB");
}
MidoraProject CreateProject(string fixture, int n)
{
    MidoraProject project = new(480, epoch);
    project.Metadata.ProjectName = "Stage 4 \u97f3\u697d \U0001f3b5";
    if (fixture is "conductor" or "mixed")
        project.Conductor.Tempos.AddRange(Enumerable.Range(1, n).Select(i => new TempoChange(project, i * 4L, 60m + i % 181)));
    if (fixture is not "conductor")
    {
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument \u97f3");
        instrument.TemplateLengthTicks = n * 4L + 960;
        instrument.LoopStartTick = 192;
        instrument.LoopEndTick = 384;
        instrument.PreRollTicks = 96;
        instrument.Description = new string('x', 60_000);
        SubVoice voice = instrument.SubVoices[0];
        voice.InitialState.Controllers[11] = 110;
        if (fixture is "subvoice" or "mixed")
            for (int i = 0; i < n; i++) voice.Events.Add(TemplateEvent.Note(project, i * 4L, 3 + i % 5, i % 128, 1 + i % 127));
        if (fixture is "logical" or "mixed")
        {
            EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
            project.EventInstrumentUsages.Add(usage);
            LogicalTrack track = new(project) { Name = "Logical", EventInstrumentUsageId = usage.Id, ColorOverride = new(80, 90, 100) };
            Segment segment = new(project) { ProjectStartTick = 480, ContentOffsetTick = 32, LengthTicks = n * 4L + 960 };
            for (int i = 0; i < n; i++) segment.Notes.Add(new LogicalNote(project)
                { StartTick = i * 4L, LengthTicks = 3 + i % 5, Note = i % 128, Velocity = 1 + i % 127 });
            track.Segments.Add(segment);
            project.Tracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        }
    }
    return project;
}
void WriteCodec(Type codec, object value, string path)
{
    MethodInfo? streamMethod = codec.GetMethod("Serialize", [value.GetType(), typeof(Stream), typeof(CancellationToken)]);
    using FileStream outputStream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    if (streamMethod is not null) streamMethod.Invoke(null, [value, outputStream, CancellationToken.None]);
    else outputStream.Write((byte[])codec.GetMethod("Serialize", [value.GetType()])!.Invoke(null, [value])!);
}
object ReadCodec(Type codec, MidoraProject project, string path)
{
    MethodInfo? streamMethod = codec.GetMethod("Restore", [typeof(MidoraProject), typeof(Stream), typeof(CancellationToken)]);
    if (streamMethod is not null)
    {
        using FileStream input = File.OpenRead(path);
        return streamMethod.Invoke(null, [project, input, CancellationToken.None])!;
    }
    byte[] bytes = File.ReadAllBytes(path);
    return codec == typeof(LogicalTrackProtobufCodecV1)
        ? LogicalTrackProtobufCodecV1.Restore(project, bytes)
        : EventInstrumentProtobufCodecV2.Restore(project, bytes);
}
sealed class FixedClock(DateTimeOffset time) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => time;
}
