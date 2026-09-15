using System.Collections;
using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

// Opt in to the large resource probe; the ordinary correctness suite remains
// small. No window, audio device, external MIDI, or user file is required.
public sealed class BoundedLogicalNotePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void ProfileMillionValueOnlyPreparation()
    {
        if (Environment.GetEnvironmentVariable("MIDORA_BOUNDED_NOTE_PERF") != "profile") return;
        string scratch = Path.Combine(AppContext.BaseDirectory, ".tmp", "bounded-note-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using MidoraProject project = new(480);
        try
        {
            LogicalTrack track = new(project) { Name = "Profile" }; Segment original = new(project) { LengthTicks = 8_000_000 };
            project.Tracks.Add(track); track.Segments.Add(original);
            original.Notes.AdoptSource(project, new GeneratedSource(1_000_000));
            var resources = new BoundedEditResources(temporaryRoot: scratch);
            using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
            var snapshot = original.Notes.CreateQuerySnapshot();
            var ids = CompactMidoraIdList.Freeze(new GeneratedIds(1_000_000));
            Stopwatch timer = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes();
            using var selected = BoundedLogicalNotePlanning.Select(snapshot, ids, _ => true, resources, default);
            Log("selection");
            using var plans = BoundedLogicalNotePlanning.Transform(snapshot, selected, v => v with { Velocity = 99 },
                v => new(v.StartTick, v.Note), v => v.Id, key => snapshot.EnumerateExactStart(key.Tick, key.Key), false, resources, default);
            Log("plan");
            Segment result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, original);
            result.Notes.Clear(); result.Notes.AdoptEditedSnapshot(project, snapshot, plans);
            Log("adopt root");
            ProjectChangeSet receipt = new();
            ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(receipt, original, result, plans.Select(p => snapshot.GetByOrdinal(p.Ordinal).Id));
            Log("receipt general");
            ProjectChangeSet optimizedReceipt = new();
            Assert.True(ProjectTimelineOwnerChangeSetBuilder.TryAddLogicalNoteValueEdits(optimizedReceipt, original, result, plans));
            Log("receipt value-only");
            var expected = Assert.Single(Assert.Single(receipt.TimelineOwnerChanges).Sources);
            var actual = Assert.Single(Assert.Single(optimizedReceipt.TimelineOwnerChanges).Sources);
            Assert.Equal(expected.PreviousContentRanges, actual.PreviousContentRanges);
            Assert.Equal(expected.CurrentContentRanges, actual.CurrentContentRanges);
            void Log(string stage)
            {
                output.WriteLine($"{stage}: {timer.Elapsed.TotalSeconds:F3}s; allocated={(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0:F1}MiB");
                timer.Restart(); allocated = GC.GetTotalAllocatedBytes();
            }
        }
        finally { project.Dispose(); Directory.Delete(scratch, true); }
    }

    [Fact]
    public void MeasureMillionAndTenMillionOwnerEdits()
    {
        if (Environment.GetEnvironmentVariable("MIDORA_BOUNDED_NOTE_PERF") != "1") return;
        foreach (int count in new[] { 1_000_000, 10_000_000 }) Measure(count);
    }

    private void Measure(int count)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, ".tmp", "bounded-note-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using MidoraProject project = new(480);
        try
        {
            LogicalTrack track = new(project) { Name = "Performance" };
            Segment original = new(project) { LengthTicks = count * 8L };
            project.Tracks.Add(track); track.Segments.Add(original);
            Stopwatch timer = Stopwatch.StartNew();
            original.Notes.AdoptSource(project, new GeneratedSource(count));
            project.AdvanceNextStableId(count + 1000L);
            output.WriteLine($"OWNER {count:N0}: source root {timer.Elapsed.TotalSeconds:F3}s; heap {GC.GetTotalMemory(false) / 1048576.0:F1}MiB");
            var ids = CompactMidoraIdList.Freeze(new GeneratedIds(60_000));
            Run("move 60k", () => ProjectDomainEditCommands.MoveLogicalNotes(original.Id, ids, 1, 1));
            Run("resize 60k", () => ProjectDomainEditCommands.AdjustLogicalNoteEdges(original.Id, ids, 0, 2));
            Run("duplicate 60k", () => ProjectDomainEditCommands.DuplicateLogicalNotes(original.Id, ids, original.Id, count * 8L));
            var millionIds = CompactMidoraIdList.Freeze(new GeneratedIds(1_000_000));
            Run("velocity 1m", () => ProjectDomainEditCommands.SetLogicalNoteVelocities(original.Id, millionIds, 99, ProjectBatchValueEditMode.ExactSet));
            using var cancellation = new CancellationTokenSource();
            var cancelResources = new BoundedEditResources(temporaryRoot: scratch);
            using (var scope = BulkEditPreparationContext.Enter(cancellation.Token, resources: cancelResources, project: project))
            {
                cancellation.CancelAfter(100);
                timer.Restart();
                Assert.Throws<OperationCanceledException>(() => ProjectDomainEditCommands.MoveLogicalNotes(original.Id, millionIds, 1, 1).Prepare(project));
                output.WriteLine($"CANCEL {count:N0}: {timer.Elapsed.TotalSeconds:F3}s; retained source={ReferenceEquals(original, track.Segments[0])}; resident={cancelResources.ResidentBytes / 1048576.0:F1}MiB; spill={cancelResources.SpillBytes / 1048576.0:F1}MiB");
                Assert.Same(original, track.Segments[0]);
            }

            void Run(string label, Func<IProjectEditCommand> command)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long heapBefore = GC.GetTotalMemory(false);
                long allocatedBefore = GC.GetTotalAllocatedBytes();
                var resources = new BoundedEditResources(temporaryRoot: scratch);
                using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
                using var lease = resources.BeginResourceLease();
                timer.Restart();
                var prepared = command().Prepare(project);
                using var publication = lease.Complete();
                double prepare = timer.Elapsed.TotalSeconds;
                Assert.Equal(0, resources.ResidentBytes);
                timer.Restart(); prepared.Apply(project); double apply = timer.Elapsed.TotalMilliseconds;
                publication.MarkPublished();
                timer.Restart(); prepared.Undo(project); double undo = timer.Elapsed.TotalMilliseconds;
                output.WriteLine($"{count:N0} {label}: prepare+final-spill={prepare:F3}s apply={apply:F3}ms undo={undo:F3}ms; peak resident={resources.PeakResidentBytes / 1048576.0:F1}MiB working={resources.PeakWorkingBytes / 1048576.0:F1}MiB spill={resources.PeakSpillBytes / 1048576.0:F1}MiB; retained resident={resources.ResidentBytes / 1048576.0:F1}MiB allocated={(GC.GetTotalAllocatedBytes() - allocatedBefore) / 1048576.0:F1}MiB heap delta={(GC.GetTotalMemory(false) - heapBefore) / 1048576.0:F1}MiB");
                Assert.Same(original, track.Segments[0]);
                Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
                Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
                if (prepared is IDisposable disposable) disposable.Dispose();
            }
        }
        finally { project.Dispose(); Directory.Delete(scratch, recursive: true); }
    }

    private sealed class GeneratedIds(int count) : IReadOnlyList<MidoraId>
    {
        public int Count => count;
        public MidoraId this[int index] => new(1000L + index);
        public IEnumerator<MidoraId> GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GeneratedSource(int count) : IIndexedImmutableTimelineValueSource<LogicalNoteSnapshotValue>
    {
        private int _page = -1;
        private LogicalNoteSnapshotValue[]? _decoded;
        public int Count => count;
        public int PageCapacity => 4096;
        public LogicalNoteSnapshotValue this[int index] => new(new(1000L + index), index * 8L, 3, index % 128, 100);
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        { long value = id.Value - 1000; ordinal = (int)value; return value >= 0 && value < Count; }
        public ReadOnlyMemory<LogicalNoteSnapshotValue> ReadPage(int pageIndex)
        {
            if (_page == pageIndex) return _decoded!;
            int start = pageIndex * PageCapacity;
            var values = new LogicalNoteSnapshotValue[Math.Min(PageCapacity, count - start)];
            for (int i = 0; i < values.Length; i++) values[i] = this[start + i];
            _page = pageIndex; _decoded = values;
            return values;
        }
        public IEnumerator<LogicalNoteSnapshotValue> GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
