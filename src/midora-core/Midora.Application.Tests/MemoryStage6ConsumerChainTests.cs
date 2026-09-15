using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.MidiExport;
using Midora.Persistence;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

/// <summary>
/// A small source-to-consumer oracle. It prepares plans, never initializes an
/// audio device or native backend, and does not claim PCM or audible equality.
/// </summary>
[Collection("MemoryStage5HistoryOwnership")]
public sealed class MemoryStage6ConsumerChainTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MixedEditsReachColdWarmSeekPlansMidiExportAndEditedPackageRoundTrip()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp",
            "memory-stage6-consumers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using MidoraProject project = CreateProject();
            using ProjectCompilationSession session = new(project);
            using ProjectDocumentSession document = new(session, ProjectDocumentOrigin.Persisted);
            EventInstrument instrument = project.EventInstruments[0];
            Segment logical = project.Tracks[0].Segments[0];
            MidiSegment midi = project.PureMidiTracks[0].Segments[0];
            HashSet<MidoraId> audible = project.ArrangementTracks.Select(value => value.TrackId).ToHashSet();
            CanonicalCompiledResult before = session.CompileForPlayback(300, 600);
            MidiRenderPlan previousPlan = session.GetOrCreateRealtimeRenderPlan(before, 48_000, audible);
            ScheduledMidiMessage[] previousMessages = Messages(previousPlan);

            Assert.True(document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(
                instrument.Id, 48)).Changed);
            Assert.True(document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(
                logical.Id, [logical.Notes[0].Id], velocity: 95)).Changed);
            Assert.True(document.Execute(ProjectDomainEditCommands.SetDirectMidiEventValues(
                midi.Id, [midi.ChannelEvents[0].Id], data2: 55)).Changed);
            Assert.Equal(3, document.History.Count);
            Assert.Equal(48, instrument.PreRollTicks);
            Assert.Equal(95, logical.Notes[0].Velocity);
            Assert.Equal(55, midi.ChannelEvents[0].Data2);

            CanonicalCompiledResult incremental = await session.EnsureCurrentCompilationAsync()
                .WaitAsync(TimeSpan.FromSeconds(60));
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult full = compiler.CompileFull(project);
            AssertCanonical(full, incremental);
            Assert.Equal(full.Fingerprint, incremental.Fingerprint);
            AssertLogicalProjection(full, logical.Notes[0].Id);
            CanonicalAudioUnitFragment rootFragment = Assert.Single(
                CanonicalAudioUnitProjection.Create(full).Fragments.ToArray(),
                value => value.MidiChannelRootId == project.MidiChannelRoots[0].Id);
            Assert.Contains(rootFragment.Events.ToArray(), value =>
                value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 36);
            Assert.Contains(rootFragment.Events.ToArray(), value =>
                value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 38);

            CanonicalCompiledResult range = session.CompileForPlayback(300, 600);
            Assert.NotSame(before, range);
            Assert.Same(range, session.CompileForPlayback(300, 600));
            AssertRange(range);
            Assert.Equal(previousMessages, Messages(previousPlan));
            foreach (int rate in new[] { 44_100, 48_000, 50_123 })
            {
                MidiRenderPlan plan = session.GetOrCreateRealtimeRenderPlan(range, rate, audible);
                Assert.Same(plan, session.GetOrCreateRealtimeRenderPlan(range, rate, audible));
                AssertPlan(plan, rate);
                MidiRenderPlan offline = MidiRenderPlanAdapter.Create(range, rate);
                Assert.Equal(plan.TotalFrameCount, offline.TotalFrameCount);
                Assert.Equal(Messages(plan), Messages(offline));
                MidiRenderPlan muted = session.GetOrCreateRealtimeRenderPlan(range, rate, new HashSet<MidoraId>());
                Assert.Equal(Messages(plan), Messages(muted));
                Assert.Same(plan.Ports[0], muted.Ports[0]);
                int rootSource = muted.FindSourceIndex(project.MidiChannelRoots[0].Id.Value);
                int trackSource = muted.FindSourceIndex(project.PureMidiTracks[0].Id.Value);
                Assert.True(rootSource >= 0 && trackSource >= 0);
                Assert.DoesNotContain(rootSource, muted.InitiallyDisabledSourceIndices.ToArray());
                Assert.Contains(trackSource, muted.InitiallyDisabledSourceIndices.ToArray());
            }

            MidiRenderPlan retainedPlan = session.GetOrCreateRealtimeRenderPlan(range, 48_000, audible);
            ScheduledMidiMessage[] retainedMessages = Messages(retainedPlan);
            using (session.RetainPreparationStorage(retainedPlan))
            {
                session.InvalidateSampleDomainCaches();
                Assert.Same(range, session.CompileForPlayback(300, 600));
                MidiRenderPlan rebuilt = session.GetOrCreateRealtimeRenderPlan(range, 48_000, audible);
                Assert.NotSame(retainedPlan, rebuilt);
                Assert.Equal(retainedMessages, Messages(rebuilt));
                long previousEvictions = session.PreparationStorage.Evictions;
                // Two cache families share 256 slots. Each range is a four-note
                // project; this exercises eviction, not large-source throughput.
                for (int start = 301; start <= 560; start++)
                {
                    CanonicalCompiledResult other = session.CompileForPlayback(start, 600);
                    session.GetOrCreateRealtimeRenderPlan(other, 48_000, audible);
                }
                Assert.True(session.PreparationStorage.Evictions > previousEvictions);
                Assert.InRange(session.PreparationStorage.CachedEntries, 0, 256);
                Assert.InRange(session.PreparationStorage.CachedBytes, 0, session.PreparationStorage.BudgetBytes);
                Assert.Equal(retainedMessages, Messages(retainedPlan));
                CanonicalCompiledResult revisited = session.CompileForPlayback(300, 600);
                Assert.NotSame(range, revisited);
                AssertCanonical(range, revisited);
                Assert.Equal(retainedMessages, Messages(
                    session.GetOrCreateRealtimeRenderPlan(revisited, 48_000, audible)));
            }

            // Export has its own formal purpose/context, never the seek plan or
            // monitoring-filtered runtime view. The encoder sees only canonical.
            byte[] midiBytes = Encode(project, compiler);
            AssertExport(midiBytes);
            var packages = new MidoraProjectPackageV1("1.0.0-test");
            string savedPath = Path.Combine(directory, "edited.midora");
            string copiedPath = Path.Combine(directory, "copy.midora");
            await packages.SaveProjectAsync(project, savedPath);
            await packages.SaveCopyAsync(project, copiedPath);
            foreach (string path in new[] { savedPath, copiedPath })
            {
                var opened = await packages.OpenAsync(path);
                using MidoraProject restored = opened.Project;
                using MidoraCompiler restoredCompiler = new();
                Assert.Empty(opened.Diagnostics);
                Assert.Equal(48, restored.EventInstruments[0].PreRollTicks);
                Assert.Equal(192, restored.EventInstruments[0].LoopStartTick);
                Assert.Equal(384, restored.EventInstruments[0].LoopEndTick);
                Assert.Equal(restored.Tracks[0].EventInstrumentUsageId, restored.Tracks[1].EventInstrumentUsageId);
                Assert.Equal(restored.PureMidiTracks[0].MidiChannelRootId, restored.PureMidiTracks[1].MidiChannelRootId);
                // Storage packing may change the source-aware fingerprint;
                // source identities and exact formal events/bytes may not.
                AssertCanonical(full, restoredCompiler.CompileFull(restored));
                Assert.Equal(midiBytes, Encode(restored, restoredCompiler));
                using ProjectCompilationSession restoredSession = new(restored);
                CanonicalCompiledResult restoredRange = restoredSession.CompileForPlayback(300, 600);
                AssertCanonical(range, restoredRange);
                AssertRange(restoredRange);
                MidiRenderPlan restoredPlan = MidiRenderPlanAdapter.CreateRealtime(restoredRange, 50_123);
                Assert.Equal(restoredRange.HasPagedEvents, restoredPlan.EventPageProvider is not null);
                AssertPlan(restoredPlan, 50_123);
            }
            for (int index = 0; index < 3; index++) document.Undo();
            Assert.Equal(0, instrument.PreRollTicks);
            Assert.Equal(100, logical.Notes[0].Velocity);
            Assert.Equal(52, midi.ChannelEvents[0].Data2);
            for (int index = 0; index < 3; index++) document.Redo();
            AssertCanonical(full, await session.EnsureCurrentCompilationAsync()
                .WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.Equal(midiBytes, Encode(project, compiler));
            output.WriteLine("four source notes; three edits; exact loop/pre-roll/mapping/range state; "
                + "44100/48000/50123 Hz realtime/offline plans; 260 range evictions/revisit; "
                + "monitoring isolation; edited Save/Copy/Open; source Undo/Redo; no native backend");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        project.Metadata.ProjectName = "Stage 6 consumer 链";
        project.Conductor.Tempos.Add(new(project, 480, 60m));
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Loop with pre-roll");
        instrument.TemplateLengthTicks = 768;
        instrument.RequiresChannelIsolation = true;
        instrument.LoopStartTick = 192;
        instrument.LoopEndTick = 384;
        SubVoice voice = instrument.SubVoices[0];
        voice.Events.Add(TemplateEvent.Note(project, 0, 768, 60, 100));
        voice.Events.Add(new(project) { Tick = 192, Kind = TemplateEventKind.PitchBend, Value = -7701 });
        voice.Events.Add(new(project) { Tick = 288, Kind = TemplateEventKind.PitchBend, Value = 8191 });
        TemplateEvent expression = TemplateEvent.ControlChange(project, 96, 11, 21);
        expression.ValueMappings.Add(new(project)
        { Source = MappingSource.Constant, Constant = 2, Operation = MappingOperation.Multiply });
        voice.Events.Add(expression);
        LogicalTrack first = new(project) { Name = "Logical A" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, first, instrument.Id);
        LogicalTrack second = new(project)
        { Name = "Logical B", EventInstrumentUsageId = first.EventInstrumentUsageId };
        project.Tracks.Add(second);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, second.Id));
        foreach ((LogicalTrack track, long tick) in new[] { (first, 0L), (second, 960L) })
        {
            Segment segment = new(project) { ProjectStartTick = tick, LengthTicks = 960 };
            segment.Notes.Add(new(project) { StartTick = 48, LengthTicks = 576, Note = 60, Velocity = 100 });
            track.Segments.Add(segment);
        }
        MidiChannelRoot root = new(project)
        {
            Name = "Shared Pure Root", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 1, FixedZeroBasedChannel = 9, ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        for (int index = 0; index < 2; index++)
        {
            PureMidiTrack track = new(project) { Name = "Pure " + index, MidiChannelRootId = root.Id };
            MidiSegment segment = new(project) { LengthTicks = 1920 };
            segment.Notes.Add(new(project)
            {
                StartTick = index == 0 ? 0 : 360, LengthTicks = index == 0 ? 960 : 480,
                Key = index == 0 ? 36 : 38, NoteOnVelocity = index == 0 ? 100 : 90,
                NoteOffVelocity = index == 0 ? 37 : 45, NoteOnOrder = 0, NoteOffOrder = 1
            });
            segment.ChannelEvents.Add(new(project)
            {
                Tick = index == 0 ? 240 : 400, Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index == 0 ? 11 : 91, Data2 = index == 0 ? 52 : 99, Order = 2
            });
            if (index == 1) segment.OpaqueEvents.Add(new(project)
            { Tick = 420, Kind = OpaqueMidiEventKind.Meta, MetaType = StandardMidiFile.TextMetaType,
                Payload = "stage6"u8.ToArray(), Order = 3 });
            track.Segments.Add(segment);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        }
        return project;
    }

    private static void AssertLogicalProjection(CanonicalCompiledResult result, MidoraId noteId)
    {
        CanonicalMidiEvent[] events = CanonicalEvents(result).Where(value => value.Source.LogicalNoteId == noteId).ToArray();
        Assert.Equal(0, Assert.Single(events, value => value.Role == CanonicalEventRole.NoteOn).Tick);
        Assert.Equal(624, Assert.Single(events, value => value.Role == CanonicalEventRole.NoteOff).Tick);
        Assert.Equal(new[] { (192L, -7701), (288L, 8191), (384L, -7701), (480L, 8191), (576L, -7701) },
            events.Where(value => value.Role == CanonicalEventRole.PitchBend
                && value.Source.Origin == SourceOrigin.TemplateEvent)
                .Select(value => (value.Tick, (value.Message.Byte2 << 7 | value.Message.Byte1) - 8192)));
        Assert.Contains(events, value => value.Tick == 96 && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 42);
    }

    private static void AssertRange(CanonicalCompiledResult range)
    {
        Assert.True(range.IsConsumable, string.Join(Environment.NewLine, range.Diagnostics));
        CanonicalMidiEvent[] events = CanonicalEvents(range);
        Assert.Contains(events, value => value.Tick == 300 && value.Source.Origin == SourceOrigin.RangeRestore
            && value.Message.MessageType == MidiMessageType.PitchWheelChange
            && (value.Message.Byte2 << 7 | value.Message.Byte1) - 8192 == 8191);
        foreach (int expression in new[] { 42, 55 }) Assert.Contains(events, value =>
            value.Tick == 300 && value.Source.Origin == SourceOrigin.RangeRestore
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == expression);
        // Direct MIDI keeps its formal DirectMidi role even for NoteOn/Off;
        // the NoteOn role identifies a Logical/template projection instead.
        CanonicalMidiEvent noteOn = Assert.Single(events,
            value => value.Message.MessageType == MidiMessageType.NoteOn);
        Assert.Equal(CanonicalEventRole.DirectMidi, noteOn.Role);
        Assert.Equal(SourceOrigin.DirectMidiNote, noteOn.Source.Origin);
        Assert.Equal((360L, (byte)1, (byte)9, (byte)38, (byte)90),
            (noteOn.Tick, noteOn.ZeroBasedPort, noteOn.ZeroBasedChannel,
                noteOn.Message.Byte1, noteOn.Message.Byte2));
        Assert.Contains(events, value => value.Tick == 600 && value.Role == CanonicalEventRole.NoteOff
            && value.Message.Byte1 == 38);
    }

    private static void AssertPlan(MidiRenderPlan plan, int rate)
    {
        // [300,480) at 120 BPM + [480,600) at 60 BPM = 7/16 s.
        long frames = (long)decimal.Round(rate * 7m / 16m, 0, MidpointRounding.AwayFromZero);
        Assert.Equal(frames, plan.TotalFrameCount);
        ScheduledMidiMessage[] events = Messages(plan);
        ScheduledMidiMessage on = Assert.Single(events, value => value.Message.MessageType == MidiMessageType.NoteOn);
        Assert.Equal((long)decimal.Round(rate / 16m, 0, MidpointRounding.AwayFromZero), on.SampleFrame);
        Assert.Equal((byte)38, on.Message.Byte1);
        if (plan.EventPageProvider is null)
        {
            Assert.Contains(events, value => value.SampleFrame == frames
                && value.Message.MessageType == MidiMessageType.NoteOff && value.Message.Byte1 == 38);
        }
        else
        {
            // Paged audio Query is half-open in frames, so its terminal cleanup
            // is not a returned scheduled event. AssertRange separately checks
            // the exact canonical NoteOff, while the fragment owns this hard end.
            MidiUnitFragmentRenderPlan root = Assert.Single(plan.UnitFragments.ToArray(),
                value => value.MidiChannelRootId != 0);
            Assert.Equal(frames, root.EndFrame);
            ScheduledPortMidiMessage pagedOn = Assert.Single(
                plan.EventPageProvider.Query(0, frames),
                value => value.Scheduled.Message.MessageType == MidiMessageType.NoteOn);
            Assert.Equal((byte)1, pagedOn.ZeroBasedPortNumber);
            Assert.Equal(on, pagedOn.Scheduled);
        }
        Assert.DoesNotContain(events, value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 91);
    }

    private static ScheduledMidiMessage[] Messages(MidiRenderPlan plan)
    {
        IEnumerable<ScheduledPortMidiMessage> resident = plan.Ports.ToArray()
            .SelectMany(port => port.Events.ToArray().Select(value =>
                new ScheduledPortMidiMessage(port.ZeroBasedPortNumber, value)));
        IEnumerable<ScheduledPortMidiMessage> paged = plan.EventPageProvider is null
            ? [] : plan.EventPageProvider.Query(0, plan.TotalFrameCount);
        // Preserve source order for ties; never reconstruct or drop duplicates.
        return resident.Concat(paged).OrderBy(value => value.ZeroBasedPortNumber)
            .ThenBy(value => value.Scheduled.SampleFrame).Select(value => value.Scheduled).ToArray();
    }

    private static CanonicalMidiEvent[] CanonicalEvents(CanonicalCompiledResult result) =>
        result.QueryEventPages(result.StartTick, result.EndTick, includeStateAtStart: true)
            .SelectMany(page => page.Items).ToArray();

    private static void AssertCanonical(CanonicalCompiledResult expected, CanonicalCompiledResult actual)
    {
        Assert.True(expected.IsConsumable, string.Join(Environment.NewLine, expected.Diagnostics));
        Assert.True(actual.IsConsumable, string.Join(Environment.NewLine, actual.Diagnostics));
        Assert.Equal(expected.TotalEventCount, actual.TotalEventCount);
        Assert.Equal(expected.TotalNoteOnEventCount, actual.TotalNoteOnEventCount);
        Assert.Equal(CanonicalEvents(expected), CanonicalEvents(actual));
        Assert.Equal(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
    }

    private static byte[] Encode(MidoraProject project, MidoraCompiler compiler)
    {
        CanonicalCompiledResult compiled = compiler.CompileFull(project,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        MidiExportLogicalTrackLayout[] layouts = compiled.Allocations.ToArray()
            .Where(value => value.MidiChannelRootId == default)
            .GroupBy(value => value.TrackId)
            .Select(group => new MidiExportLogicalTrackLayout(group.Key, group
                .Select(value => new MidiExportChannelUnit(value.ZeroBasedPort, value.ZeroBasedChannel))
                .Distinct().ToDictionary(unit => unit,
                    unit => $"Port {unit.ZeroBasedPort + 1} / Channel {unit.ZeroBasedChannel + 1}")))
            .ToArray();
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        { CompiledResult = compiled, ConductorTrackName = project.Metadata.ProjectName, LogicalTracks = layouts });
        Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
        return encoded.FileBytes;
    }

    private static void AssertExport(byte[] bytes)
    {
        ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(bytes);
        Assert.Equal(4, parsed.Tracks.Count); // Conductor, two Pure tracks, one shared Logical Unit.
        ParsedStandardMidiFileEvent[] events = parsed.Tracks.SelectMany(value => value.Events).ToArray();
        Assert.Equal(4, events.Count(value => value.Kind == StandardMidiFileEventKind.ChannelVoice
            && value.Message.MessageType == MidiMessageType.NoteOn));
        foreach ((int key, int velocity) in new[] { (36, 37), (38, 45) }) Assert.Contains(events, value =>
            value.Kind == StandardMidiFileEventKind.ChannelVoice && value.Message.MessageType == MidiMessageType.NoteOff
            && value.Message.Byte1 == key && value.Message.Byte2 == velocity);
        Assert.Contains(events, value => value.Tick == 400 && value.Kind == StandardMidiFileEventKind.ChannelVoice
            && value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 91 && value.Message.Byte2 == 99);
        Assert.Contains(events, value => value.Tick == 420 && value.Kind == StandardMidiFileEventKind.Meta
            && value.Type == StandardMidiFile.TextMetaType && value.Data.Span.SequenceEqual("stage6"u8));
    }
}
