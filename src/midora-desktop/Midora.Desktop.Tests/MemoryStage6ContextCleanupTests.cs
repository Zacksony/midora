using System.Reflection;
using System.ComponentModel;
using Midora.Application;
using Midora.Audio;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Midora.Midi;
using Midora.Persistence;
using Midora.Playback;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class MemoryStage6ContextCleanupTests
{
    [Theory]
    [InlineData("create", "none")]
    [InlineData("create", "workspace")]
    [InlineData("create", "owner")]
    [InlineData("create", "presentation")]
    [InlineData("open", "none")]
    [InlineData("open", "workspace")]
    [InlineData("open", "owner")]
    [InlineData("open", "presentation")]
    [InlineData("import", "none")]
    [InlineData("import", "workspace")]
    [InlineData("import", "owner")]
    [InlineData("import", "presentation")]
    public async Task ProjectActivationReportsFailuresWithoutDestroyingItsAdoptedContext(string entry, string fault)
    {
        using MidoraProject oldProject = new(480);
        using ProjectCompilationSession oldCompilation = new(oldProject);
        using ProjectDocumentSession oldDocument = new(oldCompilation);
        ProjectPersistenceCoordinator oldPersistence = new(oldDocument, new MidoraProjectPackageV1("1.0.0-dev-test"));
        List<string> released = [];
        oldDocument.Execute(new TrackedHistoryCommand(released));
        InvalidOperationException injected = new("Injected " + fault + " activation failure.");
        RecordingOwner oldOwner = new(released, () =>
        {
            Assert.Empty(oldDocument.History);
            Assert.Throws<ObjectDisposedException>(() => oldCompilation.LastAttempt);
        }, fault == "owner" ? injected : null);
        IAsyncDisposable oldContext = CreateContext(oldOwner, oldCompilation, oldDocument, oldPersistence, null, null);
        await using DesktopSessionController desktop = new();
        typeof(DesktopSessionController).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(desktop, oldContext);
        RecordingWorkspace first = new(oldProject.AllocateStableId(), "first", released,
            fault == "workspace" ? injected : null);
        RecordingWorkspace second = new(oldProject.AllocateStableId(), "second", released, null);
        desktop.Workspaces.Add(first);
        desktop.Workspaces.Add(second);
        PropertyChangedEventHandler failPresentation = (_, args) =>
        {
            if (fault == "presentation" && args.PropertyName == nameof(DesktopSessionController.CompilerDiagnostics))
                throw injected;
        };
        string? inputPath = null;
        try
        {
            if (entry == "open")
            {
                string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "memory-stage6-activation");
                Directory.CreateDirectory(directory);
                inputPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".midora");
                using MidoraProject nextSource = new(192);
                nextSource.Metadata.ProjectName = "Next";
                await new MidoraProjectPackageV1("1.0.0-dev-test").SaveProjectAsync(nextSource, inputPath);
            }
            desktop.PropertyChanged += failPresentation;
            Exception? failure;
            try { failure = await Record.ExceptionAsync(Activate); }
            finally { desktop.PropertyChanged -= failPresentation; }

            if (fault == "none") Assert.Null(failure);
            else Assert.Same(injected, failure);
            Assert.Equal(new[] { "first", "second", "history", "owner" }, released);
            Assert.Equal(1, oldOwner.DisposeCalls);
            Assert.Empty(oldDocument.History);
            Assert.Throws<ObjectDisposedException>(() => oldCompilation.LastAttempt);
            Assert.Equal(0, oldCompilation.PreparationStorage.TotalRetainedBytes);
            Assert.True(desktop.HasProject);
            Assert.NotSame(oldProject, desktop.Project);
            Assert.Equal("Next", desktop.Project!.Metadata.ProjectName);
            Assert.True(desktop.Document!.Compilation.LastAttempt.IsConsumable);
            Assert.DoesNotContain(first, desktop.Workspaces);
            Assert.DoesNotContain(second, desktop.Workspaces);
            if (fault != "presentation")
                Assert.Contains(desktop.Workspaces, workspace => workspace.Kind == WorkspaceKind.Arrangement);

            await oldContext.DisposeAsync();
            Assert.Equal(1, oldOwner.DisposeCalls);
        }
        finally
        {
            desktop.PropertyChanged -= failPresentation;
            await desktop.CloseProjectAsync();
            if (inputPath is not null) File.Delete(inputPath);
        }

        async Task Activate()
        {
            if (entry == "create")
                await desktop.CreateProjectAsync(new() { ProjectName = "Next" });
            else if (entry == "open")
                await desktop.OpenProjectAsync(inputPath!);
            else
            {
                StandardMidiFileTrack track = new(240,
                [
                    StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                    StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 23))
                ]);
                await desktop.ImportMidiBytesAsNewProjectAsync(StandardMidiFile.EncodeType1(192, [track]), "Next");
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClosingAttemptsEveryWorkspaceAndContextAfterEarlierFailures(bool failNotification, bool failOwner)
    {
        using MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, new MidoraProjectPackageV1("1.0.0-dev-test"));
        List<string> released = [];
        document.Execute(new TrackedHistoryCommand(released));
        InvalidOperationException workspaceFailure = new("Injected first Workspace cleanup failure.");
        ArgumentException notificationFailure = new("Injected diagnostic notification failure.");
        IOException ownerFailure = new("Injected Project owner cleanup failure.");
        RecordingOwner owner = new(released, () =>
        {
            Assert.Empty(document.History);
            Assert.Throws<ObjectDisposedException>(() => compilation.LastAttempt);
        }, failOwner ? ownerFailure : null);
        IAsyncDisposable context = CreateContext(owner, compilation, document, persistence, null, null);
        await using DesktopSessionController desktop = new();
        typeof(DesktopSessionController).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(desktop, context);
        RecordingWorkspace first = new(project.AllocateStableId(), "first", released, workspaceFailure);
        RecordingWorkspace second = new(project.AllocateStableId(), "second", released, null);
        desktop.Workspaces.Add(first);
        desktop.Workspaces.Add(second);
        typeof(DesktopSessionController).GetField("_activeWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(desktop, first);
        if (failNotification)
            desktop.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DesktopSessionController.CompilerDiagnostics))
                    throw notificationFailure;
            };

        Exception? failure = await Record.ExceptionAsync(() => desktop.DisposeAsync().AsTask());

        List<Exception> expected = [workspaceFailure];
        if (failNotification) expected.Add(notificationFailure);
        if (failOwner) expected.Add(ownerFailure);
        if (expected.Count == 1) Assert.Same(workspaceFailure, failure);
        else
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Equal(expected.Count, aggregate.InnerExceptions.Count);
            for (int index = 0; index < expected.Count; index++)
                Assert.Same(expected[index], aggregate.InnerExceptions[index]);
        }
        Assert.Equal(new[] { "first", "second", "history", "owner" }, released);
        Assert.Empty(desktop.Workspaces);
        Assert.Null(desktop.ActiveWorkspace);
        Assert.False(desktop.HasProject);
        Assert.Null(desktop.Document);
        Assert.Empty(desktop.CompilerDiagnostics);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(1, owner.DisposeCalls);

        await desktop.CloseProjectAsync();
        await context.DisposeAsync();
        Assert.Equal(new[] { "first", "second", "history", "owner" }, released);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContextAttemptsAllOwnersAndPreservesCleanupFailures(bool failBackend, bool failOwner)
    {
        using MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Unsaved);
        ProjectPersistenceCoordinator persistence = new(document, new MidoraProjectPackageV1("1.0.0-dev-test"));
        List<string> released = [];
        document.Execute(new TrackedHistoryCommand(released));
        Assert.Single(document.History);
        InvalidOperationException backendFailure = new("Injected backend disposal failure.");
        IOException ownerFailure = new("Injected Project owner disposal failure.");
        RecordingBackend backend = new(released, failBackend ? backendFailure : null);
        using PlaybackController playback = new(compilation, backend);
        using ApplicationTaskCoordinator tasks = new(compilation, playback);
        RecordingOwner owner = new(released, () =>
        {
            Assert.Empty(document.History);
            Assert.Throws<ObjectDisposedException>(() => compilation.LastAttempt);
            Assert.Equal(0, compilation.PreparationStorage.TotalRetainedBytes);
            Assert.True((bool)typeof(ApplicationTaskCoordinator)
                .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tasks)!);
        }, failOwner ? ownerFailure : null);
        IAsyncDisposable context = CreateContext(owner, compilation, document, persistence, playback, tasks);

        Exception? failure = await Record.ExceptionAsync(() => context.DisposeAsync().AsTask());

        if (failBackend && failOwner)
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Collection(aggregate.InnerExceptions,
                value => Assert.Same(backendFailure, value),
                value => Assert.Same(ownerFailure, value));
        }
        else if (failBackend) Assert.Same(backendFailure, failure);
        else if (failOwner) Assert.Same(ownerFailure, failure);
        else Assert.Null(failure);
        Assert.Equal(new[] { "backend", "history", "owner" }, released);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1, owner.DisposeCalls);

        await context.DisposeAsync();
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1, owner.DisposeCalls);
    }

    private static IAsyncDisposable CreateContext(IAsyncDisposable owner, ProjectCompilationSession compilation,
        ProjectDocumentSession document, ProjectPersistenceCoordinator persistence,
        PlaybackController? playback, ApplicationTaskCoordinator? tasks) =>
        (IAsyncDisposable)Activator.CreateInstance(
            typeof(DesktopSessionController).GetNestedType("ProjectContext", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [owner, compilation, document, persistence, playback, tasks, null], null)!;

    private sealed class RecordingWorkspace(MidoraId id, string name, List<string> released, Exception? failure)
        : WorkspaceViewModel(WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, id), name)
    {
        public int DisposeCalls { get; private set; }
        public override void Rebuild(MidoraProject project, long revision) { }
        protected override void DisposeCore()
        {
            DisposeCalls++;
            released.Add(Header);
            if (failure is not null) throw failure;
        }
    }

    private sealed class TrackedHistoryCommand(List<string> released) : IProjectEditCommand
    {
        public string Name => "Track cleanup ownership";
        public IPreparedProjectEdit Prepare(MidoraProject project) => new Prepared(released);
        private sealed class Prepared(List<string> released) : IPreparedProjectEdit, IDisposable
        {
            public bool HasChanges => true;
            public ProjectChangeSet Changes { get; } = new();
            public void Apply(MidoraProject project) { }
            public void Undo(MidoraProject project) { }
            public void Dispose() => released.Add("history");
        }
    }

    private sealed class RecordingOwner(List<string> released, Action verifyPreviousOwners, Exception? failure)
        : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await Task.Yield();
            verifyPreviousOwners();
            released.Add("owner");
            if (failure is not null) throw failure;
        }
    }

    private sealed class RecordingBackend(List<string> released, Exception? failure) : IRealtimePlaybackBackend
    {
        public int DisposeCalls { get; private set; }
        public int ActualSampleRate => 48_000;
        public long PositionFrames => 0;
        public long RenderPositionFrames => 0;
        public bool IsBuffering => false;
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public int Prepare() => ActualSampleRate;
        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master) { }
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands) { }
        public void Stop(bool flush) { }
        public void Reset() { }
        public void SelectOutputDevice(string? deviceId) { }
        public void Dispose()
        {
            DisposeCalls++;
            released.Add("backend");
            if (failure is not null) throw failure;
        }
    }
}
