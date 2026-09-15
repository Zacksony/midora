using System.Collections;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class RasterAggregationCancellationTests
{
    [Fact]
    public void PublicRasterSourcesObserveAnAlreadyCanceledGeneration()
    {
        using var project = new MidoraProject(192);
        var logical = new Segment(project) { LengthTicks = 1024 };
        logical.Notes.Add(new(project) { StartTick = 0, LengthTicks = 100, Note = 60, Velocity = 100 });
        var voice = new SubVoice(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 100, 60, 100));
        var midi = new MidiSegment(project) { LengthTicks = 1024 };
        midi.Notes.Add(new(project) { StartTick = 0, LengthTicks = 100, Key = 60, NoteOnVelocity = 100 });
        var projection = new TimelineRasterColumnProjection(0, 1024, 0, 0, .5, 512);
        var columns = new TimelineRasterColumnSummary[512];
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => logical.Notes.CreateQuerySnapshot()
            .AccumulateRasterColumns(projection, 0, 127, columns, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => voice.Events.CreateQuerySnapshot()
            .AccumulateNoteRasterColumns(projection, 0, 127, columns, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => voice.Events.CreateQuerySnapshot()
            .AccumulateEventRasterColumns(projection, columns, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => midi.Notes.CreateQuerySnapshot()
            .TryAccumulateRasterColumns(projection, 0, 127, columns, out _, cancellation.Token));
    }

    [Fact]
    public void LogicalTemplateSpatialPagesCancelInsideAnExecutingScanAndPreserveUncanceledColumns()
    {
        using var cancellation = new CancellationTokenSource();
        bool armed = false; int visited = 0;
        var values = Enumerable.Range(0, 4096).ToArray();
        var page = new PagedTimelineValuePage<int>(values,
            value => value * 2L, value => value * 2L + 1, value => value % 128, value => (ulong)value,
            null, value =>
            {
                if (armed && ++visited == 300) cancellation.Cancel();
                return value % 128 / 127d;
            }, null);
        var index = PagedTimelineSpatialBlockIndex<int>.Create([page]);
        var projection = new TimelineRasterColumnProjection(0, 8192, 0, 0, .0625, 512);
        var expected = new TimelineRasterColumnSummary[512];
        int expectedWork = index.AccumulateRasterColumns(projection, 0, 127, ulong.MaxValue, expected);
        var actual = new TimelineRasterColumnSummary[512];
        armed = true;
        Assert.Throws<OperationCanceledException>(() => index.AccumulateRasterColumns(
            projection, 0, 127, ulong.MaxValue, actual, cancellation.Token));
        Assert.InRange(visited, 300, 556);
        armed = false; Array.Clear(actual);
        int actualWork = index.AccumulateRasterColumns(projection, 0, 127, ulong.MaxValue, actual, CancellationToken.None);
        Assert.Equal(expectedWork, actualWork); Assert.Equal(expected, actual);
    }

    [Fact]
    public void DirectOverlayExclusionScanChecksCancellationEvenWhenEveryRecordIsFiltered()
    {
        using var project = new MidoraProject(192);
        var segment = new MidiSegment(project) { LengthTicks = 8192 };
        segment.Notes.AddRange(Enumerable.Range(0, 4096).Select(i => new DirectMidiNote(project)
        { StartTick = i * 2, LengthTicks = 1, Key = i % 128, NoteOnVelocity = 100 }));
        var snapshot = segment.Notes.CreateQuerySnapshot();
        var projection = new TimelineRasterColumnProjection(0, 8192, 0, 0, .0625, 512);
        var columns = new TimelineRasterColumnSummary[512];
        using var cancellation = new CancellationTokenSource();
        var excluded = new CancelingExclusionSet(cancellation, cancelAfter: 300, excludeAll: true);
        Assert.Throws<OperationCanceledException>(() => snapshot.TryAccumulateRasterColumnsExcluding(
            projection, 0, 127, columns, excluded, out _, cancellation.Token));
        Assert.InRange(excluded.Reads, 300, 556);
    }

    [Fact]
    public void PureMidiPackDecodedPageScanCancelsWithinTwoHundredFiftySixRecords()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "CompilerRuns", "raster-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var project = new MidoraProject(192);
            var segment = new MidiSegment(project) { LengthTicks = 8192 };
            using var writer = new PureMidiContentPackWriter(Path.Combine(directory, "notes.mpk"));
            for (int i = 0; i < 4096; i++)
                writer.AddNote(segment.Id, new(project.AllocateStableId(), i * 2, 1, i % 128, 100, 0, i * 2, i * 2 + 1));
            using var pack = writer.Complete();
            var source = Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(pack.GetSegmentSource(segment.Id));
            var projection = new TimelineRasterColumnProjection(0, 8192, 0, 0, .0625, 512);
            var expected = new TimelineRasterColumnSummary[512];
            Assert.True(source.TryAccumulateNoteRasterColumns(projection, 0, 127, expected, null, out int expectedWork));
            var actual = new TimelineRasterColumnSummary[512];
            using var cancellation = new CancellationTokenSource();
            var excluded = new CancelingExclusionSet(cancellation, cancelAfter: 300, excludeAll: false);
            Assert.Throws<OperationCanceledException>(() => source.TryAccumulateNoteRasterColumns(
                projection, 0, 127, actual, excluded, out _, cancellation.Token));
            Assert.InRange(excluded.Reads, 300, 556);
            Array.Clear(actual);
            Assert.True(source.TryAccumulateNoteRasterColumns(projection, 0, 127, actual, null, out int actualWork, CancellationToken.None));
            Assert.Equal(expectedWork, actualWork); Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CancelingExclusionSet(CancellationTokenSource cancellation, int cancelAfter, bool excludeAll) : IReadOnlySet<MidoraId>
    {
        public int Reads { get; private set; }
        public int Count => 1;
        public bool Contains(MidoraId item)
        {
            if (++Reads == cancelAfter) cancellation.Cancel();
            return excludeAll;
        }
        public IEnumerator<MidoraId> GetEnumerator() { yield return new(long.MaxValue); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<MidoraId> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<MidoraId> other) => throw new NotSupportedException();
    }
}
