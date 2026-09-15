using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

/// <summary>
/// Opt-in, real WPF memory probe for the large-MIDI presentation path. It is
/// intentionally excluded from normal cost by requiring an explicit sample
/// path, but remains repeatable for future regressions.
/// </summary>
[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class WpfMemoryProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void OptInLargeMidiMeasuresColdForegroundFrameLatency()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_WPF_MEMORY_SAMPLE_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The WPF performance sample does not exist.", path);

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        try
        {
            TimelineWorkspaceViewModel arrangement = new(
                WorkspaceKey.ForType(WorkspaceKind.Arrangement),
                "Arrangement",
                TimelineWorkspaceMode.Arrangement);
            arrangement.Rebuild(imported.Project, revision: 1);
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            TimelineWorkspaceViewModel editor = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            editor.Rebuild(imported.Project, revision: 2);

            (
                double ArrangementInitialMs,
                double ArrangementNewRegionMs,
                double ArrangementZoomMs,
                double PianoInitialMs,
                double PianoNewRegionMs,
                double PianoZoomMs) timings = default;
            TimelineSurfaceRenderPhaseTiming pianoInitialPhases = default;
            TimelineSurfaceRenderPhaseTiming pianoNewRegionPhases = default;
            TimelineSurfaceRenderPhaseTiming pianoZoomPhases = default;
            RunOnSta(() =>
            {
                TimelineRasterCacheSession.Clear();
                TimelineSurface arrangementSurface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.Arrangement,
                    Snapshot = arrangement.Snapshot,
                    TickSpan = 3_072,
                    LaneHeight = 60,
                    GridVisible = false
                };
                Layout(arrangementSurface);
                timings.ArrangementInitialMs = RenderOne(arrangementSurface);
                arrangementSurface.StartTick = Math.Max(
                    0,
                    arrangement.Snapshot!.MaximumEndTick / 2);
                timings.ArrangementNewRegionMs = RenderOne(arrangementSurface);
                arrangementSurface.TickSpan = 6_144;
                timings.ArrangementZoomMs = RenderOne(arrangementSurface);

                TimelineRasterCacheSession.Clear();
                TimelineSurface piano = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    Snapshot = editor.Snapshot,
                    TickSpan = 3_072,
                    LaneHeight = 6,
                    GridVisible = false,
                    CaptureRenderPhaseTimings = true
                };
                Layout(piano);
                timings.PianoInitialMs = RenderOne(piano);
                pianoInitialPhases = piano.LastRenderPhaseTiming;
                piano.StartTick = Math.Max(0, editor.Snapshot!.MaximumEndTick / 2);
                timings.PianoNewRegionMs = RenderOne(piano);
                pianoNewRegionPhases = piano.LastRenderPhaseTiming;
                piano.TickSpan = 6_144;
                timings.PianoZoomMs = RenderOne(piano);
                pianoZoomPhases = piano.LastRenderPhaseTiming;
            });

            output.WriteLine(
                "[cold-wpf] arrangementInitial={0:0.0}ms; arrangementNew={1:0.0}ms; "
                + "arrangementZoom={2:0.0}ms; pianoInitial={3:0.0}ms; "
                + "pianoNew={4:0.0}ms; pianoZoom={5:0.0}ms",
                timings.ArrangementInitialMs,
                timings.ArrangementNewRegionMs,
                timings.ArrangementZoomMs,
                timings.PianoInitialMs,
                timings.PianoNewRegionMs,
                timings.PianoZoomMs);
            output.WriteLine(
                "[dense-piano-phases] initial={0:0.00}/{1:0.00}/{2:0.00}ms; "
                + "new-region={3:0.00}/{4:0.00}/{5:0.00}ms; "
                + "zoom={6:0.00}/{7:0.00}/{8:0.00}ms (background/content/overlay)",
                pianoInitialPhases.Background.TotalMilliseconds,
                pianoInitialPhases.Content.TotalMilliseconds,
                pianoInitialPhases.Overlay.TotalMilliseconds,
                pianoNewRegionPhases.Background.TotalMilliseconds,
                pianoNewRegionPhases.Content.TotalMilliseconds,
                pianoNewRegionPhases.Overlay.TotalMilliseconds,
                pianoZoomPhases.Background.TotalMilliseconds,
                pianoZoomPhases.Content.TotalMilliseconds,
                pianoZoomPhases.Overlay.TotalMilliseconds);
            Assert.InRange(timings.ArrangementInitialMs, 0, 1_000);
            Assert.InRange(timings.ArrangementNewRegionMs, 0, 1_000);
            Assert.InRange(timings.ArrangementZoomMs, 0, 1_000);
            Assert.InRange(timings.PianoInitialMs, 0, 1_000);
            Assert.InRange(timings.PianoNewRegionMs, 0, 1_000);
            Assert.InRange(timings.PianoZoomMs, 0, 1_000);
            Assert.InRange(pianoInitialPhases.Content.TotalMilliseconds, 0, 250);
            Assert.InRange(pianoNewRegionPhases.Content.TotalMilliseconds, 0, 250);
            Assert.InRange(pianoZoomPhases.Content.TotalMilliseconds, 0, 250);
        }
        finally
        {
            TimelineRasterCacheSession.Clear();
            imported.Project.Dispose();
        }

        static void Layout(TimelineSurface surface)
        {
            surface.Measure(new Size(1_280, 720));
            surface.Arrange(new Rect(0, 0, 1_280, 720));
        }

        static double RenderOne(TimelineSurface surface)
        {
            surface.InvalidateVisual();
            Stopwatch watch = Stopwatch.StartNew();
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.Render,
                new Action(() => { }));
            watch.Stop();
            // Keep a real WPF rasterization in the probe, but do not include
            // RenderTargetBitmap's software composition cost in the UI-thread
            // OnRender latency measurement.
            RenderTargetBitmap target = new(1_280, 720, 96, 96, PixelFormats.Pbgra32);
            target.Render(surface);
            return watch.Elapsed.TotalMilliseconds;
        }
    }

    [Fact]
    public void OptInLargeMidiMeasuresProjectSnapshotsAndWpfRasterMemory()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_WPF_MEMORY_SAMPLE_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The WPF memory sample does not exist.", path);

        TimelineRasterCacheSession.Clear();
        MemoryReading baseline = ReadMemory(forceCollection: true);
        Stopwatch importWatch = Stopwatch.StartNew();
        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        importWatch.Stop();
        try
        {
            MemoryReading importedMemory = ReadMemory(forceCollection: true);
            Stopwatch arrangementWatch = Stopwatch.StartNew();
            TimelineWorkspaceViewModel arrangement = new(
                WorkspaceKey.ForType(WorkspaceKind.Arrangement),
                "Arrangement",
                TimelineWorkspaceMode.Arrangement);
            arrangement.Rebuild(imported.Project, revision: 1);
            arrangementWatch.Stop();
            MemoryReading arrangementMemory = ReadMemory(forceCollection: true);

            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            Stopwatch editorWatch = Stopwatch.StartNew();
            TimelineWorkspaceViewModel editor = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            editor.Rebuild(imported.Project, revision: 2);
            editorWatch.Stop();
            MemoryReading editorMemory = ReadMemory(forceCollection: true);

            MemoryReading rasterMemory = RenderActualWpfFrames(editor.Snapshot!);
            Write("baseline", baseline);
            Write($"import ({importWatch.Elapsed.TotalSeconds:0.000}s)", importedMemory);
            Write($"arrangement rebuild ({arrangementWatch.Elapsed.TotalMilliseconds:0.0}ms)", arrangementMemory);
            Write($"segment rebuild ({editorWatch.Elapsed.TotalMilliseconds:0.0}ms)", editorMemory);
            Write("frozen WPF piano tiles + RenderTargetBitmap", rasterMemory);

            Assert.True(arrangement.Snapshot!.SegmentPreviews.Count != 0);
            Assert.Empty(editor.Snapshot!.Items);
            Assert.True(editor.Snapshot.TotalItemCount > editor.Snapshot.Items.Count);
            Assert.InRange(
                arrangementMemory.ManagedHeapBytes - importedMemory.ManagedHeapBytes,
                long.MinValue,
                64L * 1024 * 1024);
            Assert.InRange(
                editorMemory.ManagedHeapBytes - arrangementMemory.ManagedHeapBytes,
                long.MinValue,
                64L * 1024 * 1024);
        }
        finally
        {
            TimelineRasterCacheSession.Clear();
            imported.Project.Dispose();
        }

        void Write(string phase, MemoryReading value) => output.WriteLine(
            "{0}: managed={1:N0} MiB, heap={2:N0} MiB, private={3:N0} MiB, working={4:N0} MiB, raster={5:N0} MiB/{6} entries/{7} in-flight",
            phase,
            value.ManagedHeapBytes / 1048576d,
            value.GcHeapBytes / 1048576d,
            value.PrivateBytes / 1048576d,
            value.WorkingSetBytes / 1048576d,
            value.RasterBytes / 1048576d,
            value.RasterEntries,
            value.RasterInFlight);
    }

    [Fact]
    public void OptInLargeMidiMeasuresContinuousPanZoomAcrossTheRightBlankBoundary()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_WPF_MEMORY_SAMPLE_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The WPF performance sample does not exist.", path);

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            TimelineWorkspaceViewModel editor = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            editor.Rebuild(imported.Project, revision: 1);

            double[] foregroundMilliseconds = [];
            double[] backgroundPhaseMilliseconds = [];
            double[] contentPhaseMilliseconds = [];
            double[] overlayPhaseMilliseconds = [];
            double[] totalPhaseMilliseconds = [];
            double coldConvergenceMilliseconds = 0;
            long cacheBytes = 0;
            RunOnSta(() =>
            {
                TimelineRasterCacheSession.Clear();
                const int width = 1_280;
                const int height = 720;
                long baseSpan = Math.Clamp(segment.LengthTicks / 200, 1_536, 24_576);
                TimelineSurface surface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    Snapshot = editor.Snapshot,
                    TickSpan = baseSpan,
                    StartTick = Math.Max(0, segment.ContentEndTick - baseSpan / 2),
                    LaneHeight = 6,
                    GridVisible = false,
                    RangeStartTick = segment.ContentOffsetTick,
                    RangeEndTick = segment.ContentEndTick,
                    CaptureRenderPhaseTimings = true
                };
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);

                Stopwatch convergence = Stopwatch.StartNew();
                target.Render(surface);
                WaitForRasterIdle(TimeSpan.FromSeconds(30));
                target.Render(surface);
                convergence.Stop();
                coldConvergenceMilliseconds = convergence.Elapsed.TotalMilliseconds;

                List<double> frames = new(120);
                List<double> backgroundPhases = new(120);
                List<double> contentPhases = new(120);
                List<double> overlayPhases = new(120);
                List<double> totalPhases = new(120);
                for (int frame = 0; frame < 120; frame++)
                {
                    // Repeatedly cross the exact Segment end while also moving
                    // between adjacent zoom values.  The visible blank half must
                    // remain O(1); it must not force a synchronous cold-page walk.
                    // Piano pixels use the current exact projection, while work
                    // queued for an obsolete zoom projection is canceled.
                    double phase = frame / 119d;
                    long span = Math.Max(1, checked((long)Math.Round(
                        baseSpan * (0.75 + 0.5 * Math.Abs(Math.Sin(phase * Math.PI * 6))))));
                    long oscillation = checked((long)Math.Round(
                        Math.Sin(phase * Math.PI * 10) * span * 0.35));
                    surface.TickSpan = span;
                    surface.StartTick = Math.Max(
                        0,
                        segment.ContentEndTick - span / 2 + oscillation);
                    Stopwatch frameWatch = Stopwatch.StartNew();
                    target.Render(surface);
                    Dispatcher.CurrentDispatcher.Invoke(
                        DispatcherPriority.Render,
                        new Action(() => { }));
                    frameWatch.Stop();
                    frames.Add(frameWatch.Elapsed.TotalMilliseconds);
                    backgroundPhases.Add(surface.LastRenderPhaseTiming.Background.TotalMilliseconds);
                    contentPhases.Add(surface.LastRenderPhaseTiming.Content.TotalMilliseconds);
                    overlayPhases.Add(surface.LastRenderPhaseTiming.Overlay.TotalMilliseconds);
                    totalPhases.Add(surface.LastRenderPhaseTiming.Total.TotalMilliseconds);
                }
                foregroundMilliseconds = frames.ToArray();
                backgroundPhaseMilliseconds = backgroundPhases.ToArray();
                contentPhaseMilliseconds = contentPhases.ToArray();
                overlayPhaseMilliseconds = overlayPhases.ToArray();
                totalPhaseMilliseconds = totalPhases.ToArray();
                WaitForRasterIdle(TimeSpan.FromSeconds(30));
                cacheBytes = TimelineRasterCacheSession.CurrentBytes;
                GC.KeepAlive(target);
                GC.KeepAlive(surface);
            });

            Array.Sort(foregroundMilliseconds);
            Array.Sort(backgroundPhaseMilliseconds);
            Array.Sort(contentPhaseMilliseconds);
            Array.Sort(overlayPhaseMilliseconds);
            Array.Sort(totalPhaseMilliseconds);
            double p50 = Percentile(foregroundMilliseconds, 0.50);
            double p95 = Percentile(foregroundMilliseconds, 0.95);
            double maximum = foregroundMilliseconds[^1];
            double contentP50 = Percentile(contentPhaseMilliseconds, 0.50);
            double contentP95 = Percentile(contentPhaseMilliseconds, 0.95);
            double contentMaximum = contentPhaseMilliseconds[^1];
            double backgroundP95 = Percentile(backgroundPhaseMilliseconds, 0.95);
            double backgroundMaximum = backgroundPhaseMilliseconds[^1];
            double overlayP95 = Percentile(overlayPhaseMilliseconds, 0.95);
            double overlayMaximum = overlayPhaseMilliseconds[^1];
            double phaseTotalP50 = Percentile(totalPhaseMilliseconds, 0.50);
            double phaseTotalP95 = Percentile(totalPhaseMilliseconds, 0.95);
            double phaseTotalMaximum = totalPhaseMilliseconds[^1];
            string measurement = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[pan-zoom-blank] cold-convergence={0:0.0}ms; p50={1:0.00}ms; "
                + "p95={2:0.00}ms; max={3:0.00}ms; content-phase="
                + "{4:0.00}/{5:0.00}/{6:0.00}ms; total-phase="
                + "{7:0.00}/{8:0.00}/{9:0.00}ms; background-p95/max="
                + "{10:0.00}/{11:0.00}ms; overlay-p95/max={12:0.00}/{13:0.00}ms; "
                + "raster={14:0.0}MiB",
                coldConvergenceMilliseconds,
                p50,
                p95,
                maximum,
                contentP50,
                contentP95,
                contentMaximum,
                phaseTotalP50,
                phaseTotalP95,
                phaseTotalMaximum,
                backgroundP95,
                backgroundMaximum,
                overlayP95,
                overlayMaximum,
                cacheBytes / 1048576d);
            output.WriteLine(measurement);
            Console.WriteLine(measurement);

            Assert.InRange(maximum, 0, 1_000);
            Assert.InRange(contentMaximum, 0, 250);
            Assert.InRange(phaseTotalMaximum, 0, 500);
            Assert.InRange(cacheBytes, 0, 256L * 1024 * 1024);
        }
        finally
        {
            TimelineRasterCacheSession.Clear();
            imported.Project.Dispose();
        }

        static double Percentile(double[] sorted, double value)
        {
            int index = Math.Clamp(
                (int)Math.Ceiling(sorted.Length * value) - 1,
                0,
                sorted.Length - 1);
            return sorted[index];
        }
    }

    private static MemoryReading RenderActualWpfFrames(TimelineRenderSnapshot snapshot)
    {
        MemoryReading result = default;
        RunOnSta(() =>
        {
            List<BitmapSource> tiles = [];
            const double pixelsPerTick = 0.25;
            const double pixelsPerLane = 6;
            HashSet<(long X, long Y)> coordinates = [];
            foreach (TimelineRenderItem item in snapshot.EnumerateAllItems()
                .Where(static value => value.Kind is TimelineItemKind.DirectMidiNote
                    or TimelineItemKind.LogicalNote
                    or TimelineItemKind.TemplateNote))
            {
                coordinates.Add((
                    (long)Math.Floor(item.StartTick * pixelsPerTick / TimelinePianoTileRasterizer.TileSize),
                    (long)Math.Floor(item.Lane * pixelsPerLane / TimelinePianoTileRasterizer.TileSize)));
                if (coordinates.Count == 64) break;
            }
            foreach ((long x, long y) in coordinates)
            {
                TimelineRasterBuffer buffer = TimelinePianoTileRasterizer.Rasterize(
                    snapshot,
                    pixelsPerTick,
                    pixelsPerLane,
                    x,
                    y,
                    Colors.LightGray,
                    Colors.OrangeRed);
                BitmapSource bitmap = BitmapSource.Create(
                    buffer.Width,
                    buffer.Height,
                    96,
                    96,
                    PixelFormats.Pbgra32,
                    null,
                    buffer.Pixels,
                    buffer.Stride);
                bitmap.Freeze();
                tiles.Add(bitmap);
            }

            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                Snapshot = snapshot,
                TickSpan = 3_072,
                LaneHeight = 6,
                GridVisible = false
            };
            surface.Measure(new Size(1_280, 720));
            surface.Arrange(new Rect(0, 0, 1_280, 720));
            RenderTargetBitmap target = new(1_280, 720, 96, 96, PixelFormats.Pbgra32);
            target.Render(surface);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            result = ReadMemory(forceCollection: true);
            GC.KeepAlive(tiles);
            GC.KeepAlive(target);
            GC.KeepAlive(surface);
        });
        return result;
    }

    private static void WaitForRasterIdle(TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (TimelineRasterCacheSession.InFlightCount != 0)
        {
            if (watch.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"The WPF raster scheduler did not become idle; "
                    + $"{TimelineRasterCacheSession.InFlightCount} request(s) remain.");
            }
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.Background,
                new Action(() => { }));
            Thread.Sleep(2);
        }
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.Render,
            new Action(() => { }));
    }

    private static MemoryReading ReadMemory(bool forceCollection)
    {
        if (forceCollection)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new(
            GC.GetTotalMemory(forceFullCollection: false),
            gc.HeapSizeBytes,
            process.PrivateMemorySize64,
            process.WorkingSet64,
            TimelineRasterCacheSession.CurrentBytes,
            TimelineRasterCacheSession.CompletedCount,
            TimelineRasterCacheSession.InFlightCount);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("The WPF memory probe did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly record struct MemoryReading(
        long ManagedHeapBytes,
        long GcHeapBytes,
        long PrivateBytes,
        long WorkingSetBytes,
        long RasterBytes,
        int RasterEntries,
        int RasterInFlight);
}
