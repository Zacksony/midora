using System.Diagnostics;
using System.Reflection;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Persistence.Tests;

public sealed class StableIdValidatorV1Tests(ITestOutputHelper output)
{
    private const string NoteSource = "Pure MIDI Track object";

    [Fact]
    public void ThousandSparseIdsDoNotAllocateOneLargeBitmapPerId()
    {
        using var ids = new StableIdValidatorV1();
        for (int i = 0; i < 1000; i++) ids.Add(new(i * (1L << 20) + 1), long.MaxValue, NoteSource);
        ids.Complete();
        Assert.False(ids.HasSpilled);
        Assert.InRange(ids.PeakResidentBytes, 1, 512 * 1024);
        output.WriteLine($"1,000 sparse IDs: {ids.PeakResidentBytes} accounted peak bytes.");
    }

    [Fact]
    public void EighteenMillionDenseIdsKeepTheFastBitmapPath()
    {
        using var ids = new StableIdValidatorV1();
        var timer = Stopwatch.StartNew();
        for (int i = 1; i <= 18_000_000; i++) ids.Add(new(i), 18_000_001, NoteSource);
        ids.Complete();
        timer.Stop();
        Assert.False(ids.HasSpilled);
        Assert.InRange(ids.PeakResidentBytes, 1, 4L * 1024 * 1024);
        Assert.Equal(18_000_000, ids.InputCount);
        output.WriteLine($"18M dense IDs: {timer.Elapsed.TotalMilliseconds:F2} ms; {ids.PeakResidentBytes} accounted peak bytes.");
        Assert.Throws<InvalidDataException>(() =>
        {
            using var duplicate = new StableIdValidatorV1();
            for (int i = 1; i <= 65_536; i++) duplicate.Add(new(i), 100_000, NoteSource);
            duplicate.Add(new(32_768), 100_000, NoteSource);
        });
    }

    [Fact]
    public void DefaultBudgetSpillsSparseIdsAndCleansUpItsOwnedFiles()
    {
        using TemporaryDirectory directory = new();
        var ids = new StableIdValidatorV1(temporaryRoot: directory.Path);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 80_000; i++) ids.Add(new(i * (1L << 20) + 1), long.MaxValue, NoteSource);
        ids.Complete();
        timer.Stop();
        Assert.True(ids.HasSpilled);
        Assert.InRange(ids.PeakResidentBytes, 1, ids.MemoryBudgetBytes);
        Assert.InRange(ids.PeakSpillBytes, 1, ids.InputCount * 48);
        Assert.Equal(ids.InputCount * 24, ids.LiveSpillBytes);
        output.WriteLine($"80,000 sparse IDs: {timer.Elapsed.TotalMilliseconds:F2} ms; resident peak {ids.PeakResidentBytes}; disk peak {ids.PeakSpillBytes}; written {ids.TotalSpillBytesWritten}.");
        string spill = ids.SpillDirectory!;
        Assert.True(Directory.Exists(spill));
        ids.Dispose();
        Assert.Equal(0, ids.AccountedResidentBytes);
        Assert.Equal(0, ids.LiveSpillBytes);
        Assert.False(Directory.Exists(spill));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void DuplicatesAcrossRunsKeepOriginalScanOrderAndSourceCategory()
    {
        using TemporaryDirectory directory = new();
        using var ids = SmallValidator(directory);
        AddSparse(ids, 0, 5000);
        Assert.True(ids.HasSpilled);
        // The numerically later ID repeats first; sorting must not change which
        // original source category is reported. One duplicate is in the prefix.
        ids.Add(new(4000L * 65536 + 1), long.MaxValue, "Logical Track nested object");
        AddSparse(ids, 5000, 3000);
        ids.Add(new(1), long.MaxValue, "Event Instrument nested object");
        Assert.Equal(Failure("Logical Track nested object"), Assert.Throws<InvalidDataException>(ids.Complete).Message);
        Assert.InRange(ids.PeakResidentBytes, 1, ids.MemoryBudgetBytes);
    }

    [Fact]
    public void EarlierSpilledDuplicateWinsOverLaterOutOfRangeId()
    {
        using TemporaryDirectory directory = new();
        using var ids = SmallValidator(directory);
        AddSparse(ids, 0, 5000);
        ids.Add(new(1), long.MaxValue, "Arrangement Track");
        Assert.Equal(Failure("Arrangement Track"), Assert.Throws<InvalidDataException>(() =>
            ids.Add(default, long.MaxValue, NoteSource)).Message);
    }

