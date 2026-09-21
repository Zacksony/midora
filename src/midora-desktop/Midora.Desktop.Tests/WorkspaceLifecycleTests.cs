using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class WorkspaceLifecycleTests
{
    [Fact]
    public void CancelingQueuedCompletionReleasesItsGraphWithoutPumpingDispatcher()
    {
        using CancellationTokenSource cancellation = new();
        WeakReference owner = QueuePresentationCompletion(cancellation.Token);
        cancellation.Cancel();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(owner.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueuePresentationCompletion(CancellationToken token)
    {
        object owner = new();
        WorkspacePresentationDispatch.Post(Dispatcher.CurrentDispatcher, token, () => GC.KeepAlive(owner));
        return new(owner);
    }

    [Fact]
    public void SharedSettingsUnsubscribeExactlyOnceAndReleaseClosedViewModelsAndSnapshots()
    {
        TimelineEditorSettings settings = new();
        List<WeakReference> released = [];
        for (int i = 0; i < 100; i++)
        {
            released.AddRange(CreateClosedConductor(settings));
            Assert.Equal(0, CountWorkspaceSubscribers(settings));
        }
        // Controlled liveness check, deliberately only in this diagnostic test.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(released, value => Assert.False(value.IsAlive));
        GC.KeepAlive(settings);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateClosedConductor(TimelineEditorSettings settings)
    {
        using MidoraProject project = new(192);
        TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor, settings);
        workspace.Rebuild(project, 1);
        Assert.Equal(1, CountWorkspaceSubscribers(settings));
        WeakReference[] references = [new(workspace), new(workspace.Snapshot!), new(workspace.ConductorTempoSnapshot!)];
        workspace.Dispose();
        workspace.Dispose();
        workspace.SuspendPresentation();
        workspace.ResumePresentation();
        Assert.True(workspace.IsDisposed);
        Assert.True(workspace.IsPresentationSuspended);
        Assert.Null(workspace.Snapshot);
        Assert.Null(workspace.ConductorTempoSnapshot);
        Assert.Null(workspace.ConductorListSource);
        return references;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedEditorReleasesProjectEvenWhenTheDisposedViewModelIsRetained(bool instrumentEditor)
    {
        (WorkspaceViewModel workspace, WeakReference project) = CreateDisposedEditor(instrumentEditor);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(project.IsAlive);
        Assert.True(workspace.IsDisposed);
        Assert.Null(workspace.ObjectList.Source);
        GC.KeepAlive(workspace);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkspaceViewModel Workspace, WeakReference Project) CreateDisposedEditor(bool instrumentEditor)
    {
        using MidoraProject project = new(192);
        WorkspaceViewModel workspace;
        if (instrumentEditor)
        {
            EventInstrument instrument = new(project) { Name = "Instrument" };
            instrument.SubVoices.Add(new(project) { Name = "Voice" });
            project.EventInstruments.Add(instrument);
            workspace = new InstrumentWorkspaceViewModel(instrument.Id, "Instrument");
        }
        else
        {
            MidiChannelRoot root = new(project) { Name = "Root" };
            project.MidiChannelRoots.Add(root);
            PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
            project.PureMidiTracks.Add(track);
            MidiSegment segment = new(project) { LengthTicks = 192 };
            track.Segments.Add(segment);
            workspace = new TimelineWorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI", TimelineWorkspaceMode.Segment);
        }
        workspace.SuspendPresentation();
        workspace.Rebuild(project, 1);
        workspace.Dispose();
        return (workspace, new(project));
    }

    [Fact]
    public async Task ConductorSuspendRestoresListAndNavigationWithoutChangingSelection()
    {
        using MidoraProject project = new(192);
        using TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForType(WorkspaceKind.ConductorTrack), "Conductor", TimelineWorkspaceMode.Conductor);
        workspace.Rebuild(project, 1);
        workspace.StartTick = 300;
        workspace.TickSpan = 768;
        workspace.ConductorListWidth = new(510);
        workspace.ConductorListFirstRow = 19;
        workspace.ConductorTempoHeight = new(4, GridUnitType.Star);
        workspace.Selection.Replace(project.Conductor.Tempos[0].Id);
        TimelineRenderSnapshot snapshot = workspace.Snapshot!;
        ConductorEventListSource previous = workspace.ConductorListSource!;

        workspace.SuspendPresentation();
        workspace.SuspendPresentation();
        Assert.Null(workspace.ConductorListSource);
        Assert.Same(snapshot, workspace.Snapshot);
        workspace.ResumePresentation();
        workspace.ResumePresentation();

        Assert.False(workspace.IsPresentationSuspended);
        Assert.NotSame(previous, workspace.ConductorListSource);
        Assert.NotEmpty(await workspace.ConductorListSource!.ReadRowsAsync(0, 5, CancellationToken.None));
        Assert.Equal(300, workspace.StartTick);
        Assert.Equal(768, workspace.TickSpan);
        Assert.Equal(new GridLength(510), workspace.ConductorListWidth);
        Assert.Equal(19, workspace.ConductorListFirstRow);
        Assert.Equal(new GridLength(4, GridUnitType.Star), workspace.ConductorTempoHeight);
        Assert.Equal(project.Conductor.Tempos[0].Id, workspace.Selection.Primary);
    }

    [Fact]
    public void SettingsRemainLiveWhileHiddenButCannotNotifyDisposedWorkspace()
    {
        TimelineEditorSettings settings = new();
        TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new(123)), "Segment", TimelineWorkspaceMode.Segment, settings);
        int notifications = 0;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workspace.GridVisible)) notifications++;
        };
        workspace.SuspendPresentation();
        settings.GridVisible = false;
        Assert.False(workspace.GridVisible);
        Assert.Equal(1, notifications);
        workspace.ResumePresentation();
        workspace.Dispose();
        settings.GridVisible = true;
        Assert.Equal(1, notifications);
        Assert.Equal(0, CountWorkspaceSubscribers(settings));
    }

    [Fact]
    public async Task ControllerClosesAllKindsAcrossOneHundredCyclesAndProjectReplacement()
    {
        await using DesktopSessionController session = new();
        WorkspaceViewModel? previousArrangement = null;
        for (int projectIndex = 0; projectIndex < 2; projectIndex++)
        {
            await session.CreateProjectAsync(new NewProjectCreationRequest
            {
                ProjectName = $"Lifecycle {projectIndex}",
                PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
            });
            if (previousArrangement is not null) Assert.True(previousArrangement.IsDisposed);
            previousArrangement = session.ActiveWorkspace;
            session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
            LogicalTrack logical = Assert.Single(session.Project.Tracks);
            session.Execute(ProjectDomainEditCommands.CreateSegment(logical.Id, 0, 192));
            session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            PureMidiTrack midi = Assert.Single(session.Project.PureMidiTracks);
            session.Execute(ProjectDomainEditCommands.CreateMidiSegment(midi.Id, 0, 192));
            int historyCount = session.Document!.History.Count;

            for (int cycle = 0; cycle < 50; cycle++)
            {
                TimelineWorkspaceViewModel logicalWorkspace = session.OpenSegment(logical.Segments[0].Id);
                logicalWorkspace.StartTick = 17;
                logicalWorkspace.ObjectList.IsVisible = true;
                logicalWorkspace.ObjectList.ColumnWidth = new(460);
                logicalWorkspace.ObjectList.FirstRow = 37;
                TimelineWorkspaceViewModel midiWorkspace = session.OpenSegment(midi.Segments[0].Id);
                InstrumentWorkspaceViewModel instrumentWorkspace = session.OpenInstrument(instrument.Id);
                WorkspaceViewModel conductor = session.OpenWorkspace(session.ProjectTree.Single(x => x.Kind == ProjectTreeNodeKind.Conductor));
                Assert.True(logicalWorkspace.IsPresentationSuspended);
                session.ActiveWorkspace = logicalWorkspace;
                Assert.Equal(17, logicalWorkspace.StartTick);
                Assert.Equal(new GridLength(460), logicalWorkspace.ObjectList.ColumnWidth);
                Assert.Equal(37, logicalWorkspace.ObjectList.FirstRow);
                Assert.False(logicalWorkspace.IsPresentationSuspended);
                Assert.NotSame(logicalWorkspace.EditorSettings, midiWorkspace.EditorSettings);
                Assert.Equal(1, CountWorkspaceSubscribers(logicalWorkspace.EditorSettings));
                Assert.Equal(1, session.EditorStates.SubscriberCount);
                WorkspaceViewModel diagnostics = session.OpenWorkspace(session.ProjectTree.Single(x => x.Kind == ProjectTreeNodeKind.Diagnostics));
                session.CloseWorkspace(logicalWorkspace);
                Assert.Null(typeof(DesktopSessionController).GetField("_diagnosticScopeWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
                foreach (WorkspaceViewModel workspace in new[] { midiWorkspace, instrumentWorkspace, conductor, diagnostics })
                {
                    session.CloseWorkspace(workspace);
                    Assert.True(workspace.IsDisposed);
                }
                session.ActiveWorkspace = logicalWorkspace;
                Assert.NotSame(logicalWorkspace, session.ActiveWorkspace);
                Assert.Equal(0, session.EditorStates.SubscriberCount);
                Assert.Equal(1, CountWorkspaceSubscribers(session.ArrangementEditorSettings));
                Assert.Single(session.Workspaces);
                Assert.Equal(historyCount, session.Document.History.Count);
            }
        }
        await session.CloseProjectAsync();
        Assert.True(previousArrangement!.IsDisposed);
        Assert.Equal(0, CountWorkspaceSubscribers(session.ArrangementEditorSettings));
        Assert.Equal(0, session.EditorStates.SubscriberCount);
        Assert.Null(session.ArrangementEditorSettings.TimeSignatureMap);
        Assert.True(session.EditorStates.IsDisposed);
    }

    private static int CountWorkspaceSubscribers(TimelineEditorSettings settings) =>
        ((PropertyChangedEventHandler?)typeof(ObservableObject)
            .GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings))?
            .GetInvocationList().Count(handler => handler.Target is WorkspaceViewModel) ?? 0;
}
