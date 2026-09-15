using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class ConductorWorkspaceTests(ITestOutputHelper output)
{
    [Fact]
    public void StreamingProgressShowsProcessedCountWithoutInventingATotal()
    {
        var progress = new Midora.Application.TimelineEditPreparationProgress(
            Midora.Application.TimelineEditPreparationPhase.Planning, 500, 0);
        Assert.Equal("Planning edit (500 processed)", MainWindow.FormatTimelineEditPreparationProgress(progress));
        Assert.True(progress.IsIndeterminate);
    }

    [Fact]
    public async Task SparseListMergesTypesAndStableMarkerOrderAcrossPageBoundaries()
    {
        MidoraProject project = new(192);
        for (int i = 1; i <= 600; i++)
        {
            project.Conductor.Tempos.Add(new(project, i * 3, 100m + i));
            project.Conductor.Markers.Add(new(project, i * 3, $"M{i}"));
            project.Conductor.Markers.Add(new(project, i * 3, $"N{i}"));
        }
        using ConductorEventListSource source = new(project.Conductor);
        await source.PrepareAsync();
        var all = await source.ReadRowsAsync(0, source.Count, CancellationToken.None);
        var page = await source.ReadRowsAsync(253, 19, CancellationToken.None);
        Assert.Equal(all.Skip(253).Take(19), page);
        Assert.Equal(all.Select(x => x.Tick).Order(), all.Select(x => x.Tick));
        Assert.Equal(new[] { "Tempo", "Marker", "Marker" }, all.Where(x => x.Tick == 3).Select(x => x.Type));
        var selected = await source.ReadSelectionAsync(253, 800, CancellationToken.None);
        Assert.Equal(548, selected.Count);
        Assert.True(selected.ToHashSet().SetEquals(all.Skip(253).Take(548).Select(x => x.Id)));
        project.Conductor.Markers.Clear();
        Assert.Equal(all, await source.ReadRowsAsync(0, source.Count, CancellationToken.None));
    }

    [Fact]
    public async Task MillionRowsKeepOnlySparseDirectoryAndReadDistantRowsLocally()
    {
        const int count = 1_000_000;
        int reads = 0;
        using ConductorEventListSource source = new([count], [i =>
        {
            Interlocked.Increment(ref reads);
            return new(new(i + 1), i * 2L, "Tempo", "120 BPM");
        }]);
        Stopwatch timer = Stopwatch.StartNew();
        await source.PrepareAsync();
        TimeSpan directoryTime = timer.Elapsed;
        Assert.Equal(3907, source.DirectoryEntryCount);
        int before = reads;
        timer.Restart();
        var rows = await source.ReadRowsAsync(899_900, 40, CancellationToken.None);
        Assert.Equal(1_799_800, rows[0].Tick);
        Assert.InRange(reads - before, 40, 256 + 85);
        output.WriteLine($"million_list directory_ms={directoryTime.TotalMilliseconds:F2}; distant_40_rows_ms={timer.Elapsed.TotalMilliseconds:F2}; reads={reads - before}; anchors={source.DirectoryEntryCount}");
    }

    [Fact]
    public async Task CancelledListRequestDoesNotPublishAPartialRange()
    {
        using ConductorEventListSource source = new([1000], [i => new(new(i + 1), i, "Marker", "")]);
        await source.PrepareAsync();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadRowsAsync(10, 300, cancel.Token));
        Assert.Equal(300, (await source.ReadRowsAsync(10, 300, CancellationToken.None)).Length);
    }

    [Fact]
    public async Task ExplicitAxisCancelsOlderFitWithoutOverwritingTheNewRange()
    {
        using MidoraProject project = new(192);
        project.Conductor.Tempos.Add(new(project, 192, 180m));
        TimelineWorkspaceViewModel workspace = new(WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor);
        workspace.Rebuild(project, 0);
        // Exercise both completion orders, including a pool continuation racing
        // the explicit request when no WPF SynchronizationContext is installed.
        for (int i = 0; i < 128; i++)
        {
            Task oldFit = workspace.FitVisibleTempoAsync(CancellationToken.None);
            workspace.SetTempoAxis(10 + i, 300 + i);
            await oldFit;
            Assert.Equal(10 + i, workspace.TempoAxisMinimum);
            Assert.Equal(300 + i, workspace.TempoAxisMaximum);
        }
        workspace.CancelBackgroundPresentationWork();
    }

    [Fact]
    public async Task FirstRowsAreAvailableWhileTheLargeDirectoryIsStillWarming()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using ConductorEventListSource source = new([100_000], [i =>
        {
            if (i == 1024) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            return new(new(i + 1), i, "Tempo", "120 BPM");
        }]);
        Task directory = source.PrepareAsync();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            var rows = await source.ReadRowsAsync(0, 40, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(40, rows.Length);
            Assert.False(directory.IsCompleted);
        }
        finally { release.Set(); await directory; }
    }

    [Fact]
    public void VirtualListRestoresScrollWhetherTheSourceOrOffsetBindsFirst()
    {
        RunOnSta(() =>
        {
            using ConductorEventListSource source = new([1000], [i => new(new(i + 1), i, "Tempo", "120 BPM")]);
            ConductorEventList offsetFirst = new() { FirstRow = 123, Source = source };
            ConductorEventList sourceFirst = new() { Source = source, FirstRow = 123 };
            foreach (var list in new[] { offsetFirst, sourceFirst })
            {
                list.Measure(new Size(420, 280));
                list.Arrange(new Rect(0, 0, 420, 280));
                list.UpdateLayout();
                Assert.Equal(123, list.FirstRow);
            }
        });
    }

    [Fact]
    public void ActualConductorBamlLoadsAndAxesDoNotChangeProjectTempo()
    {
        RunOnSta(() =>
        {
            MidoraProject project = new(192);
            project.Conductor.Tempos.Add(new(project, 192, 180m));
            project.Conductor.Markers.Add(new(project, 300, "Marker"));
            TimelineWorkspaceViewModel workspace = new(WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor);
            workspace.Rebuild(project, 0);
            Assert.Empty(workspace.Snapshot!.Items);
            Assert.Empty(workspace.ConductorTempoSnapshot!.Items);
            Assert.Equal(2, workspace.ConductorTempoSnapshot.EnumerateAllItems().Count());
            workspace.SetTempoAxis(30, 300);
            Assert.Equal(120m, project.Conductor.Tempos[0].BeatsPerMinute);
            Assert.Equal(180m, project.Conductor.Tempos[1].BeatsPerMinute);
            Assert.Throws<ArgumentException>(() => workspace.SetTempoAxis(100, 100));
            Assert.Throws<ArgumentException>(() => workspace.SetTempoAxis(0, 1e50));
            workspace.ConductorTempoHeight = new(4, GridUnitType.Star);
            workspace.ConductorMetaHeight = new(2, GridUnitType.Star);
            ConductorWorkspaceView view = new() { DataContext = workspace, Width = 1100, Height = 640 };
            view.Measure(new(1100, 640));
            view.Arrange(new Rect(0, 0, 1100, 640));
            view.UpdateLayout();
            Assert.True(view.TempoTimeline.ActualHeight >= 140);
            Assert.True(view.MetaTimeline.ActualHeight >= 200);
            Assert.True(view.EventList.ActualWidth >= 260);
            Assert.Equal(TimelineSurface.ConductorEditorLaneHeaderWidth, view.TempoTimeline.LaneHeaderWidth);
            Assert.Equal(view.TempoTimeline.LaneHeaderWidth, view.MetaTimeline.LaneHeaderWidth);
            Assert.Equal(view.TempoTimeline.TranslatePoint(new Point(), view).X,
                view.MetaTimeline.TranslatePoint(new Point(), view).X, precision: 5);
            Point tempoOrigin = view.TempoTimeline.TranslatePoint(new Point(view.TempoTimeline.LaneHeaderWidth, 0), view);
            Point metaOrigin = view.MetaTimeline.TranslatePoint(new Point(view.MetaTimeline.LaneHeaderWidth, 0), view);
            Assert.Equal(tempoOrigin.X, metaOrigin.X, precision: 5);
            Assert.Equal(view.TempoTimeline.ActualWidth, view.MetaTimeline.ActualWidth, precision: 5);
            Assert.Equal(new GridLength(4, GridUnitType.Star), workspace.ConductorTempoHeight);
            workspace.UpdatePlaybackCursor(project, 192);
            Assert.Equal(192, view.TempoPlaybackCursor.PlaybackCursorTick);
            Assert.Equal(192, view.MetaPlaybackCursor.PlaybackCursorTick);
            Assert.Equal(view.TempoTimeline.LaneHeaderWidth, view.TempoPlaybackCursor.LaneHeaderWidthOverride);
            Assert.Equal(view.MetaTimeline.LaneHeaderWidth, view.MetaPlaybackCursor.LaneHeaderWidthOverride);
            Assert.Equal(view.MetaTimeline.ActualWidth, view.MetaPlaybackCursor.ActualWidth, precision: 5);
            Assert.Equal(view.MetaTimeline.TranslatePoint(new Point(), view), view.MetaPlaybackCursor.TranslatePoint(new Point(), view));
            RenderTargetBitmap bitmap = new(1100, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            workspace.ConductorListSource?.Dispose();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetRestoresTheCompleteDefaultAxisAndPreservesHorizontalNavigation(bool alreadyDefault)
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            var workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor);
            workspace.Rebuild(project, 0);
            if (!alreadyDefault) workspace.SetTempoAxis(30, 300);
            workspace.StartTick = 1000;
            workspace.TickSpan = 768;
            workspace.Selection.Replace(project.Conductor.Tempos[0].Id);
            ConductorWorkspaceView view = new() { DataContext = workspace };
            view.Measure(new(1100, 640));
            view.Arrange(new Rect(0, 0, 1100, 640));
            view.UpdateLayout();
            typeof(TimelineSurface).GetMethod("ZoomValueAxis", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view.TempoTimeline, [160d, 120, 24d]);
            Assert.True(view.TempoTimeline.CanScrollValues);
            string? command = null;
            view.CommandRequested += (_, value) => { command = value; view.ResetTempoView(); };
            view.ResetTempoViewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("ResetAxis", command);
            Assert.Equal(0, workspace.TempoAxisMinimum);
            Assert.Equal(240, workspace.TempoAxisMaximum);
            Assert.Equal(0, view.TempoTimeline.ValueAxisMinimum);
            Assert.Equal(240, view.TempoTimeline.ValueAxisMaximum);
            Assert.False(view.TempoTimeline.CanScrollValues);
            Assert.Equal(1, view.TempoTimeline.ValueScrollViewportSize);
            Assert.Equal(0, view.TempoTimeline.ValueScrollOffset);
            Assert.Equal(1000, workspace.StartTick);
            Assert.Equal(768, workspace.TickSpan);
            Assert.Equal(1000, view.TempoTimeline.StartTick);
            Assert.Equal(768, view.MetaTimeline.TickSpan);
            Assert.Equal(project.Conductor.Tempos[0].Id, workspace.Selection.Primary);
            Assert.Equal(120m, project.Conductor.Tempos[0].BeatsPerMinute);
            workspace.CancelBackgroundPresentationWork();
        });
    }

    [Fact]
    public void ConductorSnapUsesTheToolbarSettingsOnBothGraphsAndSegmentLanesStayIndependent()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            var workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor);
            workspace.Rebuild(project, 0);
            ConductorWorkspaceView view = new() { DataContext = workspace };
            view.Measure(new(1100, 640));
            view.Arrange(new Rect(0, 0, 1100, 640));
            view.UpdateLayout();
            foreach (TimelineSurface? surface in new[] { view.TempoTimeline, view.MetaTimeline, null })
            {
                var settings = MainWindow.GetWorkspaceEditorSettingsForSurface(workspace, surface);
                Assert.Same(workspace.EditorSettings, settings);
                settings!.SnapEnabled = true;
                settings.SnapEnabled = false;
                Assert.Equal(1, workspace.OperationStepTicks);
                Assert.Equal(1, view.TempoTimeline.OperationStepTicks);
                Assert.Equal(1, view.MetaTimeline.OperationStepTicks);
            }
            var segment = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new(50)), "Segment", TimelineWorkspaceMode.Segment);
            Assert.Same(segment.LaneEditorSettings, MainWindow.GetWorkspaceEditorSettingsForSurface(segment, view.TempoTimeline));
            TimelineSurface piano = new() { SurfaceMode = TimelineSurfaceMode.PianoRoll };
            Assert.Same(segment.EditorSettings, MainWindow.GetWorkspaceEditorSettingsForSurface(segment, piano));
            InstrumentWorkspaceViewModel instrument = new(new(51), "Instrument");
            Assert.Same(instrument.EventLaneEditorSettings, MainWindow.GetWorkspaceEditorSettingsForSurface(instrument, view.TempoTimeline));
            Assert.Same(instrument.EditorSettings, MainWindow.GetWorkspaceEditorSettingsForSurface(instrument, piano));
            workspace.CancelBackgroundPresentationWork();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Conductor WPF test timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
