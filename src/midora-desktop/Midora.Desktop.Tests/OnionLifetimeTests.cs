using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class OnionLifetimeTests
{
    [Fact]
    public async Task UndrawnTrackOnionReleasesProjectionWithoutChangingItsPresetOrMusic()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        { ProjectName = "Onion lifetime", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        foreach (string name in new[] { "Target", "Source" })
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(name, instrument.Id));
        var target = session.Project.Tracks[0]; var source = session.Project.Tracks[1];
        session.Execute(ProjectDomainEditCommands.CreateSegment(target.Id, 0, 192));
        session.Execute(ProjectDomainEditCommands.CreateSegment(source.Id, 0, 192));
        var workspace = session.OpenSegment(target.Segments[0].Id);
        long revision = session.Document!.PublicationRevision;
        session.SetCustomOnionSources(workspace, [source.Id]);
        session.ShowAdjacentOnionSource(workspace, previous: false);
        var released = ProjectionReferences(workspace);
        session.SetOnionOpacity(workspace, 0);
        Assert.True(workspace.IsOnionEnabled); Assert.Null(workspace.OnionSnapshot);
        Collect(); Assert.False(released.Snapshot.IsAlive); Assert.False(released.Source.IsAlive);
        Assert.Equal(OnionSourceMode.Next, session.GetOnionControls(workspace)!.SourceMode);
        Assert.Equal(source.Id, Assert.Single(session.GetOnionControls(workspace)!.Sources));
        session.SetOnionOpacity(workspace, .5);
        Assert.NotNull(workspace.OnionSnapshot);
        session.SetOnionEnabled(workspace, false);
        Assert.Null(workspace.OnionSnapshot);
        session.SetOnionEnabled(workspace, true);
        Assert.NotNull(workspace.OnionSnapshot);
        session.SetCustomOnionSources(workspace, []);
        Assert.True(workspace.IsOnionEnabled); Assert.Null(workspace.OnionSnapshot);
        session.SetCustomOnionSources(workspace, [source.Id]);
        var all = session.OpenAllTracks();
        Assert.True(workspace.IsPresentationSuspended); Assert.Null(workspace.OnionSnapshot);
        session.ActiveWorkspace = workspace;
        Assert.NotNull(workspace.OnionSnapshot); Assert.Null(all.OnionSnapshot);
        Assert.Equal(.5, session.GetOnionControls(workspace)!.Opacity);
        Assert.Equal(source.Id, Assert.Single(session.GetOnionControls(workspace)!.Sources));
        Assert.Equal(revision, session.Document.PublicationRevision);
        session.SetOnionEnabled(workspace, false);
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(workspace.IsOnionEnabled) && workspace.IsOnionEnabled)
                session.CloseWorkspace(workspace);
        };
        session.SetOnionEnabled(workspace, true);
        Assert.True(workspace.IsDisposed); Assert.Null(workspace.OnionSnapshot);
    }

    [Fact]
    public async Task ZeroOpacityAndClearedSubVoiceSourcesDoNotKeepSourceGraph()
    {
        await using var session = new DesktopSessionController();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        { ProjectName = "Voice lifetime", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        var instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Source"));
        var workspace = session.OpenInstrument(instrument.Id);
        session.ActivateSubVoiceEditor(workspace, instrument.SubVoices[0].Id);
        session.SetCustomOnionSources(workspace, [instrument.SubVoices[1].Id]);
        Assert.NotNull(workspace.OnionSnapshot);
        session.SetOnionOpacity(workspace, 0);
        Assert.Null(workspace.OnionSnapshot); Assert.Single(session.GetOnionControls(workspace)!.Sources);
        session.SetOnionOpacity(workspace, .25);
        Assert.NotNull(workspace.OnionSnapshot);
        session.SetCustomOnionSources(workspace, []);
        Assert.Null(workspace.OnionSnapshot); Assert.True(workspace.IsOnionEnabled);
    }

    [Fact]
    public void AllTracksRepeatedHideResumeKeepsStaleIndexAndViewportButDisposeReleasesEverything()
    {
        Sta(() =>
        {
            using var project = CreateProject();
            using var compiler = new MidoraCompiler();
            var canonical = compiler.CompileFull(project);
            Assert.True(canonical.IsConsumable);
            var callback = new ModeCallback();
            using var workspace = new AllTracksWorkspaceViewModel(callback.SetMode);
            workspace.Rebuild(project, 0);
            workspace.Mode = ProjectPresentationAllTracksModeV3.Compiled;
            workspace.UpdateCompilation(canonical, true);
            PumpUntil(() => !workspace.IsBuilding);
            Assert.False(workspace.IsStale);
            object compiled = Field(workspace, "_compiled")!;
            workspace.StartTick = 33; workspace.TickSpan = 1234; workspace.FirstLane = 42; workspace.LaneHeight = 21;
            for (int i = 0; i < 100; i++)
            {
                workspace.SuspendPresentation(); workspace.SuspendPresentation();
                Assert.Null(workspace.OnionSnapshot);
                Assert.Empty((IReadOnlyList<TimelineOnionTrack>)Field(workspace, "_tracks")!);
                Assert.Null(Field(workspace, "_candidate")); Assert.Null(Field(workspace, "_requested"));
                Assert.Same(compiled, Field(workspace, "_compiled"));
                workspace.ResumePresentation(); workspace.ResumePresentation();
                workspace.Rebuild(project, i + 1); workspace.UpdateCompilation(canonical, false);
                Assert.True(workspace.IsStale); Assert.False(workspace.IsBuilding);
                Assert.NotNull(workspace.OnionSnapshot); Assert.Same(compiled, Field(workspace, "_compiled"));
                Assert.Equal((33L, 1234L, 42, 21d), (workspace.StartTick, workspace.TickSpan, workspace.FirstLane, workspace.LaneHeight));
            }
            workspace.Dispose(); workspace.Dispose(); workspace.CancelBackgroundPresentationWork();
            Assert.True(workspace.IsDisposed); Assert.Null(workspace.OnionSnapshot);
            foreach (string field in new[] { "_compiled", "_candidate", "_requested", "_publishedTracks", "_publishedCompiled", "_setMode", "_progressDispatch" })
                Assert.Null(Field(workspace, field));
            workspace.UpdateCompilation(canonical, true); workspace.Rebuild(project, 1001);
            workspace.RetryBuild(); workspace.CancelBuild();
            Assert.False(workspace.IsBuilding); Assert.Null(workspace.OnionSnapshot);
        });
    }

    [Fact]
    public void ReturningToAnExistingCompiledIndexClearsAnUnrelatedCanceledBuildStatus()
    {
        Sta(() =>
        {
            using var project = CreateProject();
            using var compiler = new MidoraCompiler();
            var first = compiler.CompileFull(project);
            using var workspace = new AllTracksWorkspaceViewModel(_ => { });
            workspace.Rebuild(project, 0);
            workspace.Mode = ProjectPresentationAllTracksModeV3.Compiled;
            workspace.UpdateCompilation(first, true);
            PumpUntil(() => !workspace.IsBuilding);
            Assert.False(workspace.IsStale);
            object firstIndex = Field(workspace, "_compiled")!;
            project.Tracks[0].Segments[0].Notes[0].Note = 62;
            var second = compiler.CompileFull(project);
            Assert.NotEqual(first.Fingerprint, second.Fingerprint);
            var gate = (SemaphoreSlim)typeof(AllTracksWorkspaceViewModel)
                .GetField("BuildGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            gate.Wait();
            try
            {
                workspace.UpdateCompilation(second, true);
                Assert.True(workspace.IsBuilding);
                workspace.CancelBuild();
                workspace.UpdateCompilation(second, true);
                Assert.False(workspace.IsBuilding); Assert.True(workspace.IsStale);
                Assert.Contains("canceled", workspace.Status);
                project.Tracks[0].Segments[0].Notes[0].Note = 60;
                var restored = compiler.CompileFull(project);
                Assert.Equal(first.Fingerprint, restored.Fingerprint);
                workspace.Rebuild(project, 2); workspace.UpdateCompilation(restored, true);
                Assert.False(workspace.IsBuilding); Assert.False(workspace.IsStale);
                Assert.Contains("Current", workspace.Status);
                Assert.DoesNotContain("canceled", workspace.Status);
                Assert.Same(firstIndex, Field(workspace, "_compiled"));
            }
            finally { gate.Release(); }
        });
    }

    [Fact]
    public void ClosingAllTracksFromBuildStartedNotificationDoesNotStartOrRepublishWork()
    {
        Sta(() =>
        {
            using var project = CreateProject();
            using var compiler = new MidoraCompiler();
            var canonical = compiler.CompileFull(project);
            using var workspace = new AllTracksWorkspaceViewModel(_ => { });
            workspace.Rebuild(project, 0);
            workspace.Mode = ProjectPresentationAllTracksModeV3.Compiled;
            workspace.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(workspace.IsBuilding) && workspace.IsBuilding) workspace.Dispose();
            };
            workspace.UpdateCompilation(canonical, true);
            Assert.True(workspace.IsDisposed); Assert.False(workspace.IsBuilding);
            Assert.Null(workspace.OnionSnapshot); Assert.Null(Field(workspace, "_progressDispatch"));
        });
    }

    [Fact]
    public void TenThousandBuildProgressReportsKeepOneRevocableDispatcherOperation()
    {
        Sta(() =>
        {
            using var workspace = new AllTracksWorkspaceViewModel(_ => { });
            using var cancellation = new CancellationTokenSource();
            var type = typeof(AllTracksWorkspaceViewModel).GetNestedType("BuildProgressDispatch", BindingFlags.NonPublic)!;
            var dispatch = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [new WeakReference<AllTracksWorkspaceViewModel>(workspace), Dispatcher.CurrentDispatcher, 0L, cancellation.Token], null)!;
            var report = type.GetMethod("Report")!;
            DispatcherOperation? operation = null;
            for (int i = 0; i < 10_000; i++)
            {
                report.Invoke(dispatch, [new CompiledOnionBuildProgress("Controlled progress", i, 10_000)]);
                var pending = Assert.IsType<DispatcherOperation>(Field(dispatch, "_pending"));
                if (operation is null) operation = pending;
                else Assert.Same(operation, pending);
            }
            ((IDisposable)dispatch).Dispose(); ((IDisposable)dispatch).Dispose();
            Assert.Equal(DispatcherOperationStatus.Aborted, operation!.Status);
            Assert.Null(Field(dispatch, "_pending"));
        });
    }

    [Fact]
    public void DisposedAllTracksDoesNotRemainOwnedByQueuedBuildOrModeCallback()
    {
        Sta(() =>
        {
            using var project = CreateProject();
            using var compiler = new MidoraCompiler();
            var canonical = compiler.CompileFull(project);
            var gate = (SemaphoreSlim)typeof(AllTracksWorkspaceViewModel)
                .GetField("BuildGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            gate.Wait();
            try
            {
                var references = CreateAndDisposeWaitingWorkspace(project, canonical);
                Collect();
                Assert.False(references.Workspace.IsAlive);
                Assert.False(references.Callback.IsAlive);
                Assert.False(references.Snapshot.IsAlive);
            }
            finally { gate.Release(); }
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Snapshot, WeakReference Source) ProjectionReferences(WorkspaceViewModel workspace)
    {
        // Keep the strong reference, including assertion argument temporaries, in
        // this non-inlined scope rather than the async lifetime test's GC frame.
        var snapshot = Assert.IsType<TimelineOnionSnapshot>(workspace.OnionSnapshot);
        return (new(snapshot), new(snapshot.Blocks.SelectMany(b => b).First().Clips[0].Source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Workspace, WeakReference Callback, WeakReference Snapshot) CreateAndDisposeWaitingWorkspace(
        MidoraProject project, CanonicalCompiledResult canonical)
    {
        var callback = new ModeCallback();
        var workspace = new AllTracksWorkspaceViewModel(callback.SetMode);
        workspace.Rebuild(project, 0);
        var snapshot = new WeakReference(workspace.OnionSnapshot!);
        workspace.Mode = ProjectPresentationAllTracksModeV3.Compiled;
        workspace.UpdateCompilation(canonical, true);
        Assert.True(workspace.IsBuilding);
        workspace.CancelBuild(); workspace.UpdateCompilation(canonical, true);
        Assert.False(workspace.IsBuilding);
        workspace.RetryBuild(); Assert.True(workspace.IsBuilding);
        workspace.Dispose();
        return (new(workspace), new(callback), snapshot);
    }

    private static MidoraProject CreateProject()
    {
        var project = new MidoraProject(192);
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 48, 60, 100));
        var track = new LogicalTrack(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        var segment = new Segment(project) { LengthTicks = 192 }; track.Segments.Add(segment);
        segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 48, Note = 60, Velocity = 100 });
        return project;
    }
    private sealed class ModeCallback { public void SetMode(ProjectPresentationAllTracksModeV3 mode) { } }
    private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static void PumpUntil(Func<bool> complete)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!complete())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
