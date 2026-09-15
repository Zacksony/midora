using System.ComponentModel;
using System.Runtime.Versioning;
using Midora.Audio;
using Midora.Application;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;
using Midora.Playback.BassWasapi;
using Xunit.Sdk;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioWorkerSessionPolicyTests
{
    [Fact]
    public void ManagedRealtimeWorkerPausesAppliesHeldPreviewGenerationAndResumes()
    {
        VerifyHeldPreviewWorkerRoundTrip(
            NativeAudioIntegrationEnvironment.RequireManagedWorkerPath(),
            allowManagedTestWorker: true);
    }

    [Fact]
    public void NativeAotRealtimeWorkerPausesAppliesHeldPreviewGenerationAndResumes()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT held-preview integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }
        VerifyHeldPreviewWorkerRoundTrip(
            Path.GetFullPath(configured),
            allowManagedTestWorker: false);
    }

    [Fact]
    public void NativeAotRealtimeWorkerOpensTransferredRecoverySpool()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT recovery-spool integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(48_000, 480_000, []);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-worker-recovery-{Guid.NewGuid():N}");
        try
        {
            using AudioCacheSessionStore cache = new(cacheRoot, 0);
            using AudioCacheSessionStore.AudioRecoverySpool spool =
                cache.CreateRecoverySpool(checked(plan.TotalFrameCount * 2L * sizeof(float)));
            spool.ReleaseFileHandleForExternalUse();

            using BassMidiAudioWorkerSession session = new(
                plan,
                soundFontPath,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV2,
                renderAheadMilliseconds: 20,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                Path.GetFullPath(configured),
                nativeDirectory,
                preparingTimeout: TimeSpan.FromSeconds(30),
                allowManagedTestWorker: false,
                audioCache: null,
                bufferingRecoverySpoolPath: spool.Path,
                bufferingRecoveryMemoryFrameCapacity: plan.TotalFrameCount);

            Assert.True(
                session.Status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering);
            Assert.True(File.Exists(spool.Path));
            session.Stop(flush: true, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotRealtimeWorkerConsumesAnAudibleNotePlanWithoutBuffering()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT audible-note integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string soundFontSetCacheIdentity =
            NativeAudioIntegrationEnvironment.RequireSoundFontSetCacheIdentity(soundFontPath);
        MidiRenderPlan plan = new(
            48_000,
            480_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.ProgramChange(0, 0)),
                    new(4_800, MidiMessage.NoteOn(0, 60, 100)),
                    new(240_000, MidiMessage.NoteOff(0, 60, 0))
                ])]);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-worker-audible-{Guid.NewGuid():N}");
        try
        {
            using AudioCacheSessionStore cache = new(cacheRoot, 16 * 1024 * 1024);
            using AudioCacheSessionStore.AudioRecoverySpool recovery =
                cache.CreateRecoverySpool(checked(plan.TotalFrameCount * 2L * sizeof(float)));
            recovery.ReleaseFileHandleForExternalUse();
            using BassMidiAudioWorkerSession session = new(
                plan,
                soundFontPath,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV2,
                renderAheadMilliseconds: 100,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                Path.GetFullPath(configured),
                nativeDirectory,
                preparingTimeout: TimeSpan.FromSeconds(30),
                allowManagedTestWorker: false,
                audioCache: new CacheAccess(cache),
                bufferingRecoverySpoolPath: recovery.Path,
                bufferingRecoveryMemoryFrameCapacity: plan.TotalFrameCount,
                playbackSpanCacheEnabled: true,
                soundFontSetCacheIdentity: soundFontSetCacheIdentity);

            long deadline = Environment.TickCount64 + 2_000;
            AudioWorkerStatus status = session.Status;
            while (status.PositionFrame < 48_000
                && status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
                && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                status = session.Status;
            }

            Assert.Equal(AudioWorkerState.Playing, status.State);
            Assert.True(status.PositionFrame >= 48_000, $"Position={status.PositionFrame}; render={status.RenderPositionFrame}.");
            Assert.Equal(0, status.UnderrunCount);
            session.Stop(flush: true, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotRealtimeWorkerCoalescesRepeatedMonitoringAndResumesDeviceProgress()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT monitoring integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(
            48_000,
            480_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.ProgramChange(0, 0), 0),
                    new(0, MidiMessage.NoteOn(0, 60, 100), 0),
                    new(470_000, MidiMessage.NoteOff(0, 60, 0), 0)
                ])],
            sourceIds: [1]);
        using BassMidiAudioWorkerSession session = new(
            plan,
            soundFontPath,
            new BassMidiRendererSettings(500, 256),
            AudioMasterSettings.LimiterV2,
            renderAheadMilliseconds: 100,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            Path.GetFullPath(configured),
            nativeDirectory,
            preparingTimeout: TimeSpan.FromSeconds(30),
            allowManagedTestWorker: false,
            playbackSpanCacheEnabled: false);

        long initialProgressDeadline = Environment.TickCount64 + 2_000;
        AudioWorkerStatus initialStatus = session.Status;
        while (initialStatus.PositionFrame < 1_024
            && initialStatus.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
            && Environment.TickCount64 < initialProgressDeadline)
        {
            Thread.Sleep(5);
            initialStatus = session.Status;
        }
        Assert.True(
            initialStatus.PositionFrame >= 1_024,
            $"Playback did not begin before the monitoring stress; state={initialStatus.State}; "
            + $"position={initialStatus.PositionFrame}; render={initialStatus.RenderPositionFrame}.");
        long positionBeforeMonitoring = initialStatus.PositionFrame;
        for (int burst = 0; burst < 8; burst++)
        {
            for (int index = 0; index < 16; index++)
            {
                int ordinal = burst * 16 + index;
                session.EnqueueMonitoringCommands([
                    (ordinal & 1) == 0
                        ? MidiMonitoringCommand.DisableSource(0)
                        : MidiMonitoringCommand.EnableSource(0)
                ]);
                Thread.Sleep(1);
            }
            // Exceed the coalescing quiet period so this test exercises repeated
            // stop/reset/start cycles as well as commands within one burst.
            Thread.Sleep(60);
        }

        long deadline = Environment.TickCount64 + 5_000;
        AudioWorkerStatus status = session.Status;
        while (status.PositionFrame <= positionBeforeMonitoring
            && status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
            && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
            status = session.Status;
        }
        Assert.Contains(
            status.State,
            new[] { AudioWorkerState.Playing, AudioWorkerState.Buffering });
        Assert.Equal(0, status.FaultCode);
        Assert.True(
            status.PositionFrame > positionBeforeMonitoring,
            $"Playback did not resume after rapid monitoring changes; before={positionBeforeMonitoring}; "
            + $"after={status.PositionFrame}; render={status.RenderPositionFrame}; state={status.State}; "
            + $"exit={session.ExitCode}; stderr={session.StandardError}.");

        long firstResumedPosition = status.PositionFrame;
        Thread.Sleep(250);
        status = session.Status;
        Assert.Equal(AudioWorkerState.Playing, status.State);
        Assert.True(
            status.PositionFrame > firstResumedPosition,
            $"Playback resumed only transiently and then stalled; first={firstResumedPosition}; "
            + $"after={status.PositionFrame}; render={status.RenderPositionFrame}; "
            + $"exit={session.ExitCode}; stderr={session.StandardError}.");
        session.Stop(flush: true, TimeSpan.FromSeconds(5));
        AudioWorkerStatus stopped = session.Status;
        Assert.Equal(AudioWorkerState.Stopped, stopped.State);
        Assert.Equal(0, stopped.CallbackAllocatedBytes);
        Assert.Equal(0, stopped.RenderingAllocatedBytes);
    }

    [Fact]
    public void NativeAotRealtimeWorkerStopSupersedesQueuedMonitoringBatch()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT monitoring/Stop integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(
            48_000,
            480_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.ProgramChange(0, 0), 0),
                    new(0, MidiMessage.NoteOn(0, 60, 100), 0),
                    new(470_000, MidiMessage.NoteOff(0, 60, 0), 0)
                ])],
            sourceIds: [1]);
        using BassMidiAudioWorkerSession session = new(
            plan,
            soundFontPath,
            new BassMidiRendererSettings(500, 256),
            AudioMasterSettings.LimiterV2,
            renderAheadMilliseconds: 20,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            Path.GetFullPath(configured),
            nativeDirectory,
            preparingTimeout: TimeSpan.FromSeconds(30),
            allowManagedTestWorker: false,
            playbackSpanCacheEnabled: true);

        MidiMonitoringCommand[] commands = new MidiMonitoringCommand[128];
        for (int index = 0; index < commands.Length; index++)
        {
            commands[index] = (index & 1) == 0
                ? MidiMonitoringCommand.DisableSource(0)
                : MidiMonitoringCommand.EnableSource(0);
        }
        session.EnqueueMonitoringCommands(commands);

        session.Stop(flush: true, TimeSpan.FromSeconds(5));

        Assert.Equal(AudioWorkerState.Stopped, session.Status.State);
        Assert.Equal(0, session.Status.FaultCode);
    }

    [Fact]
    public void NativeAotDesktopPlaybackPipelineCompilesAndConsumesProjectNotes()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT desktop-pipeline integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-desktop-playback-{Guid.NewGuid():N}");
        try
        {
            MidoraProject project = CreateAudibleProject(soundFontPath);
            using ProjectCompilationSession compilation = new(project, soundFontPath);
            _ = compilation.ConfigureAudioCache(cacheRoot, 16 * 1024 * 1024);
            using BassWasapiChildPlaybackBackend backend = new(new(
                Path.GetFullPath(configured),
                nativeDirectory,
                DeviceId: null,
                RenderAheadMilliseconds: 100,
                DeviceBufferRequestMilliseconds: 50,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV2,
                TimeSpan.FromSeconds(30)));
            using PlaybackController controller = new(compilation, backend);

            controller.Start();
            long deadline = Environment.TickCount64 + 2_000;
            while (controller.CurrentTick < 240
                && controller.State is PlaybackState.Playing or PlaybackState.Buffering
                && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                controller.Update();
            }

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.True(controller.CurrentTick >= 240, $"Tick={controller.CurrentTick}; position={backend.PositionFrames}; render={backend.RenderPositionFrames}.");
            Assert.Equal(0, backend.UnderrunCount);
            controller.Stop();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotDesktopPlaybackCompletesTwiceWithPlaybackSpanCache()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT repeated-playback integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-repeated-playback-{Guid.NewGuid():N}");
        try
        {
            MidoraProject project = CreateAudibleProject(soundFontPath);
            using ProjectCompilationSession compilation = new(project, soundFontPath);
            _ = compilation.ConfigureAudioCache(cacheRoot, 16 * 1024 * 1024);
            using BassWasapiChildPlaybackBackend backend = new(new(
                Path.GetFullPath(configured),
                nativeDirectory,
                DeviceId: null,
                RenderAheadMilliseconds: 100,
                DeviceBufferRequestMilliseconds: 50,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV2,
                TimeSpan.FromSeconds(30)));
            using PlaybackController controller = new(compilation, backend);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                controller.Start();
                long deadline = Environment.TickCount64 + 5_000;
                while (controller.State is PlaybackState.Playing or PlaybackState.Buffering
                    && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2);
                    controller.Update();
                }

                Assert.True(
                    controller.State == PlaybackState.Stopped,
                    $"Attempt={attempt}; state={controller.State}; error={controller.LastError}; "
                    + $"workerStateFault={backend.FaultDescription}");
                Assert.Null(controller.LastError);
            }
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeAotFreshPureMidiEditPipelineKeepsAudibleAndMutedCachePublicationHealthy(
        bool trackAudible)
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT Pure MIDI cache integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string workerPath = Path.GetFullPath(configured);
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string soundFontSetCacheIdentity =
            NativeAudioIntegrationEnvironment.RequireSoundFontSetCacheIdentity(soundFontPath);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-fresh-pure-midi-cache-{Guid.NewGuid():N}");
        MidoraProject project = new(192);
        ProjectCompilationSession compilation = new(
            project,
            effectiveSoundFontPath: null,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        ProjectDocumentSession document = new(compilation);
        try
        {
            _ = compilation.ConfigureAudioCache(
                cacheRoot,
                AudioCachePreferences.DefaultMaximumReusableBytes);
            compilation.SetEffectiveSoundFontPath(soundFontPath);
            _ = document.Execute(
                ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot());
            PureMidiTrack track = Assert.Single(project.PureMidiTracks);
            _ = document.Execute(
                ProjectDomainEditCommands.CreateMidiSegment(
                    track.Id,
                    projectStartTick: 0,
                    lengthTicks: 192));
            MidiSegment segment = Assert.Single(track.Segments);
            _ = document.Execute(
                ProjectDomainEditCommands.CreateDirectMidiNote(
                    segment.Id,
                    startTick: 0,
                    lengthTicks: 96,
                    key: 60,
                    noteOnVelocity: 100));

            CanonicalCompiledResult compiled = await compilation
                .EnsureCurrentCompilationAsync();
            Assert.True(
                compiled.IsConsumable,
                string.Join(Environment.NewLine, compiled.Diagnostics));
            using PersistentBassMidiAudioWorkerHost host = new(
                workerPath,
                nativeDirectory,
                soundFontPath,
                TimeSpan.FromSeconds(30),
                allowManagedTestWorker: false);
            BassMidiAudioWorkerProbeResult probe = host.Probe(
                deviceId: null,
                deviceBufferRequestMilliseconds: 50);
            MidiRenderPlan plan = compilation.GetOrCreateRealtimeRenderPlan(
                compiled,
                probe.ActualSampleRate,
                trackAudible
                    ? new HashSet<MidoraId> { track.Id }
                    : new HashSet<MidoraId>());
            Assert.Contains(
                plan.Ports.ToArray().SelectMany(static port => port.Events.ToArray()),
                static value => value.Message.MessageType == MidiMessageType.NoteOn
                    && value.Message.Byte2 != 0);
            Assert.Single(plan.Segments.ToArray());

            using (PersistentBassMidiAudioWorkerSession playback = new(
                host,
                plan,
                soundFontSetCacheIdentity,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV2,
                renderAheadMilliseconds: 100,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                preparingTimeout: TimeSpan.FromSeconds(30),
                audioCache: compilation,
                bufferingRecoverySpoolPath: null,
                bufferingRecoveryMemoryFrameCapacity: 0,
                playbackSpanCacheEnabled: true))
            {
                long deadline = Environment.TickCount64 + 10_000;
                AudioWorkerStatus status = playback.Status;
                while (status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
                    && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2);
                    status = playback.Status;
                }
                Assert.Equal(AudioWorkerState.Completed, status.State);
                playback.Stop(flush: true, TimeSpan.FromSeconds(5));
            }

            AudioCacheSessionSnapshot snapshot = Assert.IsType<AudioCacheSessionSnapshot>(
                compilation.AudioCacheSnapshot);
            Assert.True(
                snapshot.RetentionState == AudioCacheRetentionState.Enabled,
                $"state={snapshot.RetentionState}; reusable={snapshot.ReusableBytes}; "
                    + $"maximum={snapshot.MaximumReusableBytes}; physical={snapshot.PhysicalReusableBytes}; "
                    + $"journalEntries={snapshot.JournalPublishedEntryCount}; "
                    + $"journalBytes={snapshot.JournalPublishedLiveBytes}; "
                    + $"pending={snapshot.PendingPublishCount}; backlog={snapshot.WriterBacklogBytes}; "
                    + $"planFrames={plan.TotalFrameCount}; "
                    + $"segmentBytes={plan.Segments[0].PcmPayloadByteCount}; "
                    + $"warning={snapshot.Warning.Message}");
            string nativeIdentity = AudioUnitCacheStaging.ComputeNativeIdentity(nativeDirectory);
            string segmentKey = MidiSegmentPcmCacheKey.Create(
                plan.Segments[0],
                plan.SampleRate,
                soundFontSetCacheIdentity,
                nativeIdentity,
                maximumSampleVoicesPerUnitStream: 500);
            if (trackAudible)
            {
                Assert.True(compilation.TryReadReusableAudio(segmentKey, out byte[] payload));
                Assert.Contains(
                    payload.AsSpan(AudioPcmCachePayload.HeaderByteCount).ToArray(),
                    static value => value != 0);
            }
            else
            {
                Assert.False(compilation.TryReadReusableAudio(segmentKey, out _));
            }
        }
        finally
        {
            compilation.Dispose();
            project.Dispose();
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static void VerifyHeldPreviewWorkerRoundTrip(
        string workerPath,
        bool allowManagedTestWorker)
    {
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan initial = new(48_000, 480_000, []);
        using BassMidiAudioWorkerSession session = new(
            initial,
            soundFontPath,
            new BassMidiRendererSettings(750, 256),
            AudioMasterSettings.LimiterV2,
            renderAheadMilliseconds: 20,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            workerPath,
            nativeDirectory,
            preparingTimeout: TimeSpan.FromSeconds(30),
            allowManagedTestWorker);

        long frontier = session.PauseHeldPreviewAtProducerFrontier(TimeSpan.FromSeconds(5));
        Assert.True(frontier > 0);
        Assert.Equal(AudioWorkerState.HeldPreviewPaused, session.Status.State);
        MidiRenderPlan replacement = new(48_000, Math.Max(576_000, frontier + 48_000), []);

        session.ReplaceHeldPreviewFutureAndResume(
            replacement,
            frontier,
            TimeSpan.FromSeconds(5));

        AudioWorkerStatus status = session.Status;
        Assert.Equal(1, status.HeldPreviewPlanGeneration);
        Assert.NotEqual(AudioWorkerState.HeldPreviewPaused, status.State);
        session.Stop(flush: true, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StartupFailureReleasesOwnedPlanDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        string soundFont = Path.Combine(directory, "project.sf2");
        File.WriteAllBytes(nativeWorker, [0]);
        File.WriteAllBytes(soundFont, [0]);
        HashSet<string> before = EnumerateOwnedPlanDirectories();
        try
        {
            _ = Assert.Throws<Win32Exception>(() => new BassMidiAudioWorkerSession(
                CreatePlan(),
                soundFont,
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV2,
                100,
                50,
                null,
                nativeWorker,
                directory,
                TimeSpan.FromSeconds(1)));

            Assert.Empty(EnumerateOwnedPlanDirectories().Except(before));
        }
        finally
        {
            foreach (string residual in EnumerateOwnedPlanDirectories().Except(before))
            {
                Directory.Delete(residual, recursive: true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingSoundFontFailsBeforeCreatingOwnedPlanDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        File.WriteAllBytes(nativeWorker, [0]);
        HashSet<string> before = EnumerateOwnedPlanDirectories();
        try
        {
            _ = Assert.Throws<FileNotFoundException>(() => new BassMidiAudioWorkerSession(
                CreatePlan(),
                Path.Combine(directory, "missing.sf2"),
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV2,
                100,
                50,
                null,
                nativeWorker,
                directory,
                TimeSpan.FromSeconds(1)));

            Assert.True(before.SetEquals(EnumerateOwnedPlanDirectories()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormalRealtimeClientAcceptsOnlyNativeExecutableWorker()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        string managedWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.dll");
        string otherWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.bin");
        File.WriteAllBytes(nativeWorker, [0]);
        File.WriteAllBytes(managedWorker, [0]);
        File.WriteAllBytes(otherWorker, [0]);
        try
        {
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                nativeWorker,
                allowManagedTestWorker: false);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    managedWorker,
                    allowManagedTestWorker: false));
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                managedWorker,
                allowManagedTestWorker: true);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    otherWorker,
                    allowManagedTestWorker: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(AudioWorkerState.Stopped, 0, true)]
    [InlineData(AudioWorkerState.Completed, 0, true)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 0, true)]
    [InlineData(AudioWorkerState.Playing, 0, false)]
    [InlineData(AudioWorkerState.Stopped, 1, false)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 1, false)]
    [InlineData(AudioWorkerState.Faulted, 1, false)]
    public void TerminalExitRequiresSuccessCodeAndTerminalState(
        AudioWorkerState state,
        int exitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            BassMidiAudioWorkerSession.IsSuccessfulTerminalExit(state, exitCode));
    }

    private static MidiRenderPlan CreatePlan() => new(48_000, 0, []);

    private static MidoraProject CreateAudibleProject(string soundFontPath)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return project;
    }

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();

        public bool TryCopyReusableAudio(
            string key,
            Stream destination,
            out long payloadLength) => store.TryCopyReusable(key, destination, out payloadLength);
        public ReusableAudioReadLease? AcquireReusableAudioReadLease(
            IReadOnlyList<string> keys) => store.AcquireReusableReadLease(keys);

        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => store.PublishReusable(key, source, payloadLength);

        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);

        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
            long lengthBytes) => store.CreateRecoverySpool(lengthBytes);

        public AudioCacheSessionStore.AudioRecoverySpool CreateSparseTransientAudioSpool(
            long lengthBytes) => store.CreateRecoverySpool(lengthBytes, sparse: true);

        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);
    }

    private static HashSet<string> EnumerateOwnedPlanDirectories() =>
        Directory.Exists(Midora.Common.MidoraProgramData.Current.AudioWorkerExchangeDirectory)
        ? Directory.EnumerateDirectories(
            Midora.Common.MidoraProgramData.Current.AudioWorkerExchangeDirectory,
            "midora-audio-worker-*")
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
