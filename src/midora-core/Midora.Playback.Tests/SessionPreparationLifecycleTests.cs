using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class SessionPreparationLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HeldReplacementTracksOldAndNewDuringBackendCallAndReleasesTemporaryLeases(bool endGate, bool fail)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "preparation-lifecycle-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sf2");
        using var font = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        using var project = PreparationStorageCacheTests.CreateProject();
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        instrument.TemplateLengthTicks = 480;
        SubVoice voice = Assert.Single(instrument.SubVoices);
        voice.Events.Clear();
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        using var session = new ProjectCompilationSession(project, path);
        using var backend = new HeldInspectingBackend(session, fail);
        using var controller = new PlaybackController(session, backend);
        controller.StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(instrument.Id, Tempo: 120m));
        Assert.Equal(2, session.PreparationStorage.ActiveLeases);
        backend.BytesBeforeReplacement = session.PreparationStorage.ActiveBytes;
        backend.PositionFrames = endGate ? 12_000 : 0;
        backend.RenderPositionFrames = endGate ? 16_800 : backend.CurrentPlan!.TotalFrameCount - 3L * backend.ActualSampleRate;

        if (fail) Assert.Throws<InvalidOperationException>(Replace);
        else Replace();

        Assert.Equal(1, backend.Replacements);
        Assert.Equal(fail ? 0 : 2, session.PreparationStorage.ActiveLeases);
        if (fail)
        {
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(0, session.PreparationStorage.ActiveBytes);
        }
        else
        {
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
            Assert.Equal(0, session.PreparationStorage.ActiveLeases);
        }
        void Replace()
        {
            if (endGate) _ = controller.EndHeldPreviewGate();
            else controller.Update();
        }
    }

    [Theory]
    [InlineData(ProjectCompilationExecutionMode.Synchronous)]
    [InlineData(ProjectCompilationExecutionMode.Background)]
    public void DisposedSessionDoesNotKeepReleasedCanonicalBackingAlive(ProjectCompilationExecutionMode mode)
    {
        using var project = PreparationStorageCacheTests.CreateProject();
        using var session = new ProjectCompilationSession(project, executionMode: mode);
        var references = CaptureCanonical(session);
        Assert.True(session.PreparationStorage.BaselineBytes > 0);

        session.Dispose();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.LastAttempt);
        Assert.Null(session.LastSuccessfulResult);
        Assert.Same(project, session.Project);
        Assert.Equal(0, session.PreparationStorage.BaselineBytes);
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
        AssertCollected(references);
        GC.KeepAlive(session);
    }

    [Theory]
    [InlineData(ProjectCompilationExecutionMode.Synchronous)]
    [InlineData(ProjectCompilationExecutionMode.Background)]
    public void ExternalActiveLeaseOutlivesSessionAndReleasesCanonicalAtLastReader(ProjectCompilationExecutionMode mode)
    {
        using var project = PreparationStorageCacheTests.CreateProject();
        using var session = new ProjectCompilationSession(project, executionMode: mode);
        var references = CaptureCanonical(session);
        IDisposable reader = RetainCurrentCanonical(session);
        session.Dispose();

        Assert.Equal(0, session.PreparationStorage.BaselineBytes);
        Assert.Equal(1, session.PreparationStorage.ActiveLeases);
        Assert.True(session.PreparationStorage.ActiveBytes > 0);
        Collect();
        VerifyLiveCanonical(references[0]);
        reader.Dispose();
        reader.Dispose();
        Assert.Null(reader.GetType().GetField("_description", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader));
        Assert.Equal(0, session.PreparationStorage.ActiveLeases);
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
        AssertCollected(references);
        GC.KeepAlive(session);
    }

    [Fact]
    public void DisposeReleasesOnlyBackgroundMirrorWithoutDisposingSharedProjectPages()
    {
        using var project = PreparationStorageCacheTests.CreateProject();
        using var session = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background);
        long fingerprint = session.LastAttempt.Fingerprint;
        WeakReference mirror = CaptureMirror(session);

        session.Dispose();

        AssertCollected([mirror]);
        Assert.Same(project, typeof(ProjectCompilationSession)
            .GetField("_compilationProject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
        using var compiler = new MidoraCompiler();
        var result = compiler.CompileFull(project);
        Assert.True(result.IsConsumable);
        Assert.Equal(fingerprint, result.Fingerprint);
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task DisposeCancelsBackgroundAttemptBeforeDroppingOwnedCanonicalReferences()
    {
        using var project = PreparationStorageCacheTests.CreateProject();
        using var session = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.Zero);
        var references = CaptureCanonical(session);
        using var entered = new ManualResetEventSlim();
        session.CompilationStartingForTests = token =>
        {
            entered.Set();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        };
        LogicalTrack track = Assert.Single(project.Tracks);
        var changes = new ProjectChangeSet();
        changes.TrackIds.Add(track.Id);
        session.ApplyEdit(_ => track.Segments[0].Notes[0].Velocity = 73, changes);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        await Task.Run(session.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.LastAttempt);
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
        AssertCollected(references);
        GC.KeepAlive(session);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable RetainCurrentCanonical(ProjectCompilationSession session) =>
        session.RetainPreparationStorage(session.LastAttempt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CaptureCanonical(ProjectCompilationSession session)
    {
        CanonicalCompiledResult result = session.LastAttempt;
        object events = typeof(CanonicalCompiledResult)
            .GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;
        Assert.True(((Array)events).Length > 0);
        return [new(result), new(events)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureMirror(ProjectCompilationSession session)
    {
        object mirror = typeof(ProjectCompilationSession)
            .GetField("_compilationProject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Assert.NotSame(session.Project, mirror);
        return new(mirror);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyLiveCanonical(WeakReference reference)
    {
        var result = Assert.IsType<CanonicalCompiledResult>(reference.Target);
        Assert.True(result.IsConsumable);
        Assert.False(result.Events.IsEmpty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference[] references)
    {
        Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class HeldInspectingBackend(ProjectCompilationSession session, bool fail)
        : IRealtimePlaybackBackend, IHeldPreviewRealtimePlaybackBackend
    {
        public int ActualSampleRate => 48_000;
        public long PositionFrames { get; set; }
        public long RenderPositionFrames { get; set; }
        public bool IsBuffering => false;
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public MidiRenderPlan? CurrentPlan { get; private set; }
        public long BytesBeforeReplacement { get; set; }
        public int Replacements { get; private set; }
        public int Prepare() => ActualSampleRate;
        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master) => CurrentPlan = plan;
        public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout) => RenderPositionFrames;
        public void ReplaceHeldPreviewFutureAndResume(MidiRenderPlan plan, long producerFrontierFrame, TimeSpan timeout)
        {
            Replacements++;
            Assert.Equal(4, session.PreparationStorage.ActiveLeases);
            Assert.True(session.PreparationStorage.ActiveBytes > BytesBeforeReplacement);
            Assert.NotNull(CurrentPlan);
            Assert.NotSame(CurrentPlan, plan);
            if (fail) throw new InvalidOperationException("Injected held replacement failure.");
            CurrentPlan = plan;
        }
        public void ResumeHeldPreviewFromProducerFrontier() { }
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands) { }
        public void Stop(bool flush) => CurrentPlan = null;
        public void Reset() => CurrentPlan = null;
        public void SelectOutputDevice(string? deviceId) { }
        public void Dispose() => CurrentPlan = null;
    }
}