    [Fact]
    public void PositiveInt64ExtremesAndAllocatorBoundaryStayStrict()
    {
        using var ids = new StableIdValidatorV1();
        foreach (long value in new long[] { 1, 65535, 65536, long.MaxValue - 1, long.MaxValue - 65536 })
            ids.Add(new(value), long.MaxValue, NoteSource);
        ids.Complete();
        foreach (long value in new long[] { 0, 17, long.MaxValue })
        {
            using var invalid = new StableIdValidatorV1();
            Assert.Equal(Failure(NoteSource), Assert.Throws<InvalidDataException>(() =>
                invalid.Add(value == 0 ? default : new(value), value == long.MaxValue ? long.MaxValue : 17, NoteSource)).Message);
        }
    }

    [Fact]
    public void HighestSparseBlocksRemainDistinctDuringSpill()
    {
        using TemporaryDirectory directory = new();
        using var ids = SmallValidator(directory);
        for (int i = 0; i < 8000; i++) ids.Add(new(long.MaxValue - 1 - i * (1L << 40)), long.MaxValue, NoteSource);
        ids.Add(new(1), long.MaxValue, NoteSource);
        ids.Complete();
        Assert.True(ids.HasSpilled);
        Assert.Equal(8001, ids.InputCount);
        Assert.InRange(ids.PeakResidentBytes, 1, ids.MemoryBudgetBytes);
    }

