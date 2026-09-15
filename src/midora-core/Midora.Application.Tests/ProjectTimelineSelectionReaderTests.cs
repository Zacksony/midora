using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTimelineSelectionReaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectSelectionUsesBoundedBatchesAndNeverRepeatsScalarReads(bool formalOrder)
    {
        using var fixture = new Fixture();
        MidiSegment segment = new(fixture.Project);
        var source = new DirectSource();
        segment.AttachPagedContent(source);
        var ids = Enumerable.Range(1, 8193).Select(i => new MidoraId(i)).ToHashSet();
        var progress = new RecordingProgress();
        var values = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project,
            segment.Notes.CreateObjectSource(), ids, progress: progress, preserveFormalOrder: formalOrder).ToArray();
        Assert.Equal(ids.Count, values.Length);
        Assert.Equal(3, source.BatchCalls);
        Assert.InRange(source.MaximumBatch, 1, 4096);
        Assert.Equal(0, source.SingleFindCalls);
        Assert.Equal(0, source.ScalarReads);
        Assert.Equal(ids.Order(), values.Select(v => v.Id).Order());
        if (formalOrder) Assert.Equal(ids.OrderDescending(), values.Select(v => v.Id));
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Contains(progress.Values, p => p.Phase == TimelineEditPreparationPhase.ReadingSelection);
        AssertMonotonic(progress.Values);
    }

    [Fact]
    public void CandidateOwnerProbeCanSkipUnknownIdsButStrictCopyRejectsThem()
    {
        using var fixture = new Fixture();
        MidiSegment segment = new(fixture.Project);
        segment.AttachPagedContent(new DirectSource());
        MidoraId[] ids = [new(100), new(200), new(2_000_000)];
        var source = segment.Notes.CreateObjectSource();
        var values = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project,
            source, ids, requireAll: false).ToArray();
        Assert.Equal([200L, 100L], values.Select(v => v.Id.Value));
        Assert.Throws<ArgumentException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
            fixture.Project, source, ids).ToArray());
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void LogicalSparseSelectionPreparesBoundedIndexAndReadsEachRequiredPageOnce()
    {
        using var fixture = new Fixture();
        var source = new LogicalSource(100_000);
        // Array is deliberately not a membership set, and formal order is the
        // reverse of Stable ID order, as with edited/reopened projects.
        MidoraId[] ids = Enumerable.Range(1, 5000).Select(i => new MidoraId(i)).ToArray();
        var values = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).ToArray();
        Assert.Equal(1, source.IndexBuilds);
        Assert.Equal(5000, source.FindCalls);
        Assert.Equal(2, source.PageReads);
        Assert.Equal(ids.Reverse(), values.Select(v => v.Id));
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        long retained = fixture.Resources.SpillBytes;
        Assert.True(retained > 0);
        source.PageReads = 0;
        Assert.Equal(5000, ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).Count());
        Assert.Equal(1, source.IndexBuilds);
        Assert.Equal(2, source.PageReads);
        Assert.Equal(retained, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void DenseFastMembershipReadsOneFormalPassWithoutBuildingAnyIdIndex()
    {
        using var fixture = new Fixture();
        var source = new LogicalSource(10_000);
        var ids = Enumerable.Range(1, 6000).Select(i => new MidoraId(i)).ToHashSet();
        var values = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).ToArray();
        Assert.Equal(6000, values.Length);
        Assert.Equal(3, source.PageReads);
        Assert.Equal(0, source.FindCalls);
        Assert.Equal(0, source.IndexBuilds);
        Assert.Equal(ids.OrderDescending(), values.Select(v => v.Id));
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void EarlyEnumerationDisposalReleasesSelectionSortPages()
    {
        using var fixture = new Fixture();
        MidiSegment segment = new(fixture.Project);
        segment.AttachPagedContent(new DirectSource());
        var ids = Enumerable.Range(1, 9000).Select(i => new MidoraId(i)).ToHashSet();
        using (var iterator = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project,
            segment.Notes.CreateObjectSource(), ids).GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            Assert.True(fixture.Resources.SpillBytes > 0);
        }
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledDomainIndexSurvivesEarlyReadDisposalOrReadCancellation(bool cancelReading)
    {
        using var fixture = new Fixture();
        Segment segment = new(fixture.Project) { LengthTicks = 20_000 };
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new LogicalNote(fixture.Project)
            { StartTick = 12_000 - i * 2, LengthTicks = 1, Note = i % 128, Velocity = 90 }));
        var source = segment.Notes.CreateQuerySnapshot();
        MidoraId[] ids = source.EnumerateAll().Select(v => v.Id).Reverse().ToArray();
        using var cancellation = new CancellationTokenSource();
        if (cancelReading)
            Assert.ThrowsAny<OperationCanceledException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
                fixture.Project, source, ids, cancellation.Token, new CancelReadProgress(cancellation)).ToArray());
        else
        {
            using var iterator = ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).GetEnumerator();
            Assert.True(iterator.MoveNext());
        }
        Assert.True(source.TryFindOrdinalById(ids[0], out int ordinal));
        Assert.Equal(ids[0], source.GetByOrdinal(ordinal).Id);
        long retainedIndex = fixture.Resources.SpillBytes;
        Assert.True(retainedIndex > 0);
        Assert.Equal(6000, ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).Count());
        Assert.Equal(retainedIndex, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    [Fact]
    public void CancellationBeforeDomainIndexInstallationAllowsACompleteRetry()
    {
        using var fixture = new Fixture();
        Segment segment = new(fixture.Project) { LengthTicks = 20_000 };
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new LogicalNote(fixture.Project)
            { StartTick = i * 2, LengthTicks = 1, Note = i % 128, Velocity = 90 }));
        var source = segment.Notes.CreateQuerySnapshot();
        MidoraId[] ids = source.EnumerateAll().Select(v => v.Id).ToArray();
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
            fixture.Project, source, ids, cancellation.Token, new CancelIndexProgress(cancellation)).ToArray());
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(6000, ProjectTimelineReadPreparation.ReadSelectedValues(fixture.Project, source, ids).Count());
        Assert.True(source.TryFindOrdinalById(ids[^1], out int ordinal));
        Assert.Equal(ids[^1], source.GetByOrdinal(ordinal).Id);
    }

    private sealed class CancelIndexProgress(CancellationTokenSource cancellation)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value)
        {
            if (value.Phase == TimelineEditPreparationPhase.PreparingIndex && value.Completed > 0)
                cancellation.Cancel();
        }
    }

    [Fact]
    public void CancellationDuringDenseReadDoesNotPublishOrRetainSelectionStorage()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var source = new LogicalSource(20_000) { CancelAfterFirstPage = cancellation };
        var ids = Enumerable.Range(1, 20_000).Select(i => new MidoraId(i)).ToHashSet();
        Assert.ThrowsAny<OperationCanceledException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
            fixture.Project, source, ids, cancellation.Token).ToArray());
        Assert.Equal(1, source.PageReads);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
    }

    [Fact]
    public void InvalidAndDuplicateSelectionIdentitiesAreRejected()
    {
        using var fixture = new Fixture();
        var source = new LogicalSource(10_000);
        Assert.Throws<ArgumentException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
            fixture.Project, source, new MidoraId[] { new(1), new(1) }).ToArray());
        Assert.Throws<ArgumentException>(() => ProjectTimelineReadPreparation.ReadSelectedValues(
            fixture.Project, source, new HashSet<MidoraId> { default }).ToArray());
        Assert.Equal(0, source.IndexBuilds);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipboardTransferReportsRealReadAndStorageStagesAndCutPublishesOnlyAfterPreparation(bool cut)
    {
        using var fixture = new Fixture();
        var project = fixture.Project;
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(segment);
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new LogicalNote(project)
            { StartTick = 100 + i * 2, LengthTicks = 1, Note = i % 128, Velocity = 91 }));
        var ids = segment.Notes.CreateQuerySnapshot().EnumerateAll().Select(v => v.Id).ToHashSet();
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        var progress = new RecordingProgress();
        using var transfer = ProjectObjectClipboard.PrepareTransfer(document,
            () => ProjectObjectClipboard.CopyLogicalNotes(document, segment.Id, ids),
            cut ? ProjectDomainEditCommands.DeleteLogicalNotes(segment.Id, ids) : null,
            progress: progress);
        var payload = Assert.IsType<LogicalNoteClipboardData>(transfer.Payload.Data);
        Assert.Equal(6000, payload.Notes.Count);
        Assert.Equal(0, payload.Notes[0].StartOffset);
        Assert.Equal(11_998, payload.Notes[^1].StartOffset);
        Assert.Equal(6000, project.Tracks[0].Segments[0].Notes.Count);
        Assert.Empty(document.History);
        AssertMonotonic(progress.Values);
        Assert.Contains(progress.Values, p => p.Phase == TimelineEditPreparationPhase.ReadingSelection);
        Assert.Contains(progress.Values, p => p.Phase == TimelineEditPreparationPhase.WritingStorage);
        Assert.Equal(1, progress.Values[^1].OverallFraction);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        if (cut)
        {
            Assert.NotNull(transfer.Deletion);
            document.ExecutePrepared(transfer.Deletion!);
            Assert.Empty(project.Tracks[0].Segments[0].Notes);
            document.Undo();
            Assert.Equal(6000, project.Tracks[0].Segments[0].Notes.Count);
        }
        else Assert.Null(transfer.Deletion);
    }

    [Fact]
    public void ClipboardReadCancellationLeavesProjectHistoryAndStorageUnchanged()
    {
        using var fixture = new Fixture();
        var project = fixture.Project;
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(segment);
        segment.Notes.AddRange(Enumerable.Range(0, 6000).Select(i => new LogicalNote(project)
            { StartTick = i, LengthTicks = 1, Note = i % 128, Velocity = 91 }));
        var ids = segment.Notes.CreateQuerySnapshot().EnumerateAll().Select(v => v.Id).ToHashSet();
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ProjectObjectClipboard.PrepareTransfer(document,
            () => ProjectObjectClipboard.CopyLogicalNotes(document, segment.Id, ids),
            ProjectDomainEditCommands.DeleteLogicalNotes(segment.Id, ids), cancellation.Token,
            new CancelReadProgress(cancellation)));
        Assert.Equal(6000, segment.Notes.Count);
        Assert.Empty(document.History);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    private sealed class CancelReadProgress(CancellationTokenSource cancellation)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value)
        {
            if (value.Phase == TimelineEditPreparationPhase.ReadingSelection && value.Completed > 0)
                cancellation.Cancel();
        }
    }

    private static void AssertMonotonic(IReadOnlyList<TimelineEditPreparationProgress> values)
    {
        for (int index = 1; index < values.Count; index++)
            Assert.True(values[index].OverallFraction >= values[index - 1].OverallFraction);
    }

    private sealed class RecordingProgress : IProgress<TimelineEditPreparationProgress>
    {
        public List<TimelineEditPreparationProgress> Values { get; } = [];
        public void Report(TimelineEditPreparationProgress value) => Values.Add(value);
    }

    private sealed class LogicalSource(int count) : ITimelineObjectSource<LogicalNoteSnapshotValue>
    {
        private IImmutableTimelineIdIndex? _index;
        public CancellationTokenSource? CancelAfterFirstPage { get; init; }
        public int IndexBuilds { get; private set; }
        public int FindCalls { get; private set; }
        public int PageReads { get; set; }
        public int Count => count;
        public int PageCapacity => 4096;
        public long SourceRevision => 17;
        public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default)
        {
            if (_index is not null) return;
            IndexBuilds++;
            _index = builder.CreateOrdinalIndex(Enumerable.Range(0, count)
                .Select(i => new TimelineIdOrdinal(new(count - i), i)), token);
            _index.RetainForSourceLifetime();
        }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            FindCalls++;
            if (_index is null) throw new InvalidOperationException("The bounded address index was not prepared.");
            return _index.TryFindOrdinalById(id, out ordinal);
        }
        public LogicalNoteSnapshotValue GetByOrdinal(int ordinal) => throw new InvalidOperationException("Read the entire page once.");
        public bool TryGetPageByOrdinal(int firstOrdinal, int requested, out TimelineObjectPage<LogicalNoteSnapshotValue> page)
        {
            PageReads++;
            if (PageReads == 1) CancelAfterFirstPage?.Cancel();
            var values = Enumerable.Range(firstOrdinal, Math.Min(requested, Count - firstOrdinal))
                .Select(i => new LogicalNoteSnapshotValue(new(Count - i), i, 1, i % 128, 100)).ToArray();
            page = new(SourceRevision, firstOrdinal, values);
            return true;
        }
        public int FindOrdinalAtOrAfterTick(long tick) => throw new NotSupportedException();
        public IEnumerable<LogicalNoteSnapshotValue> QueryTickRange(TimelineObjectRangeQuery query) => throw new NotSupportedException();
        public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) { }
    }

    private sealed class DirectSource : IPureMidiSegmentContentSource
    {
        public int NoteCount => 1_000_000;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "selection-reader-direct-source";
        public int BatchCalls { get; private set; }
        public int MaximumBatch { get; private set; }
        public int SingleFindCalls { get; private set; }
        public int ScalarReads { get; private set; }
        public DirectMidiNoteValue GetNote(int index)
        { ScalarReads++; throw new InvalidOperationException("The resolved scalar must not be read again."); }
        public int FindNoteIndex(MidoraId id)
        { SingleFindCalls++; throw new InvalidOperationException("A selection must resolve IDs in batches."); }
        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(IReadOnlySet<MidoraId> ids)
        {
            BatchCalls++;
            MaximumBatch = Math.Max(MaximumBatch, ids.Count);
            foreach (var id in ids)
            {
                if (id.Value < 1 || id.Value > NoteCount) continue;
                int ordinal = checked(NoteCount - (int)id.Value);
                yield return new(ordinal, new(id, ordinal, 2, ordinal % 128, 90, 42, ordinal * 2L, ordinal * 2L + 1));
            }
        }
        public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public OpaqueMidiEventValue GetOpaqueEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(AppContext.BaseDirectory, ".tmp", "selection-reader-" + Guid.NewGuid().ToString("N"));
        private readonly BulkEditPreparationContext _scope;
        public MidoraProject Project { get; } = new(480);
        public BoundedEditResources Resources { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024,
                maximumWorkingBytes: 4 * 1024 * 1024), _path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
        }
        public void Dispose()
        { _scope.Dispose(); Project.Dispose(); Directory.Delete(_path, true); }
    }
}
