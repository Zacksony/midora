using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Application;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class SelectionReadOptimizationTests
{
    [Fact]
    public void ScalePointSpanDoesNotIncludeSyntheticRenderWidth()
    {
        Assert.Equal(0, MainWindow.MeasurePointSelectionSpan([17, 17]));
        Assert.Equal(96, MainWindow.MeasurePointSelectionSpan([192, 96, 144]));
        Assert.Equal(0, MainWindow.MeasurePointSelectionSpan([]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MainWindow.MeasurePointSelectionSpan([96], cancellation.Token));
    }

    [Fact]
    public void PropertiesReadsDenseOwnerOnceForAllSameAndMixedFieldsWithoutAnIdIndex()
    {
        using MidoraProject project = new(480);
        CountingNoteSource source = new(8192);
        var ids = CompressedMidoraIdSet.Create(Enumerable.Range(1, source.Count).Select(i => new MidoraId(i)));
        var start = ObjectPropertiesProjection.PropertySummary<LogicalNoteSnapshotValue>.Create("start", "START", x => x.StartTick);
        var length = ObjectPropertiesProjection.PropertySummary<LogicalNoteSnapshotValue>.Create("length", "LENGTH", x => x.LengthTicks);
        var key = ObjectPropertiesProjection.PropertySummary<LogicalNoteSnapshotValue>.Create("key", "KEY", x => x.Note);
        var velocity = ObjectPropertiesProjection.PropertySummary<LogicalNoteSnapshotValue>.Create("velocity", "VELOCITY", x => x.Velocity);
        Assert.True(ObjectPropertiesProjection.SummarizePropertyOwner(project, source, ids,
            [start, length, key, velocity], knownOwner: true));
        Assert.Equal(8192, source.ReadValues);
        Assert.Equal(0, source.IdLookups);
        Assert.Equal("Mixed", start.Field().Value);
        Assert.Equal("12", length.Field().Value);
        Assert.Equal("Mixed", key.Field().Value);
        Assert.Equal("100", velocity.Field().Value);
    }

    [Fact]
    public void PropertiesOwnerHintNeverAcceptsPartiallyMatchingIdentities()
    {
        using MidoraProject project = new(480);
        CountingNoteSource source = new(8192);
        var ids = CompressedMidoraIdSet.Create(Enumerable.Range(1, 4095).Select(i => new MidoraId(i)).Append(new(90000)));
        Assert.False(ObjectPropertiesProjection.SummarizePropertyOwner(project, source, ids, [], knownOwner: true));
        Assert.Equal(8192, source.ReadValues);
    }

    [Fact]
    public void IncorrectPropertiesHintFallsBackToTheActualStableIdentityOwner()
    {
        using MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Properties owner" };
        Segment segment = new(project) { LengthTicks = 1000 };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        LogicalNote first = new(project) { StartTick = 0, LengthTicks = 10, Note = 60, Velocity = 100 };
        LogicalNote second = new(project) { StartTick = 50, LengthTicks = 10, Note = 60, Velocity = 100 };
        segment.Notes.AddRange([first, second]);
        var selection = new ObjectPropertiesSelectionContext(TimelineWorkspaceMode.Segment, false, segment.Id,
            CompressedMidoraIdSet.Create([first.Id, second.Id]),
            new(WorkspaceTimelineSelectionKind.LogicalParameterPoint, segment.Id, new MidoraId(999999)));
        var result = ObjectPropertiesProjection.ReadMultiSelection(project, selection, default, null);
        Assert.Equal("2 Logical Notes", result.Title);
        Assert.Equal("60", Assert.Single(result.Fields, field => field.Key == "batch.note.number").Value);
    }

    [Fact]
    public void ScaleReusesOnlyExactCurrentDocumentAndSelectionMetrics()
    {
        MetricsWorkspace workspace = new();
        workspace.Selection.ApplyRange([new(11), new(12)], WorkspaceSelectionRangeMode.Replace);
        var ids = workspace.Selection.SharedIds;
        workspace.PresentationDocumentRevision = 4;
        workspace.PublishMaterializedSelection(new Dictionary<TimelineItemKind, TimelineSelectionMetrics>
        {
            [TimelineItemKind.LogicalNote] = new(2, 5, 200, 60, 61, 1, 100, default)
        }, metricsAreComplete: true);
        Assert.True(MainWindow.TryGetCurrentSelectionSpan(workspace, 4, ids, TimelineItemKind.LogicalNote, out long span));
        Assert.Equal(195, span);
        Assert.False(MainWindow.TryGetCurrentSelectionSpan(workspace, 5, ids, TimelineItemKind.LogicalNote, out _));
        Assert.False(MainWindow.TryGetCurrentSelectionSpan(workspace, 4,
            CompressedMidoraIdSet.Create([new(11), new(13)]), TimelineItemKind.LogicalNote, out _));
        workspace.Selection.Replace(new MidoraId(11));
        Assert.False(MainWindow.TryGetCurrentSelectionSpan(workspace, 4, ids, TimelineItemKind.LogicalNote, out _));
        workspace.CancelBackgroundPresentationWork();
    }

    private sealed class MetricsWorkspace() : WorkspaceViewModel(
        WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new MidoraId(10)), "Segment")
    {
        public override void Rebuild(MidoraProject project, long revision) { }
    }

    private sealed class CountingNoteSource(int count) : ITimelineObjectSource<LogicalNoteSnapshotValue>
    {
        public int Count => count;
        public long SourceRevision => 1;
        public int PageCapacity => 4096;
        public int ReadValues { get; private set; }
        public int IdLookups { get; private set; }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            IdLookups++;
            ordinal = checked((int)id.Value - 1);
            return ordinal >= 0 && ordinal < count;
        }
        public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) =>
            throw new InvalidOperationException("A dense aggregate must not construct an identity index.");
        public bool TryGetPageByOrdinal(int firstOrdinal, int requestedCount, out TimelineObjectPage<LogicalNoteSnapshotValue> page)
        {
            if (firstOrdinal < 0 || firstOrdinal >= count) { page = default; return false; }
            int length = Math.Min(Math.Min(requestedCount, PageCapacity), count - firstOrdinal);
            var values = new LogicalNoteSnapshotValue[length];
            for (int i = 0; i < length; i++)
            {
                int ordinal = firstOrdinal + i;
                values[i] = new(new(ordinal + 1), ordinal, 12, ordinal == count - 1 ? 61 : 60, 100);
            }
            ReadValues += length;
            page = new(SourceRevision, firstOrdinal, values);
            return true;
        }
        public int FindOrdinalAtOrAfterTick(long tick) => throw new NotSupportedException();
        public IEnumerable<LogicalNoteSnapshotValue> QueryTickRange(TimelineObjectRangeQuery query) => throw new NotSupportedException();
        public void Prefetch(TimelineObjectRangeQuery query, CancellationToken token = default) { }
    }
}
