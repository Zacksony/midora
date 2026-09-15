using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Persistence.Tests;

public sealed class ConductorStreamingCodecTests(ITestOutputHelper output)
{
    [Fact]
    public void ExplicitRootLayoutMatchesFrozenSourceGeneratedSchema()
    {
        var properties = MidoraJsonSerializerContextV1.Default.ConductorTrackJsonV1.Properties;
        Assert.Equal(["schemaVersion", "tempos", "timeSignatures", "keySignatures", "markers", "endMarker"], properties.Select(property => property.Name));
        Assert.Equal([true, true, true, true, true, false], properties.Select(property => property.IsRequired));
    }

    [Fact]
    public void StreamWriterMatchesFrozenDtoWriterBytesForEveryRecordAndUnicode()
    {
        using var project = CreateMixedProject();
        byte[] oracle = OriginalDtoBytes(project);
        using var destination = new MemoryStream();
        ConductorTrackCodecV1.Serialize(project, destination);
        Assert.Equal(oracle, destination.ToArray());
        Assert.Equal(oracle, ConductorTrackCodecV1.Serialize(project));
        Assert.True(destination.CanWrite);
        Assert.Equal((byte)'\n', oracle[^1]);
        Assert.DoesNotContain((byte)'\r', oracle);
    }

    [Fact]
    public void FrozenWriterKeepsDecimalBytesAndFlushesInChunksInsteadOfPerRecord()
    {
        const int count = 20_000;
        using var project = new MidoraProject(480);
        decimal[] tempos = [0.0000000000000000000000000001m, 1.0000m, 123.4500m, decimal.MaxValue];
        using (project.Conductor.Tempos.BeginBatchChange())
            for (int i = 1; i < count; i++) project.Conductor.Tempos.Add(new TempoChange(project, i, tempos[i % tempos.Length]));
        using var oldOutput = new CountingWriteStream();
        using (var writer = new Utf8JsonWriter(oldOutput, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            var dto = CreateDto(project);
            var context = MidoraJsonSerializerContextV1.Default;
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", dto.SchemaVersion);
            writer.WriteStartArray("tempos");
            foreach (var item in dto.Tempos) JsonSerializer.Serialize(writer, item, context.TempoChangeJsonV1);
            writer.WriteEndArray();
            writer.WriteStartArray("timeSignatures");
            foreach (var item in dto.TimeSignatures) JsonSerializer.Serialize(writer, item, context.TimeSignatureChangeJsonV1);
            writer.WriteEndArray();
            writer.WriteStartArray("keySignatures");
            writer.WriteEndArray();
            writer.WriteStartArray("markers");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            oldOutput.WriteByte((byte)'\n');
        }
        using var newOutput = new CountingWriteStream();
        ConductorTrackCodecV1.Serialize(project, newOutput);
        Assert.Equal(OriginalDtoBytes(project), newOutput.ToArray());
        Assert.Equal(oldOutput.ToArray(), newOutput.ToArray());
        Assert.True(oldOutput.WriteCount >= count);
        Assert.InRange(newOutput.WriteCount, 1, count / 100);
        output.WriteLine($"records={count + 1}, bytes={newOutput.Length}, perRecordWrites={oldOutput.WriteCount}, chunkedWrites={newOutput.WriteCount}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(65536)]
    public void NonSeekableShortReadsRoundTripAllRecordsAndLeaveSourceOpen(int shortRead)
    {
        using var original = CreateMixedProject();
        byte[] oracle = OriginalDtoBytes(original);
        using var source = new ShortReadStream(oracle, shortRead);
        using var restored = new MidoraProject(480);
        ConductorTrackCodecV1.Restore(restored, source);
        Assert.Equal(oracle, ConductorTrackCodecV1.Serialize(restored));
        Assert.True(source.CanRead);
        Assert.True(restored.Conductor.Tempos.CaptureQuerySnapshot().UsesExternalStorage);
        Assert.True(restored.Conductor.Markers.CaptureQuerySnapshot().UsesExternalStorage);
    }

    [Fact]
    public void EmptyOptionalArraysAndAbsentOrNullEndMarkerKeepLegacyMeaning()
    {
        using var project = new MidoraProject(480);
        string json = Encoding.UTF8.GetString(OriginalDtoBytes(project));
        using var destination = new MidoraProject(480);
        Restore(destination, json);
        Assert.Null(destination.Conductor.EndMarker);
        Restore(destination, json[..json.LastIndexOf('}')] + ",\"endMarker\":null}");
        Assert.Null(destination.Conductor.EndMarker);
        Assert.Equal(OriginalDtoBytes(project), ConductorTrackCodecV1.Serialize(destination));
    }

    [Fact]
    public void ReorderedAndEscapedPropertiesStillUseExactVersionedDtoRules()
    {
        using var project = new MidoraProject(480);
        const string json = "{\"markers\":[],\"keySignatures\":[],\"timeSignatures\":[{\"denominator\":4,\"numerator\":4,\"tick\":0,\"id\":2}],\"tempos\":[{\"beatsPerMinute\":1.20e2,\"tick\":0,\"i\\u0064\":1}],\"schemaVersion\":1}";
        var legacy = ConductorTrackCodecV1.Parse(Encoding.UTF8.GetBytes(json));
        ConductorTrackCodecV1.Restore(project, legacy);
        byte[] oracle = OriginalDtoBytes(project);
        Restore(project, json);
        Assert.Equal(oracle, ConductorTrackCodecV1.Serialize(project));
    }

    [Fact]
    public void UnsortedMultiPageInputKeepsLegacyTickAndIdOrderAndAllMarkers()
    {
        using var project = new MidoraProject(480);
        ConductorTrackJsonV1 dto = CreateDto(project);
        dto = new()
        {
            SchemaVersion = 1, TimeSignatures = dto.TimeSignatures, KeySignatures = [],
            Tempos = Enumerable.Range(0, 10000).Reverse().Select(i => new TempoChangeJsonV1
            { Id = new(i + 3), Tick = i, BeatsPerMinute = 120m + i % 70 }).ToArray(),
            Markers = Enumerable.Range(0, 9000).Reverse().Select(i => new ProjectMarkerJsonV1
            { Id = new(i + 20000), Tick = i / 4, Name = "marker" }).ToArray()
        };
        byte[] bytes = StrictJsonV1.SerializeWithFinalLf(dto, MidoraJsonSerializerContextV1.Default.ConductorTrackJsonV1);
        ConductorTrackCodecV1.Restore(project, ConductorTrackCodecV1.Parse(bytes));
        byte[] expected = OriginalDtoBytes(project);
        using var source = new ShortReadStream(bytes, 109);
        ConductorTrackCodecV1.Restore(project, source);
        Assert.Equal(expected, ConductorTrackCodecV1.Serialize(project));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void MalformedInputDoesNotPartiallyReplaceConductor(string json)
    {
        using var project = CreateMixedProject();
        ConductorTrack before = project.Conductor;
        byte[] expected = OriginalDtoBytes(project);
        using var source = new ShortReadStream(Encoding.UTF8.GetBytes(json), 3);
        AssertDataFailure(() => ConductorTrackCodecV1.Restore(project, source));
        Assert.Same(before, project.Conductor);
        Assert.Equal(expected, OriginalDtoBytes(project));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        const string valid = "{\"schemaVersion\":1,\"tempos\":[{\"id\":1,\"tick\":0,\"beatsPerMinute\":120}],\"timeSignatures\":[{\"id\":2,\"tick\":0,\"numerator\":4,\"denominator\":4}],\"keySignatures\":[],\"markers\":[]}";
        foreach (string value in new[]
        {
            "null", "[]", "{}", valid[..^1], valid + "{}", "\uFEFF" + valid,
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":999999999999"),
            valid.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1"),
            valid.Replace("\"markers\":[]", "\"markers\":[],\"unknown\":true"),
            valid.Replace("\"markers\":[]", "\"markers\":null"),
            valid.Replace(",\"markers\":[]", ""),
            valid.Replace("\"id\":1", "\"id\":1,\"i\\u0064\":1"),
            valid.Replace("\"id\":1", "\"id\":1,\"unknown\":0"),
            valid.Replace("\"id\":1", "\"id\":1,\"\\uD800\":0"),
            valid.Replace("\"id\":1,", ""),
            valid.Replace("\"id\":1", "\"id\":null"),
            valid.Replace("\"id\":1", "\"id\":1e0"),
            valid.Replace("\"id\":1", "\"id\":\"1\""),
            valid.Replace("\"id\":1", "\"id\":9223372036854775808"),
            valid.Replace("\"id\":1", "\"id\":0"),
            valid.Replace("\"id\":1", "\"id\":-1"),
            valid.Replace("\"id\":1", "\"id\":2"),
            valid.Replace("\"tick\":0", "\"tick\":-1"),
            valid.Replace("\"tick\":0", "\"tick\":1"),
            valid.Replace("\"beatsPerMinute\":120", "\"beatsPerMinute\":0"),
            valid.Replace("\"beatsPerMinute\":120", "\"beatsPerMinute\":1e99"),
            valid.Replace("\"beatsPerMinute\":120", "\"beatsPerMinute\":NaN"),
            valid.Replace("\"beatsPerMinute\":120", "\"beatsPerMinute\":{}"),
            valid.Replace("\"beatsPerMinute\":120", "\"beatsPerMinute\":[]"),
            valid.Replace("\"numerator\":4", "\"numerator\":100"),
            valid.Replace("\"denominator\":4", "\"denominator\":3"),
            valid.Replace("\"keySignatures\":[]", "\"keySignatures\":[{\"id\":3,\"tick\":1,\"sharpsFlats\":8,\"isMinor\":false}]"),
            valid.Replace("\"keySignatures\":[]", "\"keySignatures\":[{\"id\":3,\"tick\":1,\"sharpsFlats\":0,\"isMinor\":1}]"),
            valid.Replace("\"markers\":[]", "\"markers\":[null]"),
            valid.Replace("\"markers\":[]", "\"markers\":[{\"id\":3,\"tick\":1,\"name\":null}]"),
            valid.Replace("\"markers\":[]", "\"markers\":[{\"id\":3,\"tick\":1,\"name\":\"\\u0000\"}]"),
            valid.Replace("\"markers\":[]", "\"markers\":[{\"id\":3,\"tick\":1,\"name\":\"\\uD800\"}]"),
            valid.Replace("\"markers\":[]", "\"markers\":[{\"id\":3,\"tick\":1,\"name\":\"" + new string('x', 257) + "\"}]"),
            valid.Replace("\"markers\":[]", "\"markers\":[],\"endMarker\":{\"id\":2,\"tick\":99}"),
            valid.Replace("\"markers\":[]", "\"markers\":[],\"endMarker\":{\"id\":3,\"tick\":-1}"),
            valid.Replace("\"markers\":[]", "\"markers\":[],\"endMarker\":{\"id\":3}"),
            valid.Replace("\"markers\":[]", "\"markers\":[],"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1/*comment*/"),
        }) yield return [value];
    }

    [Fact]
    public void RestoredSourceDoesNotRetainInputStreamOrCompleteJsonBytes()
    {
        using var project = new MidoraProject(480);
        WeakReference[] references = RestoreAndReleaseInput(project);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.Equal(2, project.Conductor.Tempos.Count);
        Assert.Equal(3, project.Conductor.Markers.Count);
        GC.KeepAlive(project);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(16_777_216)]
    public void LongRecordWhitespaceDoesNotIntroduceAnInputLengthRestriction(int whitespaceBytes)
    {
        using var project = new MidoraProject(480);
        string json = Encoding.UTF8.GetString(OriginalDtoBytes(project))
            .Replace("\"beatsPerMinute\": 120", "\"beatsPerMinute\":" + new string(' ', whitespaceBytes) + "120");
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var legacy = ConductorTrackCodecV1.Parse(bytes);
        ConductorTrackCodecV1.Restore(project, legacy);
        byte[] expected = OriginalDtoBytes(project);
        var metrics = new ConductorCodecMetricsV1();
        using var source = new ShortReadStream(bytes, 5000);
        ConductorTrackCodecV1.Restore(project, source, metrics: metrics);
        Assert.Equal(expected, ConductorTrackCodecV1.Serialize(project));
        Assert.Equal(ConductorJsonStreamReaderV1.InitialBufferBytes, metrics.PeakInputBufferBytes);
        Assert.Equal(512, metrics.PeakRecordBufferBytes);
    }

    [Fact]
    public void OversizeDecimalLiteralKeepsSourceGeneratedAcceptanceAndValue()
    {
        using var project = new MidoraProject(480);
        string json = Encoding.UTF8.GetString(OriginalDtoBytes(project))
            .Replace("\"beatsPerMinute\": 120", "\"beatsPerMinute\":120." + new string('0', 100_000));
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ConductorTrackJsonV1? oracle = null;
        Exception? oldFailure = Record.Exception(() => oracle = ConductorTrackCodecV1.Parse(bytes));
        using var restored = new MidoraProject(480);
        using var source = new ShortReadStream(bytes, 5000);
        var metrics = new ConductorCodecMetricsV1();
        if (oldFailure is not null)
        {
            AssertDataFailure(() => ConductorTrackCodecV1.Restore(restored, source, metrics: metrics));
            return;
        }
        ConductorTrackCodecV1.Restore(project, oracle!);
        ConductorTrackCodecV1.Restore(restored, source, metrics: metrics);
        Assert.Equal(OriginalDtoBytes(project), ConductorTrackCodecV1.Serialize(restored));
        output.WriteLine($"oversize legal scalar: inputBuffer={metrics.PeakInputBufferBytes}, recordBuffer={metrics.PeakRecordBufferBytes}");
    }

    [Fact]
    public void InvalidUtf8AndDeepMalformedObjectAreRejectedWithoutPublishing()
    {
        using var project = CreateMixedProject();
        ConductorTrack before = project.Conductor;
        byte[] bytes = OriginalDtoBytes(project);
        bytes[Array.IndexOf(bytes, (byte)'m')] = 0xff;
        using var malformed = new ShortReadStream(bytes, 1);
        AssertDataFailure(() => ConductorTrackCodecV1.Restore(project, malformed));
        Assert.Same(before, project.Conductor);
        string deep = "{\"schemaVersion\":1,\"tempos\":[{\"id\":" + new string('[', 101) + "1" + new string(']', 101) + "}]}";
        AssertDataFailure(() => Restore(project, deep));
        Assert.Same(before, project.Conductor);
    }

    [Fact]
    public void DuplicateStateTicksAndMaximumIdAreValidatedAcrossArrays()
    {
        using var sourceProject = CreateMixedProject();
        byte[] bytes = OriginalDtoBytes(sourceProject);
        string text = Encoding.UTF8.GetString(bytes);
        string maximum = text.Replace("\"id\": 1,", "\"id\": 9223372036854775807,");
        using var project = new MidoraProject(480);
        Restore(project, maximum);
        Assert.Equal(long.MaxValue, project.Conductor.Tempos[0].Id.Value);
        Assert.ThrowsAny<Exception>(() => Restore(project, maximum.Replace("\"id\": 2,", "\"id\": 9223372036854775807,")));
        Assert.ThrowsAny<Exception>(() => Restore(project, text.Replace("\"tick\": 960,", "\"tick\": 0,")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCancellationOrIoFailureDoesNotPublish(bool cancel)
    {
        using var original = CreateMixedProject();
        byte[] bytes = OriginalDtoBytes(original);
        using var project = new MidoraProject(480);
        ConductorTrack before = project.Conductor;
        using var cancellation = new CancellationTokenSource();
        using var stream = new ShortReadStream(bytes, 11, () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("Injected read failure.");
        }, bytes.Length / 2);
        if (cancel) Assert.Throws<OperationCanceledException>(() => ConductorTrackCodecV1.Restore(project, stream, cancellation.Token));
        else Assert.Throws<IOException>(() => ConductorTrackCodecV1.Restore(project, stream));
        Assert.Same(before, project.Conductor);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void WriteCancellationAndFailureLeaveSourceUnchangedAndDestinationOpen()
    {
        using var project = CreateMixedProject();
        byte[] before = OriginalDtoBytes(project);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var outputStream = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => ConductorTrackCodecV1.Serialize(project, outputStream, cancelled.Token));
        Assert.Empty(outputStream.ToArray());
        using var failing = new FailingWriteStream();
        Assert.Throws<IOException>(() => ConductorTrackCodecV1.Serialize(project, failing));
        Assert.True(failing.CanWrite);
        Assert.Equal(before, OriginalDtoBytes(project));
    }

    [Fact]
    public void IncompatibleTimeSignatureFailsBeforePublishingOrWriting()
    {
        using var original = new MidoraProject(480);
        string text = Encoding.UTF8.GetString(OriginalDtoBytes(original)).Replace("\"denominator\": 4", "\"denominator\": 8");
        using var project = new MidoraProject(1);
        ConductorTrack before = project.Conductor;
        Assert.Throws<InvalidDataException>(() => Restore(project, text));
        Assert.Same(before, project.Conductor);
        project.Conductor.TimeSignatures[0] = project.Conductor.TimeSignatures[0] with { Denominator = 8 };
        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => ConductorTrackCodecV1.Serialize(project, destination));
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void LargeConductorUsesFixedJsonBufferAndPagedDomainStorage(int count)
    {
        using var directory = new TestFile();
        using var project = new MidoraProject(480);
        using (project.Conductor.Tempos.BeginBatchChange())
            for (int i = 1; i < count; i++) project.Conductor.Tempos.Add(new TempoChange(project, i, 60 + i % 180));
        var metrics = new ConductorCodecMetricsV1();
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        ConductorTrackCodecV1.Serialize(project, directory.Stream, metrics: metrics);
        output.WriteLine($"write records={metrics.Records}, ms={watch.Elapsed.TotalMilliseconds:F2}, allocated={GC.GetAllocatedBytesForCurrentThread()-allocated}, bytes={directory.Stream.Length}");
        Assert.InRange(metrics.PeakIdValidationBytes, 1, StableIdValidatorV1.DefaultMemoryBudgetBytes);
        directory.Stream.Position = 0;
        using var restored = new MidoraProject(480);
        watch.Restart();
        allocated = GC.GetAllocatedBytesForCurrentThread();
        ConductorTrackCodecV1.Restore(restored, directory.Stream, metrics: metrics);
        output.WriteLine($"read records={metrics.Records}, ms={watch.Elapsed.TotalMilliseconds:F2}, allocated={GC.GetAllocatedBytesForCurrentThread()-allocated}, jsonBuffer={metrics.PeakInputBufferBytes}, recordBuffer={metrics.PeakRecordBufferBytes}, idBytes={metrics.PeakIdValidationBytes}");
        Assert.Equal(count, restored.Conductor.Tempos.Count);
        Assert.Equal(count + 1, metrics.Records);
        Assert.Equal(ConductorJsonStreamReaderV1.InitialBufferBytes, metrics.PeakInputBufferBytes);
        Assert.InRange(metrics.PeakIdValidationBytes, 1, StableIdValidatorV1.DefaultMemoryBudgetBytes);
        Assert.True(restored.Conductor.Tempos.CaptureQuerySnapshot().UsesExternalStorage);
        Assert.Equal(project.Conductor.Tempos.CaptureQuerySnapshot().ContentFingerprint, restored.Conductor.Tempos.CaptureQuerySnapshot().ContentFingerprint);
        Assert.Equal(project.Conductor.Tempos[^1], restored.Conductor.Tempos[^1]);
        restored.Conductor.Tempos[33] = restored.Conductor.Tempos[33] with { BeatsPerMinute = 88m };
        Assert.Equal(88m, restored.Conductor.Tempos[33].BeatsPerMinute);
        Assert.NotEqual(88m, project.Conductor.Tempos[33].BeatsPerMinute);
    }

    private static void Restore(MidoraProject project, string text)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
        ConductorTrackCodecV1.Restore(project, source);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RestoreAndReleaseInput(MidoraProject project)
    {
        using var original = CreateMixedProject();
        byte[] bytes = OriginalDtoBytes(original);
        using var stream = new MemoryStream(bytes);
        ConductorTrackCodecV1.Restore(project, stream);
        return [new(bytes), new(stream)];
    }
    private static void AssertDataFailure(Action operation)
    {
        Exception? failure = Record.Exception(operation);
        Assert.True(failure is JsonException or InvalidDataException,
            $"Malformed conductor data must remain inside package recovery boundary; actual: {failure}");
    }
    private static MidoraProject CreateMixedProject()
    {
        var project = new MidoraProject(480);
        project.Conductor.Tempos.Add(new TempoChange(project, 960, 123.4500m));
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1280, 7, 8));
        project.Conductor.KeySignatures.Add(new KeySignatureChange(project, 12, -7, true));
        project.Conductor.KeySignatures.Add(new KeySignatureChange(project, 400, 7, false));
        project.Conductor.Markers.Add(new ProjectMarker(project, 42, "日本語 😀 <>&\"\\"));
        project.Conductor.Markers.Add(new ProjectMarker(project, 42, string.Concat(Enumerable.Repeat("😀", 256))));
        project.Conductor.Markers.Add(new ProjectMarker(project, long.MaxValue, ""));
        project.Conductor.EndMarker = new ProjectEndMarker(project, 9999);
        return project;
    }

    // Frozen pre-streaming algorithm, retained only as a small-fixture oracle.
    private static byte[] OriginalDtoBytes(MidoraProject project) => StrictJsonV1.SerializeWithFinalLf(CreateDto(project), MidoraJsonSerializerContextV1.Default.ConductorTrackJsonV1);
    private static ConductorTrackJsonV1 CreateDto(MidoraProject project) => new()
    {
        SchemaVersion = 1,
        Tempos = project.Conductor.Tempos.Select(item => new TempoChangeJsonV1 { Id = new(item.Id.Value), Tick = item.Tick, BeatsPerMinute = item.BeatsPerMinute }).ToArray(),
        TimeSignatures = project.Conductor.TimeSignatures.Select(item => new TimeSignatureChangeJsonV1 { Id = new(item.Id.Value), Tick = item.Tick, Numerator = item.Numerator, Denominator = item.Denominator }).ToArray(),
        KeySignatures = project.Conductor.KeySignatures.Select(item => new KeySignatureChangeJsonV1 { Id = new(item.Id.Value), Tick = item.Tick, SharpsFlats = item.SharpsFlats, IsMinor = item.IsMinor }).ToArray(),
        Markers = project.Conductor.Markers.Select(item => new ProjectMarkerJsonV1 { Id = new(item.Id.Value), Tick = item.Tick, Name = item.Name }).ToArray(),
        EndMarker = project.Conductor.EndMarker is not { } end ? null : new ProjectEndMarkerJsonV1 { Id = new(end.Id.Value), Tick = end.Tick }
    };
    private sealed class ShortReadStream(byte[] bytes, int shortRead, Action? onThreshold = null, int threshold = int.MaxValue) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_offset >= threshold) onThreshold?.Invoke();
            int count = Math.Min(buffer.Length, Math.Min(shortRead, bytes.Length - _offset));
            bytes.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected write failure.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Injected write failure.");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class CountingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public int WriteCount { get; private set; }
        public byte[] ToArray() => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { if (!buffer.IsEmpty) WriteCount++; _inner.Write(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class TestFile : IDisposable
    {
        public FileStream Stream { get; }
        public TestFile()
        {
            string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "conductor-streaming-tests");
            Directory.CreateDirectory(directory);
            Stream = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.DeleteOnClose);
        }
        public void Dispose() => Stream.Dispose();
    }
}
