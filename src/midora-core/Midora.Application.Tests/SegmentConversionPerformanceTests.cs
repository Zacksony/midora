using System.Collections;
using System.Diagnostics;
using Midora.Domain;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class SegmentConversionPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureOptInLargeCrossTypeSegmentTransfers()
    {
        string? setting = Environment.GetEnvironmentVariable("MIDORA_SEGMENT_CONVERSION_BENCHMARK_COUNT");
        if (string.IsNullOrEmpty(setting)) return;
        int[] counts = setting == "all" ? [100_000, 1_000_000] : [int.Parse(setting)];
        foreach (int count in counts)
        {
            Assert.InRange(count, 1, 10_000_000);
            Measure(count, sourceDirect: false);
            Measure(count, sourceDirect: true);
        }
    }

    private void Measure(int count, bool sourceDirect)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, ".tmp", "segment-conversion-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var project = new MidoraProject(480);
        var resources = new BoundedEditResources(temporaryRoot: scratch);
        try
        {
            // The session constructor performs an initial full compile even in
            // Background mode. Construct it while the fixture is still empty,
            // before attaching the generated immutable source pages.
            using var compilation = new ProjectCompilationSession(project,
                executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromHours(1));
            using var document = new ProjectDocumentSession(compilation);
            EventInstrument instrument = EventInstrumentLibrary.Create(project, "Conversion");
            LogicalTrack logicalTrack = new(project) { Name = "Logical" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
            MidiChannelRoot root = new(project) { Name = "Root" };
            project.MidiChannelRoots.Add(root);
            PureMidiTrack midiTrack = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
            project.PureMidiTracks.Add(midiTrack);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midiTrack.Id));
            MidoraId sourceId;
            long length = count * 4L;
            if (sourceDirect)
            {
                MidiSegment segment = new(project) { LengthTicks = length };
                segment.AttachPagedContent(new DirectSource(count));
                midiTrack.Segments.Add(segment);
                sourceId = segment.Id;
            }
            else
            {
                Segment segment = new(project) { LengthTicks = length };
                segment.Notes.AdoptSource(project, new LogicalSource(count));
                logicalTrack.Segments.Add(segment);
                sourceId = segment.Id;
            }
            project.AdvanceNextStableId(count + 1000L);
            using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
            // Deliberately isolate edit/clipboard/history costs from compilation.
            // The immutable synthetic source provides real paged value reads,
            // without retaining one managed object per original note.
            long allocated = GC.GetTotalAllocatedBytes();
            using var process = Process.GetCurrentProcess();
            long initialWorkingSet = process.WorkingSet64;
            Stopwatch timer = Stopwatch.StartNew();
            using SegmentConversionPlan plan = SegmentConversionService.AnalyzeTransfer(document,
                [sourceId], sourceId, sourceDirect ? logicalTrack.Id : midiTrack.Id, length + 480, false);
            double reviewSeconds = timer.Elapsed.TotalSeconds;
            Assert.False(plan.RequiresConfirmation);
            IProjectEditCommand command = plan.CreateCommand();
            using (command as IDisposable)
            {
                timer.Restart();
                using StagedProjectEdit prepared = document.PrepareEdit(command);
                double prepareSeconds = timer.Elapsed.TotalSeconds;
                MidoraId resultId = Assert.Single(prepared.PreparedSelection!.ResultSelectionIds);
                Assert.Equal(sourceId, Assert.Single(prepared.PreparedSelection.OriginalSelectionIds));
                timer.Restart(); document.ExecutePrepared(prepared);
                double publishMilliseconds = timer.Elapsed.TotalMilliseconds;
                AssertResult();
                timer.Restart(); document.Undo();
                double undoMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (sourceDirect)
                {
                    Assert.Equal(sourceId, Assert.Single(project.PureMidiTracks.Single().Segments).Id);
                    Assert.Empty(project.Tracks.Single().Segments);
                }
                else
                {
                    Assert.Equal(sourceId, Assert.Single(project.Tracks.Single().Segments).Id);
                    Assert.Empty(project.PureMidiTracks.Single().Segments);
                }
                timer.Restart(); document.Redo();
                double redoMilliseconds = timer.Elapsed.TotalMilliseconds;
                AssertResult();
                process.Refresh();
                output.WriteLine($"CONVERT {(sourceDirect ? "direct-to-logical" : "logical-to-direct")} {count:N0}: review={reviewSeconds:F3}s; prepare={prepareSeconds:F3}s; publish={publishMilliseconds:F3}ms; undo={undoMilliseconds:F3}ms; redo={redoMilliseconds:F3}ms; working={resources.PeakWorkingBytes / 1048576.0:F2}MiB; resident={resources.PeakResidentBytes / 1048576.0:F2}MiB; spill={resources.PeakSpillBytes / 1048576.0:F2}MiB; allocated={(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0:F2}MiB; managed={GC.GetTotalMemory(false) / 1048576.0:F2}MiB; ws-before={initialWorkingSet / 1048576.0:F2}MiB; ws-after={process.WorkingSet64 / 1048576.0:F2}MiB; process-peak-ws={process.PeakWorkingSet64 / 1048576.0:F2}MiB");
                Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
                Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
                Assert.InRange(resources.PeakSpillBytes, 1, resources.Budget.MaximumSpillBytes);

                void AssertResult()
                {
                    long checkedNotes = 0;
                    if (sourceDirect)
                    {
                        Segment result = Assert.Single(project.Tracks.Single().Segments);
                        Assert.Equal(resultId, result.Id);
                        Assert.Equal(length + 480, result.ProjectStartTick);
                        Assert.Equal(count, result.Notes.Count);
                        Assert.Empty(project.PureMidiTracks.Single().Segments);
                        foreach (LogicalNoteSnapshotValue note in result.Notes.CreateQuerySnapshot().EnumerateAll())
                        {
                            long index = note.StartTick / 4;
                            if (note.StartTick != index * 4 || note.LengthTicks != 2 || note.Note != index % 128
                                || note.Velocity != 64 + index % 64)
                                throw new InvalidOperationException("A converted Logical Note lost common fields.");
                            checkedNotes++;
                        }
                    }
                    else
                    {
                        MidiSegment result = Assert.Single(project.PureMidiTracks.Single().Segments);
                        Assert.Equal(resultId, result.Id);
                        Assert.Equal(length + 480, result.ProjectStartTick);
                        Assert.Equal(count, result.Notes.Count);
                        Assert.Empty(project.Tracks.Single().Segments);
                        foreach (DirectMidiNoteValue note in result.Notes.CreateQuerySnapshot().QueryValues(0, long.MaxValue))
                        {
                            long index = note.StartTick / 4;
                            if (note.StartTick != index * 4 || note.LengthTicks != 2 || note.Key != index % 128
                                || note.NoteOnVelocity != 64 + index % 64 || note.NoteOffVelocity != 0)
                                throw new InvalidOperationException("A converted Direct Note lost common fields.");
                            checkedNotes++;
                        }
                    }
                    Assert.Equal(count, checkedNotes);
                }
            }
        }
        finally
        {
            project.Dispose();
            Directory.Delete(scratch, recursive: true);
        }
        Assert.Equal(0, resources.WorkingBytes);
        Assert.Equal(0, resources.ResidentBytes);
        Assert.Equal(0, resources.SpillBytes);
    }

    private sealed class LogicalSource(int count) : IIndexedImmutableTimelineValueSource<LogicalNoteSnapshotValue>
    {
        private int _cachedPage = -1;
        private LogicalNoteSnapshotValue[]? _values;
        public int Count => count;
        public int PageCapacity => 4096;
        public LogicalNoteSnapshotValue this[int index] => new(new(1000L + index), index * 4L, 2, index % 128, 64 + index % 64);
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        { long value = id.Value - 1000; ordinal = (int)value; return value >= 0 && value < count; }
        public ReadOnlyMemory<LogicalNoteSnapshotValue> ReadPage(int pageIndex)
        {
            if (pageIndex == _cachedPage) return _values!;
            int start = pageIndex * PageCapacity;
            var values = new LogicalNoteSnapshotValue[Math.Min(PageCapacity, count - start)];
            for (int index = 0; index < values.Length; index++) values[index] = this[start + index];
            _values = values; _cachedPage = pageIndex;
            return values;
        }
        public IEnumerator<LogicalNoteSnapshotValue> GetEnumerator()
        { for (int index = 0; index < count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DirectSource(int count) : IPureMidiSegmentContentSource, IPureMidiContentBoundsSource
    {
        public int NoteCount => count;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => $"conversion-benchmark-{count}";
        public long MaximumNoteEndTick => count * 4L;
        public DirectMidiNoteValue GetNote(int index) => new(new(1000L + index), index * 4L, 2,
            index % 128, 64 + index % 64, 0, index * 2L, index * 2L + 1);
        public int FindNoteIndex(MidoraId id) => id.Value >= 1000 && id.Value < 1000L + count ? (int)(id.Value - 1000) : -1;
        public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public OpaqueMidiEventValue GetOpaqueEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127)
        {
            long first = Math.Max(0, (startTick - 1) / 4);
            for (long index = first; index < count && index * 4 < endTick; index++)
            {
                DirectMidiNoteValue note = GetNote((int)index);
                if (note.StartTick + note.LengthTicks > startTick && note.Key >= minimumKey && note.Key <= maximumKey)
                    yield return note;
            }
        }
    }
}
