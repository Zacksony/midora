using System.Reflection;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiQueryRegressionTests
{
    [Fact]
    public void VelocityHolesUseCachedOrdinalMasksEvenWhenTheIdIndexIsCold()
    {
        using var fixture = new Fixture(70_000);
        var selected = fixture.Original.QueryValues(0, long.MaxValue).Select(static v => v.Id).ToHashSet();
        using var context = BulkEditPreparationContext.Enter(project: fixture.Project);
        var edit = ProjectDomainEditCommands.HumanizeDirectMidiNotes(fixture.Segment.Id, selected,
            new(TimelineHumanizeField.Disabled, TimelineHumanizeField.Disabled, TimelineHumanizeField.Add(-1, 1), 123))
            .Prepare(fixture.Project);
        edit.Apply(fixture.Project);
        var source = Assert.IsType<BoundedDirectMidiNoteSource>(fixture.Current.Notes.PagedSource);
        Assert.InRange(source.Count, 1, selected.Count - 1); // Genuine unchanged holes, not a full-page replacement.
        Assert.Equal(selected.Count, source.NoteCount);
        const long start = 120_000, end = 122_048;
        var expected = source.QueryNotes(start, end, 52, 76).OrderBy(static v => v.Id).ToArray();
        source.PrefetchNotes(start, end, 52, 76, default);
        Evict(source, "_ids");
        long misses = fixture.Pack.PageCacheMissCount;
        List<DirectMidiNoteValue> actual = [];
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.True(source.TryQueryCachedNotes(start, end, 52, 76, actual));
        Assert.Equal(misses, fixture.Pack.PageCacheMissCount);
        Assert.Equal(expected, actual.OrderBy(static v => v.Id));

        // A selection/exclusion union must retain the known ordinal portion;
        // unknown IDs are checked only in the additional selection set.
        HashSet<MidoraId> excluded = expected.Where(static (_, i) => i % 3 == 0).Select(static v => v.Id).ToHashSet();
        actual.Clear();
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.True(source.TryQueryCachedNotesExcluding(start, end, 52, 76, excluded, actual));
        Assert.Equal(expected.Where(v => !excluded.Contains(v.Id)), actual.OrderBy(static v => v.Id));
        edit.Undo(fixture.Project);
        Assert.Same(fixture.Segment, fixture.Current);
        Assert.Equal(70_000, fixture.Current.Notes.Count);
    }

    [Fact]
    public void ColdOrdinalMaskIsPendingWithoutPartialOutputOrIoAndPrefetchMakesItReady()
    {
        using var fixture = new Fixture(70_000);
        var selected = fixture.Original.QueryValues(0, long.MaxValue).Select(static v => v.Id).ToHashSet();
        using var context = BulkEditPreparationContext.Enter(project: fixture.Project);
        var edit = ProjectDomainEditCommands.HumanizeDirectMidiNotes(fixture.Segment.Id, selected,
            new(TimelineHumanizeField.Disabled, TimelineHumanizeField.Disabled, TimelineHumanizeField.Add(-1, 1), 321))
            .Prepare(fixture.Project);
        edit.Apply(fixture.Project);
        var source = Assert.IsType<BoundedDirectMidiNoteSource>(fixture.Current.Notes.PagedSource);
        source.PrefetchNotes(0, 280_000, 0, 127, default);
        var expected = source.QueryNotes(0, 280_000).OrderBy(static v => v.Id).ToArray();
        Evict(source, "_ordinals");
        long misses = fixture.Pack.PageCacheMissCount;
        DirectMidiNoteValue sentinel = new(new(9_000_000), 0, 1, 60, 1, 0, 0, 0);
        List<DirectMidiNoteValue> actual = [sentinel];
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.False(source.TryQueryCachedNotes(0, 280_000, 0, 127, actual));
        Assert.Equal([sentinel], actual);
        Assert.Equal(misses, fixture.Pack.PageCacheMissCount);
        source.PrefetchNotes(0, 280_000, 0, 127, default);
        actual.Clear();
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.True(source.TryQueryCachedNotes(0, 280_000, 0, 127, actual));
        Assert.Equal(expected, actual.OrderBy(static v => v.Id));
    }

    [Fact]
    public void QuantizeTombstoneRankSelectAndSelectionMatchEverySurvivorThroughUndoRedo()
    {
        using var fixture = new Fixture(18_000, sameKey: true);
        DirectMidiNoteValue[] original = fixture.Original.QueryValues(0, long.MaxValue).ToArray();
        var selected = original.Select(static v => v.Id).ToHashSet();
        using var context = BulkEditPreparationContext.Enter(project: fixture.Project);
        var command = ProjectDomainEditCommands.QuantizeDirectMidiNotes(fixture.Segment.Id, selected,
            new(TimelineQuantizeGrid.FromCustomTicks(16)));
        var edit = command.Prepare(fixture.Project);
        edit.Apply(fixture.Project);
        MidiSegment applied = fixture.Current;
        var source = Assert.IsType<BoundedDirectMidiNoteSource>(applied.Notes.PagedSource);
        DirectMidiNoteValue[] expected = original
            .Select(v => v with { StartTick = (v.StartTick + 7) / 16 * 16 })
            .GroupBy(static v => (v.StartTick, v.Key)).Select(static group => group.First()).ToArray();
        Assert.Equal(expected.Length, source.NoteCount);
        Assert.Equal(expected.Select(static v => v.Id), command.ResultSelectionIds);
        for (int ordinal = 0; ordinal < expected.Length; ordinal++)
        {
            Assert.Equal(expected[ordinal], source.GetNote(ordinal));
            Assert.Equal(ordinal, source.FindNoteIndex(expected[ordinal].Id));
        }
        Assert.Equal(expected, source.QueryNotesByIds(selected).OrderBy(static v => v.Index).Select(static v => v.Value));
        Assert.Equal(expected.OrderBy(static v => v.Id), source.QueryNotes(0, long.MaxValue).OrderBy(static v => v.Id));
        source.PrefetchNotes(0, 80_000, 0, 127, default);
        List<DirectMidiNoteValue> actual = [];
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.True(source.TryQueryCachedNotes(0, 80_000, 0, 127, actual));
        Assert.Equal(expected.OrderBy(static v => v.Id), actual.OrderBy(static v => v.Id));
        edit.Undo(fixture.Project);
        Assert.Same(fixture.Segment, fixture.Current);
        Assert.Equal(original, fixture.Current.Notes.CreateObjectSource().QueryTickRange(new(0, long.MaxValue)));
        edit.Apply(fixture.Project);
        Assert.Same(applied, fixture.Current);
        Assert.Equal(expected[^1], source.GetNote(expected.Length - 1));
    }

    [Fact]
    public void RankSelectHandlesSparseDeletedRunsAndSharesOldLeafIndexes()
    {
        using var fixture = new Fixture(20_000);
        var original = fixture.Original.QueryValues(0, long.MaxValue).ToArray();
        using var context = BulkEditPreparationContext.Enter(project: fixture.Project);
        var source = BoundedDirectMidiNoteSource.Capture(fixture.Segment.Notes);
        static bool Deleted(int ordinal) => ordinal < 5 || ordinal % 11 == 0 || ordinal >= 19_990;
        using var store = new BoundedEditRecordStore<BoundedDirectNoteDelta>(context.Resources);
        for (int i = 0; i < original.Length; i++)
            if (Deleted(i)) store.Add(new(i, true, original[i], original[i]), default);
        store.Seal();
        using var patch = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(store);
        var result = source.Apply(patch, context.Resources, default);
        var expected = original.Where(static (_, ordinal) => !Deleted(ordinal)).ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], result.GetNote(i));
            Assert.Equal(i, result.FindNoteIndex(expected[i].Id));
        }
        foreach (var value in original.Where(static (_, ordinal) => Deleted(ordinal))) Assert.Equal(-1, result.FindNoteIndex(value.Id));
        Assert.Equal(original[0], source.GetNote(0));
        Assert.Equal(original[^1], source.GetNote(original.Length - 1));
        Assert.True(context.Resources.PeakResidentBytes <= context.Resources.Budget.MaximumResidentBytes);
        Assert.True(context.Resources.PeakWorkingBytes <= context.Resources.Budget.MaximumWorkingBytes);
    }

    [Theory]
    [InlineData(0)] // one complete deleted leaf
    [InlineData(1)] // three contiguous deleted leaves have the same survivor position
    [InlineData(2)] // deletion through the final physical ordinal
    [InlineData(3)] // no survivors
    [InlineData(4)] // deletions followed by appended physical ordinals
    public void RankSelectHandlesDeletedLeafBoundariesAndAppend(int pattern)
    {
        using var context = BulkEditPreparationContext.Enter();
        using var resources = context.Resources.BeginResourceLease();
        const int count = 16_384;
        bool Deleted(int ordinal) => pattern switch
        {
            0 => ordinal >= 4096 && ordinal < 8192,
            1 => ordinal >= 4096,
            2 => ordinal >= count - 17,
            3 => true,
            _ => ordinal < 8192 || ordinal >= count - 17 && ordinal < count
        };
        int extent = pattern == 4 ? count + 32 : count;
        var empty = BoundedDirectMidiIndex<RankValue>.Empty(
            Comparer<RankValue>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            static v => (v.Ordinal, v.Ordinal + 1L, 0), static _ => 0,
            static v => v.Deleted, ordinal: static v => v.Ordinal);
        var index = empty.ApplyPatch(Enumerable.Range(0, extent).Select(i => new RankValue(i, Deleted(i))),
            context.Resources, default);
        int[] survivors = Enumerable.Range(0, extent).Where(i => !Deleted(i)).ToArray();
        Assert.Equal(extent - survivors.Length, index.DeletedCount);
        for (int i = 0; i < survivors.Length; i++)
        {
            Assert.Equal(survivors[i], index.SelectUndeletedOrdinal(i));
            Assert.Equal(survivors[i] - i, index.CountDeletedBefore(new(survivors[i], false)));
        }
        Assert.Equal(index.DeletedCount, index.CountDeletedBefore(new(extent, false)));

        // Restore one tombstone without mutating its previous index. Untouched
        // leaves (and their compact deletion pages) remain reference-shared.
        int restore = Enumerable.Range(0, count).First(Deleted);
        var restored = index.ApplyPatch([new(restore, false)], context.Resources, default);
        Assert.Equal(index.DeletedCount - 1, restored.DeletedCount);
        Assert.Contains(index.Leaves, old => restored.Leaves.Any(current => ReferenceEquals(old, current)));
        var restoredSurvivors = survivors.Append(restore).Order().ToArray();
        for (int i = 0; i < restoredSurvivors.Length; i++)
            Assert.Equal(restoredSurvivors[i], restored.SelectUndeletedOrdinal(i));
        Assert.Equal(extent - survivors.Length, index.DeletedCount);
        using var publication = resources.Complete();
        Assert.Equal(0, context.Resources.WorkingBytes);
    }

    [Fact]
    public void IndexDirectoriesAreAccountedTogetherUntilPublication()
    {
        using var context = BulkEditPreparationContext.Enter();
        using var resources = context.Resources.BeginResourceLease();
        var empty = BoundedDirectMidiIndex<RankValue>.Empty(
            Comparer<RankValue>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)),
            static v => (v.Ordinal, v.Ordinal + 1L, 0), static _ => 0,
            static v => v.Deleted, ordinal: static v => v.Ordinal);
        var first = empty.ApplyPatch(Enumerable.Range(0, 12_288).Select(i => new RankValue(i, true)),
            context.Resources, default);
        long oneDirectory = context.Resources.WorkingBytes;
        Assert.True(oneDirectory >= 128 + (first.Leaves.Count + 1) * 8 + first.Leaves.Count * 8);
        var second = first.ApplyPatch([new(0, false)], context.Resources, default);
        Assert.True(context.Resources.WorkingBytes > oneDirectory);
        using var publication = resources.Complete();
        Assert.Equal(0, context.Resources.WorkingBytes);
        Assert.Equal(12_287, second.DeletedCount);
    }

    [Fact]
    public void OversizedIndexDirectoryFailsBeforeReplacingTheOldRoot()
    {
        var budget = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 4096, maximumResidentBytes: 0, pageRecordCount: 64));
        using var context = BulkEditPreparationContext.Enter(resources: budget);
        using (var resources = budget.BeginResourceLease())
        {
            var empty = BoundedDirectMidiIndex<RankValue>.Empty(
                Comparer<RankValue>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)),
                static v => (v.Ordinal, v.Ordinal + 1L, 0), static _ => 0,
                static v => v.Deleted, ordinal: static v => v.Ordinal);
            Assert.Throws<InvalidOperationException>(() => empty.ApplyPatch(
                Enumerable.Range(0, 16_384).Select(i => new RankValue(i, true)), budget, default));
            Assert.Equal(0, empty.Count);
            Assert.True(budget.PeakWorkingBytes <= budget.Budget.MaximumWorkingBytes);
        }
        Assert.Equal(0, budget.WorkingBytes);
        Assert.Equal(0, budget.SpillBytes);
    }

    private readonly record struct RankValue(int Ordinal, bool Deleted);

    private static void Evict(BoundedDirectMidiNoteSource source, string field)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object index = source.GetType().GetField(field, flags)!.GetValue(source)!;
        var leaves = (System.Collections.IEnumerable)index.GetType().GetProperty("Leaves", flags)!.GetValue(index)!;
        foreach (object leaf in leaves)
        {
            object values = leaf.GetType().GetProperty("Source", flags)!.GetValue(leaf)!;
            long identity = (long)values.GetType().GetField("_cacheIdentity", flags)!.GetValue(values)!;
            BoundedEditPageCache.Remove(identity);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "DirectQueryRegression", Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480);
        public PureMidiTrack Track { get; }
        public MidiSegment Segment { get; }
        public MidiSegment Current => Track.Segments[0];
        public PureMidiContentPack Pack { get; }
        public DirectMidiNoteQuerySnapshot Original { get; }
        public Fixture(int count, bool sameKey = false)
        {
            Directory.CreateDirectory(_directory);
            var root = new MidiChannelRoot(Project) { Name = "Root" };
            Track = new(Project) { Name = "Notes", MidiChannelRootId = root.Id };
            Segment = new(Project) { LengthTicks = count * 4L + 100 };
            Track.Segments.Add(Segment); Project.MidiChannelRoots.Add(root); Project.PureMidiTracks.Add(Track);
            Project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, Track.Id));
            using var writer = new PureMidiContentPackWriter(Path.Combine(_directory, "source.mpk"));
            for (int i = 0; i < count; i++) writer.AddNote(Segment.Id,
                new(Project.AllocateStableId(), i * 4L, 2, sameKey ? 60 : i % 128, 100, 23, i * 2L, i * 2L + 1));
            Pack = writer.Complete();
            Segment.AttachPagedContent(Pack.GetSegmentSource(Segment.Id));
            Original = Segment.Notes.CreateQuerySnapshot();
        }
        public void Dispose() { Project.Dispose(); Pack.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
