using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Desktop.Presentation.Tests;

public sealed class ConductorRenderingPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MillionTempoAndMarkerRecordsUseBoundedRasterOutputAcrossLocalAndWholeSongZooms()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MIDORA_CONDUCTOR_RENDER_PERF"), "1", StringComparison.Ordinal))
            return;
        const int count = 1_000_000;
        MidoraProject project = new(192);
        TempoChange[] tempos = new TempoChange[count];
        ProjectMarker[] markers = new ProjectMarker[count];
        for (int i = 0; i < count; i++)
        {
            tempos[i] = new(project, i, 60 + i * 37 % 180);
            markers[i] = new(project, i, "Section");
        }
        project.Conductor.Tempos.AdoptSource(new ArraySource<TempoChange>(tempos));
        project.Conductor.Markers.AdoptSource(new ArraySource<ProjectMarker>(markers));
        var projection = new ConductorTimelineProjection(project.Conductor, 1, 0, 240);

        // Warm code paths only. The measured whole-song cases still visit every
        // real domain event rather than testing synthetic constant summaries.
        _ = Tempo(.1, 100);
        _ = Meta(.1, 100);
        Measure("Tempo whole song", 256d / count, 0, Tempo, expectedMinimumCandidates: count);
        Measure("Meta whole song", 256d / count, 0, Meta, expectedMinimumCandidates: count);
        foreach (double scale in new[] { .001, .01, .1 })
        {
            long tile = (long)(500_000 * scale / 256);
            Measure($"Tempo local scale={scale.ToString(CultureInfo.InvariantCulture)}", scale, tile, Tempo, 1);
            Measure($"Meta local scale={scale.ToString(CultureInfo.InvariantCulture)}", scale, tile, Meta, 1);
        }

        TimelineRasterBuffer Tempo(double scale, long tile) => TimelineEventPointTileRasterizer.Rasterize(
            projection.TempoSnapshot, null, scale, 256, tile, 0, 1, 1, Colors.SteelBlue, Colors.Red, Colors.Black);
        TimelineRasterBuffer Meta(double scale, long tile)
        {
            (long start, long end) = TimelineConductorMetaRasterizer.GetReadTickRange(scale, tile, 1);
            var key = new TimelineRasterCacheKey(TimelineRasterLayer.ConductorMeta, projection.MetaSnapshot.ProjectionKey,
                projection.MetaSource.GetRangeFingerprint(start, end, 2, 3),
                BitConverter.DoubleToInt64Bits(scale), BitConverter.DoubleToInt64Bits(44),
                tile, 2, 0xff4682b4, 0xffff0000, 0xff000000, 1024, 1024);
            TimelineRasterBuffer raster = TimelineConductorMetaRasterizer.Rasterize(projection.MetaSnapshot,
                null, scale, 44, tile, 2, 1, 1, Colors.SteelBlue, Colors.Red, Colors.Black, key, CancellationToken.None);
            Assert.True(TimelineConductorMetaRasterizer.TryGetLabels(key, out var labels));
            Assert.InRange(labels!.Length, 0, 4);
            return raster;
        }
        void Measure(string name, double scale, long tile, Func<double, long, TimelineRasterBuffer> render, int expectedMinimumCandidates)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch elapsed = Stopwatch.StartNew();
            TimelineRasterBuffer raster = render(scale, tile);
            elapsed.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine($"{name}: {elapsed.Elapsed.TotalMilliseconds:F2} ms; candidates={raster.CandidateCount:N0}; allocated={allocated:N0} B; raster={raster.ByteSize:N0} B");
            Assert.InRange(raster.CandidateCount, expectedMinimumCandidates, count);
            Assert.InRange(raster.ByteSize, 1, 512 * 1024);
            Assert.InRange(allocated, 1, 32L * 1024 * 1024);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), name);
        }
    }

    private sealed class ArraySource<T>(T[] values) : IImmutableTimelineValueSource<T>
    {
        public int Count => values.Length;
        public int PageCapacity => 128;
        public T this[int index] => values[index];
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            int start = pageIndex * PageCapacity;
            return values.AsMemory(start, Math.Min(PageCapacity, Count - start));
        }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
