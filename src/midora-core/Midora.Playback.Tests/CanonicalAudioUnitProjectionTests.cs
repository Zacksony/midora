using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Playback.Tests;

public sealed class CanonicalAudioUnitProjectionTests
{
    [Fact]
    public void ShortGateLoopReachesAudioPlanAndInvalidatesPreviouslyUnloopedPcm()
    {
        using MidoraProject project = new(480);
        var fixture = AddVoice(project, 60);
        fixture.Instrument.TemplateLengthTicks = 768;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Segment.LengthTicks = 1920;
        fixture.Segment.Notes[0].LengthTicks = 576;
        SubVoice voice = fixture.Instrument.SubVoices[0];
        voice.Events[0].LengthTicks = 768;
        voice.Events.Add(new(project) { Tick = 192, Kind = TemplateEventKind.PitchBend, Value = -7701 });
        voice.Events.Add(new(project) { Tick = 288, Kind = TemplateEventKind.PitchBend, Value = 8191 });
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult unlooped = compiler.CompileFull(project);
        MidiUnitFragmentRenderPlan before = Assert.Single(
            MidiRenderPlanAdapter.CreateRealtime(unlooped, 48_000).UnitFragments.ToArray());

        fixture.Instrument.LoopStartTick = 192;
        fixture.Instrument.LoopEndTick = 384;
        ProjectChangeSet changes = new();
        changes.EventInstrumentIds.Add(fixture.Instrument.Id);
        CanonicalCompiledResult looped = compiler.CompileIncremental(project, changes);
        Assert.True(looped.IsConsumable, string.Join(Environment.NewLine, looped.Diagnostics));
        CanonicalAudioUnitFragment canonical = Assert.Single(
            CanonicalAudioUnitProjection.Create(looped).Fragments.ToArray());
        Assert.Equal(new long[] { 192, 288, 384, 480 }, canonical.Events.ToArray()
            .Where(e => e.Role == CanonicalEventRole.PitchBend && e.Source.Origin == SourceOrigin.TemplateEvent)
            .Select(e => e.RelativeTick));
        MidiUnitFragmentRenderPlan after = Assert.Single(
            MidiRenderPlanAdapter.CreateRealtime(looped, 48_000).UnitFragments.ToArray());
        Assert.Equal(new[] { (9600L, -7701), (14400L, 8191), (19200L, -7701), (24000L, 8191) },
            after.Events.ToArray().Where(e => e.Message.MessageType == MidiMessageType.PitchWheelChange
                && e.SampleFrame > 0 && e.SampleFrame < 576 * 50)
                .Select(e => (e.SampleFrame, (e.Message.Byte2 << 7 | e.Message.Byte1) - 8192)));
        Assert.Equal(before.EndFrame, after.EndFrame);
        Assert.NotEqual(before.SemanticFingerprint, after.SemanticFingerprint);
        string fontIdentity = new('a', 64);
        Assert.NotEqual(
            MidiUnitPcmCacheKey.Create(before, 48_000, fontIdentity, "test-native", 500),
            MidiUnitPcmCacheKey.Create(after, 48_000, fontIdentity, "test-native", 500));
    }

