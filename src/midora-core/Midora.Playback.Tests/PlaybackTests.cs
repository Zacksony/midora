using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Playback.Tests;

public sealed class PlaybackTests
{
    [Fact]
    public void PresetWithoutSoundFontReleasesEditLockAndDoesNotMoveMainCursor()
    {
        using var project = CreateProject();
        using var session = new ProjectCompilationSession(project);
        using var controller = new PlaybackController(session, new FakeBackend());
        controller.Seek(240);
        Guid owner = Guid.NewGuid();
        Assert.ThrowsAny<Exception>(() => controller.StartInstrumentPresetPreview(owner, new(0, 0, 0), false));
        controller.StopInstrumentPresetPreview(owner);
        Assert.False(session.EditsLocked); Assert.Equal(240, controller.CurrentTick);
        Assert.NotEqual(PlaybackState.Playing, controller.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresetPreviewUsesExplicitOwnershipAndKeepsMainCursor(bool held)
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using var project = CreateProject();
            using var session = new ProjectCompilationSession(project, soundFont);
            var backend = new FakeBackend();
            using var controller = new PlaybackController(session, backend);
            Guid owner = Guid.NewGuid(); controller.Seek(240);
            controller.StartInstrumentPresetPreview(owner, new(0, 0, 7), held);
            Assert.Equal(PlaybackTaskKind.InstrumentPresetPreview, controller.ActiveTaskKind);
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(240, controller.CurrentTick); Assert.True(session.EditsLocked);
            controller.StopInstrumentPresetPreview(Guid.NewGuid());
            Assert.Equal(PlaybackState.Playing, controller.State);
            if (held)
            {
                controller.ReleaseInstrumentPresetPreviewKey(owner);
                Assert.False(controller.IsHeldPreviewGateOpen);
            }
            controller.StopInstrumentPresetPreview(owner);
            Assert.False(session.EditsLocked); Assert.Equal(240, controller.CurrentTick);
            controller.Start();
            controller.StopInstrumentPresetPreview(owner);
            Assert.Equal(PlaybackTaskKind.MainTimeline, controller.ActiveTaskKind);
        }
        finally { File.Delete(soundFont); }
    }

    [Fact]
    public void EffectiveSoundFontSetIdentityChangesWhenFileMetadataChangesAtTheSamePath()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            using ProjectCompilationSession session = new(project, soundFont);
            session.RefreshEffectiveSoundFontCacheIdentity();
            string firstIdentity = session.EffectiveSoundFontSetCacheIdentity!;
            int changedCount = 0;
            session.EffectiveSoundFontChanged += (_, _) => changedCount++;

            File.WriteAllBytes(soundFont, [1, 2, 3]);
            session.RefreshEffectiveSoundFontCacheIdentity();

            Assert.Equal(soundFont, session.EffectiveSoundFontPath);
            Assert.NotEqual(firstIdentity, session.EffectiveSoundFontSetCacheIdentity);
            Assert.Equal(1, changedCount);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ProjectOpenTimeCountsAllActiveSessionTimeAndPausesOnlyForSuspendOrClosing()
    {
        ManualTimeProvider clock = new();
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project, editingTimeProvider: clock);
        long fingerprint = session.LastAttempt.Fingerprint;

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(10_000, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemSuspending();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(10_000, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemResumed();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(10_250, session.SnapshotTotalEditingTimeMilliseconds());

        session.BeginProjectClosing();
        session.NotifySystemSuspending();
        session.CancelProjectClosing();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(10_250, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemResumed();
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(11_000, session.SnapshotTotalEditingTimeMilliseconds());
        Assert.Equal(
            fingerprint,
            session.Recompile(new ProjectChangeSet()).Fingerprint);
        session.Dispose();

        Assert.Equal(11_000, project.Metadata.TotalEditingTimeMilliseconds);
    }

    [Fact]
    public void ProjectRejectsConcurrentEditingTimeOwnersAndAllowsNextOpenAfterClose()
    {
        ManualTimeProvider clock = new();
        MidoraProject project = CreateProject();
        ProjectCompilationSession first = new(project, editingTimeProvider: clock);

        Assert.Throws<InvalidOperationException>(() =>
            new ProjectCompilationSession(project, editingTimeProvider: clock));

        clock.Advance(TimeSpan.FromMilliseconds(1_500));
        first.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        using ProjectCompilationSession reopened = new(project, editingTimeProvider: clock);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(3_500, reopened.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public void TempoMapUsesCumulativeTempoAndSingleFinalRounding()
    {
        TempoSampleMap map = new(480, [new(0, 120m), new(480, 60m)]);

        Assert.Equal(24_000, map.TickToSampleFrame(480, 0, 48_000));
        Assert.Equal(72_000, map.TickToSampleFrame(960, 0, 48_000));
        Assert.Equal(960, map.SampleFrameToTick(72_000, 0, 48_000, 2_000));
    }

    [Fact]
    public void TempoMapRoundsOnceAfterIntegratingFromNonZeroOriginAcrossTempoBoundary()
    {
        TempoSampleMap map = new(480, [new(0, 120m), new(480, 60m)]);

        // [240, 480) and [480, 600) each last 0.25 seconds. Rounding each
        // segment independently would produce 0 + 0 frames instead of 1.
        Assert.Equal(0, map.TickToSampleFrame(599, 240, 1));
        Assert.Equal(1, map.TickToSampleFrame(600, 240, 1));
    }

    [Fact]
    public void TempoMapInverseSearchSupportsInt64MaximumTick()
    {
        TempoSampleMap map = new(int.MaxValue, [new(0, 60_000_000m)]);
        long finalFrame = map.TickToSampleFrame(long.MaxValue, 0, 1);

        long tick = map.SampleFrameToTick(finalFrame, 0, 1, long.MaxValue);

        Assert.Equal(long.MaxValue, tick);
        Assert.Equal(finalFrame, map.TickToSampleFrame(tick, 0, 1));
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(192_000)]
    [InlineData(12_345)]
    public void AdapterSupportsAllRequiredSampleRateShapes(int sampleRate)
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest { EndTick = 960 });

        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(compiled, sampleRate);

        Assert.Equal(sampleRate, plan.SampleRate);
        Assert.Equal(sampleRate, plan.TotalFrameCount); // 960 ticks at 120 BPM = 1 second
        Assert.NotEmpty(plan.Ports.ToArray());
    }

    [Fact]
    public void RealtimeAdapterUsesPositiveDeviceRateOutsideFileRenderRange()
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(project);

        MidiRenderPlan realtime = MidiRenderPlanAdapter.CreateRealtime(
            compiled, 384_000, new HashSet<MidoraId> { project.Tracks[0].Id });

        Assert.Equal(384_000, realtime.SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiRenderPlanAdapter.Create(compiled, 384_000));
    }

    [Fact]
    public void RealtimeAdapterPreservesMutedTrackEventsAndMarksTheirSourceDisabled()
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(project);

        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
            compiled,
            48_000,
            new HashSet<MidoraId>());

