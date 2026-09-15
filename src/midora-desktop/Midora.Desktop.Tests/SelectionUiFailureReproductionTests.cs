using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

/// <summary>
/// Executable reproductions for the two remaining real-UI selection failures.
/// These state the required behavior through real TimelineSurface/Dispatcher
/// paths and must not be weakened into implementation-detail tests.
/// </summary>
[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class SelectionUiFailureReproductionTests
{
    [Fact]
    public void OptIn9KX2ExistingViewportHighlightsCompletedMillionNoteMarqueeWithoutPanning()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_UI_SAMPLE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;

        RunOnSta(() =>
        {
            TimelineRasterCacheSession.Clear();
            MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
                Path.GetFullPath(path),
                Path.GetFileNameWithoutExtension(path));
            TimelineSurface? surface = null;
            try
            {
                PureMidiTrack track = Assert.Single(
                    imported.Project.PureMidiTracks,
                    static candidate => string.Equals(
                        candidate.Name,
                        "MIDI Out #23",
                        StringComparison.Ordinal));
                MidiSegment segment = Assert.Single(track.Segments);
                TimelineWorkspaceViewModel workspace = new(
                    WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                    "MIDI Segment",
                    TimelineWorkspaceMode.Segment);
                workspace.Rebuild(imported.Project, revision: 1);
                TimelineRenderSnapshot snapshot = workspace.Snapshot!;
                surface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    ToolMode = TimelineToolMode.Select,
                    Snapshot = snapshot,
                    SelectionSnapshot = workspace.SelectionSnapshot,
                    RangeStartTick = 0,
                    RangeEndTick = segment.LengthTicks,
                    StartTick = 168_816,
                    TickSpan = 24_720,
                    FirstLane = 0,
                    LaneHeight = 3,
                    GridVisible = false
                };
                surface.Measure(new Size(800, 500));
                surface.Arrange(new Rect(0, 0, 800, 500));
                WaitForCommittedFrame(surface);
                byte[] baseline = RenderVisual(surface, 800, 500);

                TimelineMaterializedSelection selection = snapshot.MaterializeRangeSelection(
                    168_816,
                    193_536,
                    0,
                    128,
                    0,
                    1,
                    filterByValue: false,
                    workspace.SelectionSnapshot,
                    WorkspaceSelectionRangeMode.Replace);
                Assert.Equal(1_382_908, selection.Ids.Count);
                Assert.NotNull(selection.RenderIndex);
                workspace.Selection.AdoptMaterialized(
                    selection.Ids,
                    selection.Primary,
                    selection.Anchor);
                workspace.PublishMaterializedSelection(
                    selection.Metrics,
                    selection.MetricsAreComplete,
                    selection.RenderIndex);
                surface.SelectionSnapshot = workspace.SelectionSnapshot;

                byte[] actual = RenderVisual(surface, 800, 500);
                Stopwatch timeout = Stopwatch.StartNew();
                while ((actual.SequenceEqual(baseline)
                        || GetCommittedSelectionTileCount(surface) == 0)
                    && timeout.Elapsed < TimeSpan.FromSeconds(15))
                {
                    DrainDispatcher();
                    Thread.Sleep(2);
                    actual = RenderVisual(surface, 800, 500);
                }

                Assert.True(
                    GetCommittedSelectionTileCount(surface) > 0,
                    "The completed 9KX2 marquee never committed a visible selection tile.");
                Assert.False(
                    actual.SequenceEqual(baseline),
                    "The existing 9KX2 viewport remained unhighlighted until StartTick changed.");
            }
            finally
            {
                Unload(surface);
                TimelineRasterCacheSession.Clear();
                imported.Project.Dispose();
            }
        });
    }

    [Fact]
    public void SelectModeBackgroundClickOnSecondSurfaceFencesPendingReplaceMarquee()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            using BlockingRangeSource source = new();
            TimelineSurface? noteSurface = null;
            TimelineSurface? laneSurface = null;
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Selection gesture epoch",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                ProbeWorkspace workspace = new();
                session.Workspaces.Add(workspace);
                MidoraId noteId = source.Item.Id;
                workspace.Selection.Replace(noteId);
                workspace.RefreshSelectionPresentation();

                TimelineRenderSnapshot snapshot = new(
                    1,
                    "selection-gesture-epoch",
                    [],
                    itemSource: source);
                noteSurface = CreateBoundSurface(workspace, snapshot);
                laneSurface = CreateBoundSurface(workspace, snapshot);
                // This case specifies an unmodified Replace gesture. Do not read
                // the operator's live Ctrl/Shift key state during the test run.
                FieldInfo modifiers = typeof(TimelineSurface).GetField(
                    "_replayingConductorModifiers",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                modifiers.SetValue(noteSurface, System.Windows.Input.ModifierKeys.None);
                modifiers.SetValue(laneSurface, System.Windows.Input.ModifierKeys.None);
                noteSurface.SelectionReplacementStarted += (_, args) =>
                    args.BaseSelection = session.BeginWorkspaceSelectionReplacement(workspace);
                noteSurface.MarqueeCompleted += (_, args) =>
                    session.TryApplyMaterializedWorkspaceSelection(
                        workspace,
                        args.Materialization);
                laneSurface.BackgroundInvoked += (_, _) =>
                {
                    workspace.Selection.Clear();
                    session.RefreshWorkspaceSelection(workspace);
                };

                MethodInfo complete = typeof(TimelineSurface).GetMethod(
                    "CompleteMarquee",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                _ = complete.Invoke(
                    noteSurface,
                    [new Point(48, 25), new Point(300, 40)]);
                Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5)));
                Assert.Empty(workspace.Selection.Ids);
                Assert.Same(
                    workspace.SelectionSnapshot,
                    laneSurface.SelectionSnapshot);

                // This is the actual Select-mode click path on the second
                // (Event/Parameter lane) surface. A sub-threshold marquee calls
                // BackgroundInvoked instead of querying the paged source. The
                // earlier Replace gesture already published Empty, so the click
                // still has to advance the selection-intent epoch.
                long beforeDeselectRevision = workspace.Selection.Revision;
                _ = complete.Invoke(
                    laneSurface,
                    [new Point(60, 40), new Point(60, 40)]);
                Assert.True(workspace.Selection.Revision > beforeDeselectRevision);
                Assert.Empty(workspace.Selection.Ids);
                source.Release.Set();
                PumpUntil(
                    () => !source.IsRunning,
                    TimeSpan.FromSeconds(5));
                DrainDispatcher();

                Assert.Empty(workspace.Selection.Ids);
                Assert.Empty(workspace.SelectionSnapshot.Ids);
                Assert.Empty(noteSurface.SelectionSnapshot!.Ids);
                Assert.Empty(laneSurface.SelectionSnapshot!.Ids);
            }
            finally
            {
                source.Release.Set();
                Unload(noteSurface);
                Unload(laneSurface);
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void VisiblePartOfLargeFormalSelectionHighlightsBeforeOffscreenIndexCompletes()
    {
        RunOnSta(() =>
        {
            TimelineRasterCacheSession.Clear();
            using BlockingSelectionIndexSource source = new(4_096);
            MidoraId visibleId = new(61_000_001);
            TimelineRenderItem visible = new(
                visibleId,
                TimelineItemKind.DirectMidiNote,
                20,
                60,
                60,
                100,
                0,
                TimelineItemState.None);
            TimelineRenderSnapshot snapshot = new(
                1,
                "selection-visible-before-offscreen-index",
                [visible],
                itemSource: source);
            ProbeWorkspace workspace = new();
            ImmutableHashSet<MidoraId> ids = source.Ids.Add(visibleId);
            MidoraId offscreenPrimary = source.Ids.First();
            workspace.Selection.AdoptMaterialized(ids, offscreenPrimary, offscreenPrimary);
            workspace.RefreshAgainst(snapshot);
            TimelineSurface selected = CreateBoundSurface(workspace, snapshot);
            TimelineSurface baseline = CreateSurface(
                snapshot,
                new TimelineSelectionSnapshot(0, [], null));
            try
            {
                Assert.Equal(4_097, workspace.SelectionSnapshot.Count);
                Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(workspace.SelectionSnapshot.HasRenderIndex);

                WaitForCommittedFrame(selected);
                WaitForCommittedFrame(baseline);
                byte[] selectedPixels = RenderVisual(selected);
                byte[] baselinePixels = RenderVisual(baseline);

                Assert.False(
                    selectedPixels.SequenceEqual(baselinePixels),
                    "A visible selected note was indistinguishable from an unselected note "
                    + "while unrelated offscreen selection IDs were still being indexed.");
            }
            finally
            {
                source.Release.Set();
                Unload(selected);
                Unload(baseline);
                TimelineRasterCacheSession.Clear();
            }
        });
    }

    [Fact]
    public void ExistingVisiblePianoTilesHighlightAfterLargeSelectionWithoutViewportMovement()
    {
        RunOnSta(() =>
        {
            TimelineRasterCacheSession.Clear();
            using BlockingSelectionIndexSource source = new(4_096);
            MidoraId visibleId = new(61_000_101);
            TimelineRenderItem visible = new(
                visibleId,
                TimelineItemKind.DirectMidiNote,
                20,
                60,
                60,
                100,
                0,
                TimelineItemState.None);
            TimelineRenderSnapshot snapshot = new(
                1,
                "existing-visible-selection-tiles",
                [visible],
                itemSource: source);
            ProbeWorkspace workspace = new();
            TimelineSurface selected = CreateBoundSurface(workspace, snapshot);
            TimelineSurface baseline = CreateSurface(
                snapshot,
                new TimelineSelectionSnapshot(0, [], null));
            try
            {
                // First commit the unselected viewport. This is the state that
                // already exists when the user completes a large marquee.
                WaitForCommittedFrame(selected);
                WaitForCommittedFrame(baseline);
                byte[] baselinePixels = RenderVisual(baseline);
                Assert.True(RenderVisual(selected).SequenceEqual(baselinePixels));

                ImmutableHashSet<MidoraId> ids = source.Ids.Add(visibleId);
                MidoraId offscreenPrimary = source.Ids.First();
                workspace.Selection.AdoptMaterialized(ids, offscreenPrimary, offscreenPrimary);
                workspace.RefreshAgainst(snapshot);
                DrainDispatcher();

                Assert.Equal(4_097, workspace.SelectionSnapshot.Count);
                Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(workspace.SelectionSnapshot.HasRenderIndex);

                // Do not change StartTick, TickSpan, FirstLane, or LaneHeight.
                // Rendering must converge solely from selection invalidation;
                // viewport movement must not be required to reveal highlights.
                byte[] actual = RenderVisual(selected);
                Stopwatch timeout = Stopwatch.StartNew();
                while (actual.SequenceEqual(baselinePixels)
                    && timeout.Elapsed < TimeSpan.FromSeconds(5))
                {
                    DrainDispatcher();
                    Thread.Sleep(2);
                    actual = RenderVisual(selected);
                }

                Assert.False(
                    actual.SequenceEqual(baselinePixels),
                    "The already-presented visible note remained unhighlighted until the viewport moved.");
            }
            finally
            {
                source.Release.Set();
                Unload(selected);
                Unload(baseline);
                TimelineRasterCacheSession.Clear();
            }
        });
    }

    [Fact]
    public void ExistingVisiblePianoTilesHighlightAfterCompletedLargeMarqueeWithoutViewportMovement()
    {
        RunOnSta(() =>
        {
            TimelineRasterCacheSession.Clear();
            MidoraId visibleId = new(61_000_201);
            TimelineRenderItem visible = new(
                visibleId,
                TimelineItemKind.DirectMidiNote,
                20,
                60,
                60,
                100,
                0,
                TimelineItemState.None);
            TimelineRenderItem[] offscreen = Enumerable.Range(0, 4_096)
                .Select(index => new TimelineRenderItem(
                    new MidoraId(63_000_000 + index),
                    TimelineItemKind.DirectMidiNote,
                    10_000 + index,
                    10_002 + index,
                    60,
                    100,
                    0,
                    TimelineItemState.None))
                .ToArray();
            TimelineRenderItem[] all = [visible, .. offscreen];
            TimelineRenderSnapshot snapshot = new(
                1,
                "existing-visible-completed-marquee",
                all);
            ProbeWorkspace workspace = new();
            TimelineSurface selected = CreateBoundSurface(workspace, snapshot);
            TimelineSurface baseline = CreateSurface(
                snapshot,
                new TimelineSelectionSnapshot(0, [], null));
            try
            {
                WaitForCommittedFrame(selected);
                WaitForCommittedFrame(baseline);
                byte[] baselinePixels = RenderVisual(baseline);
                Assert.True(RenderVisual(selected).SequenceEqual(baselinePixels));

                ImmutableHashSet<MidoraId> ids = all
                    .Select(static item => item.Id)
                    .ToImmutableHashSet();
                MidoraId offscreenPrimary = offscreen[0].Id;
                workspace.Selection.AdoptMaterialized(
                    ids,
                    offscreenPrimary,
                    offscreenPrimary);
                workspace.PublishMaterializedSelection(
                    new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
                    metricsAreComplete: true,
                    TimelineSelectionRenderIndex.Create(all));
                DrainDispatcher();

                Assert.Equal(4_097, workspace.SelectionSnapshot.Count);
                Assert.True(workspace.SelectionSnapshot.HasRenderIndex);
                Assert.Same(workspace.SelectionSnapshot, selected.SelectionSnapshot);
                Assert.True(selected.SelectionSnapshot!.HasRenderIndex);

                byte[] actual = RenderVisual(selected);
                Stopwatch timeout = Stopwatch.StartNew();
                while ((GetCommittedSelectionTileCount(selected) == 0
                        || actual.SequenceEqual(baselinePixels))
                    && timeout.Elapsed < TimeSpan.FromSeconds(5))
                {
                    DrainDispatcher();
                    Thread.Sleep(2);
                    actual = RenderVisual(selected);
                }

                Assert.True(
                    GetCommittedSelectionTileCount(selected) > 0,
                    "The exact large marquee produced no committed selection tile. "
                    + DescribePianoRasterState(selected));
                Assert.False(
                    actual.SequenceEqual(baselinePixels),
                    "The already-presented visible note remained unhighlighted after the exact large marquee was published. "
                    + DescribePianoRasterState(selected));
            }
            finally
            {
                Unload(selected);
                Unload(baseline);
                TimelineRasterCacheSession.Clear();
            }
        });
    }

    private sealed class ProbeWorkspace()
        : WorkspaceViewModel(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new MidoraId(90_001)),
            "Selection probe")
    {
        public override void Rebuild(MidoraProject project, long revision)
        {
        }

        public void RefreshAgainst(TimelineRenderSnapshot snapshot) =>
            TryRefreshSelectionPresentation([snapshot]);
    }

    private sealed class BlockingRangeSource : ITimelineRenderItemSource, IDisposable
    {
        private int _running;

        public TimelineRenderItem Item { get; } = new(
            new MidoraId(60_000_001),
            TimelineItemKind.DirectMidiNote,
            5,
            10,
            0,
            100,
            0,
            TimelineItemState.None);
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool IsRunning => Volatile.Read(ref _running) != 0;
        public long Count => 1;
        public long MaximumEndTick => Item.EndTick;
        public ulong ContentFingerprint => 0x619b_17abUL;

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            Volatile.Write(ref _running, 1);
            Started.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The range query was not released.");
                if (Item.StartTick < endTick
                    && Item.EndTick > startTick
                    && Item.Lane >= firstLane
                    && Item.Lane < lastLaneExclusive)
                {
                    destination.Add(Item);
                }
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        public bool TryGetById(MidoraId id, out TimelineRenderItem item)
        {
            item = Item;
            return id == Item.Id;
        }

        public IEnumerable<TimelineRenderItem> EnumerateAll()
        {
            yield return Item;
        }

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class BlockingSelectionIndexSource : ITimelineRenderItemSource, IDisposable
    {
        public BlockingSelectionIndexSource(int count)
        {
            Ids = Enumerable.Range(0, count)
                .Select(index => new MidoraId(62_000_000 + index))
                .ToImmutableHashSet();
        }

        public ImmutableHashSet<MidoraId> Ids { get; }
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public long Count => Ids.Count;
        public long MaximumEndTick => 100_000;
        public ulong ContentFingerprint => 0x7f00_11e5UL;

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            // The synthetic source is entirely offscreen. Rendering the visible
            // materialized note must not wait for it.
        }

        public bool TryGetById(MidoraId id, out TimelineRenderItem item)
        {
            if (!Ids.Contains(id))
            {
                item = default;
                return false;
            }
            long offset = id.Value - 62_000_000;
            item = new(
                id,
                TimelineItemKind.DirectMidiNote,
                10_000 + offset,
                10_002 + offset,
                60,
                100,
                0,
                TimelineItemState.None);
            return true;
        }

        public void VisitByIds(
            IReadOnlySet<MidoraId> ids,
            Action<TimelineRenderItem> visitor)
        {
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The offscreen selection scan was not released.");
            foreach (MidoraId id in ids)
            {
                if (TryGetById(id, out TimelineRenderItem item)) visitor(item);
            }
        }

        public IEnumerable<TimelineRenderItem> EnumerateAll()
        {
            foreach (MidoraId id in Ids)
            {
                _ = TryGetById(id, out TimelineRenderItem item);
                yield return item;
            }
        }

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
        }
    }

    private static TimelineSurface CreateBoundSurface(
        WorkspaceViewModel workspace,
        TimelineRenderSnapshot snapshot)
    {
        TimelineSurface surface = CreateSurface(snapshot, workspace.SelectionSnapshot);
        surface.DataContext = workspace;
        BindingOperations.SetBinding(
            surface,
            TimelineSurface.SelectionSnapshotProperty,
            new Binding(nameof(WorkspaceViewModel.SelectionSnapshot))
            {
                Mode = BindingMode.OneWay
            });
        return surface;
    }

    private static TimelineSurface CreateSurface(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot selection)
    {
        TimelineSurface surface = new()
        {
            SurfaceMode = TimelineSurfaceMode.PianoRoll,
            ToolMode = TimelineToolMode.Select,
            Snapshot = snapshot,
            SelectionSnapshot = selection,
            RangeStartTick = 0,
            RangeEndTick = 100_000,
            StartTick = 0,
            TickSpan = 100,
            FirstLane = 48,
            LaneHeight = 8,
            GridVisible = false
        };
        surface.Measure(new Size(800, 260));
        surface.Arrange(new Rect(0, 0, 800, 260));
        return surface;
    }

    private static void WaitForCommittedFrame(TimelineSurface surface)
    {
        FieldInfo field = typeof(TimelineSurface).GetField(
            "_committedPianoFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Stopwatch timeout = Stopwatch.StartNew();
        while (field.GetValue(surface) is null
            && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            _ = RenderVisual(surface);
            Thread.Sleep(2);
            DrainDispatcher();
        }
        Assert.NotNull(field.GetValue(surface));
    }

    private static byte[] RenderVisual(TimelineSurface surface)
        => RenderVisual(surface, 800, 260);

    private static byte[] RenderVisual(TimelineSurface surface, int width, int height)
    {
        RenderTargetBitmap target = new(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        target.Render(surface);
        byte[] pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void Unload(TimelineSurface? surface)
    {
        if (surface is null) return;
        surface.RaiseEvent(new RoutedEventArgs(
            FrameworkElement.UnloadedEvent,
            surface));
    }

    private static string DescribePianoRasterState(TimelineSurface surface)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? frame = typeof(TimelineSurface).GetField(
            "_committedPianoFrame", flags)!.GetValue(surface);
        long revision = frame is null
            ? long.MinValue
            : (long)frame.GetType().GetProperty("SelectionRevision")!.GetValue(frame)!;
        int selectionTiles = GetCommittedSelectionTileCount(surface);
        object prepared = typeof(TimelineSurface).GetField(
            "_preparedTileFingerprints", flags)!.GetValue(surface)!;
        object requested = typeof(TimelineSurface).GetField(
            "_requestedRasterKeys", flags)!.GetValue(surface)!;
        int preparedCount = (int)prepared.GetType().GetProperty("Count")!.GetValue(prepared)!;
        int requestedCount = (int)requested.GetType().GetProperty("Count")!.GetValue(requested)!;
        return $"frameRevision={revision}; selectionTiles={selectionTiles}; "
            + $"preparedFingerprints={preparedCount}; requested={requestedCount}; "
            + $"cacheCompleted={TimelineRasterCacheSession.CompletedCount}; "
            + $"cacheInFlight={TimelineRasterCacheSession.InFlightCount}";
    }

    private static int GetCommittedSelectionTileCount(TimelineSurface surface)
    {
        object? frame = typeof(TimelineSurface).GetField(
            "_committedPianoFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface);
        return frame is null
            ? -1
            : ((Array)frame.GetType().GetProperty("SelectionTiles")!.GetValue(frame)!).Length;
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            DrainDispatcher();
            Thread.Sleep(1);
        }
        Assert.True(condition());
    }

    private static void PumpUntil(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
