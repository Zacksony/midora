using System.Collections;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class ConductorProjectionTests
{
    [Fact]
    public void ProjectionsSeparateTempoAndFourMetaLanesWithoutMaterializingRows()
    {
        MidoraProject project = new(192);
        project.Conductor.Tempos.Add(new(project, 100, 600m));
        project.Conductor.KeySignatures.Add(new(project, 40, -3, true));
        ProjectMarker marker = new(project, 70, "Verse");
        project.Conductor.Markers.Add(marker);
        project.Conductor.EndMarker = new(project, 500);
        ConductorTimelineProjection projection = new(project.Conductor, 1, 0, 240);

        Assert.Empty(projection.TempoSnapshot.Items);
        Assert.Empty(projection.MetaSnapshot.Items);
        Assert.Equal(2, projection.TempoSource.Count);
        Assert.Equal(4, projection.MetaSource.Count);
        Assert.Equal(6, projection.ArrangementSource.Count);
        TimelineRenderItem tempo = projection.TempoSource.EnumerateAll().Last();
        Assert.Equal(TimelineItemKind.TempoPoint, tempo.Kind);
        Assert.Equal(0xff58c487U, tempo.AccentColor);
        Assert.True(projection.ArrangementSource.TryGetById(tempo.Id, out TimelineRenderItem arrangementTempo));
        Assert.Equal(tempo.AccentColor, arrangementTempo.AccentColor);
        Assert.Equal(2.5, tempo.Value); // The displayed axis is not a legal BPM clamp.
        Assert.Equal("600 BPM", ConductorRenderItemSource.GetDisplayLabel(tempo));
        Assert.True(projection.MetaSource.TryGetById(marker.Id, out TimelineRenderItem value));
        Assert.Equal(2, value.Lane);
        Assert.Equal("Verse", value.Label);
        Assert.All(projection.ArrangementSource.EnumerateAll(), item =>
        {
            Assert.Equal(0, item.Lane);
            Assert.True(item.State.HasFlag(TimelineItemState.HitTestDisabled));
        });
        Assert.All(projection.RulerSnapshot.EnumerateAllItems(), item =>
            Assert.True(item.State.HasFlag(TimelineItemState.HitTestDisabled)));
    }

    [Fact]
    public void StepHoldsThenChangesVerticallyAndContinuesPastLastPoint()
    {
        MidoraProject project = new(192);
        project.Conductor.Tempos.Add(new(project, 100, 60m));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        TimelineRasterBuffer first = Raster(projection, 1, 0);
        int gutter = TimelineEventPointTileRasterizer.GetGutter(1);
        Assert.NotEqual(0, Alpha(first, gutter + 50, gutter + 128));
        Assert.NotEqual(0, Alpha(first, gutter + 150, gutter + 192));
        Assert.NotEqual(0, Alpha(first, gutter + 100, gutter + 160));
        Assert.Equal(0, Alpha(first, gutter + 50, gutter + 160));

        TimelineRasterBuffer afterLastPoint = Raster(projection, 1, 1);
        Assert.Equal(0, afterLastPoint.CandidateCount);
        Assert.NotEqual(0, Alpha(afterLastPoint, gutter + 40, gutter + 192));
    }

    [Fact]
    public void DenseTempoColumnRetainsMinimumMaximumAndLastHeldState()
    {
        MidoraProject project = new(192);
        for (int tick = 1; tick <= 1000; tick++)
            project.Conductor.Tempos.Add(new(project, tick, (tick & 1) == 0 ? 60m : 180m));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        TimelineRasterBuffer tile = Raster(projection, .01, 0);
        int gutter = TimelineEventPointTileRasterizer.GetGutter(1);
        Assert.NotEqual(0, Alpha(tile, gutter + 3, gutter + 64));
        Assert.NotEqual(0, Alpha(tile, gutter + 3, gutter + 192));
        Assert.NotEqual(0, Alpha(tile, gutter + 30, gutter + 192));
        Assert.Equal(1001, tile.CandidateCount);
    }

    [Fact]
    public void DenseSelectedTempoColumnUsesRedEnvelopeWithoutRequiringPointGlyphs()
    {
        MidoraProject project = new(192);
        for (int tick = 1; tick <= 1000; tick++)
            project.Conductor.Tempos.Add(new(project, tick, (tick & 1) == 0 ? 60m : 180m));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        var selection = new TimelineSelectionSnapshot(1, project.Conductor.Tempos.Select(x => x.Id), null);
        TimelineRasterBuffer tile = TimelineEventPointTileRasterizer.Rasterize(
            projection.TempoSnapshot, selection, .01, 256, 0, 0,
            1, 1, Colors.SteelBlue, Colors.Red, Colors.White);
        int gutter = TimelineEventPointTileRasterizer.GetGutter(1);
        int pixel = ((gutter + 128) * tile.Width + gutter + 3) * 4;
        Assert.Equal(255, tile.Pixels[pixel + 2]);
        Assert.Equal(0, tile.Pixels[pixel]);
        Assert.Equal(0, tile.Pixels[pixel + 1]);
        Assert.Equal(255, tile.Pixels[pixel + 3]);
    }

    [Fact]
    public void ProjectionAndLocalFingerprintAreCacheOnlyOnColdExternalPages()
    {
        MidoraProject project = new(192);
        TempoChange[] values = Enumerable.Range(0, 10000)
            .Select(index => new TempoChange(project, index * 12L, 60 + index % 180)).ToArray();
        CountingSource source = new(values);
        project.Conductor.Tempos.AdoptSource(source);
        source.Reads = 0;
        ConductorTimelineProjection projection = new(project.Conductor, 1, 0, 240);
        _ = projection.TempoSource.ContentFingerprint;
        _ = projection.TempoSource.MaximumEndTick;
        _ = projection.TempoSource.GetRangeFingerprint(40000, 42000, 0, 1);
        Assert.Equal(0, source.Reads);
        List<TimelineRenderItem> items = [];
        Assert.False(projection.TempoSource.TryQueryIntoCached(40000, 42000, 0, 1, items));
        Assert.Empty(items);
        Assert.Equal(0, source.Reads);
        TimelineRenderItem? closest = projection.TempoSource.FindNearest(40008, 30, 0,
            (60 + (3334 % 180)) / 240d, .01, CancellationToken.None);
        Assert.NotNull(closest);
        Assert.Equal(40008, closest.Value.StartTick);
        Assert.True(source.Reads > 0);
    }

    [Fact]
    public void LargeSelectionVisitorStreamsBeforeReadingTheRestOfTheSource()
    {
        MidoraProject project = new(192);
        TempoChange[] values = Enumerable.Range(0, 10000).Select(i => new TempoChange(project, i, 120)).ToArray();
        CountingSource source = new(values);
        project.Conductor.Tempos.AdoptSource(source);
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        var ids = CompressedMidoraIdSet.Create(values.Select(x => x.Id));
        source.Reads = 0;
        ITimelineRenderItemSource polymorphic = projection.TempoSource;
        Assert.Throws<StopVisitingException>(() => polymorphic.VisitByIds(ids, _ => throw new StopVisitingException()));
        Assert.InRange(source.Reads, 1, 2);
    }

    [Fact]
    public void ConductorOverviewDensityUsesTheExternalSource()
    {
        MidoraProject project = new(192);
        for (int tick = 1; tick <= 100; tick++) project.Conductor.Tempos.Add(new(project, tick, 120));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        int[] density = new int[32];
        projection.TempoSource.AccumulateOverviewDensity(128, density);
        Assert.Equal(101, density.Sum());
        Assert.True(density[0] > 0);
        Assert.True(density[25] > 0);
    }

    [Fact]
    public void OldRootAndFarTileFingerprintRemainStableAfterLocalEdit()
    {
        MidoraProject project = new(192);
        for (int index = 1; index < 1024; index++) project.Conductor.Tempos.Add(new(project, index * 100L, 120));
        var before = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        TempoChange original = project.Conductor.Tempos[900];
        project.Conductor.Tempos[900] = original with { BeatsPerMinute = 180 };
        var after = new ConductorTimelineProjection(project.Conductor, 2, 0, 240);
        Assert.Equal(before.TempoSource.GetRangeFingerprint(100, 200, 0, 1),
            after.TempoSource.GetRangeFingerprint(100, 200, 0, 1));
        Assert.NotEqual(before.TempoSource.GetRangeFingerprint(90001, 90050, 0, 1),
            after.TempoSource.GetRangeFingerprint(90001, 90050, 0, 1));
        Assert.True(before.TempoSource.TryGetById(original.Id, out TimelineRenderItem oldValue));
        Assert.True(after.TempoSource.TryGetById(original.Id, out TimelineRenderItem newValue));
        Assert.Equal(120, oldValue.SecondaryValue);
        Assert.Equal(180, newValue.SecondaryValue);
    }

    [Fact]
    public void TempoUsesEventLaneShiftCopyAndPointerPolicies()
    {
        Assert.True(TimelineToolPolicy.RequestsTimeLockedItemMove(TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes, TimelineItemKind.TempoPoint, TimelineItemEditKind.Move, ModifierKeys.Shift));
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes, TimelineItemKind.TempoPoint, TimelineItemEditKind.Move));
        Assert.Equal(TimelinePointerIntent.ResizeVertical, TimelineToolPolicy.GetPointerIntent(
            TimelineToolMode.Draw, TimelineSurfaceMode.EventLanes, true, TimelineItemKind.TempoPoint, false));
    }

    [Fact]
    public void SparseMetadataLabelsDoNotDisappearAtTheLaterPhaseOfLabelCells()
    {
        MidoraProject project = new(192);
        project.Conductor.Markers.Add(new(project, 75, "Late in the cell"));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        var key = MetaKey(projection, 0);
        _ = TimelineConductorMetaRasterizer.Rasterize(projection.MetaSnapshot, null,
            1, 44, 0, 2, 1, 1, Colors.Blue, Colors.Red, Colors.Black, key, CancellationToken.None);

        Assert.True(TimelineConductorMetaRasterizer.TryGetLabels(key, out var labels));
        ConductorMetaLabel label = Assert.Single(labels!);
        Assert.Equal("Late in the cell", label.Text);
        Assert.InRange(label.DeviceX, 4, 24);
    }

    [Fact]
    public void MetadataLabelCellsCrossingTilesHaveOneRepresentativeAndLocalInvalidation()
    {
        MidoraProject project = new(192);
        project.Conductor.Markers.Add(new(project, 257, "Before"));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);
        var left = MetaKey(projection, 0);
        var right = MetaKey(projection, 1);
        foreach (var key in new[] { left, right })
            _ = TimelineConductorMetaRasterizer.Rasterize(projection.MetaSnapshot, null,
                1, 44, key.TileX, 2, 1, 1, Colors.Blue, Colors.Red, Colors.Black, key, CancellationToken.None);
        Assert.True(TimelineConductorMetaRasterizer.TryGetLabels(left, out var leftLabels));
        Assert.True(TimelineConductorMetaRasterizer.TryGetLabels(right, out var rightLabels));
        Assert.Equal("Before", Assert.Single(leftLabels!).Text);
        Assert.Empty(rightLabels!);

        project.Conductor.Markers[0] = project.Conductor.Markers[0] with { Name = "After" };
        var after = new ConductorTimelineProjection(project.Conductor, 2, 0, 240);
        Assert.NotEqual(left.ContentFingerprint, MetaKey(after, 0).ContentFingerprint);
    }

    [Fact]
    public void StreamingTraceMatchesExistingSamplerIncludingBackwardOverwriteAndRange()
    {
        TimelineValueTracePoint[] trace = [new(1, .1), new(15, .9), new(5, .3)];
        Dictionary<long, double> expected = [];
        TimelineValueTraceSampler.SampleInto(trace, expected, 4, false, null, 4, 16);
        Dictionary<long, double> actual = [];
        foreach (var point in TimelineValueTraceSampler.EnumerateSamples(trace, 4, false, null, 4, 16))
            actual[(long)point.Tick] = point.NormalizedValue;
        Assert.Equal(expected.OrderBy(x => x.Key), actual.OrderBy(x => x.Key));
    }

    [Fact]
    public void HundredMillionTickTraceIsLazyAndCancellationStopsSampling()
    {
        using CancellationTokenSource cancellation = new();
        using IEnumerator<TimelineValueTracePoint> iterator = TimelineValueTraceSampler.EnumerateSamples(
            [new(0, 0), new(100000000, 1)], 1, false, null,
            cancellationToken: cancellation.Token).GetEnumerator();
        Assert.True(iterator.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
    }

    private static TimelineRasterBuffer Raster(ConductorTimelineProjection projection, double scale, long tile) =>
        TimelineEventPointTileRasterizer.Rasterize(projection.TempoSnapshot, null, scale, 256, tile, 0,
            1, 1, Colors.SteelBlue, Colors.Red, Colors.White);
    private static byte Alpha(TimelineRasterBuffer buffer, int x, int y) => buffer.Pixels[(y * buffer.Width + x) * 4 + 3];
    private static TimelineRasterCacheKey MetaKey(ConductorTimelineProjection projection, long tile)
    {
        (long start, long end) = TimelineConductorMetaRasterizer.GetReadTickRange(1, tile, 1);
        return new(TimelineRasterLayer.ConductorMeta, projection.MetaSnapshot.ProjectionKey,
            projection.MetaSource.GetRangeFingerprint(start, end, 2, 3),
            BitConverter.DoubleToInt64Bits(1), BitConverter.DoubleToInt64Bits(44),
            tile, 2, 0xff0000ff, 0xffff0000, 0xff000000, 1024, 1024);
    }

    private sealed class CountingSource(TempoChange[] values) : IImmutableTimelineValueSource<TempoChange>
    {
        public int Reads;
        public int Count => values.Length;
        public int PageCapacity => 128;
        public TempoChange this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public ReadOnlyMemory<TempoChange> ReadPage(int pageIndex)
        {
            Reads++;
            int start = pageIndex * PageCapacity;
            return values.AsMemory(start, Math.Min(PageCapacity, Count - start));
        }
        public IEnumerator<TempoChange> GetEnumerator() => ((IEnumerable<TempoChange>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class StopVisitingException : Exception;
}
