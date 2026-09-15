using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Audio;
using Midora.Common;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.MidiExport;

namespace Midora.Playback.Tests;

public sealed class LogicalPagedConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagingOnlyLogicalEventsPreservesAudioAndSmfIncludingInlinePure(bool range)
    {
        using MidoraProject project = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult resident = compiler.CompileFull(project, new()
        {
            Purpose = CompilationPurpose.MidiExport,
            StartTick = range ? 180 : 0,
            EndTick = 960
        });
        Assert.True(resident.IsConsumable, string.Join("; ", resident.Diagnostics));
        Assert.False(resident.HasPagedEvents);
        CanonicalCompiledResult paged = PageLogical(resident);
        Assert.True(paged.HasPagedLogicalEvents);
        Assert.False(paged.HasPagedPureMidiEvents);
        Assert.Equal(resident.QueryEventPages(resident.StartTick, resident.EndTick).SelectMany(p => p.Items),
            paged.QueryEventPages(paged.StartTick, paged.EndTick).SelectMany(p => p.Items));

        MidiRenderPlan before = MidiRenderPlanAdapter.CreateRealtime(resident, 48_000);
        MidiRenderPlan after = MidiRenderPlanAdapter.CreateRealtime(paged, 48_000);
        Assert.Equal(before.SourceIds.ToArray(), after.SourceIds.ToArray());
        Assert.Equal(before.ReferencedPresetKeys.ToArray(), after.ReferencedPresetKeys.ToArray());
        Assert.Equal(ReadAudio(before), ReadAudio(after));
        ScheduledPortMidiMessage[] completePages = after.EventPageProvider!.Query(0, after.TotalFrameCount).ToArray();
        List<ScheduledPortMidiMessage> rolling = [];
        for (long frame = 0; frame < after.TotalFrameCount; frame += 137)
            rolling.AddRange(after.EventPageProvider.Query(frame, Math.Min(after.TotalFrameCount, frame + 137)));
        Assert.Equal(completePages, rolling);
        Assert.All(after.UnitFragments.ToArray(), fragment => Assert.True(fragment.Events.IsEmpty));
        Assert.Equal(CanonicalAudioUnitProjection.Create(resident).Fragments.ToArray().Select(f => f.SemanticFingerprint),
            CanonicalAudioUnitProjection.Create(paged).Fragments.ToArray().Select(f => f.SemanticFingerprint));

        MidiExportLogicalTrackLayout[] layouts = project.Tracks.Select(track => new MidiExportLogicalTrackLayout(track.Id,
            resident.Allocations.ToArray().Where(a => a.TrackId == track.Id)
                .Select(a => new MidiExportChannelUnit(a.ZeroBasedPort, a.ZeroBasedChannel)).Distinct()
                .ToDictionary(unit => unit, unit => $"Port {unit.ZeroBasedPort} / Channel {unit.ZeroBasedChannel}"))).ToArray();
        Assert.Equal(Encode(resident, layouts), Encode(paged, layouts));
        foreach (MidiExportLogicalTrackLayout layout in layouts)
        {
            MidiExportEncodingResult original = CanonicalMidiFileExporter.EncodeLogicalTrack(new()
                { CompiledResult = resident, ConductorTrackName = "Song", LogicalTrack = layout });
            MidiExportEncodingResult streamed = CanonicalMidiFileExporter.EncodeLogicalTrack(new()
                { CompiledResult = paged, ConductorTrackName = "Song", LogicalTrack = layout });
            Assert.True(original.Succeeded, string.Join("; ", original.Diagnostics));
            Assert.True(streamed.Succeeded, string.Join("; ", streamed.Diagnostics));
            Assert.Equal(original.FileBytes, streamed.FileBytes);
        }
    }

    [Fact]
    public void PagedPcmIdentityIncludesTempoEvenWhenFragmentDurationIsUnchanged()
    {
        using MidoraProject project = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult first = PageLogical(compiler.CompileFull(project));
        MidiRenderPlan before = MidiRenderPlanAdapter.CreateRealtime(first, 48_000);
        project.Conductor.Tempos[0] = project.Conductor.Tempos[0] with { BeatsPerMinute = 80 };
        project.Conductor.Tempos.Add(new(project, 480, 240));
        CanonicalCompiledResult second = PageLogical(compiler.CompileFull(project));
        MidiRenderPlan after = MidiRenderPlanAdapter.CreateRealtime(second, 48_000);
        Assert.Equal(before.TotalFrameCount, after.TotalFrameCount);
        MidiUnitFragmentRenderPlan a = Assert.Single(before.UnitFragments.ToArray(), f => f.MidiChannelRootId == 0);
        MidiUnitFragmentRenderPlan b = Assert.Single(after.UnitFragments.ToArray(), f => f.MidiChannelRootId == 0);
        Assert.Equal(a.EndFrame - a.StartFrame, b.EndFrame - b.StartFrame);
        Assert.NotEqual(a.SemanticFingerprint, b.SemanticFingerprint);
        Assert.NotEqual(MidiUnitPcmCacheKey.Create(a, 48_000, new string('a', 64), "baseline", 500),
            MidiUnitPcmCacheKey.Create(b, 48_000, new string('a', 64), "baseline", 500));
    }

    [Fact]
    public void MetadataIsReusedAndCancelledBuildDoesNotPoisonFuturePreparation()
    {
        using MidoraProject project = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult paged = PageLogical(compiler.CompileFull(project));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CanonicalAudioUnitProjection.Create(paged,
            cancellationToken: cancellation.Token));
        CanonicalAudioUnitProjection projection = CanonicalAudioUnitProjection.Create(paged);
        Assert.Same(projection, CanonicalAudioUnitProjection.Create(paged));
        Assert.Throws<OperationCanceledException>(() => MidiRenderPlanAdapter.Create(paged, 48_000,
            cancellationToken: cancellation.Token));
        using MemoryStream output = new();
        Assert.Throws<OperationCanceledException>(() => CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = paged, ConductorTrackName = "Song", LogicalTracks = []
        }, cancellation.Token));
    }

    [Fact]
    public void RetainingPreparedMetadataDoesNotKeepItsCanonicalResultAlive()
    {
        (WeakReference result, CanonicalAudioUnitProjection metadata) = CreateDetachedMetadata();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(result.IsAlive, "Prepared metadata retained its former canonical owner.");
        Assert.NotEmpty(metadata.Fragments.ToArray());
        GC.KeepAlive(metadata);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, CanonicalAudioUnitProjection) CreateDetachedMetadata()
    {
        using MidoraProject project = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult paged = PageLogical(compiler.CompileFull(project));
        return (new WeakReference(paged), CanonicalAudioUnitProjection.Create(paged));
    }

    [Fact]
    public async Task DisposingSessionDoesNotWaitForProjectionOrKeepItsOldResult()
    {
        using MidoraProject project = CreateProject();
        using ProjectCompilationSession session = new(project);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        WeakReference old = InstallBlockingResult(session, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Projection test was not released.");
        });
        Task<Exception> preparing = Task.Run(() => Record.Exception(() => session.GetOrCreateRenderPlan(48_000)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            // Dispose must acquire the session lock while projection is still blocked.
            await Task.Run(session.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.IsType<ObjectDisposedException>(await preparing.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertReleased(old);
        GC.KeepAlive(session);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InstallBlockingResult(ProjectCompilationSession session, Action beforeRead)
    {
        CanonicalCompiledResult paged = PageLogical(session.LastAttempt, beforeRead);
        typeof(ProjectCompilationSession).GetProperty(nameof(ProjectCompilationSession.LastAttempt))!.SetValue(session, paged);
        return new WeakReference(paged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ObsoleteProjectionIsRejectedWithoutCachingAndNextPreparationSucceeds(int invalidation)
    {
        using MidoraProject project = CreateProject();
        using ProjectCompilationSession session = new(project);
        CanonicalCompiledResult initial = session.LastAttempt;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        InstallBlockingResult(session, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Projection test was not released.");
        });
        Task<Exception> preparing = Task.Run(() => Record.Exception(() => session.GetOrCreateRenderPlan(48_000)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                if (invalidation == 0)
                    session.ApplyEdit(p => p.Tracks[0].Segments[0].Notes[0].Note = 67,
                        ProjectChangeSet.Everything);
                else if (invalidation == 1)
                    session.InvalidateSampleDomainCaches();
                else if (invalidation == 3)
                    // A manual compile advances the request/publication generation
                    // without changing the Project source revision.
                    session.Recompile(ProjectChangeSet.Everything);
                else
                    // Isolate published-result replacement from the revision and
                    // sample-generation guards; both counters remain unchanged.
                    typeof(ProjectCompilationSession).GetProperty(nameof(ProjectCompilationSession.LastAttempt))!
                        .SetValue(session, initial);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }

        InvalidOperationException failure = Assert.IsType<InvalidOperationException>(
            await preparing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("changed while the render plan was being prepared", failure.Message);
        Assert.Equal(0, session.PreparationStorage.CachedEntries);
        MidiRenderPlan next = session.GetOrCreateRenderPlan(48_000);
        Assert.Equal(ReadAudio(MidiRenderPlanAdapter.Create(session.LastAttempt, 48_000)), ReadAudio(next));
        Assert.Same(next, session.GetOrCreateRenderPlan(48_000));
        Assert.Equal(1, session.PreparationStorage.CachedEntries);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertReleased(WeakReference result)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(result.IsAlive, "A disposed session or completed projection still retained its former result.");
    }

    private static byte[] Encode(CanonicalCompiledResult result, MidiExportLogicalTrackLayout[] layouts)
    {
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
            { CompiledResult = result, ConductorTrackName = "Song", LogicalTracks = layouts });
        Assert.True(encoded.Succeeded, string.Join("; ", encoded.Diagnostics));
        return encoded.FileBytes;
    }

    private static ScheduledPortMidiMessage[] ReadAudio(MidiRenderPlan plan)
    {
        IEnumerable<ScheduledPortMidiMessage> inline = plan.Ports.ToArray().SelectMany(port => port.Events.ToArray()
            .Where(e => e.SampleFrame < plan.TotalFrameCount)
            .Select(e => new ScheduledPortMidiMessage(port.ZeroBasedPortNumber, e)));
        IEnumerable<ScheduledPortMidiMessage> paged = plan.EventPageProvider?.Query(0, plan.TotalFrameCount) ?? [];
        // Ports are disjoint here. Stable per-port ordering preserves every same-tick event.
        return inline.Concat(paged).OrderBy(e => e.ZeroBasedPortNumber)
            .ThenBy(e => e.Scheduled.Message.ChannelNumber).ThenBy(e => e.Scheduled.SampleFrame).ToArray();
    }

    private static CanonicalCompiledResult PageLogical(CanonicalCompiledResult source, Action? beforeRead = null)
    {
        Assert.False(source.HasPagedEvents);
        CanonicalMidiEvent[] logical = source.Events.ToArray().Where(e => e.Source.MidiChannelRootId == default).ToArray();
        CanonicalMidiEvent[] pure = source.Events.ToArray().Where(e => e.Source.MidiChannelRootId != default).ToArray();
        ConstructorInfo constructor = Assert.Single(typeof(CanonicalCompiledResult).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        return (CanonicalCompiledResult)constructor.Invoke([
            source.TicksPerQuarterNote, source.Context, pure, source.Conductor, source.Allocations.ToArray(),
            source.Diagnostics, source.IsPartial, source.IsConsumable, source.FailureStage, source.Fingerprint,
            source.Statistics, source.SmfTracks.ToArray(), source.OpaqueMidiEvents.ToArray(), null,
            source.ChannelModeSystemExclusiveEvents.ToArray(), new TestLogicalSource(logical, beforeRead), null
        ]);
    }

    private sealed class TestLogicalSource(CanonicalMidiEvent[] values, Action? beforeRead) : ILogicalCanonicalEventSource
    {
        public long EventCount => values.LongLength;
        public long NoteOnEventCount => values.LongCount(e => e.Message.MessageType == MidiMessageType.NoteOn && e.Message.Byte2 != 0);
        public IEnumerable<CanonicalMidiEvent> Enumerate(long startTick, long endTick, bool includeEnd, CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke();
            foreach (CanonicalMidiEvent value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Tick >= startTick && (value.Tick < endTick || includeEnd && value.Tick == endTick)) yield return value;
            }
        }
        public void CollectRetainedStorage(RetainedStorageCollector collector) { collector.Add(this, 32); collector.Array(values); }
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Logical", TemplateLengthTicks = 480 };
        SubVoice voice = new(project);
        voice.InitialState.BankMsb = 2; voice.InitialState.Program = 7;
        voice.Events.Add(TemplateEvent.Note(project, 0, 360, 60, 100));
        voice.Events.Add(new(project) { Tick = 120, Kind = TemplateEventKind.PitchBend, Value = 1200 });
        instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
        LogicalTrack logical = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logical, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new(project) { StartTick = 0, LengthTicks = 400, Note = 60, Velocity = 100 });
        segment.Notes.Add(new(project) { StartTick = 480, LengthTicks = 400, Note = 64, Velocity = 90 });
        logical.Segments.Add(segment);
        MidiChannelRoot root = new(project) { Name = "MIDI", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 1, FixedZeroBasedChannel = 9, ChannelMode = MidiChannelMode.Melodic };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack track = new(project) { Name = "Pure", MidiChannelRootId = root.Id };
        MidiSegment midi = new(project) { LengthTicks = 960 };
        midi.Notes.Add(new(project) { StartTick = 24, LengthTicks = 480, Key = 50, NoteOnVelocity = 98, NoteOffVelocity = 37,
            NoteOnOrder = 2, NoteOffOrder = 3 });
        midi.ChannelEvents.Add(new(project) { Tick = 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 40, Order = 1 });
        track.Segments.Add(midi); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return project;
    }
}
