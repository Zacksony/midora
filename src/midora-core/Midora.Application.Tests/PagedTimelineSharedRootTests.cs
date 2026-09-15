using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PagedTimelineSharedRootTests
{
    [Fact]
    public void StructuralAddressIndexesSpillCompactAndDoNotPinSupersededMusicalValues()
    {
        using MidoraProject project = new(480);
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var (result, priorValues) = ReplaceWithDeletions(project, scope);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(priorValues, value => Assert.False(value.IsAlive));
        var snapshot = result.Notes.CreateQuerySnapshot();
        Assert.Equal(8_180, snapshot.Count);
        Assert.Equal(12, snapshot.GetByOrdinal(0).Velocity);
        for (int ordinal = 0; ordinal < snapshot.Count; ordinal += 97)
        {
            Assert.True(snapshot.TryFindOrdinalById(snapshot.GetByOrdinal(ordinal).Id, out int resolved));
            Assert.Equal(ordinal, resolved);
        }
        GC.KeepAlive(result);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Segment Current, List<WeakReference> Previous) ReplaceWithDeletions(MidoraProject project, BulkEditPreparationContext scope)
    {
        Segment current = new(project);
        current.Notes.AdoptSource(project, new GeneratedNoteSource(8_192));
        List<WeakReference> previous = [];
        for (int iteration = 1; iteration <= 12; iteration++)
        {
            var snapshot = current.Notes.CreateQuerySnapshot();
            var store = new BoundedEditRecordStore<TimelineValueEdit<LogicalNoteSnapshotValue>>(scope.Resources);
            int ordinal = 0;
            foreach (var value in snapshot.EnumerateAll())
                store.Add(new(ordinal, ordinal++ == 0, value with { Velocity = iteration }));
            store.Seal(); store.SpillResidentPages();
            var edits = new BoundedTimelineEditValueSource<LogicalNoteSnapshotValue>(store);
            if (iteration != 12) previous.Add(new(edits));
            Segment next = new(project);
            next.Notes.AdoptEditedSnapshot(project, snapshot, edits);
            current = next;
        }
        return (current, previous);
    }

    [Fact]
    public void FullValueReplacementDoesNotRetainThePreviousValueSourceThroughItsIdIndex()
    {
        using MidoraProject project = new(480);
        var (current, previousSource) = ReplaceWholeSource(project);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(previousSource.IsAlive);
        Assert.Equal(71, current.Notes[4_321].Velocity);
        Assert.True(current.Notes.CreateQuerySnapshot().TryFindOrdinalById(new MidoraId(4_322), out int ordinal));
        Assert.Equal(4_321, ordinal);
        GC.KeepAlive(current);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Segment Current, WeakReference OldSource) ReplaceWholeSource(MidoraProject project)
    {
        GeneratedNoteSource source = new(8_192);
        Segment old = new(project);
        old.Notes.AdoptSource(project, source);
        var snapshot = old.Notes.CreateQuerySnapshot();
        var changes = snapshot.EnumerateAll().Select((v, i) => new TimelineValueEdit<LogicalNoteSnapshotValue>(
            i, false, v with { Velocity = 71 })).ToArray();
        Segment current = new(project);
        current.Notes.AdoptEditedSnapshot(project, snapshot, new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>(changes));
        return (current, new WeakReference(source));
    }

    [Fact]
    public void SplicedRootPreservesFormalFragmentOrderAndIdsAcrossDeletedSources()
    {
        using MidoraProject project = new(480);
        Segment segment = new(project);
        segment.Notes.AddRange(Enumerable.Range(0, 200).Select(i => new LogicalNote(project)
        { StartTick = i * 10, LengthTicks = 5, Note = 60, Velocity = 90 }));
        _ = segment.Notes.CreateQuerySnapshot();
        segment.Notes[150].StartTick = 40_000;
        var source = segment.Notes.CreateQuerySnapshot();
        var values = new LogicalNoteSnapshotValue[]
        {
            source.GetByOrdinal(20) with { LengthTicks = 1 },
            new(project.AllocateStableId(), 201, 1, 60, 90),
            new(project.AllocateStableId(), 202, 3, 60, 90),
            new(project.AllocateStableId(), 730, 2, 60, 90),
            new(project.AllocateStableId(), 732, 3, 60, 90)
        };
        using var resourcesScope = BulkEditPreparationContext.Enter(project: project);
        var valueStore = new BoundedEditRecordStore<LogicalNoteSnapshotValue>(resourcesScope.Resources);
        valueStore.AddRange(values); valueStore.Seal();
        var indexed = new BoundedIndexedTimelineValueSource<LogicalNoteSnapshotValue>(new(valueStore), static v => v.Id, resourcesScope.Resources, default);
        TimelineValueSplice[] splices = [new(0, 0, 0), new(20, 0, 3), new(56, 3, 0), new(73, 3, 2), new(199, 5, 0)];
        Segment result = new(project);
        result.Notes.AdoptSplicedSnapshot(project, source, new ArraySource<TimelineValueSplice>(splices), indexed);
        var expected = source.EnumerateAll().SelectMany((v, i) => splices.FirstOrDefault(s => s.Ordinal == i) is var s
            && splices.Any(s => s.Ordinal == i) ? values.Skip(s.FirstValue).Take(s.Count) : [v]).ToArray();
        var actual = result.Notes.CreateQuerySnapshot();
        Assert.Equal(expected, actual.EnumerateAll());
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(actual.TryFindOrdinalById(expected[i].Id, out int ordinal));
            Assert.Equal(i, ordinal);
        }
        foreach (int deleted in new[] { 0, 56, 73, 199 })
            Assert.False(actual.TryFindOrdinalById(source.GetByOrdinal(deleted).Id, out _));
        Assert.Equal(expected.Where(v => v.StartTick < 500 && v.StartTick + v.LengthTicks > 0).Select(v => v.Id).Order(),
            actual.QueryValues(0, 500).Select(v => v.Id).Order());
        result.Notes[0].Velocity = 5;
        Assert.Equal(90, segment.Notes[1].Velocity);
    }
    [Fact]
    public void EditedRootSharesUnchangedLeavesAndKeepsFormalIdsAndRangeQueriesExact()
    {
        using MidoraProject project = new(480);
        Segment source = new(project) { LengthTicks = 50_000 };
        source.Notes.AddRange(Enumerable.Range(0, 10_000).Select(i => new LogicalNote(project)
        { StartTick = i * 4, LengthTicks = 3, Note = i % 128, Velocity = 100 }));
        _ = source.Notes.CreateQuerySnapshot();
        source.Notes[9_999].StartTick = 2; // Existing value overlay outside the next edit.
        LogicalNoteQuerySnapshot old = source.Notes.CreateQuerySnapshot();
        LogicalNoteSnapshotValue[] original = old.EnumerateAll().ToArray();
        TimelineValueEdit<LogicalNoteSnapshotValue>[] edits = Enumerable.Range(0, 4_321)
            .Select(i => new TimelineValueEdit<LogicalNoteSnapshotValue>(i, i % 3 == 0,
                original[i] with { StartTick = 25_000 + i, Velocity = 70 })).ToArray();
        LogicalNoteSnapshotValue[] additions = Enumerable.Range(0, 19).Select(i =>
            new LogicalNoteSnapshotValue(project.AllocateStableId(), i, 3, 60, 55)).ToArray();
        Segment result = new(project);
        result.Notes.AdoptEditedSnapshot(project, old, new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>(edits),
            new ArraySource<LogicalNoteSnapshotValue>(additions));
        LogicalNoteSnapshotValue[] expected = original.Select((value, ordinal) => ordinal < edits.Length
                ? edits[ordinal].IsDeleted ? (LogicalNoteSnapshotValue?)null : edits[ordinal].Replacement : value)
            .Where(static value => value.HasValue).Select(static value => value!.Value).Concat(additions).ToArray();
        LogicalNoteQuerySnapshot actual = result.Notes.CreateQuerySnapshot();
        Assert.Equal(expected, actual.EnumerateAll());
        Assert.Equal(original, old.EnumerateAll());
        for (int i = 0; i < expected.Length; i += 7)
        {
            Assert.True(actual.TryFindOrdinalById(expected[i].Id, out int ordinal));
            Assert.Equal(i, ordinal);
            Assert.True(result.Notes.TryGetById(expected[i].Id, out LogicalNote? note));
            Assert.Equal(expected[i].StartTick, note!.StartTick);
        }
        foreach (var value in edits.Where(static value => value.IsDeleted))
            Assert.False(result.Notes.TryGetById(original[value.Ordinal].Id, out _));
        Assert.Equal(expected.Where(static value => value.StartTick < 10 && value.StartTick + value.LengthTicks > 0)
                .Select(static value => value.Id).Order(),
            actual.QueryValues(0, 10).Select(static value => value.Id).Order());
        Assert.Equal(expected.Where(static value => value.StartTick < 30_000 && value.StartTick + value.LengthTicks > 25_000)
                .Select(static value => value.Id).Order(),
            actual.QueryValues(25_000, 30_000).Select(static value => value.Id).Order());
        result.Notes[0].Velocity = 12;
        Assert.Equal(100, source.Notes[1].Velocity);
        Assert.Equal(12, result.Notes[0].Velocity);
    }

    [Fact]
    public void EditedRootRejectsUnorderedRepeatedAndOutOfRangeOrdinalsAtomically()
    {
        using MidoraProject project = new(480);
        Segment source = new(project);
        source.Notes.AddRange(Enumerable.Range(0, 10).Select(i => new LogicalNote(project)
        { StartTick = i, LengthTicks = 1 }));
        var snapshot = source.Notes.CreateQuerySnapshot();
        foreach (int[] ordinals in new int[][] { [3, 2], [4, 4], [-1], [10] })
        {
            Segment target = new(project);
            var changes = ordinals.Select(i => new TimelineValueEdit<LogicalNoteSnapshotValue>(i, true, default)).ToArray();
            Assert.Throws<InvalidOperationException>(() => target.Notes.AdoptEditedSnapshot(
                project, snapshot, new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>(changes)));
            Assert.Empty(target.Notes);
        }
        Assert.Equal(10, source.Notes.Count);
    }

    [Fact]
    public void SourceBackedRootDoesNotKeepDecodedPagesAndRemainsMutableAndCloneable()
    {
        using MidoraProject project = new(480);
        GeneratedNoteSource source = new(32_768);
        Segment segment = new(project) { LengthTicks = 200_000 };
        segment.Notes.AdoptSource(project, source);
        LogicalNoteQuerySnapshot first = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(32_768, first.Count);
        Assert.Equal(new MidoraId(20_000), segment.Notes[19_999].Id);
        Assert.True(first.TryFindOrdinalById(new MidoraId(20_000), out int ordinal));
        Assert.Equal(19_999, ordinal);
        int reads = source.PageReads;
        Segment clone = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, segment);
        Assert.Equal(reads, source.PageReads);
        clone.Notes[19_999].Note = 90;
        Assert.Equal(19_999 % 128, segment.Notes[19_999].Note);
        Assert.Equal(90, Assert.Single(clone.Notes.CreateQuerySnapshot().QueryValues(
            79_996, 79_997, 90, 90)).Note);
        Assert.DoesNotContain(first.QueryValues(79_996, 79_997, 90, 90), static value => value.Id.Value == 20_000);
        source.ClearDecodedPage();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(source.LastDecodedPage!.TryGetTarget(out _));
        Assert.Equal(new MidoraId(30_001), clone.Notes[30_000].Id);
    }

    [Fact]
    public void CanceledSourceAdoptionLeavesTheTargetEmpty()
    {
        using MidoraProject project = new(480);
        Segment target = new(project);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => target.Notes.AdoptSource(
            project, new GeneratedNoteSource(10_000), cancellation.Token));
        Assert.Empty(target.Notes);
        target.Notes.Add(new LogicalNote(project) { LengthTicks = 1 });
        Assert.Single(target.Notes);
    }

    [Fact]
    public void AdoptedLogicalRootKeepsSnapshotsAndMutableReferencesIsolatedThroughStructuralEdits()
    {
        using MidoraProject project = new(480);
        Segment original = new(project) { LengthTicks = 100_000 };
        original.Notes.AddRange(Enumerable.Range(0, 10_000).Select(i => new LogicalNote(project)
        { StartTick = i * 4, LengthTicks = 3, Note = i % 128, Velocity = 90 }));
        LogicalNoteQuerySnapshot before = original.Notes.CreateQuerySnapshot();
        Segment copy = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, original);
        Assert.Equal(before.ContentFingerprint, copy.Notes.CreateQuerySnapshot().ContentFingerprint);
        LogicalNote held = copy.Notes[5000];
        Assert.Same(held, copy.Notes[5000]);
        Assert.NotSame(original.Notes[5000], held);
        copy.Notes.Insert(0, new LogicalNote(project) { LengthTicks = 1 });
        held.Velocity = 30;
        Assert.Equal(30, copy.Notes[5001].Velocity);
        Assert.Equal(90, original.Notes[5000].Velocity);
        Assert.Equal(90, before.EnumerateAll().ElementAt(5000).Velocity);
        Assert.True(copy.Notes.Remove(held));
        held.Velocity = 45;
        Assert.False(copy.Notes.TryGetById(held.Id, out _));
        copy.Notes.Add(held);
        held.StartTick = 70_000;
        Assert.Equal(held.Id, Assert.Single(copy.Notes.CreateQuerySnapshot().QueryValues(70_000, 70_001)).Id);
        Assert.Equal(10_000, original.Notes.Count);
    }

    [Fact]
    public void AdoptedCurveAndTemplateRootsKeepReplacementAndOwnerMappingIndependent()
    {
        using MidoraProject project = new(480);
        SubVoice source = new(project);
        source.Events.Add(TemplateEvent.Note(project, 10, 20, 60, 100));
        source.Events.Add(TemplateEvent.ControlChange(project, 15, 11, 80));
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(11) };
        curve.Points.Add(new CurvePoint(project, 10, 0.3));
        source.Curves.Add(curve);
        SubVoice copy = ProjectTimelineOwnerRootClone.CloneSubVoice(project, source);
        copy.Events[0].Number = 72;
        copy.Events[0].ValueMappings.IsEnabled = false;
        CurvePoint oldPoint = copy.Curves[0].Points[0];
        CurvePoint replacement = oldPoint with { Value = 0.9 };
        copy.Curves[0].Points.ReplaceRange([oldPoint], [replacement]);
        Assert.Equal(60, source.Events[0].Number);
        Assert.True(source.Events[0].ValueMappings.IsEnabled);
        Assert.Equal(0.3, source.Curves[0].Points[0].Value);
        Assert.Equal(0.9, copy.Curves[0].Points.CreateQuerySnapshot().EnumerateAll().Single().Value);
        Assert.True(copy.Curves[0].Points.Remove(replacement));
    }

    [Fact]
    public void WarmMillionNoteCloneSharesPagesAndAllocatesOnlyShellState()
    {
        using MidoraProject project = new(480);
        Segment original = new(project) { LengthTicks = 4_000_000 };
        using (original.Notes.BeginBatchChange())
        {
            for (int i = 0; i < 1_000_000; i++)
                original.Notes.Add(new LogicalNote(project)
                { StartTick = i * 4L, LengthTicks = 3, Note = i % 128, Velocity = 100 });
        }
        LogicalNoteQuerySnapshot snapshot = original.Notes.CreateQuerySnapshot();
        _ = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, original);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Segment clone = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, original);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(1_000_000, clone.Notes.Count);
        Assert.Equal(snapshot.ContentFingerprint, clone.Notes.CreateQuerySnapshot().ContentFingerprint);
        Assert.InRange(allocated, 0, 64 * 1024);
        clone.Notes[999_999].Velocity = 27;
        Assert.Equal(100, original.Notes[999_999].Velocity);
        Assert.Equal(27, clone.Notes[999_999].Velocity);
    }

    [Fact]
    public void BatchedMutableFacadeUpdatesPublishOneGenerationAndRetainOldRoot()
    {
        using MidoraProject project = new(480);
        Segment source = new(project) { LengthTicks = 20_000 };
        source.Notes.AddRange(Enumerable.Range(0, 9_000).Select(i => new LogicalNote(project)
        { StartTick = i * 2, LengthTicks = 1, Note = 60, Velocity = 100 }));
        Segment copy = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, source);
        LogicalNoteQuerySnapshot old = copy.Notes.CreateQuerySnapshot();
        long generation = copy.Notes.Generation;
        using (copy.Notes.BeginBatchChange())
        {
            foreach (LogicalNote note in copy.Notes) note.Velocity = 40;
        }
        Assert.Equal(generation + 1, copy.Notes.Generation);
        Assert.All(copy.Notes.CreateQuerySnapshot().EnumerateAll(), value => Assert.Equal(40, value.Velocity));
        Assert.All(old.EnumerateAll(), value => Assert.Equal(100, value.Velocity));
    }

    private sealed class GeneratedNoteSource(int count) : IImmutableTimelineValueSource<LogicalNoteSnapshotValue>
    {
        private LogicalNoteSnapshotValue[]? _decoded;
        private int _decodedPage = -1;
        public int Count => count;
        public int PageCapacity => 4096;
        public int PageReads { get; private set; }
        public WeakReference<LogicalNoteSnapshotValue[]>? LastDecodedPage { get; private set; }
        public LogicalNoteSnapshotValue this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public ReadOnlyMemory<LogicalNoteSnapshotValue> ReadPage(int pageIndex)
        {
            if (pageIndex != _decodedPage)
            {
                int first = pageIndex * PageCapacity;
                _decoded = new LogicalNoteSnapshotValue[Math.Min(PageCapacity, Count - first)];
                for (int i = 0; i < _decoded.Length; i++)
                {
                    int ordinal = first + i;
                    _decoded[i] = new(new MidoraId(ordinal + 1), ordinal * 4L, 3, ordinal % 128, 100);
                }
                _decodedPage = pageIndex;
                LastDecodedPage = new(_decoded);
                PageReads++;
            }
            return _decoded!;
        }
        public void ClearDecodedPage() { _decoded = null; _decodedPage = -1; }
        public IEnumerator<LogicalNoteSnapshotValue> GetEnumerator()
        { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ArraySource<T>(T[] values) : IImmutableTimelineValueSource<T>
    {
        public int Count => values.Length;
        public int PageCapacity => 64;
        public T this[int index] => values[index];
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            int first = pageIndex * PageCapacity;
            return values.AsMemory(first, Math.Min(PageCapacity, values.Length - first));
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
