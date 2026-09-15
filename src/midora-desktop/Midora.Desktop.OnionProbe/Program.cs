using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop.OnionProbe;

/// <summary>Isolated, non-interactive WPF probe with the actual App resource scope. No visible window or audio device.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--inspect-raw-notes", var midiPath])
            {
                var imported = MidiProjectImportService.ImportFile(midiPath, Path.GetFileNameWithoutExtension(midiPath));
                using var project = imported.Project;
                using var compiler = new MidoraCompiler();
                var canonical = compiler.CompileFull(project);
                foreach (var track in project.PureMidiTracks)
                foreach (var segment in track.Segments)
                foreach (var e in segment.ChannelEvents.QueryOrderedValues(0, long.MaxValue))
                    if (e.Kind == DirectMidiChannelEventKind.NoteOn && e.Data2 != 0)
                    {
                        Console.WriteLine($"Raw endpoint: {track.Name}, segment end={segment.LengthTicks}, {e}");
                        foreach (var page in canonical.QueryEventPages(Math.Max(0, e.Tick - 1), e.Tick + 2))
                        foreach (var actual in page.Items)
                            if (actual.Source.TrackId == track.Id && actual.Message.Byte1 == e.Data1
                                && actual.Message.MessageType is Midora.Midi.MidiMessageType.NoteOn or Midora.Midi.MidiMessageType.NoteOff)
                                Console.WriteLine($"Canonical neighbor: {actual.Tick}, {actual.Message.MessageType} {actual.Message.Byte1} {actual.Message.Byte2}, order={actual.SmfEventOrder}, source={actual.Source.DirectMidiObjectId}");
                    }
                return 0;
            }
            Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
            var app = new App();
            app.InitializeComponent(); // Deliberately do not Run / invoke startup.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dialog = new OnionSettingsDialog(.3);
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new(480, 244)); content.Arrange(new Rect(0, 0, 480, 244)); content.UpdateLayout();
            if (dialog.FontFamily.Source != "/Midora.Desktop.Presentation;Component/Assets/Fonts/Sora/#Sora")
                throw new InvalidOperationException("The Onion dialog did not resolve the embedded UI font.");
            dialog.Close();
            var sources = new OnionSourcesDialog([new(new(1), "Source", 0xff609080, true)], "Select Onion Tracks");
            content = (FrameworkElement)sources.Content;
            content.Measure(new(510, 510)); content.Arrange(new Rect(0, 0, 510, 510)); content.UpdateLayout();
            if (sources.FontFamily.Source != "/Midora.Desktop.Presentation;Component/Assets/Fonts/Sora/#Sora")
                throw new InvalidOperationException("The source dialog did not resolve the embedded UI font.");
            sources.Close();
            CheckToolbarTheme(app);
            CheckGhostRows();
            foreach (int count in new[] { 100, 200, 400 }) Check(count);
            foreach (string path in args) CheckLargeMidi(path);
            Console.WriteLine("Onion WPF/App theme probe passed; no windows shown.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void CheckToolbarTheme(App app)
    {
        var normal = new Button { Style = (Style)app.FindResource("Button.TimelineIcon"),
            Content = new FluentIcon { Width = 13, Height = 13, Data = (Geometry)app.FindResource("Fluent.ZoomIn20Regular") } };
        var onion = new Button { Style = (Style)app.FindResource("Button.TimelineOption"), Tag = false,
            Content = new FluentIcon { Width = 13, Height = 13, Data = (Geometry)app.FindResource("Fluent.LayerDiagonal20Regular") } };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(normal); panel.Children.Add(onion);
        panel.Measure(new(100, 38)); panel.Arrange(new Rect(0, 0, 100, 38)); panel.UpdateLayout();
        if (normal.ActualWidth != 26 || onion.ActualWidth != 26 || normal.ActualHeight != 24 || onion.ActualHeight != 24)
            throw new InvalidOperationException("Onion and zoom toolbar sizes differ.");
        var chrome = (Border)onion.Template.FindName("Chrome", onion);
        var inactive = chrome.Background;
        onion.Tag = true; panel.UpdateLayout();
        if (chrome.Background == inactive || chrome.Background != app.FindResource("Brush.Red.Subtle")
            || onion.Foreground != app.FindResource("Brush.Red.Hover"))
            throw new InvalidOperationException("The ordinary Onion Button does not reflect the enabled preset.");
        onion.Tag = false; panel.UpdateLayout();
        if (chrome.Background != inactive) throw new InvalidOperationException("Onion active highlight did not clear.");
        Console.WriteLine("Actual App theme: layer/zoom 26x24, 13px icons, enabled/disabled preset highlight passed.");
    }
    private static void CheckGhostRows()
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(project) { Name = "Green", MidiChannelRootId = root.Id, Color = new(0, 255, 0) };
        project.PureMidiTracks.Add(track); project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var segment = new MidiSegment(project) { LengthTicks = 192 }; track.Segments.Add(segment);
        segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 96, Key = 60, NoteOnVelocity = 100 });
        var vm = new AllTracksWorkspaceViewModel(_ => { }); vm.Rebuild(project, 0);
        var timeline = new TimelineSurface { SurfaceMode = TimelineSurfaceMode.PianoRoll, Snapshot = vm.Snapshot,
            OnionSnapshot = vm.OnionSnapshot, OnionIsPrimaryLayer = true, CanEdit = false,
            FirstLane = 60, TickSpan = 192, GridVisible = false };
        try
        {
            foreach (int keyHeight in new[] { 3, 9, 15, 32 })
            foreach (double dpi in new[] { 96d, 120d, 144d, 192d })
            {
                timeline.LaneHeight = keyHeight;
                timeline.Measure(new(800, 600)); timeline.Arrange(new Rect(0, 0, 800, 600)); timeline.UpdateLayout();
                double scale = dpi / 96;
                int width = (int)(800 * scale), height = (int)(600 * scale);
                var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                Wait(() => { timeline.InvalidateVisual(); timeline.UpdateLayout(); bitmap.Render(timeline); return timeline.OnionMissingTileCount == 0; });
                byte[] pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                int column = (int)(200 * scale);
                var coloredRows = Enumerable.Range(0, height).Where(y =>
                { int i = (y * width + column) * 4; return pixels[i + 1] > pixels[i] + 30 && pixels[i + 1] > pixels[i + 2] + 30; }).ToArray();
                int first = (int)Math.Floor((24 + (67 - timeline.FirstLane) * keyHeight) * scale);
                int last = (int)Math.Ceiling((24 + (68 - timeline.FirstLane) * keyHeight) * scale) - 1;
                if (coloredRows.Length == 0 || Math.Abs(coloredRows[0] - first) > 1 || Math.Abs(coloredRows[^1] - last) > 1)
                    throw new InvalidOperationException($"Ghost key row mismatch: height {keyHeight}, DPI {dpi}, expected {first}..{last}, actual {string.Join(',', coloredRows)}");
            }
            Console.WriteLine("Ghost row pixel alignment passed: 3/9/15/32 DIP keys, 96/120/144/192 DPI render targets.");
        }
        finally { vm.Dispose(); timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
    }
    private static void Check(int count)
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Shared" }; project.MidiChannelRoots.Add(root);
        for (int i = 0; i < count; i++)
        {
            var track = new PureMidiTrack(project) { Name = $"Track {i + 1}", MidiChannelRootId = root.Id,
                Color = new((byte)(70 + i % 80), 120, 160) };
            project.PureMidiTracks.Add(track); project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            var segment = new MidiSegment(project) { LengthTicks = 3072 }; track.Segments.Add(segment);
            for (int n = 0; n < 100; n++) segment.Notes.Add(new(project)
                { StartTick = n * 24, LengthTicks = 12, Key = 40 + (i + n) % 40, NoteOnVelocity = 100 });
        }
        var vm = new AllTracksWorkspaceViewModel(_ => { }); vm.Rebuild(project, 0);
        var view = new AllTracksView { DataContext = vm };
        var status = (System.Windows.Controls.TextBlock)view.FindName("StatusText");
        if (status.Foreground != view.FindResource("Brush.Text.Primary"))
            throw new InvalidOperationException("All Tracks status did not resolve the shared theme foreground.");
        var timeline = (TimelineSurface)view.FindName("Timeline");
        long startBytes = GC.GetTotalMemory(false);
        Stopwatch watch = Stopwatch.StartNew();
        view.Measure(new(1280, 800)); view.Arrange(new Rect(0, 0, 1280, 800)); view.UpdateLayout();
        double layoutMs = watch.Elapsed.TotalMilliseconds;
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        do
        {
            Pump(); timeline.InvalidateVisual(); view.UpdateLayout(); bitmap.Render(view);
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException($"{count} tracks: {timeline.OnionMissingTileCount} missing tiles");
        } while (timeline.OnionMissingTileCount != 0);
        Console.WriteLine($"{count} tracks / {count * 100:N0} notes: layout={layoutMs:F1}ms, converged={watch.Elapsed.TotalMilliseconds:F1}ms, managed delta={(GC.GetTotalMemory(false) - startBytes) / 1048576d:F1}MiB");
        if (timeline.CanEdit || timeline.IsTimeRangeSelectionEnabled || timeline.Snapshot?.HasHitTestableItems != false)
            throw new InvalidOperationException("The All Tracks view is editable.");
        var sourceSnapshot = vm.OnionSnapshot;
        vm.UpdatePlaybackCursor(project, 1200); view.UpdateLayout();
        if (timeline.PlaybackCursorTick != 1200 || ((TimelineOverviewSurface)view.FindName("Overview")).PlaybackCursorTick != 1200
            || !ReferenceEquals(vm.OnionSnapshot, sourceSnapshot))
            throw new InvalidOperationException("Playback cursor did not propagate, or rebuilt the note snapshot.");
        var toolbar = ((DockPanel)((Border)((Grid)view.Content).Children[0]).Child).Children.OfType<StackPanel>().Single();
        foreach (var control in toolbar.Children.OfType<Control>().Where(c => c is Button or ComboBox))
            if (control.ActualHeight != 24) throw new InvalidOperationException($"All Tracks toolbar control height: {control.GetType().Name}={control.ActualHeight}");
        if (count == 100)
        {
            using var compiler = new MidoraCompiler(); var canonical = compiler.CompileFull(project);
            if (!canonical.IsConsumable) throw new InvalidOperationException(string.Join("; ", canonical.Diagnostics));
            vm.Mode = ProjectPresentationAllTracksModeV3.Compiled; vm.UpdateCompilation(canonical, true);
            Wait(() => !vm.IsBuilding);
            if (!vm.Status.Contains("Current")) throw new InvalidOperationException(vm.Status);
            vm.UpdateCompilation(compiler.CompileFull(project), true);
            if (vm.IsBuilding) throw new InvalidOperationException("An equivalent full compile restarted the note index.");
            vm.UpdateCompilation(canonical, false);
            if (!vm.IsStale || !vm.Status.Contains("stale", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Stale compilation not labeled.");
            var removed = project.PureMidiTracks[^1];
            project.PureMidiTracks.RemoveAt(project.PureMidiTracks.Count - 1);
            project.ArrangementTracks.RemoveAt(project.ArrangementTracks.Count - 1);
            vm.Rebuild(project, 1); vm.UpdateCompilation(canonical, false);
            if (vm.OnionSnapshot!.Blocks.SelectMany(b => b).Any(t => t.Id == removed.Id))
                throw new InvalidOperationException("A deleted MIDI source survived in the hybrid compiled view.");
            vm.RetryBuild(); vm.CancelBuild(); Pump();
            if (vm.IsBuilding) throw new InvalidOperationException("Cancel did not end preparation.");
        }
        vm.Dispose(); timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
    private static void Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Compiled overview did not settle.");
            Pump();
        }
    }
    private static void CheckLargeMidi(string path)
    {
        Stopwatch timer = Stopwatch.StartNew();
        var imported = MidiProjectImportService.ImportFile(path, Path.GetFileNameWithoutExtension(path));
        using var project = imported.Project;
        Console.WriteLine($"{Path.GetFileName(path)}: import {imported.Metrics?.ImportedNoteCount:N0} notes, {timer.Elapsed.TotalSeconds:F2}s");
        var vm = new AllTracksWorkspaceViewModel(_ => { }); vm.Rebuild(project, 0);
        var view = new AllTracksView { DataContext = vm };
        var timeline = (TimelineSurface)view.FindName("Timeline");
        view.Measure(new(1280, 800)); view.Arrange(new Rect(0, 0, 1280, 800)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        try
        {
        foreach (var range in new[] { (0L, 3072L), (168816L, 24720L), (168816L, 98880L), (50000L, 24720L) })
        {
            timer.Restart(); vm.StartTick = range.Item1; vm.TickSpan = range.Item2;
            timeline.InvalidateVisual(); view.UpdateLayout(); bitmap.Render(view);
            double firstFrameMs = timer.Elapsed.TotalMilliseconds;
            long nextReport = 5000;
            Wait(() =>
            {
                timeline.InvalidateVisual(); view.UpdateLayout(); bitmap.Render(view);
                if (timer.ElapsedMilliseconds > nextReport)
                {
                    Console.WriteLine($"Raw {range}: {timeline.OnionMissingTileCount} tiles pending after {timer.Elapsed.TotalSeconds:F1}s");
                    nextReport += 5000;
                }
                return timeline.OnionMissingTileCount == 0;
            });
            Console.WriteLine($"Raw range {range}: foreground={firstFrameMs:F1}ms, converged={timer.Elapsed.TotalMilliseconds:F1}ms");
        }
        using var compiler = new MidoraCompiler();
        timer.Restart(); var canonical = compiler.CompileFull(project);
        Console.WriteLine($"Large canonical: {timer.Elapsed.TotalSeconds:F2}s; usable={canonical.IsConsumable}");
        if (!canonical.IsConsumable) throw new InvalidOperationException(string.Join("; ", canonical.Diagnostics));
        timer.Restart();
        using var index = CompiledOnionNoteIndex.Build(canonical, scope: CompiledOnionNoteScope.LogicalTracks);
        if (project.Tracks.Count == 0 && index.Count != 0)
            throw new InvalidOperationException($"Pure MIDI notes leaked into the logical display index: {index.Count}.");
        Console.WriteLine($"Hybrid logical-only index: {index.Count:N0} notes, {timer.Elapsed.TotalMilliseconds:F1}ms, managed={GC.GetTotalMemory(false)/1048576d:F1}MiB, working={Process.GetCurrentProcess().WorkingSet64/1048576d:F1}MiB");
        Console.WriteLine($"Index budget peaks: resident={index.PeakResidentBytes/1048576d:F1}MiB, working={index.PeakWorkingBytes/1048576d:F1}MiB, spill={index.PeakSpillBytes/1048576d:F1}MiB; process peak WS={Process.GetCurrentProcess().PeakWorkingSet64/1048576d:F1}MiB");
        var rawTracks = vm.OnionSnapshot!.Blocks.SelectMany(b => b).ToArray();
        timer.Restart(); vm.Mode = ProjectPresentationAllTracksModeV3.Compiled; vm.UpdateCompilation(canonical, true);
        Wait(() => !vm.IsBuilding);
        timeline.InvalidateVisual(); view.UpdateLayout(); bitmap.Render(view);
        double preparationMs = timer.Elapsed.TotalMilliseconds;
        if (!vm.OnionSnapshot!.Blocks.SelectMany(b => b).Take(rawTracks.Length).Zip(rawTracks).All(t => ReferenceEquals(t.First, t.Second)))
            throw new InvalidOperationException("Hybrid mode did not reuse raw MIDI track snapshots.");
        if (timeline.OnionMissingTileCount != 0) throw new InvalidOperationException("Raw-to-Compiled lost a reusable MIDI tile.");
        Console.WriteLine($"Hybrid view ready including dispatcher + first frame: {preparationMs:F1}ms; {rawTracks.Length} raw track snapshots reused; 0 missing tiles.");
        }
        finally { vm.Dispose(); timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        TimelineRasterCacheSession.Clear();
    }
    private static void Pump()
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
