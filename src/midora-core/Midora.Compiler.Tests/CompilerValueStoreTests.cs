using System.Runtime.CompilerServices;
using System.Reflection;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Midora.Compiler.Tests;

public sealed class CompilerValueStoreTests
{
    [Theory]
    [InlineData("Count", 0L)]
    [InlineData("Count", 4097L)]
    [InlineData("StoredBytes", -1L)]
    [InlineData("StoredBytes", 32769L)]
    [InlineData("Offset", -1L)]
    [InlineData("Offset", long.MaxValue)]
    [InlineData("Offset", 1L)]
    [InlineData("Count", 4095L)]
    public void AlteredPageDescriptorsFailBeforeYieldingAnyRecord(string field, long value)
    {
        using CompilerValueStore<long> store = RepeatedSpilledValues();
        (object pages, object page) = FirstPage(store);
        PropertyInfo property = page.GetType().GetProperty(field)!;
        property.SetValue(page, Convert.ChangeType(value, property.PropertyType));
        pages.GetType().GetProperty("Item")!.SetValue(pages, page, [0]);
        Assert.Throws<InvalidDataException>(() => store.Enumerate().First());
        Assert.Throws<InvalidDataException>(() => store[0]);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(8)]
    [InlineData(0)]
    public void AuthenticatedButInvalidCodecOutputIsNeverPublished(int decodedLengthDelta)
    {
        using CompilerValueStore<long> store = RepeatedSpilledValues();
        (object pages, object page) = FirstPage(store);
        byte[] encoded;
        if (decodedLengthDelta == 0) encoded = [0xff, 0xff, 0xff, 0xff];
        else
        {
            using MemoryStream bytes = new();
            using (DeflateStream codec = new(bytes, CompressionLevel.Fastest, leaveOpen: true))
                codec.Write(new byte[4096 * sizeof(long) + decodedLengthDelta]);
            encoded = bytes.ToArray();
        }
        // Authenticate the deliberately invalid fixture, so failure must be the
        // codec/decoded-length gate, not merely a mismatched stored checksum.
        page.GetType().GetProperty("StoredBytes")!.SetValue(page, encoded.Length);
        page.GetType().GetProperty("Compressed")!.SetValue(page, true);
        byte[] identity = (byte[])typeof(CompilerValueStore<long>).GetField("RecordIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        byte[] descriptor = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, 2);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(4), sizeof(long));
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(8), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(12), 4096 * sizeof(long));
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(16), encoded.Length);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(20), 1);
        using IncrementalHash checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        checksum.AppendData(identity); checksum.AppendData(descriptor); checksum.AppendData(encoded);
        byte[] hash = checksum.GetHashAndReset();
        Type hashType = page.GetType().GetProperty("Hash")!.PropertyType;
        object authenticated = Activator.CreateInstance(hashType,
            Enumerable.Range(0, 4).Select(i => (object)BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(i * 8))).ToArray())!;
        page.GetType().GetProperty("Hash")!.SetValue(page, authenticated);
        pages.GetType().GetProperty("Item")!.SetValue(pages, page, [0]);
        FileStream file = SpillFile(store);
        RandomAccess.Write(file.SafeFileHandle, encoded, 0);
        Exception? failure = Record.Exception(() => store.Enumerate().First());
        Assert.True(failure is InvalidDataException or EndOfStreamException, failure?.ToString());
    }

    private static CompilerValueStore<long> RepeatedSpilledValues()
    {
        CompilerValueStore<long> store = new(new(0));
        for (int i = 0; i < 8201; i++) store.Add(42);
        store.Seal();
        return store;
    }
    private static FileStream SpillFile(CompilerValueStore<long> store) =>
        (FileStream)typeof(CompilerValueStore<long>).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private static (object Pages, object Page) FirstPage(CompilerValueStore<long> store)
    {
        object pages = typeof(CompilerValueStore<long>).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return (pages, pages.GetType().GetProperty("Item")!.GetValue(pages, [0])!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    [InlineData(262144)]
    public void SealedPagesRetainExactRandomSequentialReverseAndRangeValues(long residentBudget)
    {
        CompilerStorageBudget budget = new(residentBudget);
        using CompilerValueStore<long> store = new(budget);
        const int count = CompilerValueStore<long>.PageCapacity * 2 + 37;
        for (int i = 0; i < count; i++) store.Add(Value(i));
        Assert.Equal(count, store.Count);
        Assert.Equal(Value(count - 1), store[count - 1]);
        store.Seal(); store.Seal();
        Assert.True(store.IsSealed);
        Assert.Equal(3, store.PageCount);
        Assert.InRange(store.ResidentBytes, 0, residentBudget);
        Assert.InRange(store.ResidentBytes + store.SpillBytes, 1, (long)count * sizeof(long));
        Assert.Equal(Enumerable.Range(0, count).Select(Value), store.Enumerate());
        Assert.Equal(Enumerable.Range(0, count).Reverse().Select(Value), store.Enumerate(reverse: true));
        foreach ((long start, long length) in new[] { (0L, 0L), (0L, 1L), (4083L, 38L), (8190L, 39L) })
        {
            long[] expected = Enumerable.Range((int)start, (int)length).Select(Value).ToArray();
            Assert.Equal(expected, store.EnumerateRange(start, length));
            Assert.Equal(expected.Reverse(), store.EnumerateRange(start, length, reverse: true));
        }
        foreach (int index in new[] { 0, 17, 4095, 4096, 8191, count - 1 }) Assert.Equal(Value(index), store[index]);
        Assert.Throws<InvalidOperationException>(() => store.Add(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => store[count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.EnumerateRange(count, 1).ToArray());
        store.Dispose(); store.Dispose();
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void IndependentSpilledReadersInterleaveAndHonorCancellationWithoutPoisoningOtherReaders()
    {
        CompilerStorageBudget budget = new(0);
        using CompilerValueStore<long> store = new(budget);
        for (int i = 0; i < 10000; i++) store.Add(Value(i));
        store.Seal();
        using CancellationTokenSource cancellation = new();
        using IEnumerator<long> first = store.Enumerate(cancellationToken: cancellation.Token).GetEnumerator();
        using IEnumerator<long> second = store.Enumerate(reverse: true).GetEnumerator();
        for (int i = 0; i < 512; i++)
        {
            Assert.True(first.MoveNext()); Assert.True(second.MoveNext());
            Assert.Equal(Value(i), first.Current); Assert.Equal(Value(9999 - i), second.Current);
        }
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => first.MoveNext());
        Assert.True(second.MoveNext()); Assert.Equal(Value(9487), second.Current);
        Assert.Equal(Value(0), store.First());
    }

    [Fact]
    public void ReaderOwnsSpilledStorageAfterTheProducerReferenceLeavesScope()
    {
        (IEnumerator<long> reader, WeakReference owner) = MakeReader();
        using (reader)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.True(owner.IsAlive);
            for (int i = 0; i < 5000; i++)
            {
                Assert.True(reader.MoveNext()); Assert.Equal(Value(i), reader.Current);
            }
            Assert.False(reader.MoveNext());
        }
    }

    [Fact]
    public void SpillQuotaFailureReleasesReservationsAndDoesNotPublishAPartialPage()
    {
        // The quota limits encoded physical bytes, not uncompressed record bytes.
        // A one-byte budget cannot retain either a compressed or a raw nonempty page.
        CompilerStorageBudget budget = new(0, maximumSpillBytes: 1);
        using CompilerValueStore<long> store = new(budget);
        for (int i = 0; i < 4095; i++) store.Add(Value(i));
        Assert.Throws<InvalidOperationException>(() => store.Add(Value(4095)));
        Assert.False(store.IsSealed);
        Assert.Throws<InvalidOperationException>(() => store.Seal());
        Assert.False(store.IsSealed);
        Assert.Equal(0, budget.SpillBytes);
        store.Dispose();
        Assert.Equal(0, budget.SpillBytes);
        Assert.Equal(0, budget.ResidentBytes);
    }

    [Fact]
    public void CancelledWriteDoesNotCreateStorageAndWorkingBudgetRejectsBeforeAllocation()
    {
        CompilerStorageBudget budget = new(0, maximumWorkingBytes: 16 * sizeof(long) - 1);
        using CompilerValueStore<long> store = new(budget);
        Assert.Throws<InvalidOperationException>(() => store.Add(3));
        Assert.Equal(0, store.Count);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        using CompilerValueStore<long> cancelled = new(new(0), cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => cancelled.Add(1));
        Assert.Equal(0, cancelled.Count);
        Assert.Equal(0, cancelled.SpillBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedSpillPagesFailExplicitlyRatherThanReturningWrongValues(bool truncate)
    {
        using CompilerValueStore<long> store = new(new(0));
        for (int i = 0; i < 5000; i++) store.Add(Value(i));
        store.Seal();
        FileStream file = (FileStream)typeof(CompilerValueStore<long>).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        if (truncate)
        {
            file.SetLength(100);
            Assert.Throws<EndOfStreamException>(() => store.Enumerate().ToArray());
        }
        else
        {
            RandomAccess.Write(file.SafeFileHandle, new byte[] { 0x93, 0x27 }, 0);
            Assert.Throws<InvalidDataException>(() => store.Enumerate().ToArray());
        }
    }

    [Fact]
    public void AbortingNewCompilationDoesNotInvalidateReaderFromOlderBudget()
    {
        CompilerStorageBudget oldBudget = new(0), nextBudget = new(0);
        using CompilerValueStore<long> old = new(oldBudget);
        using CompilerValueStore<long> next = new(nextBudget);
        for (int i = 0; i < 5000; i++) { old.Add(Value(i)); next.Add(-Value(i)); }
        old.Seal(); next.Seal();
        long oldSpill = oldBudget.SpillBytes;
        Assert.InRange(oldSpill, 1, 5000 * sizeof(long));
        using IEnumerator<long> reader = old.GetEnumerator();
        Assert.True(reader.MoveNext()); Assert.Equal(Value(0), reader.Current);
        nextBudget.Abort(); nextBudget.Abort();
        Assert.Equal(0, nextBudget.SpillBytes);
        Assert.Equal(0, nextBudget.ResidentBytes);
        Assert.Throws<ObjectDisposedException>(() => next[0]);
        for (int i = 1; i < 5000; i++) { Assert.True(reader.MoveNext()); Assert.Equal(Value(i), reader.Current); }
        Assert.False(reader.MoveNext());
        Assert.Equal(oldSpill, oldBudget.SpillBytes);
    }

    [Fact]
    public void IncompressibleSpilledPagesPreserveAllBytesWithoutGrowingPastRawSize()
    {
        const int count = CompilerValueStore<long>.PageCapacity * 2 + 37;
        byte[] bytes = new byte[count * sizeof(long)];
        new Random(691).NextBytes(bytes);
        long[] expected = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes).ToArray();
        CompilerStorageBudget budget = new(0);
        using CompilerValueStore<long> store = new(budget);
        foreach (long value in expected) store.Add(value);
        store.Seal();
        Assert.InRange(store.SpillBytes, bytes.Length * 9L / 10, bytes.Length);
        Assert.Equal(expected, store.Enumerate());
        Assert.Equal(expected.Reverse(), store.Enumerate(reverse: true));
        Assert.Equal(expected[4096], store[4096]);
        store.Dispose(); Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void HighlyRepetitiveSpilledPagesUseCompressionAndPreserveValues()
    {
        CompilerStorageBudget budget = new(0);
        using CompilerValueStore<long> store = new(budget);
        for (int i = 0; i < 8201; i++) store.Add(42);
        store.Seal();
        Assert.InRange(store.SpillBytes, 1, 8201 * sizeof(long) / 10);
        Assert.Equal(Enumerable.Repeat(42L, 8201), store.Enumerate());
        Assert.Equal(Enumerable.Repeat(42L, 8201), store.Enumerate(reverse: true));
        Assert.Equal(42, store[4096]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedEncodedRawAndPartialPagesPromoteWithoutInvalidatingExistingReaders(bool fullPromotion)
    {
        const int count = 8211;
        long[] expected = new long[count];
        Array.Fill(expected, 42, 0, 4096);
        byte[] random = new byte[4096 * sizeof(long)]; new Random(571).NextBytes(random);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(random).CopyTo(expected.AsSpan(4096, 4096));
        Array.Fill(expected, 73, 8192, count - 8192);
        long residentLimit = (fullPromotion ? count : 4096) * sizeof(long);
        CompilerStorageBudget budget = new(residentLimit);
        using CompilerValueStore<long> blocker = new(budget);
        for (int i = 0; i < residentLimit / sizeof(long); i++) blocker.Add(0);
        blocker.Seal();
        using CompilerValueStore<long> values = new(budget);
        foreach (long value in expected) values.Add(value);
        values.Seal();
        Assert.Equal(0, values.ResidentBytes);
        long spilled = values.SpillBytes;
        Assert.InRange(spilled, random.Length, expected.LongLength * sizeof(long) - 1);
        using IEnumerator<long> reader = values.GetEnumerator();
        Assert.True(reader.MoveNext()); Assert.Equal(expected[0], reader.Current);
        blocker.Dispose();
        Assert.Equal(expected, values.EnumerateForPublication());
        values.ReleaseFullyResidentBacking();
        Assert.Equal(residentLimit, values.ResidentBytes);
        Assert.Equal(fullPromotion ? 0 : spilled, values.SpillBytes);
        for (int i = 1; i < count; i++) { Assert.True(reader.MoveNext()); Assert.Equal(expected[i], reader.Current); }
        Assert.False(reader.MoveNext());
        Assert.Equal(expected.Reverse(), values.Enumerate(reverse: true));
        Assert.Equal(expected.Skip(4088).Take(4109), values.EnumerateRange(4088, 4109));
        values.Dispose();
        Assert.Equal(0, budget.ResidentBytes); Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void CorruptCompressedNewResultAbortPreservesOlderCompressedReader()
    {
        CompilerStorageBudget oldBudget = new(0), failedBudget = new(0);
        using CompilerValueStore<long> old = new(oldBudget), failed = new(failedBudget);
        for (int i = 0; i < 8201; i++) { old.Add(42); failed.Add(73); }
        old.Seal(); failed.Seal();
        using IEnumerator<long> reader = old.GetEnumerator();
        Assert.True(reader.MoveNext());
        FileStream file = (FileStream)typeof(CompilerValueStore<long>).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(failed)!;
        RandomAccess.Write(file.SafeFileHandle, new byte[] { 0xff, 0xff, 0xff }, 0);
        Assert.Throws<InvalidDataException>(() => failed.Enumerate().ToArray());
        failedBudget.Abort();
        Assert.Equal(0, failedBudget.ResidentBytes); Assert.Equal(0, failedBudget.SpillBytes);
        for (int i = 1; i < 8201; i++) { Assert.True(reader.MoveNext()); Assert.Equal(42, reader.Current); }
        Assert.False(reader.MoveNext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyRunsAndTwoWayFanInPreserveTotalOrderingAndEveryDuplicate(bool reverse)
    {
        CompilerStorageBudget budget = new(0);
        IComparer<SortItem> comparer = Comparer<SortItem>.Create((left, right) =>
        {
            int compare = left.Key.CompareTo(right.Key);
            if (compare == 0) compare = left.Order.CompareTo(right.Order);
            return reverse ? -compare : compare;
        });
        using CompilerExternalSorter<SortItem> sorter = new(budget, comparer, runSize: 7, fanIn: 2);
        SortItem[] expected = Enumerable.Range(0, 1001).Select(i => new SortItem((i * 71) % 19, i)).ToArray();
        foreach (SortItem item in expected) sorter.Add(item);
        Array.Sort(expected, comparer);
        Assert.Equal(expected, sorter.ReadSorted());
        Assert.Throws<InvalidOperationException>(() => sorter.ReadSorted().ToArray());
        Assert.Throws<InvalidOperationException>(() => sorter.Add(default));
        sorter.Dispose();
        Assert.Equal(0, budget.SpillBytes);
        Assert.Equal(0, budget.ResidentBytes);
    }

    [Fact]
    public void CancelledMergeReleasesRunsOnDisposeAndDoesNotAffectAnotherSorter()
    {
        CompilerStorageBudget budget = new(0);
        using CancellationTokenSource cancellation = new();
        using CompilerExternalSorter<long> sorter = new(budget, Comparer<long>.Default, runSize: 3, fanIn: 2);
        for (int i = 511; i >= 0; i--) sorter.Add(i);
        using (IEnumerator<long> reader = sorter.ReadSorted(cancellation.Token).GetEnumerator())
        {
            Assert.True(reader.MoveNext()); Assert.Equal(0, reader.Current);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => { while (reader.MoveNext()) { } });
        }
        sorter.Dispose(); Assert.Equal(0, budget.SpillBytes);
        using CompilerExternalSorter<long> fresh = new(budget, Comparer<long>.Default, runSize: 3, fanIn: 2);
        fresh.Add(7); fresh.Add(2);
        Assert.Equal(new long[] { 2, 7 }, fresh.ReadSorted());
    }

    [Fact]
    public void MetadataGrowthReservesBothArraysBeforeChangingTheExistingList()
    {
        CompilerStorageBudget budget = new(maximumMetadataBytes: 100);
        using CompilerMetadataList<long> values = new(budget);
        for (int i = 0; i < 4; i++) values.Add(i);
        Assert.Equal(56, budget.MetadataBytes);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => values.Add(4));
        Assert.Contains("metadata-memory budget", error.Message);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, values);
        Assert.Equal(56, budget.MetadataBytes);
        Assert.Equal(56, budget.PeakMetadataBytes);
        values.Dispose(); values.Dispose();
        Assert.Equal(0, budget.MetadataBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    public void PageMetadataLimitFailsWithoutPublishingAndAbortReleasesEverything(long residentBytes)
    {
        CompilerStorageBudget budget = new(residentBytes, maximumMetadataBytes: 0);
        using CompilerValueStore<long> values = new(budget);
        values.Add(42);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => values.Seal());
        Assert.Contains("metadata-memory budget", error.Message);
        Assert.False(values.IsSealed);
        budget.Abort();
        Assert.Equal(0, budget.MetadataBytes);
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void SortRunMetadataLimitRejectsBoundedlyAndDisposalReleasesReservations()
    {
        CompilerStorageBudget budget = new(0, maximumMetadataBytes: 100);
        using CompilerExternalSorter<long> sorter = new(budget, Comparer<long>.Default, runSize: 1, fanIn: 2);
        for (int i = 0; i < 4; i++) sorter.Add(i);
        Assert.True(budget.MetadataBytes > 0);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => sorter.Add(4));
        Assert.Contains("metadata-memory budget", error.Message);
        sorter.Dispose(); budget.Abort();
        Assert.Equal(0, budget.MetadataBytes);
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void ReadCancellationDoesNotReleaseLivePageMetadataAndNewAbortDoesNotTouchOldPages()
    {
        CompilerStorageBudget oldBudget = new(0), nextBudget = new(0);
        using CompilerValueStore<long> old = new(oldBudget), next = new(nextBudget);
        for (int i = 0; i < 8201; i++) { old.Add(i); next.Add(-i); }
        old.Seal(); next.Seal();
        long metadata = oldBudget.MetadataBytes;
        Assert.True(metadata > 0);
        using CancellationTokenSource cancellation = new();
        using (IEnumerator<long> reader = old.Enumerate(cancellationToken: cancellation.Token).GetEnumerator())
        {
            Assert.True(reader.MoveNext()); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => { while (reader.MoveNext()) { } });
        }
        Assert.Equal(metadata, oldBudget.MetadataBytes);
        nextBudget.Abort();
        Assert.Equal(0, nextBudget.MetadataBytes);
        Assert.Equal(metadata, oldBudget.MetadataBytes);
        Assert.Equal(Enumerable.Range(0, 8201).Select(i => (long)i), old.Enumerate());
        old.Dispose();
        Assert.Equal(0, oldBudget.MetadataBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IEnumerator<long>, WeakReference) MakeReader()
    {
        CompilerValueStore<long> store = new(new(0));
        for (int i = 0; i < 5000; i++) store.Add(Value(i));
        store.Seal();
        return (store.Enumerate().GetEnumerator(), new(store));
    }

    private static long Value(int index) => ((long)index << 33) ^ (index * -371L);
    private readonly record struct SortItem(int Key, long Order);
}
