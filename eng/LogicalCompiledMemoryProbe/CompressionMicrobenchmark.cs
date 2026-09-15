using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Midora.Compiler;
using Midora.Compiler.Tests;
using Midora.Domain;

internal static class CompressionMicrobenchmark
{
    public static int Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("compression OUTPUT");
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "compression.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        void Write(object value) { string line = JsonSerializer.Serialize(value); log.WriteLine(line); Console.WriteLine(line); }
        Write(new { kind = "header", runtime = Environment.Version.ToString(),
            compilerMvid = typeof(MidoraCompiler).Assembly.ManifestModule.ModuleVersionId, recordBytes = Marshal.SizeOf<CanonicalMidiEvent>(), iterations = 40 });
        foreach ((string label, byte[] raw) in Samples())
        {
            foreach (int quality in new[] { 0, 1 }) Brotli(label, raw, quality, Write);
            Deflate(label, raw, Write);
        }
        return 0;
    }

    private static IEnumerable<(string Label, byte[] Bytes)> Samples()
    {
        using (var fixture = LogicalCompiledMemoryOracle.Create("expansion", 32, 256, 8, 4))
        using (MidoraCompiler compiler = new())
        {
            CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
            if (!result.IsConsumable) throw new InvalidDataException("Expansion fixture failed.");
            foreach (int start in new[] { 0, result.Events.Length / 2, result.Events.Length - 4096 })
                yield return ($"expansion-{start}", Bytes(result, start));
        }
        using (var fixture = LogicalCompiledMemoryOracle.Create("mixed", 32))
        using (MidoraCompiler compiler = new())
        {
            MidiSegment segment = fixture.Project.PureMidiTracks[0].Segments[0];
            segment.Notes.Clear();
            for (int i = 0; i < 8192; i++) segment.Notes.Add(new(fixture.Project)
            {
                StartTick = 256, LengthTicks = 10, Key = i % 128, NoteOnVelocity = 1 + i % 127,
                NoteOffVelocity = i % 128, NoteOnOrder = i, NoteOffOrder = i + 8192
            });
            CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
            if (!result.IsConsumable) throw new InvalidDataException("Same-tick fixture failed.");
            int start = FirstTick(result, 256);
            yield return ("same-tick-different-source-1", Bytes(result, start));
            yield return ("same-tick-different-source-2", Bytes(result, start + 4096));
        }

        static int FirstTick(CanonicalCompiledResult result, long tick)
        {
            ReadOnlySpan<CanonicalMidiEvent> events = result.Events;
            for (int i = 0; i < events.Length; i++) if (events[i].Tick == tick) return i;
            throw new InvalidDataException("Missing same-tick sample.");
        }
        static byte[] Bytes(CanonicalCompiledResult result, int start) =>
            MemoryMarshal.AsBytes(result.Events.Slice(start, 4096)).ToArray();
    }

    private static void Brotli(string label, byte[] raw, int quality, Action<object> write)
    {
        byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        byte[] restored = new byte[raw.Length];
        int length = 0;
        void Encode()
        {
            if (!BrotliEncoder.TryCompress(raw, compressed, out length, quality, window: 22)) throw new InvalidDataException("Brotli encode failed.");
        }
        void Decode()
        {
            if (!BrotliDecoder.TryDecompress(compressed.AsSpan(0, length), restored, out int written) || written != raw.Length)
                throw new InvalidDataException("Brotli decode failed.");
        }
        Encode(); Decode();
        if (!raw.AsSpan().SequenceEqual(restored)) throw new InvalidDataException("Brotli differs.");
        double encodeMs = Time(Encode), decodeMs = Time(Decode);
        if (!raw.AsSpan().SequenceEqual(restored)) throw new InvalidDataException("Brotli differs after iterations.");
        write(new { label, method = "brotli-q" + quality, rawBytes = raw.Length, compressedBytes = length,
            ratio = (double)length / raw.Length, encodeMs, decodeMs, sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)) });
    }

    private static void Deflate(string label, byte[] raw, Action<object> write)
    {
        byte[] restored = new byte[raw.Length];
        byte[] compressed = [];
        void Encode()
        {
            using MemoryStream memory = new(raw.Length);
            using (DeflateStream deflate = new(memory, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(raw);
            compressed = memory.ToArray();
        }
        void Decode()
        {
            using MemoryStream memory = new(compressed);
            using DeflateStream deflate = new(memory, CompressionMode.Decompress);
            deflate.ReadExactly(restored);
            if (deflate.ReadByte() != -1) throw new InvalidDataException("Deflate has unexpected tail.");
        }
        Encode(); Decode();
        if (!raw.AsSpan().SequenceEqual(restored)) throw new InvalidDataException("Deflate differs.");
        double encodeMs = Time(Encode), decodeMs = Time(Decode);
        if (!raw.AsSpan().SequenceEqual(restored)) throw new InvalidDataException("Deflate differs after iterations.");
        write(new { label, method = "deflate-fastest", rawBytes = raw.Length, compressedBytes = compressed.Length,
            ratio = (double)compressed.Length / raw.Length, encodeMs, decodeMs, sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)) });
    }

    private static double Time(Action action)
    {
        for (int i = 0; i < 3; i++) action();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 40; i++) action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds / 40;
    }
}
