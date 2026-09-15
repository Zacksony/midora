using System.Collections;
using System.Diagnostics;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ConductorPagedContentTests
{
    [Fact]
    public void SnapshotUsesTickThenStableIdOrderAndFrozenRootsStayIndependent()
    {
        MidoraProject project = new(480);
        var first = new ProjectMarker(project, 20, "first");
        var second = new ProjectMarker(project, 20, "second");
        var early = new ProjectMarker(project, 3, "early");
        project.Conductor.Markers.AddRange([second, early, first]);
        var captured = project.Conductor.Markers.CaptureQuerySnapshot();
        ConductorTrack clone = project.Conductor.CloneFrozen();
        Assert.Same(captured, clone.Markers.CaptureQuerySnapshot());
        Assert.Equal([early.Id, first.Id, second.Id], captured.Select(value => value.Id));
        clone.Markers[0] = early with { Tick = 30 };
        Assert.Equal([first.Id, second.Id, early.Id], clone.Markers.Select(value => value.Id));
        Assert.Equal(3, captured[0].Tick);
        Assert.Equal(20, captured.MaximumTick);
        Assert.Equal([first.Id, second.Id], captured.QueryTickRange(20, 21).Select(value => value.Id));
        Assert.Empty(captured.QueryTickRange(4, 20));
        Assert.True(captured.TryGetAtOrBeforeTick(20, out var held));
        Assert.Equal(second.Id, held.Id);
        Assert.True(captured.TryGetBeforeTick(20, out held));
        Assert.Equal(early.Id, held.Id);
    }

    [Fact]
    public void SparseEditMergesMovedRecordsAndPreservesSnapshotOrdinalsAndSameTickMarkers()
    {
        MidoraProject project = new(480);
        var markers = project.Conductor.Markers;
        markers.AddRange(Enumerable.Range(0, 1000).Select(index => new ProjectMarker(project, index * 10, $"M{index}")));
        var before = markers.CaptureQuerySnapshot();
        ProjectMarker moved = before[40] with { Tick = 9500 };
        ProjectMarker added = new(project, 15, "added");
        markers.AdoptEditedSnapshot(before, new ArraySource<TimelineValueEdit<ProjectMarker>>(
            [new(40, false, moved), new(300, true, null!)]), new ArraySource<ProjectMarker>([added]));
        var after = markers.CaptureQuerySnapshot();
        Assert.Equal(1000, after.Count);
        Assert.Equal(400, before[40].Tick);
        Assert.Equal(2, after.LowerBoundTick(15));
        Assert.Equal(added.Id, after[2].Id);
        Assert.Equal(2, after.QueryTickRange(9500, 9501).Count());
        Assert.DoesNotContain(after, value => value.Id == before[300].Id);
        Assert.Equal(after.OrderBy(value => value.Tick).ThenBy(value => value.Id), after);
        after.PrepareOrdinalLookup();
        Assert.True(after.TryFindOrdinalById(moved.Id, out int ordinal));
        Assert.Equal(moved, after.GetByOrdinal(ordinal));
    }

    [Fact]
    public void SparseEditFailureAndCancellationDoNotPublishPartialRoot()
    {
        MidoraProject project = new(480);
        var snapshot = project.Conductor.Tempos.CaptureQuerySnapshot();
        var invalid = new ArraySource<TimelineValueEdit<TempoChange>>([new(0, true, null!), new(99, true, null!)]);
        Assert.Throws<ArgumentException>(() => project.Conductor.Tempos.AdoptEditedSnapshot(snapshot, invalid));
        Assert.Same(snapshot, project.Conductor.Tempos.CaptureQuerySnapshot());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => project.Conductor.Tempos.AdoptEditedSnapshot(snapshot,
            new ArraySource<TimelineValueEdit<TempoChange>>([new(0, false, snapshot[0] with { BeatsPerMinute = 90 })]),
            cancellationToken: cancellation.Token));
        Assert.Same(snapshot, project.Conductor.Tempos.CaptureQuerySnapshot());
        Assert.Throws<ArgumentException>(() => project.Conductor.Tempos.AdoptSource(new ArraySource<TempoChange>(
            [new(new MidoraId(500), 9, 120), new(new MidoraId(501), 1, 120)])));
        Assert.Same(snapshot, project.Conductor.Tempos.CaptureQuerySnapshot());
    }

    [Fact]
    public void ColdMetadataAndSnapshotCaptureNeverReadExternalValuesAndSparsePatchIsLocal()
    {
        const int count = 100_000;
        var source = new GeneratedTempoSource(count);
        var tempos = new ConductorCollection<TempoChange>();
        tempos.AdoptSource(source);
        var before = tempos.CaptureQuerySnapshot();
        Assert.True(before.UsesExternalStorage);
        source.Evict();
        source.PageLoads = 0;
        using (TimelineValueReadScope.EnterCacheOnly())
        {
            _ = before.GetRangeFingerprint(499_990, 500_010);
            _ = before.GetStepRangeFingerprint(499_991, 499_999);
            Assert.Equal((count - 1L) * 10, before.MaximumTick);
            var cloned = new ConductorCollection<TempoChange>();
            cloned.AdoptSnapshot(before);
            Assert.Same(before, cloned.CaptureQuerySnapshot());
            Assert.False(before.TryGetSortedCached(50_000, out _));
        }
        Assert.Equal(0, source.PageLoads);
        before.PrepareOrdinalLookup();
        Assert.Equal(0, source.PageLoads);
        Assert.True(before.TryFindOrdinalById(new MidoraId(50_100), out int foundOrdinal));
        Assert.Equal(50_000, foundOrdinal);
        Assert.True(source.PageLoads <= 32, $"A single cold ID lookup loaded {source.PageLoads} pages.");
        var replacement = before[50_000] with { BeatsPerMinute = 75 };
        source.PageLoads = 0;
        tempos.AdoptEditedSnapshot(before,
            new ArraySource<TimelineValueEdit<TempoChange>>([new(50_000, false, replacement)]));
        var after = tempos.CaptureQuerySnapshot();
        Assert.True(source.PageLoads < 256, $"A local patch loaded {source.PageLoads} pages.");
        Assert.NotEqual(before.ContentFingerprint, after.ContentFingerprint);
        Assert.Equal(before.GetStepRangeFingerprint(900_001, 900_005), after.GetStepRangeFingerprint(900_001, 900_005));
        Assert.NotEqual(before.GetStepRangeFingerprint(500_001, 500_005), after.GetStepRangeFingerprint(500_001, 500_005));
        Assert.Equal(120m, before[50_000].BeatsPerMinute);
        Assert.Equal(75m, after[50_000].BeatsPerMinute);
    }

    [Fact]
    public void CompilationCaptureSharesAllConductorRootsAndCopiesOnlyMutableEndMarker()
    {
        MidoraProject project = new(480);
        project.Conductor.Markers.AddRange(Enumerable.Range(0, 10_000).Select(index => new ProjectMarker(project, index, "M")));
        project.Conductor.EndMarker = new(project, 20_000);
        MidoraProject mirror = ProjectCompilationSnapshot.Create(project);
        var markers = project.Conductor.Markers.CaptureQuerySnapshot();
        ProjectChangeSet changes = new() { AffectsConductor = true };
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var capture = ProjectCompilationSnapshot.CaptureRevision(mirror, project, changes);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 256_000, $"Conductor capture allocated {allocated} bytes.");
        Assert.Same(markers, capture.Conductor!.Markers);
        project.Conductor.EndMarker.Tick = 30_000;
        var materialized = ProjectCompilationSnapshot.MaterializeRevision(capture);
        Assert.Equal(20_000, materialized.Conductor.EndMarkerTick);
        Assert.Same(markers, materialized.Conductor.Markers.CaptureQuerySnapshot());
    }

    [Fact]
    public void DenseOrdinalPreparationReusesIndexedSourceWithoutReadingMusicalValues()
    {
        var source = new GeneratedTempoSource(100_000);
        var tempos = new ConductorCollection<TempoChange>();
        tempos.AdoptSource(source);
        var snapshot = tempos.CaptureQuerySnapshot();
        source.Evict();
        source.PageLoads = 0;
        var builder = new CountingOrdinalBuilder();
        snapshot.PrepareOrdinalLookup(builder);
        for (int index = 0; index < source.Count; index += 13)
        {
            Assert.True(snapshot.TryFindOrdinalById(new MidoraId(index + 100L), out int ordinal));
            Assert.Equal(index, ordinal);
        }
        Assert.Equal(0, builder.Builds);
        Assert.Equal(0, source.PageLoads);
    }

    [Fact]
    public void DenseEditedOrdinalPreparationIsRevisionLocalAndCancellationSafe()
    {
        var source = new GeneratedTempoSource(10_000);
        var tempos = new ConductorCollection<TempoChange>();
        tempos.AdoptSource(source);
        var before = tempos.CaptureQuerySnapshot();
        TempoChange moved = before[3000] with { Tick = 90_001 };
        tempos.AdoptEditedSnapshot(before, new ArraySource<TimelineValueEdit<TempoChange>>(
            [new(3000, false, moved), new(5000, true, null!)]));
        var after = tempos.CaptureQuerySnapshot();
        var builder = new CountingOrdinalBuilder();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => after.PrepareOrdinalLookup(builder, cancellation.Token));
        Assert.Equal(0, builder.Builds);
        after.PrepareOrdinalLookup(builder);
        Assert.Equal(1, builder.Builds);
        Assert.Equal(after.Count, builder.AddressCount);
        Assert.Equal(1, builder.Retains);
        source.Evict();
        source.PageLoads = 0;
        Assert.True(after.TryFindOrdinalById(moved.Id, out int movedOrdinal));
        Assert.Equal(8999, movedOrdinal);
        Assert.False(after.TryFindOrdinalById(before[5000].Id, out _));
        source.PageLoads = 0;
        for (int index = 0; index < 3000; index += 7)
        {
            Assert.True(after.TryFindOrdinalById(new MidoraId(index + 100L), out int ordinal));
            Assert.Equal(index, ordinal);
        }
        Assert.Equal(0, source.PageLoads);
        Assert.True(before.TryFindOrdinalById(moved.Id, out int oldOrdinal));
        Assert.Equal(3000, oldOrdinal);
        after.PrepareOrdinalLookup(builder);
        Assert.Equal(1, builder.Builds);
    }

    [Fact]
    public void PublishedImmutableRecordReadsDoNotBuildMutableFacadeDirectories()
    {
        MidoraProject project = new(480);
        project.Conductor.Markers.AddRange(Enumerable.Range(0, 10_000)
            .Select(index => new ProjectMarker(project, index, "M")));
        _ = project.Conductor.Markers.CaptureQuerySnapshot();
        _ = project.Conductor.Markers[0];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long ticks = 0;
        for (int index = 0; index < project.Conductor.Markers.Count; index++) ticks += project.Conductor.Markers[index].Tick;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(49_995_000, ticks);
        Assert.True(allocated < 16_384, $"Immutable indexed reads allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void TimeSignatureMapReusesUnchangedRootButInvalidatesChangedRootAndTpqn()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new(project, 1000, 3, 4));
        var original = ProjectTimeSignatureMap.GetOrCreate(project);
        Assert.True(ProjectTimeSignatureMap.TryGetCached(project, out var cached));
        Assert.Same(original, cached);
        var clone = project.Conductor.CloneFrozen();
        Assert.Same(original, ProjectTimeSignatureMap.GetOrCreate(480, clone.TimeSignatures.CaptureQuerySnapshot()));
        project.Conductor.Tempos[0] = project.Conductor.Tempos[0] with { BeatsPerMinute = 99 };
        Assert.Same(original, ProjectTimeSignatureMap.GetOrCreate(project));
        Assert.Equal(new ProjectMusicalPosition(2, 1, 0), original.GetPosition(1000));
        clone.TimeSignatures[1] = clone.TimeSignatures[1] with { Tick = 960 };
        var changed = ProjectTimeSignatureMap.GetOrCreate(480, clone.TimeSignatures.CaptureQuerySnapshot());
        Assert.NotSame(original, changed);
        Assert.Equal(960, changed.GetTick(new(2, 1, 0)));
        Assert.Equal(1000, original.GetTick(new(2, 1, 0)));
        Assert.NotSame(original, ProjectTimeSignatureMap.GetOrCreate(960, project.Conductor.TimeSignatures.CaptureQuerySnapshot()));
    }

    [Fact]
    public void MillionConductorRecordsOptInBenchmark()
    {
        if (Environment.GetEnvironmentVariable("MIDORA_RUN_CONDUCTOR_MILLION_BENCHMARK") != "1") return;
        Benchmark("Tempo", index => new TempoChange(new MidoraId(index + 100L), index * 10L, 120m));
        Benchmark("TimeSignature", index => new TimeSignatureChange(new MidoraId(index + 100L), index * 10L, 4, 4));
        Benchmark("KeySignature", index => new KeySignatureChange(new MidoraId(index + 100L), index * 10L, index % 15 - 7, false));
        Benchmark("Marker", index => new ProjectMarker(new MidoraId(index + 100L), index * 10L, "Marker 日本語"));

        static void Benchmark<T>(string name, Func<int, T> makeValue) where T : class
        {
            const int count = 1_000_000;
            var source = new GeneratedSource<T>(count, makeValue);
            var collection = new ConductorCollection<T>();
            var clock = Stopwatch.StartNew();
            collection.AdoptSource(source);
            var before = collection.CaptureQuerySnapshot();
            double prepareMs = clock.Elapsed.TotalMilliseconds;
            source.Evict();
            source.PageLoads = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart();
            var clone = new ConductorCollection<T>();
            clone.AdoptSnapshot(before);
            Assert.Same(before, clone.CaptureQuerySnapshot());
            double captureMs = clock.Elapsed.TotalMilliseconds;
            long captureAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(0, source.PageLoads);
            Assert.True(captureAllocated < 65_536);
            clock.Restart();
            clone.AdoptEditedSnapshot(before, new ArraySource<TimelineValueEdit<T>>([new(500_000, true, null!)]));
            var edited = clone.CaptureQuerySnapshot();
            double editMs = clock.Elapsed.TotalMilliseconds;
            int editPageLoads = source.PageLoads;
            Assert.Equal(count - 1, edited.Count);
            Assert.Equal(count, before.Count);
            Assert.True(editPageLoads < 256);
            clock.Restart();
            clone.AdoptSnapshot(before);
            double undoMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            clone.AdoptSnapshot(edited);
            double redoMs = clock.Elapsed.TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            Console.WriteLine($"Conductor {name} {count:N0}: prepare={prepareMs:F2}ms, capture={captureMs:F3}ms/{captureAllocated}B, "
                + $"single delete={editMs:F2}ms/{editPageLoads} page loads, root Undo={undoMs:F3}ms, root Redo={redoMs:F3}ms, "
                + $"sampled WS={process.WorkingSet64 / 1048576d:F1}MiB; process peak WS={process.PeakWorkingSet64 / 1048576d:F1}MiB; "
                + $"managed={GC.GetTotalMemory(false) / 1048576d:F1}MiB.");
        }
    }

    private sealed class ArraySource<T>(T[] values) : IImmutableTimelineValueSource<T>
    {
        public int Count => values.Length;
        public int PageCapacity => Math.Max(1, Count);
        public T this[int index] => values[index];
        public ReadOnlyMemory<T> ReadPage(int pageIndex) => pageIndex == 0 ? values : ReadOnlyMemory<T>.Empty;
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> page) { page = ReadPage(pageIndex); return true; }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingOrdinalBuilder : IImmutableTimelineOrdinalIndexBuilder
    {
        public int Builds { get; private set; }
        public int AddressCount { get; private set; }
        public int Retains { get; private set; }
        public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token)
        {
            Builds++;
            Dictionary<MidoraId, int> ordinals = [];
            foreach (TimelineIdOrdinal value in values)
            {
                token.ThrowIfCancellationRequested();
                ordinals.Add(value.Id, value.Ordinal);
                AddressCount++;
            }
            return new Index(ordinals, () => Retains++);
        }
        private sealed class Index(Dictionary<MidoraId, int> ordinals, Action retained) : IImmutableTimelineIdIndex
        {
            public bool TryFindOrdinalById(MidoraId id, out int ordinal) => ordinals.TryGetValue(id, out ordinal);
            public void RetainForSourceLifetime() => retained();
        }
    }

    private sealed class GeneratedTempoSource(int count) : GeneratedSource<TempoChange>(count,
        static index => new(new MidoraId(index + 100L), index * 10L, 120m)) { }

    private class GeneratedSource<T>(int count, Func<int, T> createValue) : IIndexedImmutableTimelineValueSource<T>
    {
        private int _cachedPage = -1;
        private T[] _values = [];
        public int PageLoads { get; set; }
        public int Count => count;
        public int PageCapacity => 128;
        public T this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public void Evict() { _cachedPage = -1; _values = []; }
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            if (_cachedPage != pageIndex)
            {
                TimelineValueReadScope.ThrowIfReadWouldBlock();
                int first = checked(pageIndex * PageCapacity);
                _values = new T[Math.Min(PageCapacity, Count - first)];
                for (int offset = 0; offset < _values.Length; offset++)
                    _values[offset] = createValue(first + offset);
                _cachedPage = pageIndex;
                PageLoads++;
            }
            return _values;
        }
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
        { values = _cachedPage == pageIndex ? _values : default; return _cachedPage == pageIndex; }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        { long candidate = id.Value - 100; ordinal = candidate >= 0 && candidate < Count ? (int)candidate : -1; return ordinal >= 0; }
        public IEnumerator<T> GetEnumerator()
        { for (int index = 0; index < Count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
