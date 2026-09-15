using System.Collections;
using System.Diagnostics;
using Midora.Domain;
using Midora.Persistence;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiResourceTests(ITestOutputHelper output)
{
    [Fact]
    public void MillionColdPagedNotesResizeWithinPreparationBudgetsAndPublishConstantSizeRoots()
    {
        const int count = 1_000_000;
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "DirectBulkResourceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var project = new MidoraProject(480);
            var root = new MidiChannelRoot(project) { Name = "Root" };
            var track = new PureMidiTrack(project) { Name = "Million", MidiChannelRootId = root.Id };
            var segment = new MidiSegment(project) { LengthTicks = count * 4L + 4 };
            track.Segments.Add(segment); project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            long firstId = project.NextStableId;
            using var writer = new PureMidiContentPackWriter(Path.Combine(directory, "source.mpk"));
            for (int index = 0; index < count; index++)
                writer.AddNote(segment.Id, new(project.AllocateStableId(), index * 4L, 2, index % 128, 100, 23, index * 2L, index * 2L + 1));
            using var pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            Assert.Equal(0, pack.PageCacheMissCount);
            var ids = new ConsecutiveIds(firstId, count);
            long beforeManaged = GC.GetTotalMemory(true);
            long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
            using var scope = BulkEditPreparationContext.Enter(project: project);
            using var lease = scope.Resources.BeginResourceLease();
            var clock = Stopwatch.StartNew();
            var edit = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(segment.Id, ids, 0, 1).Prepare(project);
            using var publication = lease.Complete(scope.Token);
            TimeSpan prepare = clock.Elapsed;
            long preparationAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
            Assert.Same(segment, track.Segments[0]);
            Assert.True(scope.Resources.PeakWorkingBytes <= scope.Resources.Budget.MaximumWorkingBytes);
            Assert.True(scope.Resources.PeakResidentBytes <= scope.Resources.Budget.MaximumResidentBytes);
            Assert.InRange(scope.Resources.PeakSpillBytes, 1, scope.Resources.Budget.MaximumSpillBytes);
            Assert.Equal(0, scope.Resources.ResidentBytes);

            long publishAllocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart(); edit.Apply(project); TimeSpan apply = clock.Elapsed;
            publication.MarkPublished();
            long applyAllocated = GC.GetAllocatedBytesForCurrentThread() - publishAllocated;
            var applied = track.Segments[0];
            var source = applied.Notes.CreateObjectSource();
            Assert.Equal(count, source.Count);
            Assert.Equal((0L, 3L, 23), (source.GetByOrdinal(0).StartTick, source.GetByOrdinal(0).LengthTicks, source.GetByOrdinal(0).NoteOffVelocity));
            Assert.Equal((count - 1) * 4L, source.GetByOrdinal(count - 1).StartTick);
            Assert.Equal(3, source.GetByOrdinal(count - 1).LengthTicks);
            publishAllocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart(); edit.Undo(project); TimeSpan undo = clock.Elapsed;
            long undoAllocated = GC.GetAllocatedBytesForCurrentThread() - publishAllocated;
            Assert.Same(segment, track.Segments[0]);
            clock.Restart(); edit.Apply(project); TimeSpan redo = clock.Elapsed;
            Assert.Same(applied, track.Segments[0]);
            edit.Undo(project);
            Assert.True(applyAllocated < 64 * 1024, $"Root Apply allocated {applyAllocated} bytes.");
            Assert.True(undoAllocated < 64 * 1024, $"Root Undo allocated {undoAllocated} bytes.");
            Assert.True(apply < TimeSpan.FromSeconds(2) && undo < TimeSpan.FromSeconds(2) && redo < TimeSpan.FromSeconds(2));
            output.WriteLine($"count={count}; prepareIncludingFinalSpill={prepare.TotalSeconds:F3}s; apply={apply.TotalMilliseconds:F3}ms; undo={undo.TotalMilliseconds:F3}ms; redo={redo.TotalMilliseconds:F3}ms; "
                + $"workingPeak={scope.Resources.PeakWorkingBytes / 1048576d:F2}MiB; stagingResidentPeak={scope.Resources.PeakResidentBytes / 1048576d:F2}MiB; "
                + $"spillPeak={scope.Resources.PeakSpillBytes / 1048576d:F2}MiB; preparationCumulativeAllocated={preparationAllocated / 1048576d:F2}MiB; "
                + $"managedBefore={beforeManaged / 1048576d:F2}MiB; managedAfter={GC.GetTotalMemory(false) / 1048576d:F2}MiB; "
                + $"sourcePageMisses={pack.PageCacheMissCount}; retainedResident={scope.Resources.ResidentBytes}; applyAllocated={applyAllocated}; undoAllocated={undoAllocated}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class ConsecutiveIds(long first, int count) : IReadOnlyCollection<MidoraId>
    {
        public int Count => count;
        public IEnumerator<MidoraId> GetEnumerator()
        { for (int index = 0; index < count; index++) yield return new(first + index); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
