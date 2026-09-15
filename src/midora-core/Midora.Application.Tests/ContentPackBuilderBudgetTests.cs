using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ContentPackBuilderBudgetTests : IDisposable
{
    private const long SmallBudget = 9L * 1024 * 1024;
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory,
        ".tmp", "content-builder-tests", Guid.NewGuid().ToString("N"));

    public ContentPackBuilderBudgetTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void TinySegmentTailsAllocateOnlyUsedCapacityAndReleaseOnComplete(int segments)
    {
        using var writer = new PureMidiContentPackWriter(PathFor("tiny.mpk"));
        for (int index = 0; index < segments; index++)
            writer.AddNote(new(index + 1), Note(index, 0));
        Assert.Equal(0, writer.PendingPageReservedBytes);
        Assert.Equal(0, writer.BuilderSpillCount);
        // One 256-byte byte-stream and two 16-element endpoint arrays per tail,
        // instead of one 64 KiB stream and a full 16,384-element array per kind.
        Assert.InRange(writer.ActiveBuilderCapacityBytes, 1, segments * 2048L);
        using PureMidiContentPack pack = writer.Complete();
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
        Assert.Equal(0, writer.PendingPageReservedBytes);
        Assert.Equal(0, writer.PendingActualBufferBytes);
        Assert.InRange(writer.PeakActualBufferBytes, 1, writer.PeakReservedBytes);
        for (int index = 0; index < segments; index++)
            Assert.Equal(1, pack.GetSegmentSource(new(index + 1)).NoteCount);
    }

    [Fact]
    public void IncompleteTailSpoolingKeepsFrozenBytesAndBoundedWorkingCapacity()
    {
        var low = WriteMixed("small.mpk", SmallBudget, 2);
        var high = WriteMixed("large.mpk", 64L * 1024 * 1024, 4);
        Assert.True(low.Spills > 0);
        Assert.True(low.SpoolLength > 0);
        Assert.InRange(low.Peak, 1, SmallBudget);
        Assert.Equal(0, high.Spills);
        Assert.Equal(File.ReadAllBytes(PathFor("large.mpk")), File.ReadAllBytes(PathFor("small.mpk")));
    }

    [Fact]
    public void RepeatedTailRevisitsReuseSpoolExtentsInsteadOfAppendingHistory()
    {
        using var writer = new PureMidiContentPackWriter(PathFor("revisit.mpk"), default, 1, SmallBudget);
        Populate(writer);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var builders = (System.Collections.IDictionary)typeof(PureMidiContentPackWriter)
            .GetField("_builders", flags)!.GetValue(writer)!;
        object[] values = builders.Values.Cast<object>().ToArray();
        var restore = values[0].GetType().GetMethod("EnsureResident")!;
        long previous = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (object builder in values) restore.Invoke(builder, null);
            if (pass >= 2) Assert.Equal(previous, writer.BuilderSpoolLength);
            previous = writer.BuilderSpoolLength;
        }
        using var pack = writer.Complete();
        Assert.InRange(writer.PeakReservedBytes, 1, SmallBudget);
        Assert.Equal(120, pack.GetSegmentSource(new(1)).NoteCount);
    }

    [Fact]
    public void SpilledTailCancellationReleasesLeaseAndUnpublishedOutput()
    {
        using CancellationTokenSource cancellation = new();
        var writer = new PureMidiContentPackWriter(PathFor("cancel.mpk"), cancellation.Token, 1, SmallBudget);
        Populate(writer);
        Assert.True(writer.BuilderSpillCount > 0);
        string spoolPath = Assert.IsType<string>(writer.BuilderSpoolPath);
        string spoolDirectory = Path.GetDirectoryName(spoolPath)!;
        Assert.True(File.Exists(spoolPath));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => writer.Complete());
        writer.Dispose();
        Assert.False(File.Exists(PathFor("cancel.mpk")));
        Assert.False(Directory.Exists(spoolDirectory));
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
        Assert.Equal(0, writer.PendingPageReservedBytes);
        Assert.Equal(0, writer.PendingActualBufferBytes);
    }

    [Fact]
    public void SpoolFailureDoesNotPublishOrLeaveAnUnownedRun()
    {
        var writer = new PureMidiContentPackWriter(PathFor("failed.mpk"), default, 1, SmallBudget,
            beforeBuilderSpillTestHook: () => throw new IOException("Injected builder spill failure."));
        try
        {
            Assert.Throws<IOException>(() => Populate(writer));
            Assert.Null(writer.BuilderSpoolPath);
        }
        finally { writer.Dispose(); }
        Assert.False(File.Exists(PathFor("failed.mpk")));
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
    }

    [Fact]
    public void CorruptIncompleteSpoolIsRejectedBeforePackPublication()
    {
        var writer = new PureMidiContentPackWriter(PathFor("corrupt.mpk"), default, 1, SmallBudget);
        Populate(writer);
        string spoolPath = Assert.IsType<string>(writer.BuilderSpoolPath);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object spool = typeof(PureMidiContentPackWriter).GetField("_builderSpool", flags)!.GetValue(writer)!;
        FileStream file = (FileStream)spool.GetType().GetField("_file", flags)!.GetValue(spool)!;
        var builders = (System.Collections.IDictionary)typeof(PureMidiContentPackWriter)
            .GetField("_builders", flags)!.GetValue(writer)!;
        object extent = builders.Values.Cast<object>()
            .Select(b => b.GetType().GetField("_spooled", flags)!.GetValue(b)).First(v => v is not null)!;
        long offset = (long)extent.GetType().GetProperty("Offset")!.GetValue(extent)!;
        file.Position = offset;
        int old = file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)(old ^ 1));
        try { Assert.Throws<InvalidDataException>(() => writer.Complete()); }
        finally { writer.Dispose(); }
        Assert.False(File.Exists(PathFor("corrupt.mpk")));
        Assert.False(File.Exists(spoolPath));
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
        Assert.Equal(0, writer.PendingActualBufferBytes);
    }

    [Fact]
    public void MaximumLegalOpaqueStillCompletesWithSmallInternalBudget()
    {
        using var writer = new PureMidiContentPackWriter(PathFor("opaque.mpk"), default, 1, SmallBudget);
        byte[] payload = new byte[PureMidiContentPackWriter.MaximumDecodedPageByteCount - 33];
        new Random(912).NextBytes(payload);
        writer.AddOpaqueEvent(new(1), new(new(2), 0, OpaqueMidiEventKind.Meta, 0x7F, payload, 0));
        using var pack = writer.Complete();
        Assert.Equal(1, pack.GetSegmentSource(new(1)).OpaqueEventCount);
        Assert.InRange(writer.PeakReservedBytes, 1, 24L * 1024 * 1024);
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
        Assert.Equal(0, writer.PendingActualBufferBytes);
        Assert.InRange(writer.PeakActualBufferBytes, 1, writer.PeakReservedBytes);
    }

    [Fact]
    public void OpaqueDecodedCacheChargesActualPayloadAndRecordArrayCapacity()
    {
        using var cache = new PureMidiContentPackDecodedCache(4 * 1024 * 1024);
        using var writer = new PureMidiContentPackWriter(PathFor("opaque-cache.mpk"));
        byte[] payload = new byte[1024 * 1024];
        writer.AddOpaqueEvent(new(1), new(new(2), 0, OpaqueMidiEventKind.Meta, 0x7F, payload, 0));
        using var pack = writer.Complete(cache);
        OpaqueMidiEventValue value = pack.GetSegmentSource(new(1)).GetOpaqueEvent(0);
        long expected = 24 + System.Runtime.CompilerServices.Unsafe.SizeOf<OpaqueMidiEventValue>()
            + OpaqueMidiPayloadMemory.GetRetainedAllocatedBytes([value]);
        Assert.Equal(expected, cache.ByteCount);
        Assert.Equal(payload, value.Payload.ToArray());
    }

    [Fact]
    public void SingleLegalOpaqueLargerThanCacheAfterAllocationOverheadIsNotRetainedOrRejected()
    {
        using var cache = new PureMidiContentPackDecodedCache(4 * 1024 * 1024);
        using var writer = new PureMidiContentPackWriter(PathFor("opaque-no-retain.mpk"));
        byte[] payload = new byte[PureMidiContentPackWriter.MaximumDecodedPageByteCount - 33];
        payload[^1] = 0x61;
        writer.AddOpaqueEvent(new(1), new(new(2), 0, OpaqueMidiEventKind.Meta, 0x7F, payload, 0));
        using var pack = writer.Complete(cache);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(payload, pack.GetSegmentSource(new(1)).GetOpaqueEvent(0).Payload.ToArray());
            Assert.Equal(0, cache.ByteCount);
        }
        Assert.Equal(2, pack.PageCacheMissCount);
    }

    private (long Spills, long Peak, long SpoolLength) WriteMixed(string name, long budget, int concurrency)
    {
        using var writer = new PureMidiContentPackWriter(PathFor(name), default, concurrency, budget);
        Populate(writer);
        long spoolLength = writer.BuilderSpoolLength;
        string? spoolPath = writer.BuilderSpoolPath;
        using var pack = writer.Complete();
        Assert.Equal(0, writer.ActiveBuilderCapacityBytes);
        Assert.Equal(0, writer.PendingPageReservedBytes);
        Assert.Equal(0, writer.PendingActualBufferBytes);
        Assert.InRange(writer.PeakActualBufferBytes, 1, writer.PeakReservedBytes);
        Assert.False(spoolPath is not null && File.Exists(spoolPath));
        for (int segment = 0; segment < 1000; segment++)
        {
            var source = pack.GetSegmentSource(new(segment + 1));
            Assert.Equal(120, source.NoteCount);
            Assert.Equal(2, source.ChannelEventCount);
            Assert.Equal(2, source.OpaqueEventCount);
        }
        return (writer.BuilderSpillCount, writer.PeakReservedBytes, spoolLength);
    }

    private static void Populate(PureMidiContentPackWriter writer)
    {
        // Revisit every unfinished builder. Spilling must not emit an early page
        // or sort source records differently when restoring a tail.
        byte[] payload = Enumerable.Range(0, 500).Select(i => (byte)(i % 128)).ToArray();
        for (int pass = 0; pass < 2; pass++)
        for (int segment = 999; segment >= 0; segment--)
        {
            MidoraId owner = new(segment + 1);
            for (int note = pass * 60; note < (pass + 1) * 60; note++)
                writer.AddNote(owner, Note(segment, note));
            writer.AddChannelEvent(owner, new(new(10_000_000L + segment * 10 + pass),
                pass * 200L, DirectMidiChannelEventKind.ControlChange, 11, pass * 37, pass));
            writer.AddOpaqueEvent(owner, new(new(20_000_000L + segment * 10 + pass),
                pass * 100L, OpaqueMidiEventKind.Meta, 0x7F, payload, pass));
        }
    }

    private static DirectMidiNoteValue Note(int segment, int note) => new(
        new(100_000L + segment * 1000 + note), 2000 - note * 3, 12 + note % 45,
        note % 128, 100, note % 128, note * 2L, note * 2L + 1);

    private string PathFor(string name) => Path.Combine(_directory, name);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
