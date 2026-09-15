using System.Collections.Immutable;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class SelectionPresentationLifecycleTests
{
    [Fact]
    public void PagedHorizontalFlipRetainsLargeSelectionRenderGeometry()
    {
        RunOnSta(() =>
        {
            const int count = 1_000_000;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "midora-selection-render-probe",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "notes.mpk");
            try
            {
                using MidoraProject project = new(192);
                MidiChannelRoot root = new(project) { Name = "Root" };
                PureMidiTrack track = new(project)
                {
                    Name = "MIDI",
                    MidiChannelRootId = root.Id
                };
                MidiSegment segment = new(project) { LengthTicks = count * 4L + 2 };
                track.Segments.Add(segment);
                project.MidiChannelRoots.Add(root);
                project.PureMidiTracks.Add(track);
                project.ArrangementTracks.Add(new(
                    ArrangementTrackKind.PureMidiTrack,
                    track.Id));
                MidoraId[] ids = new MidoraId[count];
                using PureMidiContentPackWriter writer = new(path);
                for (int index = 0; index < count; index++)
                {
                    MidoraId id = project.AllocateStableId();
                    ids[index] = id;
                    writer.AddNote(segment.Id, new(
                        id,
                        index * 4L,
                        2,
                        index % 128,
                        100,
                        64,
                        index * 2L,
                        index * 2L + 1));
                }
                using PureMidiContentPack pack = writer.Complete();
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

                TimelineWorkspaceViewModel workspace = new(
                    WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                    "Segment",
                    TimelineWorkspaceMode.Segment);
                workspace.Rebuild(project, 1);
                workspace.Selection.AdoptMaterialized(
                    ids.ToImmutableHashSet(),
                    ids[0],
                    ids[0]);
                workspace.RefreshSelectionPresentation();
                WaitForSelectionGeometry(workspace, count);

                IPreparedProjectEdit flip =
                    ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(
                        segment.Id,
                        ids)
                    .Prepare(project);
                flip.Apply(project);
                workspace.Rebuild(project, 2);
                workspace.RefreshSelectionPresentation();
                WaitForSelectionGeometry(workspace, count);

                TimelineSurface baseline = CreateSurface(
                    workspace.Snapshot!,
                    new TimelineSelectionSnapshot(1, [], null),
                    segment.LengthTicks);
                TimelineSurface selected = CreateSurface(
                    workspace.Snapshot!,
                    workspace.SelectionSnapshot,
                    segment.LengthTicks);
                try
                {
                    byte[] unselected = RenderVisual(baseline);
                    byte[] actual = RenderVisual(selected);
                    Stopwatch timeout = Stopwatch.StartNew();
                    while (actual.SequenceEqual(unselected)
                        && timeout.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        DrainDispatcher();
                        Thread.Sleep(5);
                        actual = RenderVisual(selected);
                    }
                    Assert.False(actual.SequenceEqual(unselected));
                }
                finally
                {
                    baseline.RaiseEvent(new RoutedEventArgs(
                        FrameworkElement.UnloadedEvent,
                        baseline));
                    selected.RaiseEvent(new RoutedEventArgs(
                        FrameworkElement.UnloadedEvent,
                        selected));
                    TimelineRasterCacheSession.Clear();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }

            static void WaitForSelectionGeometry(
                TimelineWorkspaceViewModel workspace,
                int expectedCount)
            {
                Stopwatch timeout = Stopwatch.StartNew();
                while ((!workspace.SelectionSnapshot.HasRenderIndex
                        || workspace.SelectionSnapshot.RenderIndex?.Count != expectedCount)
                    && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    DrainDispatcher();
                    Thread.Sleep(5);
                }
                Assert.True(workspace.SelectionSnapshot.HasRenderIndex);
                Assert.Equal(expectedCount, workspace.SelectionSnapshot.RenderIndex?.Count);
            }

            static TimelineSurface CreateSurface(
                TimelineRenderSnapshot snapshot,
                TimelineSelectionSnapshot selection,
                long tickSpan)
            {
                TimelineSurface surface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    Snapshot = snapshot,
                    SelectionSnapshot = selection,
                    RangeStartTick = 0,
                    RangeEndTick = tickSpan,
                    StartTick = 0,
                    TickSpan = tickSpan,
                    FirstLane = 48,
                    LaneHeight = 8,
                    GridVisible = false
                };
                surface.Measure(new Size(800, 260));
                surface.Arrange(new Rect(0, 0, 800, 260));
                return surface;
            }

            static byte[] RenderVisual(Visual visual)
            {
                visual.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                RenderTargetBitmap target = new(800, 260, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                byte[] pixels = new byte[800 * 260 * 4];
                target.CopyPixels(pixels, 800 * 4, 0);
                return pixels;
            }
        });
    }

    [Fact]
    public void ClearedSelectionSurvivesLaneRefreshAndEventEditAfterLargeFlip()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "Cleared selection lifecycle",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
                    "MIDI Track"));
                PureMidiTrack track = Assert.Single(session.Project!.PureMidiTracks);
                session.Execute(ProjectDomainEditCommands.CreateMidiSegment(
                    track.Id,
                    0,
                    20_000));
                MidiSegment segment = Assert.Single(track.Segments);
                DirectMidiNote[] notes = Enumerable.Range(0, 4_097)
                    .Select(index => new DirectMidiNote(session.Project)
                    {
                        StartTick = index * 4L,
                        LengthTicks = 2,
                        Key = index % 128,
                        NoteOnVelocity = 100,
                        NoteOffVelocity = 64,
                        NoteOnOrder = index * 2L,
                        NoteOffOrder = index * 2L + 1
                    })
                    .ToArray();
                segment.Notes.AddRange(notes);

                TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
                session.RefreshWorkspace(workspace);
                ImmutableHashSet<MidoraId> selected = notes
                    .Select(static note => note.Id)
                    .ToImmutableHashSet();
                workspace.Selection.AdoptMaterialized(
                    selected,
                    notes[0].Id,
                    notes[0].Id);
                session.RefreshWorkspaceSelection(workspace);
                TimelineMaterializedSelection staleMarquee = new(
                    workspace.Selection.Revision,
                    CompressedMidoraIdSet.Create(selected),
                    notes[0].Id,
                    notes[0].Id,
                    new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
                    MetricsAreComplete: false,
                    IsUnchanged: false);

                session.ExecutePreservingWorkspaceSelection(
                    ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(
                        segment.Id,
                        selected),
                    workspace);
                DrainDispatcher();

                long previousRevision = workspace.Selection.Revision;
                workspace.Selection.Clear();
                session.RefreshWorkspaceSelection(workspace);
                Assert.Empty(workspace.Selection.Ids);
                Assert.Empty(workspace.SelectionSnapshot.Ids);
                Assert.True(workspace.Selection.Revision > previousRevision);
                TimelineMaterializedSelection lateMarqueeFromClearedState = new(
                    workspace.Selection.Revision,
                    CompressedMidoraIdSet.Create(selected),
                    notes[0].Id,
                    notes[0].Id,
                    new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
                    MetricsAreComplete: false,
                    IsUnchanged: false);

                DirectMidiEventLaneTarget target = new(
                    DirectMidiChannelEventKind.ControlChange,
                    11);
                workspace.AddDirectMidiLaneTarget(target);
                session.RefreshWorkspace(workspace);
                workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions
                    .ToList()
                    .FindIndex(option => option.DirectMidiTarget == target);
                session.RefreshWorkspace(workspace);

                session.Execute(ProjectDomainEditCommands.UpsertDirectMidiEventPoints(
                    segment.Id,
                    target.Kind,
                    target.Data1,
                    [new DirectMidiEventPointEdit(120, 11, 96)]));

                Assert.False(session.TryApplyMaterializedWorkspaceSelection(
                    workspace,
                    lateMarqueeFromClearedState));
                DrainDispatcher();

                Assert.False(session.TryApplyMaterializedWorkspaceSelection(
                    workspace,
                    staleMarquee));

                Assert.Empty(workspace.Selection.Ids);
                Assert.Empty(workspace.SelectionSnapshot.Ids);
                MidiSegment published = session.Project.PureMidiTracks.Single(value => value.Id == track.Id)
                    .Segments.Single(value => value.Id == segment.Id);
                Assert.Equal(
                    96,
                    Assert.Single(published.ChannelEvents, value =>
                        value.Kind == DirectMidiChannelEventKind.ControlChange
                        && value.Data1 == 11).Data2);
                Assert.Empty(segment.ChannelEvents);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void DisposedSelectionIsUnavailableWhileLiveEmptySelectionRemainsComplete()
    {
        using ProbeWorkspace workspace = new();
        workspace.RefreshSelectionPresentation();
        Assert.True(workspace.SelectionSnapshot.MetricsAreComplete);

        workspace.Selection.Replace(new MidoraId(1));
        workspace.PublishMaterializedSelection(
            new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
            metricsAreComplete: true,
            renderIndex: TimelineSelectionRenderIndex.Create([
                new(new MidoraId(1), TimelineItemKind.DirectMidiNote,
                    0, 1, 60, 100, 0, TimelineItemState.None)]));
        Assert.True(workspace.SelectionSnapshot.HasRenderIndex);

        workspace.Dispose();
        TimelineSelectionSnapshot released = workspace.SelectionSnapshot;
        Assert.Empty(workspace.Selection.Ids);
        Assert.Empty(released.Ids);
        Assert.Equal(workspace.Selection.Revision, released.Revision);
        Assert.False(released.HasRenderIndex);
        Assert.False(released.MetricsAreComplete);

        workspace.RefreshSelectionPresentation();
        workspace.PublishMaterializedSelection(
            new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
            metricsAreComplete: true);
        workspace.Dispose();
        Assert.Same(released, workspace.SelectionSnapshot);
    }

    [Fact]
    public void ClosingWorkspaceCancelsDeferredSelectionMetricsBeforePublication()
    {
        RunOnSta(() =>
        {
            using BlockingMetricSource source = new();
            TimelineRenderSnapshot snapshot = new(
                1,
                "selection-lifecycle",
                [],
                itemSource: source);
            ProbeWorkspace workspace = new();
            workspace.Selection.AdoptMaterialized(
                ImmutableHashSet.Create(new MidoraId(1)),
                new MidoraId(1),
                new MidoraId(1));
            DesktopSessionController session = new();
            session.Workspaces.Add(workspace);
            workspace.StartIncompleteMetrics(snapshot);

            Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5)));
            session.CloseWorkspace(workspace);
            TimelineSelectionSnapshot released = workspace.SelectionSnapshot;
            Assert.True(workspace.IsDisposed);
            Assert.Empty(released.Ids);
            Assert.False(released.HasRenderIndex);
            Assert.False(released.MetricsAreComplete);
            source.Release.Set();

            Assert.True(source.Finished.Wait(TimeSpan.FromSeconds(5)));
            DrainDispatcher();
            Assert.DoesNotContain(workspace, session.Workspaces);
            Assert.Same(released, workspace.SelectionSnapshot);
            Assert.False(workspace.SelectionSnapshot.MetricsAreComplete);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void DeferredSelectionMetricsCannotPublishAcrossSelectionRevision()
    {
        RunOnSta(() =>
        {
            using BlockingMetricSource source = new();
            TimelineRenderSnapshot snapshot = new(
                1,
                "selection-revision",
                [],
                itemSource: source);
            ProbeWorkspace workspace = new();
            workspace.Selection.AdoptMaterialized(
                ImmutableHashSet.Create(new MidoraId(1)),
                new MidoraId(1),
                new MidoraId(1));
            workspace.StartIncompleteMetrics(snapshot);
            long publishedRevision = workspace.SelectionSnapshot.Revision;

            Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5)));
            workspace.Selection.Add(new MidoraId(2));
            source.Release.Set();

            Assert.True(source.Finished.Wait(TimeSpan.FromSeconds(5)));
            for (int attempt = 0; attempt < 20; attempt++)
            {
                DrainDispatcher();
                Thread.Sleep(5);
            }
            Assert.True(workspace.Selection.Revision > publishedRevision);
            Assert.Equal(publishedRevision, workspace.SelectionSnapshot.Revision);
            Assert.False(workspace.SelectionSnapshot.MetricsAreComplete);
            workspace.CancelBackgroundPresentationWork();
        });
    }

    private sealed class ProbeWorkspace()
        : WorkspaceViewModel(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new MidoraId(99)),
            "Probe")
    {
        public override void Rebuild(MidoraProject project, long revision)
        {
        }

        public void StartIncompleteMetrics(TimelineRenderSnapshot snapshot)
        {
            PublishMaterializedSelection(
                new Dictionary<TimelineItemKind, TimelineSelectionMetrics>(),
                metricsAreComplete: false);
            ScheduleMaterializedSelectionMetricsIfNeeded(
                metricsAreComplete: false,
                [snapshot]);
        }
    }

    private sealed class BlockingMetricSource : ITimelineRenderItemSource, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Finished { get; } = new();
        public long Count => 5_000;
        public long MaximumEndTick => 5_000;
        public ulong ContentFingerprint => 0x6173_299dUL;

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination) =>
            throw new InvalidOperationException("The lifecycle test only enumerates metrics.");

        public bool TryGetById(MidoraId id, out TimelineRenderItem item)
        {
            item = default;
            return false;
        }

        public void VisitByIds(
            IReadOnlySet<MidoraId> ids,
            Action<TimelineRenderItem> visitor)
        {
            try
            {
                Started.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The metrics scan was not released.");
                foreach (MidoraId id in ids) visitor(Item(id.Value));
            }
            finally
            {
                Finished.Set();
            }
        }

        public IEnumerable<TimelineRenderItem> EnumerateAll()
        {
            throw new InvalidOperationException(
                "Selection presentation must resolve only the requested IDs.");
        }

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
            Finished.Dispose();
        }

        private static TimelineRenderItem Item(long value) => new(
            new MidoraId(value),
            TimelineItemKind.DirectMidiNote,
            value - 1,
            value,
            60,
            100,
            0,
            TimelineItemState.None);
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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