    [Fact]
    public void SharedMidiRootProducesOneChannelStateFragmentAcrossChildTracks()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 9,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack first = AddMidiTrack(project, root, "First", startTick: 0, key: 36);
        PureMidiTrack second = AddMidiTrack(project, root, "Second", startTick: 240, key: 38);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        CanonicalAudioUnitFragment fragment = Assert.Single(
            CanonicalAudioUnitProjection.Create(compiled).Fragments.ToArray());
        Assert.Equal(root.Id, fragment.MidiChannelRootId);
        Assert.Equal(MidiChannelMode.Percussion, fragment.ChannelMode);
        Assert.Equal(0, fragment.GroupStartTick);
        Assert.Equal(720, fragment.GroupEndTick);
        Assert.Equal(first.Id, fragment.TrackId);
        Assert.Contains(fragment.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 36);
        Assert.Contains(fragment.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 38);
        Assert.All(fragment.Events.ToArray(), value => Assert.Equal(0, value.Message.ChannelNumber));
    }

    [Fact]
    public void RootLifecycleMonitoringEventsRemainEnabledWhenTheirOwnerTrackIsMuted()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack owner = AddMidiTrack(project, root, "Owner", startTick: 0, key: 60);
        PureMidiTrack audible = AddMidiTrack(project, root, "Audible", startTick: 0, key: 64);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
            compiled,
            48_000,
            new HashSet<MidoraId> { audible.Id });

        int rootSourceIndex = plan.FindSourceIndex(root.Id.Value);
        int ownerSourceIndex = plan.FindSourceIndex(owner.Id.Value);
        Assert.True(rootSourceIndex >= 0);
        Assert.True(ownerSourceIndex >= 0);
        Assert.Contains(ownerSourceIndex, plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(rootSourceIndex, plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 121
            && value.SourceIndex == rootSourceIndex);
        Assert.Contains(plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 121
            && value.SourceIndex == rootSourceIndex);
    }

    [Fact]
    public void RealtimeSessionKeepsPureMidiRootMixSourceEnabled()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: 60);
        using ProjectCompilationSession session = new(project);
        CanonicalCompiledResult compiled = session.CompileForPlayback(0, null);

        MidiRenderPlan audible = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId> { track.Id });
        MidiRenderPlan muted = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId>());

        int rootSourceIndex = audible.FindSourceIndex(root.Id.Value);
        int trackSourceIndex = audible.FindSourceIndex(track.Id.Value);
        Assert.True(rootSourceIndex >= 0);
        Assert.True(trackSourceIndex >= 0);
        Assert.Equal(rootSourceIndex, Assert.Single(audible.UnitFragments.ToArray()).SourceIndex);
        Assert.DoesNotContain(rootSourceIndex, audible.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(trackSourceIndex, audible.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(rootSourceIndex, muted.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(trackSourceIndex, muted.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(
            new MidiRenderCacheSourceBinding(trackSourceIndex, rootSourceIndex),
            audible.CacheSourceBindings.ToArray());
    }

    [Fact]
    public void PureMidiEffectsControllersRemainCanonicalButAreIgnoredByNoFxAudioProjection()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "No FX",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: 60);
        track.Segments[0].ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 12,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 91,
            Data2 = 96,
            Order = 5
        });

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);
        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);

        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Contains(compiled.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 91
            && value.Message.Byte2 == 96);
        Assert.DoesNotContain(
            plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 91);
        Assert.DoesNotContain(
            plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 91);
    }

    [Fact]
    public void PureMidiChannelModeSystemExclusiveReachesPortAndUnitPlans()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Mode",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 6,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: 60);
        track.Segments[0].OpaqueEvents.Add(new(project)
        {
            Tick = 24,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x43, 0x10, 0x4c, 0x08, 0x06, 0x07, 0x01, 0xf7],
            Order = 1
        });

        using ProjectCompilationSession session = new(project);
        Assert.Single(session.LastAttempt.ChannelModeSystemExclusiveEvents.ToArray());
        CanonicalCompiledResult compiled = session.CompileForPlayback(0, null);
        Assert.Single(compiled.ChannelModeSystemExclusiveEvents.ToArray());
        MidiRenderPlan plan = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId> { track.Id });

        ScheduledMidiMessage portEvent = Assert.Single(
            plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.IsChannelModeSystemExclusive);
        Assert.Equal((byte)6, portEvent.Message.ChannelNumber);
        Assert.Equal((byte)6, portEvent.ChannelModeSystemExclusive!.Value.TargetChannel);
        ScheduledMidiMessage fragmentEvent = Assert.Single(
            plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.IsChannelModeSystemExclusive);
        Assert.Equal((byte)0, fragmentEvent.Message.ChannelNumber);
        Assert.Equal((byte)0, fragmentEvent.ChannelModeSystemExclusive!.Value.TargetChannel);
        Assert.Equal((byte)1, fragmentEvent.ChannelModeSystemExclusive.Value.ModeValue);
    }

    [Fact]
    public void PureMidiPcmIdentityUsesActualContentInsteadOfCollectionGeneration()
    {
        MidoraProject firstProject = CreateSinglePureMidiProject(key: 60, controllerValue: 24);
        MidoraProject differentProject = CreateSinglePureMidiProject(key: 61, controllerValue: 25);
        MidoraProject equalProject = CreateSinglePureMidiProject(key: 60, controllerValue: 24);
        MidiSegment firstSegment = firstProject.PureMidiTracks[0].Segments[0];
        MidiSegment differentSegment = differentProject.PureMidiTracks[0].Segments[0];

        Assert.Equal(firstProject.PureMidiTracks[0].Id, differentProject.PureMidiTracks[0].Id);
        Assert.Equal(firstSegment.Id, differentSegment.Id);
        Assert.Equal(firstSegment.Notes.Generation, differentSegment.Notes.Generation);
        Assert.Equal(firstSegment.ChannelEvents.Generation, differentSegment.ChannelEvents.Generation);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult firstCompiled = compiler.CompileFull(firstProject);
        CanonicalCompiledResult differentCompiled = compiler.CompileFull(differentProject);
        CanonicalCompiledResult equalCompiled = compiler.CompileFull(equalProject);
        MidiSegmentRenderPlan firstPlan = Assert.Single(
            MidiRenderPlanAdapter.CreateRealtime(firstCompiled, 48_000).Segments.ToArray());
        MidiSegmentRenderPlan differentPlan = Assert.Single(
            MidiRenderPlanAdapter.CreateRealtime(differentCompiled, 48_000).Segments.ToArray());
        MidiSegmentRenderPlan equalPlan = Assert.Single(
            MidiRenderPlanAdapter.CreateRealtime(equalCompiled, 48_000).Segments.ToArray());

        Assert.NotEqual(firstPlan.SemanticFingerprint, differentPlan.SemanticFingerprint);
        Assert.Equal(firstPlan.SemanticFingerprint, equalPlan.SemanticFingerprint);
        string soundFont = new('a', 64);
        Assert.NotEqual(
            MidiSegmentPcmCacheKey.Create(firstPlan, 48_000, soundFont, "native-v1", 500),
            MidiSegmentPcmCacheKey.Create(differentPlan, 48_000, soundFont, "native-v1", 500));
        Assert.Equal(
            MidiSegmentPcmCacheKey.Create(firstPlan, 48_000, soundFont, "native-v1", 500),
            MidiSegmentPcmCacheKey.Create(equalPlan, 48_000, soundFont, "native-v1", 500));
        Assert.NotEqual(
            CreateLegacySegmentPcmCacheKey(firstPlan, 48_000, soundFont, "native-v1", 500),
            MidiSegmentPcmCacheKey.Create(firstPlan, 48_000, soundFont, "native-v1", 500));
    }

    [Fact]
    public void ProjectsCanonicalAllocationsToRouteIndependentChannelZeroFragments()
    {
        MidoraProject project = new(480);
        (EventInstrument instrument, LogicalTrack track, Segment segment) = AddVoice(
            project,
            pitch: 60);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(project);
        CanonicalAudioUnitFragment original = Assert.Single(
            CanonicalAudioUnitProjection.Create(before).Fragments.ToArray());
        ChannelUnitAllocation originalAllocation = before.Allocations[0];
        AudioSynthesisCacheEnvironment environment = new(
            48_000,
            new string('a', 64),
            "bass-2.4.18.3+bassmidi-2.4.16.0",
            500);
        string originalCacheKey = AudioUnitPcmCacheKey.Create(original, before, environment);

        (EventInstrument blockerInstrument, LogicalTrack blockerTrack, _) = AddVoice(
            project,
            pitch: 72);
        project.EventInstruments.Remove(blockerInstrument);
        project.EventInstruments.Insert(0, blockerInstrument);
        ArrangementTrackReference blockerReference = new(
            ArrangementTrackKind.LogicalTrack,
            blockerTrack.Id);
        project.ArrangementTracks.Remove(blockerReference);
        project.ArrangementTracks.Insert(0, blockerReference);

        CanonicalCompiledResult after = compiler.CompileFull(project);
        CanonicalAudioUnitFragment moved = CanonicalAudioUnitProjection.Create(after).Fragments
            .ToArray()
            .Single(value => value.SegmentId == segment.Id);
        ChannelUnitAllocation movedAllocation = after.Allocations.ToArray()
            .Single(value => value.SegmentId == segment.Id);

        Assert.Equal(instrument.Id, moved.EventInstrumentId);
        Assert.Equal(track.Id, moved.TrackId);
        Assert.Equal(original.InstanceGroupId, moved.InstanceGroupId);
        Assert.Equal(original.SubVoiceId, moved.SubVoiceId);
        Assert.Equal(original.SemanticFingerprint, moved.SemanticFingerprint);
        Assert.Equal(originalCacheKey, AudioUnitPcmCacheKey.Create(moved, after, environment));
        Assert.NotEqual(
            (originalAllocation.ZeroBasedPort, originalAllocation.ZeroBasedChannel),
            (movedAllocation.ZeroBasedPort, movedAllocation.ZeroBasedChannel));
        Assert.All(
            moved.Events.ToArray(),
            value => Assert.Equal(0, value.Message.ChannelNumber));
    }

    [Fact]
    public void UnitPcmKeyChangesForEverySoundAffectingEnvironmentDimension()
    {
        MidoraProject project = new(480);
        _ = AddVoice(project, pitch: 60);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);
        CanonicalAudioUnitFragment fragment = Assert.Single(
            CanonicalAudioUnitProjection.Create(compiled).Fragments.ToArray());
        AudioSynthesisCacheEnvironment baseline = new(
            48_000,
            new string('a', 64),
            "native-baseline-a",
            500);

        string key = AudioUnitPcmCacheKey.Create(fragment, compiled, baseline);

        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(fragment, compiled, baseline with { SampleRate = 44_100 }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { SoundFontSetCacheIdentity = new string('b', 64) }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { NativeBaselineIdentity = "native-baseline-b" }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { MaximumSampleVoicesPerUnitStream = 501 }));
    }

    private static (EventInstrument Instrument, LogicalTrack Track, Segment Segment) AddVoice(
        MidoraProject project,
        byte pitch)
    {
        EventInstrument instrument = new(project)
        {
            Name = $"Instrument {pitch}",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, pitch, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = $"Track {pitch}",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = pitch,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return (instrument, track, segment);
    }

    private static PureMidiTrack AddMidiTrack(
        MidoraProject project,
        MidiChannelRoot root,
        string name,
        long startTick,
        int key)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project)
        {
            ProjectStartTick = startTick,
            LengthTicks = 480
        };
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Key = key,
            NoteOnVelocity = 100,
            NoteOffVelocity = 32,
            NoteOnOrder = 0,
            NoteOffOrder = 1
        });
        track.Segments.Add(segment);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return track;
    }

    private static MidoraProject CreateSinglePureMidiProject(int key, int controllerValue)
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: key);
        track.Segments[0].ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 12,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = controllerValue,
            Order = 2
        });
        return project;
    }

    private static string CreateLegacySegmentPcmCacheKey(
        MidiSegmentRenderPlan segment,
        int sampleRate,
        string soundFontSetCacheIdentity,
        string nativeBaselineIdentity,
        int maximumSampleVoicesPerUnitStream)
    {
        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_SEGMENT_PCM_KEY_V2");
            writer.Write(2);
            writer.Write(segment.SemanticFingerprint);
            writer.Write(segment.FrameCount);
            writer.Write(sampleRate);
            writer.Write(soundFontSetCacheIdentity);
            writer.Write(nativeBaselineIdentity);
            writer.Write(maximumSampleVoicesPerUnitStream);
            writer.Write(true);
            writer.Write(true);
            writer.Write(1f);
            writer.Write(0f);
            writer.Write(16_384);
            writer.Write(2);
            writer.Write((int)Midora.AudioDevice.AudioSampleFormat.Float32);
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }
}