        Assert.Equal([project.Tracks[0].Id.Value], plan.SourceIds.ToArray());
        Assert.Equal([0], plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(plan.Ports[0].Events.ToArray(), value => value.SourceIndex == 0);
        MidiUnitFragmentRenderPlan fragment = Assert.Single(plan.UnitFragments.ToArray());
        Assert.Equal(project.Tracks[0].Id.Value, fragment.TrackId);
        Assert.Equal(project.Tracks[0].Segments[0].Id.Value, fragment.SegmentId);
        Assert.Equal(0, fragment.SourceIndex);
        Assert.Equal(64, fragment.SemanticFingerprint.Length);
        Assert.Equal(plan.TotalFrameCount, fragment.EndFrame);
        Assert.Contains(fragment.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.NoteOff
            && value.SampleFrame < fragment.EndFrame);
        Assert.Equal(plan.TotalFrameCount, Assert.Single(plan.Segments.ToArray()).EndFrame);
        Assert.All(
            fragment.Events.ToArray(),
            value => Assert.Equal(0, value.Message.ChannelNumber));
    }

    [Fact]
    public void EditRecompilesIncrementallyAndInvalidatesSamplePlanCache()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        MidiRenderPlan first = session.GetOrCreateRenderPlan(48_000);
        LogicalTrack track = project.Tracks[0];
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        CanonicalCompiledResult result = session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Note = 67,
            changes);
        MidiRenderPlan second = session.GetOrCreateRenderPlan(48_000);

        Assert.True(result.IsConsumable);
        Assert.NotSame(first, second);
        Assert.Equal(1, session.LastCompilationTelemetry.RecompiledTrackCount);
    }

    [Fact]
    public void EditingOneSegmentPreservesEveryOtherSegmentPcmKey()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        MidoraId changedSegmentId = track.Segments[0].Id;
        Segment unchanged = new(project)
        {
            // A gap creates a distinct Usage lifecycle/cache fragment. Adjacent
            // Segments intentionally share one Usage fragment in the flat model.
            ProjectStartTick = 1_200,
            LengthTicks = 960
        };
        unchanged.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 480,
            Note = 67,
            Velocity = 90
        });
        track.Segments.Add(unchanged);
        using ProjectCompilationSession session = new(project);
        MidiRenderPlan before = session.GetOrCreateRenderPlan(48_000);
        MidiSegmentRenderPlan unchangedBefore = before.Segments.ToArray()
            .Single(value => value.SegmentId == unchanged.Id.Value);
        string keyBefore = MidiSegmentPcmCacheKey.Create(
            unchangedBefore,
            before.SampleRate,
            new string('a', 64),
            "native-baseline",
            500);

        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);
        session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Note = 62,
            changes);
        MidiRenderPlan after = session.GetOrCreateRenderPlan(48_000);
        MidiSegmentRenderPlan unchangedAfter = after.Segments.ToArray()
            .Single(value => value.SegmentId == unchanged.Id.Value);
        string keyAfter = MidiSegmentPcmCacheKey.Create(
            unchangedAfter,
            after.SampleRate,
            new string('a', 64),
            "native-baseline",
            500);

        Assert.Equal(unchangedBefore.SemanticFingerprint, unchangedAfter.SemanticFingerprint);
        Assert.Equal(keyBefore, keyAfter);
        Assert.NotEqual(
            before.Segments.ToArray().Single(
                value => value.SegmentId == changedSegmentId.Value).SemanticFingerprint,
            after.Segments.ToArray().Single(
                value => value.SegmentId == changedSegmentId.Value).SemanticFingerprint);
    }

    [Fact]
    public void OfflineTrackProjectionExcludesOtherTrackUnitFragments()
    {
        (MidoraProject project, LogicalTrack secondTrack) = CreateMonitoringRoutingProject();
        LogicalTrack firstTrack = project.Tracks[0];
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(
            compiled,
            48_000,
            new HashSet<MidoraId> { firstTrack.Id });

        Assert.NotEmpty(plan.UnitFragments.ToArray());
        Assert.All(
            plan.UnitFragments.ToArray(),
            value => Assert.Equal(firstTrack.Id.Value, value.TrackId));
        Assert.DoesNotContain(
            plan.UnitFragments.ToArray(),
            value => value.TrackId == secondTrack.Id.Value);
    }

    [Fact]
    public void ExactPlaybackRangeReplayReusesCanonicalResultUntilSourceChanges()
    {
        MidoraProject project = CreateProject();
        using ProjectCompilationSession session = new(project);

        CanonicalCompiledResult first = session.CompileForPlayback(0, 960);
        CanonicalCompiledResult second = session.CompileForPlayback(0, 960);

        Assert.Same(first, second);
        Assert.Equal(1, session.PlaybackRangeCompilationCount);
        Assert.Equal(1, session.PlaybackRangeCacheHitCount);

        ProjectChangeSet changes = new();
        changes.TrackIds.Add(project.Tracks[0].Id);
        session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Velocity = 99,
            changes);
        CanonicalCompiledResult afterEdit = session.CompileForPlayback(0, 960);

        Assert.NotSame(first, afterEdit);
        Assert.Equal(2, session.PlaybackRangeCompilationCount);
        Assert.Equal(1, session.PlaybackRangeCacheHitCount);
    }

    [Fact]
    public void SampleDomainInvalidationDoesNotDiscardTickDomainPlaybackRangeCache()
    {
        using ProjectCompilationSession session = new(CreateProject());
        CanonicalCompiledResult first = session.CompileForPlayback(0, 960);

        session.InvalidateSampleDomainCaches();
        CanonicalCompiledResult second = session.CompileForPlayback(0, 960);

        Assert.Same(first, second);
        Assert.Equal(1, session.PlaybackRangeCompilationCount);
        Assert.Equal(1, session.PlaybackRangeCacheHitCount);
    }

    [Fact]
    public void DefaultWholeProjectPlaybackReusesCurrentFullCanonicalResult()
    {
        using ProjectCompilationSession session = new(CreateProject());
        long fullFingerprint = session.LastAttempt.Fingerprint;

        CanonicalCompiledResult first = session.CompileForPlayback(0, null);
        CanonicalCompiledResult second = session.CompileForPlayback(0, null);

        Assert.Same(first, second);
        Assert.Equal(CompilationPurpose.Playback, first.Purpose);
        Assert.Equal(fullFingerprint, first.Fingerprint);
        Assert.Equal(session.LastAttempt.Events.ToArray(), first.Events.ToArray());
        Assert.Equal(0, session.PlaybackRangeCompilationCount);
        Assert.Equal(2, session.PlaybackRangeCacheHitCount);
    }

    [Fact]
    public void RepeatedRealtimePlaybackReusesSamplePlanAndOnlyClonesMonitoringState()
    {
        MidoraProject project = CreateProject();
        using ProjectCompilationSession session = new(project);
        CanonicalCompiledResult compiled = session.CompileForPlayback(0, 960);
        HashSet<MidoraId> audible = project.Tracks.Select(value => value.Id).ToHashSet();

        MidiRenderPlan first = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            audible);
        MidiRenderPlan second = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            audible);
        MidiRenderPlan muted = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId>());

        Assert.Same(first, second);
        Assert.NotSame(first, muted);
        Assert.Same(first.Ports[0], muted.Ports[0]);
        Assert.Equal([0], muted.InitiallyDisabledSourceIndices.ToArray());

        session.InvalidateSampleDomainCaches();
        MidiRenderPlan afterInvalidation = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            audible);
        Assert.NotSame(first, afterInvalidation);
    }

    [Fact]
    public void DefaultPlaybackPreparationWarmsCanonicalAndSampleDomainCachesBeforeStart()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            controller.BeginDefaultPlaybackPreparation();

            Assert.True(SpinWait.SpinUntil(
                () => session.PlaybackRangeCacheHitCount >= 1,
                TimeSpan.FromSeconds(5)));
            controller.Start();

            Assert.Equal(2, backend.PrepareCount);
            Assert.Equal(0, session.PlaybackRangeCompilationCount);
            Assert.NotNull(backend.LastStartedPlan);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void FirstPlaybackIncludesLogicalTrackCreatedAfterPlaybackControllerConstruction()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = new(480);
            using ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            MidoraId trackId = default;

            session.ApplyEdit(
                editedProject =>
                {
                    EventInstrument instrument = new(editedProject)
                    {
                        Name = "New Instrument",
                        TemplateLengthTicks = 480,
                        OverlapPolicy = OverlapPolicy.Warn
                    };
                    SubVoice voice = new(editedProject);
                    voice.Events.Add(TemplateEvent.Note(
                        editedProject,
                        0,
                        480,
                        60,
                        100));
                    instrument.SubVoices.Add(voice);
                    editedProject.EventInstruments.Add(instrument);
                    LogicalTrack track = new(editedProject) { Name = "New Track" };
                    ProjectGraphConstruction.AddIndependentLogicalTrack(
                        editedProject,
                        track,
                        instrument.Id);
                    Segment segment = new(editedProject) { LengthTicks = 960 };
                    segment.Notes.Add(new LogicalNote(editedProject)
                    {
                        StartTick = 0,
                        LengthTicks = 480,
                        Note = 60,
                        Velocity = 100
                    });
                    track.Segments.Add(segment);
                    trackId = track.Id;
                },
                ProjectChangeSet.Everything);

            controller.Start();

            MidiRenderPlan plan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            int sourceIndex = Array.IndexOf(plan.SourceIds.ToArray(), trackId.Value);
            Assert.True(sourceIndex >= 0);
            Assert.DoesNotContain(sourceIndex, plan.InitiallyDisabledSourceIndices.ToArray());
            bool containsNoteOn = false;
            foreach (MidiPortRenderPlan port in plan.Ports)
            {
                foreach (ScheduledMidiMessage midiEvent in port.Events)
                {
                    if (midiEvent.SourceIndex == sourceIndex
                        && midiEvent.Message.MessageType == MidiMessageType.NoteOn)
                    {
                        containsNoteOn = true;
                        break;
                    }
                }

                if (containsNoteOn)
                    break;
            }

            Assert.True(containsNoteOn);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void AudioBackendWarmUpPreparesWithoutStartingPlayback()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            int sampleRate = controller.WarmUpAudioBackend();

            Assert.Equal(backend.ActualSampleRate, sampleRate);
            Assert.Equal(1, backend.PrepareCount);
            Assert.Equal(0, backend.StartCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void StartStopSeekAreColdStartsAndLockEdits()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            controller.Start();
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.True(session.EditsLocked);
            Assert.Throws<InvalidOperationException>(() =>
                session.SetEffectiveSoundFontPath(null));
            Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(_ => { }, new ProjectChangeSet()));

            controller.Seek(240);
            Assert.Equal(2, backend.StartCount);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackState.Playing, controller.State);

            controller.Stop();
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void InvalidCurrentSourceCannotReusePreviousSuccessfulAudioPlan()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        _ = session.GetOrCreateRenderPlan(48_000);
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(project.Tracks[0].Id);

        CanonicalCompiledResult invalid = session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Velocity = 0,
            changes);

        Assert.False(invalid.IsConsumable);
        Assert.Throws<InvalidOperationException>(() => session.GetOrCreateRenderPlan(48_000));
    }

    [Fact]
    public void StopCursorBehaviorUsesOriginalTaskStartEvenAfterSeek()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Start(0);
            controller.Seek(240);
            backend.PositionFrames = 12_000;
            Assert.Equal(480, controller.CurrentTick);
            controller.Stop();

            Assert.Equal(0, controller.CurrentTick);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ConfiguredApplicationPlaybackPreferencesDriveBackendAndStopCursor()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.ConfigurePlaybackPreferences(
                new PlaybackMasterConfiguration(-6f, false),
                StopCursorBehavior.StayAtStoppedTick);

            controller.Start(0);
            backend.PositionFrames = 12_000;
            Assert.Equal(240, controller.CurrentTick);
            controller.Stop();

            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(
                new PlaybackMasterConfiguration(-6f, false),
                backend.LastMasterConfiguration);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void NaturalCompletionLoopsThroughColdRangeStart()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.SetLoop(new TickRange(240, 480));
            controller.Start();

            backend.IsCompleted = true;
            controller.Update();

            Assert.Equal(2, backend.StartCount);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void DisablingLoopColdRestartsFromCurrentTickToOriginalRangeEnd()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.SetLoop(new TickRange(240, 480));
            controller.Start();
            backend.PositionFrames = 15_000; // tick 300 at 120 BPM / 48 kHz

            controller.SetLoop(null);

            Assert.Null(controller.LoopRange);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(2, backend.StartCount);
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(300, controller.CurrentTick);

            backend.IsCompleted = true;
            controller.Update();
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(2, backend.StopCount);
            Assert.Equal(2, backend.StartCount);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SettingIdenticalLoopRangeDuringPlaybackIsANoOp()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            TickRange loop = new(240, 480);
            controller.SetLoop(loop);
            controller.Start();

            controller.SetLoop(loop);

            Assert.Equal(0, backend.StopCount);
            Assert.Equal(1, backend.StartCount);
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void DisablingLoopAfterOriginalRequestedEndCompletesPlaybackWithoutReversedRestart()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.SetLoop(new TickRange(240, 480));
            controller.Start(0, 240);
            backend.PositionFrames = 15_000; // tick 300 at 120 BPM / 48 kHz

            controller.SetLoop(null);

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(1, backend.StartCount);
            Assert.Equal(1, backend.StopCount);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MuteSoloAreRuntimeOnlyAndResetPlaybackEngineClearsBackend()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            MidoraId trackId = project.Tracks[0].Id;
            long fingerprint = session.LastAttempt.Fingerprint;

            controller.Start();
            controller.SetTrackMuted(trackId, true);
            controller.SetTrackSolo(trackId, true);
            controller.SetTrackMuted(trackId, false);
            controller.ResetPlaybackEngine();

            Assert.Equal(fingerprint, session.LastAttempt.Fingerprint);
            Assert.Equal(1, backend.StartCount);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && !value.SourceEnabled);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && value.SourceEnabled);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void FailedMonitoringCommandRollsBackRuntimeFilterState()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new() { ThrowMonitoringCommands = true };
            using PlaybackController controller = new(new ProjectCompilationSession(project, soundFont), backend);
            MidoraId trackId = project.Tracks[0].Id;
            controller.Start();

            Assert.Throws<InvalidOperationException>(() => controller.SetTrackMuted(trackId, true));
            backend.ThrowMonitoringCommands = false;
            controller.SetTrackMuted(trackId, true);

            Assert.Equal(2, backend.MonitoringApplyCount);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && !value.SourceEnabled);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MonitoringRestoreUsesActivePlanRoutingInsteadOfColdRangeReallocation()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            (MidoraProject project, LogicalTrack restoredTrack) = CreateMonitoringRoutingProject();
            ProjectCompilationSession session = new(project, soundFont);
            ChannelUnitAllocation activeAllocation = Assert.Single(
                session.LastAttempt.Allocations.ToArray(),
                value => value.TrackId == restoredTrack.Id);
            Assert.Equal((byte)1, activeAllocation.ZeroBasedChannel);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 15_000; // tick 300 at 120 BPM / 48 kHz

            controller.SetTrackMuted(restoredTrack.Id, true);
            int commandCountBeforeRestore = backend.MonitoringCommands.Count;
            controller.SetTrackMuted(restoredTrack.Id, false);

            MidiMonitoringCommand[] restoreCommands = backend.MonitoringCommands
                .Skip(commandCountBeforeRestore)
                .ToArray();
            Assert.Contains(restoreCommands, value =>
                value.Kind == MidiMonitoringCommandKind.SetSourceEnabled
                && value.SourceEnabled);
            MidiMonitoringCommand programRestore = Assert.Single(restoreCommands, value =>
                value.Kind == MidiMonitoringCommandKind.SendMessage
                && value.Message.MessageType == MidiMessageType.ProgramChange);
            Assert.Equal(activeAllocation.ZeroBasedPort, programRestore.ZeroBasedPortNumber);
            Assert.Equal(activeAllocation.ZeroBasedChannel, programRestore.Message.ChannelNumber);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MonitoringRestoreUsesConsumedPositionInsteadOfSpeculativeRenderAheadPosition()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            (MidoraProject project, LogicalTrack restoredTrack) = CreateMonitoringRoutingProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.Start();
            backend.PositionFrames = 15_000; // tick 300 at 120 BPM / 48 kHz
            backend.ExplicitRenderPositionFrames = 45_000; // tick 900 is speculative render-ahead

            controller.SetTrackMuted(restoredTrack.Id, true);
            int commandCountBeforeRestore = backend.MonitoringCommands.Count;
            controller.SetTrackMuted(restoredTrack.Id, false);

            MidiMonitoringCommand programRestore = Assert.Single(
                backend.MonitoringCommands.Skip(commandCountBeforeRestore),
                value => value.Kind == MidiMonitoringCommandKind.SendMessage
                    && value.Message.MessageType == MidiMessageType.ProgramChange);
            Assert.Equal(42, programRestore.Message.Byte1);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetMonitoringStatesClearsTrackAndSharedGroupFiltersAtomically()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            (MidoraProject project, LogicalTrack secondTrack) = CreateMonitoringRoutingProject();
            LogicalTrack firstTrack = project.Tracks.Single(track => track.Id != secondTrack.Id);
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            long fingerprint = session.LastAttempt.Fingerprint;

            controller.SetTrackMuted(secondTrack.Id, true);
            controller.SetTrackSolo(firstTrack.Id, true);
            controller.SetSharedGroupMuted(secondTrack.EventInstrumentUsageId!.Value, true);
            controller.ResetMonitoringStates();
            controller.Start();

            MidiRenderPlan plan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            Assert.Empty(plan.InitiallyDisabledSourceIndices.ToArray());
            Assert.Equal(fingerprint, session.LastAttempt.Fingerprint);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Theory]
    [InlineData(MidiChannelRootRoutingMode.Auto)]
    [InlineData(MidiChannelRootRoutingMode.Fixed)]
    public void FirstPlaybackIncludesPureMidiTrackCreatedAfterControllerConstruction(
        MidiChannelRootRoutingMode routingMode)
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = new(480);
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            MidoraId trackId = default;

            _ = session.ApplyEdit(value =>
            {
                MidiChannelRoot root = new(value)
                {
                    Name = "Root",
                    RoutingMode = routingMode,
                    FixedZeroBasedPort = 2,
                    FixedZeroBasedChannel = 3,
                    ChannelMode = MidiChannelMode.Melodic
                };
                PureMidiTrack track = new(value)
                {
                    Name = "MIDI Track",
                    MidiChannelRootId = root.Id
                };
                MidiSegment segment = new(value) { LengthTicks = 480 };
                segment.Notes.Add(new DirectMidiNote(value)
                {
                    StartTick = 0,
                    LengthTicks = 240,
                    Key = 60,
                    NoteOnVelocity = 100,
                    NoteOffVelocity = 0
                });
                track.Segments.Add(segment);
                value.MidiChannelRoots.Add(root);
                value.PureMidiTracks.Add(track);
                value.ArrangementTracks.Add(new(
                    ArrangementTrackKind.PureMidiTrack,
                    track.Id));
                trackId = track.Id;
            }, ProjectChangeSet.Everything);

            controller.Start();

            MidiRenderPlan plan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            int sourceIndex = plan.FindSourceIndex(trackId.Value);
            Assert.True(sourceIndex >= 0);
            Assert.DoesNotContain(
                sourceIndex,
                plan.InitiallyDisabledSourceIndices.ToArray());
            Assert.Contains(
                plan.Ports.ToArray().SelectMany(static value => value.Events.ToArray()),
                value => value.SourceIndex == sourceIndex
                    && value.Message.MessageType == MidiMessageType.NoteOn
                    && value.Message.Byte2 != 0);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SharedGroupSoloTakesPriorityWhileGroupAndTrackMuteRemainIndependent()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            (MidoraProject project, LogicalTrack secondTrack) = CreateMonitoringRoutingProject();
            LogicalTrack firstTrack = project.Tracks.Single(track => track.Id != secondTrack.Id);
            MidoraId secondGroupId = secondTrack.EventInstrumentUsageId!.Value;
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.SetTrackSolo(firstTrack.Id, true);
            controller.SetSharedGroupSolo(secondGroupId, true);
            controller.SetTrackMuted(secondTrack.Id, true);
            controller.Start();

            MidiRenderPlan firstPlan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            Assert.Contains(
                firstPlan.FindSourceIndex(firstTrack.Id.Value),
                firstPlan.InitiallyDisabledSourceIndices.ToArray());
            Assert.Contains(
                firstPlan.FindSourceIndex(secondTrack.Id.Value),
                firstPlan.InitiallyDisabledSourceIndices.ToArray());
            controller.Stop();

            controller.SetTrackMuted(secondTrack.Id, false);
            controller.Start();
            MidiRenderPlan secondPlan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            Assert.Contains(
                secondPlan.FindSourceIndex(firstTrack.Id.Value),
                secondPlan.InitiallyDisabledSourceIndices.ToArray());
            Assert.DoesNotContain(
                secondPlan.FindSourceIndex(secondTrack.Id.Value),
                secondPlan.InitiallyDisabledSourceIndices.ToArray());
            controller.Stop();

            controller.SetSharedGroupSolo(secondGroupId, false);
            controller.SetSharedGroupMuted(secondGroupId, true);
            controller.SetTrackSolo(firstTrack.Id, false);
            controller.SetTrackSolo(secondTrack.Id, true);
            controller.Start();
            MidiRenderPlan thirdPlan = Assert.IsType<MidiRenderPlan>(backend.LastStartedPlan);
            Assert.Contains(
                thirdPlan.FindSourceIndex(firstTrack.Id.Value),
                thirdPlan.InitiallyDisabledSourceIndices.ToArray());
            Assert.Contains(
                thirdPlan.FindSourceIndex(secondTrack.Id.Value),
                thirdPlan.InitiallyDisabledSourceIndices.ToArray());
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void StartWithoutExplicitTickUsesStoppedCursor()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Seek(240);
            controller.Start();

            Assert.Equal(240, controller.CurrentTick);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ZeroLengthPlaybackPreparesAndImmediatelyReturnsToStopped()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Start(240, 240);

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(1, backend.PrepareCount);
            Assert.Equal(0, backend.StartCount);

            controller.Start(0, 240);
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void EventInstrumentPreviewSharesBackendExclusivelyAndPreservesMainCursor()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            controller.Seek(240);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackTaskKind.EventInstrumentPreview, controller.ActiveTaskKind);
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(0, controller.CurrentTaskTick);
            Assert.True(session.EditsLocked);
            Assert.Throws<InvalidOperationException>(() => controller.StartSegmentPreview(
                project.Tracks[0].Id, project.Tracks[0].Segments[0].Id));
            Assert.Equal(1, backend.PrepareCount);

            controller.Stop();

            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(240, controller.CurrentTick);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PreparingStateIsPublishedOnlyAfterProjectEditsAreLocked()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            List<bool> preparingLockStates = [];
            controller.StateChanged += (_, _) =>
            {
                if (controller.State == PlaybackState.Preparing)
                {
                    preparingLockStates.Add(session.EditsLocked);
                }
            };

            controller.Start();
            controller.Stop();
            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal([true, true], preparingLockStates);
            Assert.True(session.EditsLocked);
            controller.Stop();
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PreviewCompilationFailureClearsTaskAndProjectEditLock()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            Assert.Throws<ArgumentException>(() => controller.StartEventInstrumentPreview(
                new EventInstrumentPreviewRequest(new MidoraId(long.MaxValue))));

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.Equal(0, backend.PrepareCount);
            Assert.NotNull(controller.LastError);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void UnboundSegmentPreviewFailsBeforeBackendPreparation()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            project.Tracks[0].EventInstrumentUsageId = null;
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            CompilationRejectedException failure = Assert.Throws<CompilationRejectedException>(() =>
                controller.StartSegmentPreview(
                    project.Tracks[0].Id,
                    project.Tracks[0].Segments[0].Id));

            Assert.Contains("MIDORA1306", failure.Message, StringComparison.Ordinal);
            Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1306");
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.Equal(0, backend.PrepareCount);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void RealtimePreviewUsesPositiveActualDeviceRateOutsideFileRange()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new() { ActualSampleRate = 384_000 };
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SegmentPreviewNaturalCompletionDoesNotMoveMainCursor()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.Seek(300);
            controller.StartSegmentPreview(project.Tracks[0].Id, project.Tracks[0].Segments[0].Id);
            backend.IsCompleted = true;

            controller.Update();

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(300, controller.CurrentTick);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void BackendFaultStopsTaskUnlocksEditsAndPlayCanRecoverDirectly()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            controller.Start();
            MidiRenderPlan cachedBeforeFault = session.GetOrCreateRenderPlan(48_000);
            backend.PositionFrames = 12_000;
            backend.IsFaulted = true;
            backend.FaultDescription = "synthetic backend fault";

            controller.Update();

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.Contains("synthetic", controller.LastError?.Message);
            Assert.Equal(240, controller.CurrentTick);
            Assert.NotSame(cachedBeforeFault, session.GetOrCreateRenderPlan(48_000));

            backend.IsFaulted = false;
            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ActiveOutputDeviceLossStopsWithoutThrowAndRequiresExplicitSelection()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            controller.Start();
            MidiRenderPlan cachedBeforeLoss = session.GetOrCreateRenderPlan(48_000);
            backend.PositionFrames = 12_000;
            backend.OutputDeviceSelectionRequired = true;
            backend.OutputDeviceSelectionReason = "active device removed";

            controller.Update();

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.True(controller.OutputDeviceSelectionRequired);
            Assert.False(session.EditsLocked);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(1, backend.StopCount);
            Assert.False(backend.LastStopFlush);
            Assert.IsType<OutputDeviceSelectionRequiredException>(controller.LastError);
            Assert.Contains("active device removed", controller.LastError?.Message);
            Assert.NotSame(cachedBeforeLoss, session.GetOrCreateRenderPlan(48_000));

            Assert.Throws<OutputDeviceSelectionRequiredException>(() => controller.Start());
            Assert.Equal(1, backend.StartCount);

            controller.SelectOutputDevice("replacement-device");
            Assert.False(controller.OutputDeviceSelectionRequired);
            Assert.Equal("replacement-device", backend.SelectedOutputDeviceId);
            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(2, backend.StartCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PlaybackKindsSelectOnlyTheirApprovedReusableCacheLayers()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Start();
            controller.Stop();
            controller.StartSegmentPreview(
                project.Tracks[0].Id,
                project.Tracks[0].Segments[0].Id);
            controller.Stop();
            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(
                [
                    RealtimePlaybackCacheMode.UnitPcmAndPlaybackSpan,
                    RealtimePlaybackCacheMode.UnitPcm,
                    RealtimePlaybackCacheMode.Disabled
                ],
                backend.CacheModes);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void HeldPreviewGateEndFreezesConsumedLengthButChangesAudioAtProducerFrontier()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                Tempo: 120m));
            Assert.True(controller.IsHeldPreviewGateOpen);
            Assert.DoesNotContain(backend.LastStartedPlan!.Ports[0].Events.ToArray(), value =>
                value.SampleFrame == backend.LastStartedPlan.TotalFrameCount);

            backend.PositionFrames = 12_000;
            backend.ExplicitRenderPositionFrames = 16_800;
            HeldPreviewGateEndReport report = controller.EndHeldPreviewGate();

            Assert.False(controller.IsHeldPreviewGateOpen);
            Assert.Equal(240, report.FinalGateLengthTicks);
            Assert.Equal(12_000, report.ConsumedFrameAtGateEnd);
            Assert.Equal(16_800, report.ProducerFrontierFrame);
            Assert.Equal(4_800, report.QueuedLatencyFrameCount);
            Assert.Equal(100, report.QueuedLatencyMilliseconds, 6);
            Assert.Equal(1, backend.HeldPauseCount);
            Assert.Equal(1, backend.HeldReplaceCount);
            Assert.Contains(backend.LastStartedPlan!.Ports[0].Events.ToArray(), value =>
                value.SampleFrame == 16_800
                && value.Message.MessageType == MidiMessageType.NoteOff);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void HeldPreviewBackendFaultClearsTheCausalGateWithTheActiveTask()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                Tempo: 120m));
            Assert.True(controller.IsHeldPreviewGateOpen);

            backend.IsFaulted = true;
            backend.FaultDescription = "synthetic held-preview backend fault";
            controller.Update();

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(controller.IsHeldPreviewGateOpen);
            Assert.False(session.EditsLocked);
            controller.CancelHeldPreview();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void HeldPreviewUpdateRenewsTheFiniteCausalWindowBeforeProducerCompletion()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Tempo: 120m));
            long initialTotalFrames = backend.LastStartedPlan!.TotalFrameCount;
            backend.ExplicitRenderPositionFrames = initialTotalFrames - (3 * backend.ActualSampleRate);

            controller.Update();

            Assert.Equal(1, backend.HeldPauseCount);
            Assert.Equal(1, backend.HeldReplaceCount);
            Assert.True(backend.LastStartedPlan!.TotalFrameCount > initialTotalFrames);
            Assert.True(controller.IsHeldPreviewGateOpen);
            controller.CancelHeldPreview();
            Assert.False(controller.IsHeldPreviewGateOpen);
            Assert.Equal(PlaybackState.Stopped, controller.State);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SegmentPitchRulerPreviewUsesTheTrackBindingAndHasNoProjectEditSideEffect()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            LogicalTrack track = project.Tracks[0];
            Segment segment = track.Segments[0];
            int noteCount = segment.Notes.Count;
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.StartHeldSegmentPitchRulerPreview(
                track.Id,
                segment.Id,
                pitch: 71,
                velocity: 112,
                previewTempo: 90m);

            Assert.Contains(backend.LastStartedPlan!.Ports[0].Events.ToArray(), value =>
                value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 71);
            Assert.Equal(noteCount, segment.Notes.Count);
            controller.CancelHeldPreview();

            track.EventInstrumentUsageId = null;
            Assert.Throws<InvalidOperationException>(() =>
                controller.StartHeldSegmentPitchRulerPreview(
                    track.Id,
                    segment.Id,
                    60,
                    100,
                    120m));
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void OutputDeviceSelectionRejectsEmptyIdAndActivePlayback()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(
                new ProjectCompilationSession(project, soundFont),
                backend);

            Assert.Throws<ArgumentException>(() => controller.SelectOutputDevice(string.Empty));
            controller.Start();
            Assert.Throws<InvalidOperationException>(() =>
                controller.SelectOutputDevice("replacement-device"));
            controller.Stop();

            controller.SelectOutputDevice(null);
            Assert.Null(backend.SelectedOutputDeviceId);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MissingSoundFontStartFailureClearsTaskAndEditLockForRecovery()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        FakeBackend backend = new();
        using PlaybackController controller = new(session, backend);

        Assert.Throws<InvalidOperationException>(() => controller.Start());

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
        Assert.False(session.EditsLocked);
        Assert.Null(backend.LastStartedPlan);

        string soundFont = CreateTemporarySoundFont();
        try
        {
            session.SetEffectiveSoundFontPath(soundFont);
            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(0, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PrepareFailureClearsTaskAndEditLockForDirectRecovery()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new() { ThrowPrepare = true };
            using PlaybackController controller = new(session, backend);

            Assert.Throws<InvalidOperationException>(() => controller.Start());

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Exception originalFailure = controller.LastError!;

            controller.Seek(240);

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Same(originalFailure, controller.LastError);
            backend.ThrowPrepare = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void StopFailureClearsActiveStateAndUnlocksBeforeRecovery()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;

            Assert.Throws<InvalidOperationException>(() => controller.Stop());

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.NotNull(controller.LastError);
            backend.ThrowStop = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetContinuesAfterStopFailureAndRecoversToStopped()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;

            controller.ResetPlaybackEngine();

            Assert.Equal(1, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Null(controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetFromStoppedClearsBackendWithoutStoppingOrChangingProjectCompilation()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            long fingerprint = session.LastAttempt.Fingerprint;

            controller.ResetPlaybackEngine();

            Assert.Equal(0, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(fingerprint, session.LastAttempt.Fingerprint);
            Assert.Null(controller.LastError);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetFailurePreservesErrorAndAggregatesStopFailure()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;
            backend.ThrowReset = true;

            AggregateException failure = Assert.Throws<AggregateException>(controller.ResetPlaybackEngine);

            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Same(failure, controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void DirectPlayRecoveryKeepsErrorWhenBackendResetFails()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new() { ThrowPrepare = true };
            using PlaybackController controller = new(session, backend);
            Assert.Throws<InvalidOperationException>(() => controller.Start());
            backend.ThrowPrepare = false;
            backend.ThrowReset = true;

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => controller.Start());

            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Same(failure, controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SeekRestartStopFailureClearsTaskAndPreservesCurrentCursor()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 12_000;
            Assert.Equal(240, controller.CurrentTick);
            backend.ThrowStop = true;

            Assert.Throws<InvalidOperationException>(() => controller.Seek(480));

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(240, controller.CurrentTick);
            Assert.False(session.EditsLocked);
            backend.ThrowStop = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void InvalidEffectiveLoopRangeIsRejectedBeforeMutatingPlaybackState()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.Seek(600);
            controller.SetLoop(new TickRange(240, 480));

            Assert.Throws<ArgumentOutOfRangeException>(() => controller.Start());

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(0, backend.PrepareCount);

            controller.Seek(0);
            controller.Start();
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(600));
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(0, backend.StopCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void LatchedUnderrunRequestsOneCompleteRecoveryIntervalAndKeepsCursorFrozen()
    {
        string soundFont = CreateTemporarySoundFont();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-playback-recovery-{Guid.NewGuid():N}");
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            _ = session.ConfigureAudioCache(cacheRoot, 16 * 1024 * 1024);
            using RecoveryBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 4_800;
            backend.IsBuffering = true;
            long frozenTick = controller.CurrentTick;

            controller.Update();
            controller.Update();

            Assert.Equal(PlaybackState.Buffering, controller.State);
            Assert.Equal(1, backend.BeginRecoveryCount);
            Assert.True(backend.RecoveryEndFrame > backend.PositionFrames);
            Assert.Equal(frozenTick, controller.CurrentTick);
            Assert.True(session.AudioCacheSnapshot!.Value.TransientBytes > 0);

            backend.RenderPositionFrames = backend.PositionFrames
                + ((backend.RecoveryEndFrame - backend.PositionFrames) / 2);
            Assert.InRange(controller.BufferingProgress!.Value, 0.49, 0.51);

            backend.IsBuffering = false;
            controller.Update();
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Null(controller.BufferingProgress);
            controller.Stop();
            Assert.Equal(0, session.AudioCacheSnapshot!.Value.TransientBytes);
        }
        finally
        {
            File.Delete(soundFont);
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NonZeroPlaybackStartCanEnterBufferingWithoutLosingTheTickZeroSignature()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            using RecoveryBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Seek(240);
            controller.Start();
            backend.PositionFrames = 4_800;
            backend.IsBuffering = true;

            Exception? failure = Record.Exception(controller.Update);

            Assert.Null(failure);
            Assert.Equal(PlaybackState.Buffering, controller.State);
            Assert.Equal(PlaybackTaskKind.MainTimeline, controller.ActiveTaskKind);
            Assert.Equal(1, backend.BeginRecoveryCount);
            Assert.True(backend.RecoveryEndFrame > backend.PositionFrames);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void UnavailableDiskRecoveryStorageFallsBackToReservedWorkerMemory()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            using RecoveryBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 4_800;
            backend.IsBuffering = true;

            controller.Update();

            Assert.Equal(PlaybackState.Buffering, controller.State);
            Assert.Equal(PlaybackTaskKind.MainTimeline, controller.ActiveTaskKind);
            Assert.Equal(0, backend.StopCount);
            Assert.Equal(1, backend.BeginRecoveryCount);
            Assert.True(backend.ActiveMemoryFrameCapacity > 0);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void UnderrunWithoutDiskOrMemoryRecoveryStorageStopsAtStructuredError()
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            using RecoveryBackend backend = new() { AcceptMemoryFallback = false };
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 4_800;
            backend.IsBuffering = true;

            controller.Update();

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.IsType<AudioRecoveryStorageUnavailableException>(controller.LastError);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(0, backend.BeginRecoveryCount);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    private static MidoraProject CreateProject()
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
        LogicalTrack track = new(project) { Name = "Track"};
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

    private static (MidoraProject Project, LogicalTrack RestoredTrack) CreateMonitoringRoutingProject()
    {
        MidoraProject project = new(480);

        EventInstrument firstInstrument = new(project)
        {
            Name = "First",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice firstVoice = new(project);
        firstVoice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        firstInstrument.SubVoices.Add(firstVoice);
        project.EventInstruments.Add(firstInstrument);
        LogicalTrack firstTrack = new(project) {
            Name = "First",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, firstTrack, firstInstrument.Id);
        Segment firstSegment = new(project) { LengthTicks = 960 };
        firstSegment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        firstTrack.Segments.Add(firstSegment);

        EventInstrument restoredInstrument = new(project)
        {
            Name = "Restored",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice restoredVoice = new(project);
        restoredVoice.InitialState.Program = 42;
        restoredVoice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        restoredInstrument.SubVoices.Add(restoredVoice);
        project.EventInstruments.Add(restoredInstrument);
        LogicalTrack restoredTrack = new(project) {
            Name = "Restored",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, restoredTrack, restoredInstrument.Id);
        Segment restoredSegment = new(project) { LengthTicks = 960 };
        restoredSegment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 120,
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        restoredTrack.Segments.Add(restoredSegment);

        return (project, restoredTrack);
    }

    private sealed class FakeBackend
        : IRealtimePlaybackBackend,
          IHeldPreviewRealtimePlaybackBackend,
          IRealtimePlaybackCacheBackend
    {
        public int ActualSampleRate { get; init; } = 48_000;
        public long PositionFrames { get; set; }
        public long RenderPositionFrames => ExplicitRenderPositionFrames ?? PositionFrames;
        public long? ExplicitRenderPositionFrames { get; set; }
        public bool IsBuffering => false;
        public bool IsCompleted { get; set; }
        public bool IsFaulted { get; set; }
        public string? FaultDescription { get; set; }
        public bool OutputDeviceSelectionRequired { get; set; }
        public string? OutputDeviceSelectionReason { get; set; }
        public string? SelectedOutputDeviceId { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool? LastStopFlush { get; private set; }
        public int ResetCount { get; private set; }
        public int PrepareCount { get; private set; }
        public bool ThrowMonitoringCommands { get; set; }
        public int MonitoringApplyCount { get; private set; }
        public int HeldPauseCount { get; private set; }
        public int HeldReplaceCount { get; private set; }
        public int HeldResumeCount { get; private set; }
        public bool HeldProducerPaused { get; private set; }
        public bool ThrowPrepare { get; set; }
        public bool ThrowStop { get; set; }
        public bool ThrowReset { get; set; }
        public MidiRenderPlan? LastStartedPlan { get; private set; }
        public PlaybackMasterConfiguration? LastMasterConfiguration { get; private set; }
        public List<MidiMonitoringCommand> MonitoringCommands { get; } = [];
        public List<RealtimePlaybackCacheMode> CacheModes { get; } = [];
        public IRealtimePlaybackCacheStore? CacheStore { get; private set; }

        public void SetAudioCacheStore(IRealtimePlaybackCacheStore cacheStore) =>
            CacheStore = cacheStore;

        public void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode) =>
            CacheModes.Add(mode);

        public int Prepare()
        {
            PrepareCount++;
            if (ThrowPrepare)
            {
                throw new InvalidOperationException("Injected prepare failure.");
            }
            return ActualSampleRate;
        }
        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master)
        {
            Assert.Equal(ActualSampleRate, plan.SampleRate);
            Assert.True(File.Exists(soundFontPath));
            StartCount++;
            LastStartedPlan = plan;
            LastMasterConfiguration = master;
            PositionFrames = 0;
            ExplicitRenderPositionFrames = null;
            IsCompleted = false;
        }
        public void Stop(bool flush)
        {
            StopCount++;
            LastStopFlush = flush;
            if (ThrowStop)
            {
                throw new InvalidOperationException("Injected stop failure.");
            }
        }
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
        {
            MonitoringApplyCount++;
            if (ThrowMonitoringCommands)
            {
                throw new InvalidOperationException("Injected monitoring failure.");
            }
            MonitoringCommands.AddRange(commands);
        }
        public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout)
        {
            Assert.True(timeout > TimeSpan.Zero);
            Assert.False(HeldProducerPaused);
            HeldPauseCount++;
            HeldProducerPaused = true;
            return RenderPositionFrames;
        }
        public void ReplaceHeldPreviewFutureAndResume(
            MidiRenderPlan plan,
            long producerFrontierFrame,
            TimeSpan timeout)
        {
            Assert.True(timeout > TimeSpan.Zero);
            Assert.True(HeldProducerPaused);
            Assert.Equal(RenderPositionFrames, producerFrontierFrame);
            HeldReplaceCount++;
            LastStartedPlan = plan;
            ExplicitRenderPositionFrames = producerFrontierFrame;
            HeldProducerPaused = false;
        }
        public void ResumeHeldPreviewFromProducerFrontier()
        {
            Assert.True(HeldProducerPaused);
            HeldResumeCount++;
            HeldProducerPaused = false;
        }
        public void Reset()
        {
            ResetCount++;
            if (ThrowReset)
            {
                throw new InvalidOperationException("Injected reset failure.");
            }
            PositionFrames = 0;
        }
        public void SelectOutputDevice(string? deviceId)
        {
            SelectedOutputDeviceId = deviceId;
            OutputDeviceSelectionRequired = false;
            OutputDeviceSelectionReason = null;
            IsFaulted = false;
        }
        public void Dispose()
        {
        }
    }

    private sealed class RecoveryBackend
        : IRealtimePlaybackBackend, IBufferingRecoveryRealtimePlaybackBackend
    {
        private AudioCacheSessionStore.AudioRecoverySpool? _nextSpool;
        private AudioCacheSessionStore.AudioRecoverySpool? _activeSpool;
        private long _nextMemoryFrameCapacity;
        private long _activeMemoryFrameCapacity;

        public int ActualSampleRate => 48_000;
        public long PositionFrames { get; set; }
        public long RenderPositionFrames { get; set; }
        public bool IsBuffering { get; set; }
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public bool HasBufferingRecoveryStorage => _activeSpool is not null
            || _activeMemoryFrameCapacity > 0;
        public int BeginRecoveryCount { get; private set; }
        public long RecoveryEndFrame { get; private set; }
        public int StopCount { get; private set; }
        public bool AcceptMemoryFallback { get; init; } = true;
        public long ActiveMemoryFrameCapacity => _activeMemoryFrameCapacity;

        public int Prepare() => ActualSampleRate;

        public void Start(
            MidiRenderPlan plan,
            string soundFontPath,
            PlaybackMasterConfiguration master)
        {
            _activeSpool = _nextSpool;
            _nextSpool = null;
            _activeMemoryFrameCapacity = _nextMemoryFrameCapacity;
            _nextMemoryFrameCapacity = 0;
            PositionFrames = 0;
            RenderPositionFrames = 0;
            IsBuffering = false;
        }

        public void SetNextBufferingRecoveryStorage(
            AudioCacheSessionStore.AudioRecoverySpool? recoverySpool,
            long memoryFallbackFrameCapacity)
        {
            _nextSpool?.Dispose();
            _nextSpool = recoverySpool;
            _nextMemoryFrameCapacity = AcceptMemoryFallback
                ? memoryFallbackFrameCapacity
                : 0;
        }

        public void BeginBufferingRecovery(long recoveryEndFrame)
        {
            Assert.True(IsBuffering);
            Assert.True(HasBufferingRecoveryStorage);
            BeginRecoveryCount++;
            RecoveryEndFrame = recoveryEndFrame;
        }

        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
        {
        }

        public void Stop(bool flush)
        {
            StopCount++;
            _activeSpool?.Dispose();
            _activeSpool = null;
            _activeMemoryFrameCapacity = 0;
            IsBuffering = false;
        }

        public void Reset()
        {
        }

        public void SelectOutputDevice(string? deviceId)
        {
        }

        public void Dispose()
        {
            _nextSpool?.Dispose();
            _nextSpool = null;
            _activeSpool?.Dispose();
            _activeSpool = null;
        }
    }

    private static string CreateTemporarySoundFont()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-playback-{Guid.NewGuid():N}.sf2");
        File.WriteAllBytes(path, []);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineNavigationSeeksWhilePlayingOrBufferingAndCanStillStop(bool buffering)
    {
        string soundFont = CreateTemporarySoundFont();
        try
        {
            using ProjectCompilationSession session = new(CreateProject(), soundFont);
            using RecoveryBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 4800;
            backend.IsBuffering = buffering;
            controller.Update();
            Assert.Equal(buffering ? PlaybackState.Buffering : PlaybackState.Playing, controller.State);
            controller.Seek(240);
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackTaskKind.MainTimeline, controller.ActiveTaskKind);
            controller.Stop();
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.False(session.EditsLocked);
        }
        finally { File.Delete(soundFont); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }
            _utcNow += amount;
            _timestamp = checked(_timestamp + amount.Ticks);
        }
    }
}
