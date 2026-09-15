using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Midora.Application;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PagedSelectionAndEditTransactionTests
{
    [Fact]
    public void LogicalOrdinalPagesAndCompressedRangeSelectionAreRevisionBound()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 100_000 };
        LogicalNote[] notes = Enumerable.Range(0, 10_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        LogicalNoteQuerySnapshot source = segment.Notes.CreateQuerySnapshot();

        Assert.True(source.TryGetPageByOrdinal(4_000, 4_096, out var page));
        Assert.Equal(4_000, page.FirstOrdinal);
        Assert.Equal(4_096, page.Count);
        Assert.Equal(notes[4_000].Id, page.Values[0].Id);
        Assert.Equal(5_000, source.FindOrdinalAtOrAfterTick(20_000));
        Assert.True(source.TryFindOrdinalById(notes[9_000].Id, out int found));
        Assert.Equal(9_000, found);

        TimelineObjectSelectionDescriptor selection = new(
            segment.Id,
            source.SourceRevision,
            ranges: [new TimelineOrdinalRange(100, 8_900)],
            includedIds: [notes[9_500].Id],
            excludedIds: [notes[1_000].Id]);
        TracedTimelineObject<LogicalNoteSnapshotValue>[] resolved = selection
            .ResolveTraced(source, static value => value.Id)
            .ToArray();

        Assert.Single(selection.OrdinalRanges);
        Assert.Equal(8_900, selection.OrdinalRanges[0].Count);
        Assert.Equal(8_900, resolved.Length);
        Assert.DoesNotContain(resolved, value => value.Value.Id == notes[1_000].Id);
        Assert.Contains(resolved, value => value.Value.Id == notes[9_500].Id);
        Assert.All(resolved, value =>
        {
            Assert.Equal(segment.Id, value.Trace.OwnerId);
            Assert.Equal(source.SourceRevision, value.Trace.SourceRevision);
            Assert.Equal(value.Value.Id, value.Trace.ObjectId);
        });

        notes[0].Velocity = 80;
        LogicalNoteQuerySnapshot changed = segment.Notes.CreateQuerySnapshot();
        InvalidOperationException stale = Assert.Throws<InvalidOperationException>(() =>
            selection.Resolve(changed, static value => value.Id).ToArray());
        Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MillionObjectRangeSelectionStreamsBoundedPagesAndCancelsPromptly()
    {
        const int objectCount = 1_000_000;
        SyntheticTimelineObjectSource source = new(objectCount, pageCapacity: 4_096);
        TimelineObjectSelectionDescriptor selection = new(
            new MidoraId(900),
            source.SourceRevision,
            ranges: [new TimelineOrdinalRange(0, objectCount)]);

        int resolvedCount = 0;
        MidoraId last = default;
        foreach (SyntheticValue value in selection.Resolve(
            source,
            static value => value.Id))
        {
            resolvedCount++;
            last = value.Id;
        }

        Assert.Equal(objectCount, resolvedCount);
        Assert.Equal(new MidoraId(objectCount), last);
        Assert.Equal(
            (int)Math.Ceiling(objectCount / (double)source.PageCapacity),
            source.PageRequests);
        Assert.True(source.MaximumRequestedPageSize <= source.PageCapacity);
        Assert.Equal(0, source.IdLookupRequests);

        using CancellationTokenSource cancellation = new();
        using IEnumerator<SyntheticValue> values = selection
            .Resolve(source, static value => value.Id, cancellation.Token)
            .GetEnumerator();
        for (int index = 0; index < 5_000; index++) Assert.True(values.MoveNext());
        int requestsBeforeCancellation = source.PageRequests;
        cancellation.Cancel();
        int valuesAfterCancellation = 0;
        bool cancellationObserved = false;
        while (valuesAfterCancellation <= 256)
        {
            try
            {
                Assert.True(values.MoveNext());
                valuesAfterCancellation++;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                break;
            }
        }
        Assert.True(cancellationObserved);
        Assert.InRange(valuesAfterCancellation, 0, 255);
        Assert.InRange(source.PageRequests - requestsBeforeCancellation, 0, 1);
    }

    [Fact]
    public void MillionObjectRangeWithSparseIncludeDoesNotAllocateRangeSizedIdentitySet()
    {
        const int objectCount = 1_000_000;
        SyntheticTimelineObjectSource baselineSource = new(objectCount, pageCapacity: 4_096);
        TimelineObjectSelectionDescriptor baselineSelection = new(
            new MidoraId(900),
            baselineSource.SourceRevision,
            ranges: [new TimelineOrdinalRange(0, objectCount - 1)]);
        SyntheticTimelineObjectSource source = new(objectCount, pageCapacity: 4_096);
        TimelineObjectSelectionDescriptor selection = new(
            new MidoraId(900),
            source.SourceRevision,
            ranges: [new TimelineOrdinalRange(0, objectCount - 1)],
            includedIds:
            [
                new MidoraId(100),
                new MidoraId(objectCount)
            ]);

        // Compare against the same paged traversal without sparse exceptions.
        // Synthetic pages allocate by design; the assertion isolates the
        // sparse-include overhead and catches a range-sized ID HashSet.
        _ = baselineSelection.Resolve(baselineSource, static value => value.Id)
            .Take(1).Single();
        _ = selection.Resolve(source, static value => value.Id).Take(1).Single();
        (int baselineCount, long baselineAllocated) = Measure(
            baselineSelection,
            baselineSource);
        (int resolved, long allocated) = Measure(selection, source);

        Assert.Equal(objectCount - 1, baselineCount);
        Assert.Equal(objectCount, resolved);
        Assert.Equal(2, source.IdLookupRequests);
        Assert.True(
            allocated <= baselineAllocated + (4L * 1024 * 1024),
            $"Sparse includes added {(allocated - baselineAllocated) / 1048576d:F1} MiB.");

        static (int Count, long Allocated) Measure(
            TimelineObjectSelectionDescriptor selection,
            SyntheticTimelineObjectSource source)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            int count = 0;
            foreach (SyntheticValue _ in selection.Resolve(source, static value => value.Id))
                count++;
            return (count, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void EveryEditableTimelineSnapshotImplementsTheCommonPageContract()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 10, 20, 60, 100));
        EventInstrument instrument = new(project) { Name = "Instrument" };
        instrument.SubVoices.Add(voice);
        CurvePointCollection points = new ValueCurve(project).Points;
        points.Add(new CurvePoint(project, 12, 0.5, CurveInterpolation.Step));
        MidiSegment midi = new(project) { LengthTicks = 1_000 };
        DirectMidiNote note = new(project)
        {
            StartTick = 20,
            LengthTicks = 10,
            Key = 64,
            NoteOnVelocity = 90
        };
        DirectMidiChannelEvent channel = new(project)
        {
            Tick = 21,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80
        };
        OpaqueMidiEvent opaque = new(project)
        {
            Tick = 22,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0xf0, 0xf7]
        };
        midi.Notes.Add(note);
        midi.ChannelEvents.Add(channel);
        midi.OpaqueEvents.Add(opaque);

        AssertPage(voice.Events.CreateQuerySnapshot(), voice.Events[0].Id);
        AssertPage(points.CreateQuerySnapshot(), points[0].Id);
        AssertPage(midi.Notes.CreateObjectSource(), note.Id);
        AssertPage(midi.ChannelEvents.CreateObjectSource(), channel.Id);
        AssertPage(midi.OpaqueEvents.CreateObjectSource(), opaque.Id);

        static void AssertPage<TValue>(
            ITimelineObjectSource<TValue> source,
            MidoraId expectedId)
        {
            Assert.Equal(1, source.Count);
            Assert.True(source.TryGetPageByOrdinal(0, 1, out TimelineObjectPage<TValue> page));
            Assert.Single(page.Values);
            Assert.True(source.TryFindOrdinalById(expectedId, out int ordinal));
            Assert.Equal(0, ordinal);
        }
    }

    [Fact]
    public void DetachedStagingSpillsWithinBudgetAndCompactUndoSwapsRoots()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "midora-stage2-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PagedEditResourceBudget budget = new(
                maximumRecordCount: 1_000,
                maximumResidentBytes: 64 * Unsafe.SizeOf<TestValue>(),
                maximumSpillBytes: 1_000_000,
                pageRecordCount: 64,
                cancellationCheckInterval: 16);
            using DetachedPagedEditStaging<TestValue> staging = new(
                new TestValueCodec(),
                budget,
                root);
            staging.AddRange(Enumerable.Range(0, 150).Select(index => new TestValue(index, index * 3L)));
            staging.Seal();

            Assert.Equal(150, staging.Usage.RecordCount);
            Assert.Equal(64 * Unsafe.SizeOf<TestValue>(), staging.Usage.ResidentBytes);
            Assert.True(staging.Usage.SpillBytes > 0);
            Assert.Equal(
                Enumerable.Range(0, 150),
                staging.ReadValues().Select(static value => value.Id));
            Assert.Equal(
                Enumerable.Range(0, 150),
                staging.ReadValues().Select(static value => value.Id));

            FakeRootOwner owner = new();
            (CompactPagedEditUndo<TestValue, TestRoot> undo, TimelineOwnerChangeSet change) =
                DetachedPagedEditTransaction.Commit(owner, owner.Revision, staging);
            Assert.Equal(150, owner.Root.Values.Length);
            Assert.Equal(owner.OwnerId, change.OwnerId);

            TimelineOwnerChangeSet undone = undo.Undo();
            Assert.Empty(owner.Root.Values);
            Assert.Equal(owner.OwnerId, undone.OwnerId);
            _ = undo.Redo();
            Assert.Equal(150, owner.Root.Values.Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetachedCommitRejectsStaleRevisionWithoutPublishing()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "midora-stage2-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using DetachedPagedEditStaging<TestValue> staging = new(
                new TestValueCodec(),
                new PagedEditResourceBudget(
                    maximumRecordCount: 100,
                    maximumResidentBytes: 1_024,
                    maximumSpillBytes: 1_024,
                    pageRecordCount: 64),
                root);
            staging.Add(new(1, 10));
            FakeRootOwner owner = new();
            long staleRevision = owner.Revision;
            owner.AdvanceRevisionWithoutChangingRoot();

            Assert.Throws<InvalidOperationException>(() =>
                DetachedPagedEditTransaction.Commit(owner, staleRevision, staging));
            Assert.Empty(owner.Root.Values);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetachedStagingRejectsSpillOverflowAndDisposalRemovesOwnedRun()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "midora-stage2-spill-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DetachedPagedEditStaging<TestValue> staging = new(
                new TestValueCodec(),
                new PagedEditResourceBudget(
                    maximumRecordCount: 1_000,
                    maximumResidentBytes: 0,
                    maximumSpillBytes: 64 * TestValueCodec.RecordSize,
                    pageRecordCount: 64,
                    cancellationCheckInterval: 8),
                root);
            try
            {
                InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                    staging.AddRange(Enumerable.Range(0, 128)
                        .Select(index => new TestValue(index, index))));
                Assert.Contains("spill-file limit", failure.Message, StringComparison.Ordinal);
                Assert.NotEmpty(Directory.EnumerateDirectories(root));
            }
            finally
            {
                staging.Dispose();
            }

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(FakeBuildBehavior.Throw)]
    [InlineData(FakeBuildBehavior.AdvanceRevision)]
    public void DetachedCommitNeverPublishesAFailedOrRacingBuild(
        FakeBuildBehavior behavior)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "midora-stage2-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using DetachedPagedEditStaging<TestValue> staging = new(
                new TestValueCodec(),
                new PagedEditResourceBudget(
                    maximumRecordCount: 100,
                    maximumResidentBytes: 1_024,
                    maximumSpillBytes: 1_024,
                    pageRecordCount: 64),
                root);
            staging.AddRange(Enumerable.Range(0, 10)
                .Select(index => new TestValue(index, index)));
            FakeRootOwner owner = new() { BuildBehavior = behavior };
            long expectedRevision = owner.Revision;

            Assert.Throws<InvalidOperationException>(() =>
                DetachedPagedEditTransaction.Commit(owner, expectedRevision, staging));

            Assert.Empty(owner.Root.Values);
            Assert.Equal(
                behavior == FakeBuildBehavior.AdvanceRevision
                    ? expectedRevision + 1
                    : expectedRevision,
                owner.Revision);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetachedStagingBudgetsItsDecodedAndEncodedWorkingBuffers()
    {
        const int pageRecordCount = 64;
        int decodedPageBytes = pageRecordCount * Unsafe.SizeOf<TestValue>();
        int encodedPageBytes = pageRecordCount * TestValueCodec.RecordSize;
        long required = Math.Max(
            decodedPageBytes * 2L,
            decodedPageBytes + encodedPageBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DetachedPagedEditStaging<TestValue>(
                new TestValueCodec(),
                new PagedEditResourceBudget(
                    maximumWorkingBytes: required - 1,
                    pageRecordCount: pageRecordCount),
                Path.GetTempPath()));

        using DetachedPagedEditStaging<TestValue> accepted = new(
            new TestValueCodec(),
            new PagedEditResourceBudget(
                maximumWorkingBytes: required,
                pageRecordCount: pageRecordCount),
            Path.GetTempPath());
        accepted.Add(new TestValue(1, 2));
    }

    [Fact]
    public void DetachedCommitCancellationLeavesTheFormalRootUntouched()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "midora-stage2-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using DetachedPagedEditStaging<TestValue> staging = new(
                new TestValueCodec(),
                new PagedEditResourceBudget(
                    maximumRecordCount: 100,
                    maximumResidentBytes: 1_024,
                    maximumSpillBytes: 1_024,
                    pageRecordCount: 64),
                root);
            staging.AddRange(Enumerable.Range(0, 10)
                .Select(index => new TestValue(index, index)));
            FakeRootOwner owner = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                DetachedPagedEditTransaction.Commit(
                    owner,
                    owner.Revision,
                    staging,
                    cancellation.Token));

            Assert.Empty(owner.Root.Values);
            Assert.Equal(1, owner.Revision);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private readonly record struct TestValue(int Id, long Tick);

    private readonly record struct SyntheticValue(MidoraId Id, long Tick);

    private sealed class SyntheticTimelineObjectSource(
        int count,
        int pageCapacity) : ITimelineObjectSource<SyntheticValue>
    {
        private int _pageRequests;
        private int _maximumRequestedPageSize;
        private int _idLookupRequests;

        public int Count { get; } = count;
        public long SourceRevision => 7;
        public int PageCapacity { get; } = pageCapacity;
        public int PageRequests => Volatile.Read(ref _pageRequests);
        public int MaximumRequestedPageSize => Volatile.Read(ref _maximumRequestedPageSize);
        public int IdLookupRequests => Volatile.Read(ref _idLookupRequests);

        public bool TryGetPageByOrdinal(
            int firstOrdinal,
            int requestedCount,
            out TimelineObjectPage<SyntheticValue> page)
        {
            _ = Interlocked.Increment(ref _pageRequests);
            int previousMaximum;
            do
            {
                previousMaximum = Volatile.Read(ref _maximumRequestedPageSize);
                if (previousMaximum >= requestedCount) break;
            }
            while (Interlocked.CompareExchange(
                ref _maximumRequestedPageSize,
                requestedCount,
                previousMaximum) != previousMaximum);
            if ((uint)firstOrdinal >= (uint)Count)
            {
                page = default;
                return false;
            }
            int actual = Math.Min(requestedCount, Count - firstOrdinal);
            SyntheticValue[] values = new SyntheticValue[actual];
            for (int index = 0; index < actual; index++)
            {
                int ordinal = checked(firstOrdinal + index);
                values[index] = new(new MidoraId(ordinal + 1), ordinal * 2L);
            }
            page = new(SourceRevision, firstOrdinal, values);
            return true;
        }

        public int FindOrdinalAtOrAfterTick(long tick) =>
            tick >= Count * 2L ? -1 : checked((int)Math.Max(0, (tick + 1) / 2));

        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            _ = Interlocked.Increment(ref _idLookupRequests);
            ordinal = checked((int)id.Value - 1);
            return (uint)ordinal < (uint)Count;
        }

        public IEnumerable<SyntheticValue> QueryTickRange(TimelineObjectRangeQuery query)
        {
            int first = FindOrdinalAtOrAfterTick(query.StartTick);
            if (first < 0) yield break;
            for (int ordinal = first; ordinal < Count; ordinal++)
            {
                long tick = ordinal * 2L;
                if (tick >= query.EndTick) yield break;
                yield return new(new MidoraId(ordinal + 1), tick);
            }
        }

        public void Prefetch(
            TimelineObjectRangeQuery query,
            CancellationToken cancellationToken = default)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class TestValueCodec : IFixedSizePagedEditCodec<TestValue>
    {
        public const int RecordSize = 12;
        public int RecordByteCount => RecordSize;

        public TestValue Read(ReadOnlySpan<byte> source) => new(
            BinaryPrimitives.ReadInt32LittleEndian(source),
            BinaryPrimitives.ReadInt64LittleEndian(source[4..]));

        public void Write(TestValue value, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, value.Id);
            BinaryPrimitives.WriteInt64LittleEndian(destination[4..], value.Tick);
        }
    }

    private sealed record TestRoot(ImmutableArray<TestValue> Values);

    public enum FakeBuildBehavior
    {
        Normal,
        Throw,
        AdvanceRevision
    }

    private sealed class FakeRootOwner : IPagedEditRootOwner<TestValue, TestRoot>
    {
        private TestRoot _root = new([]);

        public MidoraId OwnerId { get; } = new(700);
        public long Revision { get; private set; } = 1;
        public TestRoot Root => _root;
        public FakeBuildBehavior BuildBehavior { get; init; }

        public TestRoot BuildRoot(
            IEnumerable<DetachedPagedEditPage<TestValue>> pages,
            CancellationToken cancellationToken)
        {
            ImmutableArray<TestValue>.Builder result = ImmutableArray.CreateBuilder<TestValue>();
            foreach (DetachedPagedEditPage<TestValue> page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.AddRange(page.Values.Span);
            }
            if (BuildBehavior == FakeBuildBehavior.Throw)
                throw new InvalidOperationException("Injected detached build failure.");
            if (BuildBehavior == FakeBuildBehavior.AdvanceRevision)
                AdvanceRevisionWithoutChangingRoot();
            return new(result.ToImmutable());
        }

        public TestRoot CaptureRoot() => _root;

        public bool TrySwapRoot(
            TestRoot expectedRoot,
            TestRoot replacementRoot,
            out TimelineOwnerChangeSet changeSet)
        {
            if (!ReferenceEquals(_root, expectedRoot))
            {
                changeSet = null!;
                return false;
            }
            long previous = Revision;
            _root = replacementRoot;
            Revision++;
            changeSet = new(
                OwnerId,
                previous,
                Revision,
                [new(0, Math.Max(1, replacementRoot.Values.Length))],
                [new(0, 1, 0, 0)]);
            return true;
        }

        public void AdvanceRevisionWithoutChangingRoot() => Revision++;
    }
}