    [Fact]
    public void DensePromotionPreservesRandomOrderMembership()
    {
        using var ids = new StableIdValidatorV1();
        long[] values = Enumerable.Range(1, 70_000).Select(value => (long)value).ToArray();
        new Random(88175).Shuffle(values);
        foreach (long value in values) ids.Add(new(value), long.MaxValue, NoteSource);
        foreach (long value in new long[] { 1, 4096, 4097, 65535, 65536, 70_000 })
            Assert.Equal(Failure(NoteSource), Assert.Throws<InvalidDataException>(() =>
                ids.Add(new(value), long.MaxValue, NoteSource)).Message);
        ids.Complete();
        Assert.False(ids.HasSpilled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationReleasesAllValidationResources(bool spill)
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource cancellation = new();
        var ids = SmallValidator(directory, cancellation.Token);
        AddSparse(ids, 0, spill ? 5000 : 10);
        Assert.Equal(spill, ids.HasSpilled);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(ids.Complete);
        ids.Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Equal(0, ids.AccountedResidentBytes);
    }

    [Fact]
    public void UnwritableSpillRootFailsWithoutChangingExistingFile()
    {
        using TemporaryDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, "not-a-directory");
        byte[] source = [4, 9, 2, 8];
        File.WriteAllBytes(path, source);
        using var ids = new StableIdValidatorV1(memoryBudgetBytes: 256 * 1024,
            temporaryRoot: path, chunkCapacity: 256, fileBufferBytes: 4096);
        Assert.ThrowsAny<IOException>(() => AddSparse(ids, 0, 10_000));
        Assert.Equal(source, File.ReadAllBytes(path));
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptSpillExtentsAndOrderingFailClosed(bool corruptOrder)
    {
        using TemporaryDirectory directory = new();
        using var ids = SmallValidator(directory);
        AddSparse(ids, 0, 8000);
        string run = Directory.GetFiles(ids.SpillDirectory!, "*.ids")[0];
        using (FileStream stream = new(run, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            if (corruptOrder) stream.Write(new byte[8]); // ID zero is not a valid sorted record.
            else stream.SetLength(stream.Length - 1);
        }
        Assert.Throws<InvalidDataException>(ids.Complete);
    }

    [Fact]
    public void IoFailureAfterSpillingStillReleasesEveryOwnedRun()
    {
        using TemporaryDirectory directory = new();
        var ids = SmallValidator(directory);
        AddSparse(ids, 0, 8000);
        string run = Directory.GetFiles(ids.SpillDirectory!, "*.ids")[0];
        using (FileStream locked = new(run, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(ids.Complete);
        ids.Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Equal(0, ids.LiveSpillBytes);
    }

    [Fact]
    public void RandomSparseInputAndRepeatedIdsMatchExactReference()
    {
        using TemporaryDirectory directory = new();
        using var ids = SmallValidator(directory);
        var random = new Random(51284);
        var expected = new HashSet<long>();
        for (int i = 0; i < 12_000; i++)
        {
            long value;
            do value = random.NextInt64(1, long.MaxValue); while (!expected.Add(value));
            ids.Add(new(value), long.MaxValue, NoteSource);
        }
        Assert.True(ids.HasSpilled);
        ids.Add(new(expected.First()), long.MaxValue, "MIDI Channel Root");
        foreach (long value in expected.Take(200)) ids.Add(new(value), long.MaxValue, NoteSource);
        Assert.Equal(Failure("MIDI Channel Root"), Assert.Throws<InvalidDataException>(ids.Complete).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossTrackDuplicateFailsBothPreflightAndLoadedValidationWithoutPublishing(bool sparsePaged)
    {
        using TemporaryDirectory directory = new();
        using MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        project.MidiChannelRoots.Add(root);
        DirectMidiNote note = new(project) { StartTick = 0, LengthTicks = 120, Key = 60 };
        for (int i = 0; i < 2; i++)
        {
            PureMidiTrack track = new(project) { Name = $"Track {i}", MidiChannelRootId = root.Id };
            MidiSegment segment = new(project) { LengthTicks = 480 };
            segment.Notes.Add(note);
            track.Segments.Add(segment);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        }
        using PureMidiContentPack? pack = sparsePaged ? CreateSparsePack(directory, project) : null;
        string target = System.IO.Path.Combine(directory.Path, "original.midora");
        byte[] original = [10, 20, 30];
        File.WriteAllBytes(target, original);
        var error = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            new MidoraProjectPackageV1("1.0.0-dev").SaveProjectAsync(project, target, overwriteAuthorized: true));
        Assert.Equal(MidoraPackageStageV1.Serialization, error.Stage);
        Assert.Equal(Failure(NoteSource), error.InnerException!.Message);
        Assert.Equal(original, File.ReadAllBytes(target));

        ProjectJsonV1 index = ProjectCodecV1.Parse(ProjectCodecV1.Serialize(project));
        MethodInfo validate = typeof(MidoraProjectPackageV1).GetMethod("ValidateLoadedStableIds",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var loaded = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null,
            [index, project, project.Conductor, project.NextStableId, CancellationToken.None]));
        Assert.IsType<InvalidDataException>(loaded.InnerException);
        Assert.Equal(Failure("Pure MIDI Track nested object"), loaded.InnerException!.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    private static PureMidiContentPack CreateSparsePack(TemporaryDirectory directory, MidoraProject project)
    {
        string path = System.IO.Path.Combine(directory.Path, "sparse.mpk");
        using PureMidiContentPackWriter writer = new(path);
        for (int track = 0; track < 2; track++)
        {
            MidiSegment segment = project.PureMidiTracks[track].Segments[0];
            segment.Notes.Clear();
            for (int i = 0; i < (track == 0 ? 80_000 : 1); i++)
                writer.AddNote(segment.Id, new(new((i + 1L) * (1L << 20)), i, 1, 60, 100, 0, i * 2L, i * 2L + 1));
        }
        PureMidiContentPack pack = writer.Complete();
        foreach (PureMidiTrack track in project.PureMidiTracks)
            track.Segments[0].AttachPagedContent(pack.GetSegmentSource(track.Segments[0].Id));
        typeof(MidoraProject).GetMethod("AdvanceNextStableId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(project, [80_001L * (1L << 20)]);
        return pack;
    }

    [Fact]
    public void CompletionAndDisposalAreExplicitIdempotentGates()
    {
        var ids = new StableIdValidatorV1();
        ids.Add(new(1), 2, NoteSource);
        ids.Complete(); ids.Complete();
        Assert.Throws<InvalidOperationException>(() => ids.Add(new(1), 2, NoteSource));
        ids.Dispose(); ids.Dispose();
        Assert.Throws<ObjectDisposedException>(ids.Complete);
        Assert.Throws<ObjectDisposedException>(() => ids.Add(new(1), 2, NoteSource));
    }

    private static StableIdValidatorV1 SmallValidator(TemporaryDirectory directory, CancellationToken cancellationToken = default) =>
        new(cancellationToken, 256 * 1024, directory.Path, chunkCapacity: 256, fileBufferBytes: 4096);

    private static void AddSparse(StableIdValidatorV1 ids, int start, int count)
    {
        for (int i = start; i < start + count; i++) ids.Add(new(i * 65536L + 1), long.MaxValue, NoteSource);
    }

    private static string Failure(string source) => $"{source} stable ID is zero, duplicated, or not below nextStableId.";

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, ".tmp", "stable-id-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
