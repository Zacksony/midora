using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class PagedPureMidiCompilationTests
{
    [Fact]
    public void MaterializedAndPagedDirectEventsShareOverflowSafeCanonicalOrdering()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "canonical-order.mpk");
        try
        {
            (MidoraProject materializedProject, MidiSegment materializedSegment) =
                CreateDirectOrderProject();
            (MidoraProject pagedProject, MidiSegment pagedSegment) =
                CreateDirectOrderProject();
            using PureMidiContentPackWriter writer = new(path);
            foreach (DirectMidiNote note in pagedSegment.Notes)
            {
                writer.AddNote(pagedSegment.Id, new(
                    note.Id,
                    note.StartTick,
                    note.LengthTicks,
                    note.Key,
                    note.NoteOnVelocity,
                    note.NoteOffVelocity,
                    note.NoteOnOrder,
                    note.NoteOffOrder));
            }
            foreach (DirectMidiChannelEvent value in pagedSegment.ChannelEvents)
            {
                writer.AddChannelEvent(pagedSegment.Id, new(
                    value.Id,
                    value.Tick,
                    value.Kind,
                    value.Data1,
                    value.Data2,
                    value.Order));
            }
            using PureMidiContentPack pack = writer.Complete();
            DirectMidiNoteValue[] expectedNotes = pagedSegment.Notes
                .Select(static value => new DirectMidiNoteValue(
                    value.Id,
                    value.StartTick,
                    value.LengthTicks,
                    value.Key,
                    value.NoteOnVelocity,
                    value.NoteOffVelocity,
                    value.NoteOnOrder,
                    value.NoteOffOrder))
                .ToArray();
            DirectMidiChannelEventValue[] expectedEvents = pagedSegment.ChannelEvents
                .Select(static value => new DirectMidiChannelEventValue(
                    value.Id,
                    value.Tick,
                    value.Kind,
                    value.Data1,
                    value.Data2,
                    value.Order))
                .ToArray();
            pagedSegment.Notes.Clear();
            pagedSegment.ChannelEvents.Clear();
            pagedSegment.AttachPagedContent(pack.GetSegmentSource(pagedSegment.Id));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult materialized = compiler.CompileFull(materializedProject);
            CanonicalCompiledResult paged = compiler.CompileFull(pagedProject);

            Assert.True(materialized.IsConsumable);
            Assert.True(paged.IsConsumable);
            CanonicalMidiEvent[] materializedDirect = DirectEvents(materialized);
            CanonicalMidiEvent[] pagedDirect = DirectEvents(paged);
            Assert.Equal(materializedDirect, pagedDirect);
            Assert.Equal(
                "MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V4",
                MidoraCompiler.PureMidiAudioFragmentFingerprintAbi);
            string audioFragmentFingerprint = Assert.Single(
                paged.PureMidiAudioFragments).SemanticFingerprint;
            // V4 invalidates reusable PCM produced with the old range/boundary projection.
            Assert.Equal(
                "8943293cadd9db42ae1e483bffe96873e0934cae0ebaf391e0312cf0e4a82efa",
                audioFragmentFingerprint);

            CanonicalMidiEvent[] boundary = materializedDirect
                .Where(static value => value.Tick == 10)
                .ToArray();
            Assert.Equal(
                [
                    Midora.Midi.MidiMessageType.NoteOff,
                    Midora.Midi.MidiMessageType.NoteOff,
                    Midora.Midi.MidiMessageType.NoteOn,
                    Midora.Midi.MidiMessageType.ControlChange
                ],
                boundary.Select(static value => value.Message.MessageType));
            Assert.All(boundary, static value => Assert.Equal(long.MaxValue, value.SmfEventOrder));
            Assert.Equal(
                [
                    checked(long.MinValue + expectedNotes[0].Id.Value),
                    checked(long.MinValue + expectedEvents[0].Id.Value),
                    expectedNotes[1].Id.Value,
                    expectedEvents[1].Id.Value
                ],
                boundary.Select(static value => value.StableOrder));
            Assert.Equal(
                boundary.Select(static value => value.StableOrder),
                boundary.Select(static value => value.SemanticGroup));
            MidoraId exportTrackId = Assert.Single(materializedProject.PureMidiTracks).Id;
            CanonicalSmfTrackChannelEvent[] materializedSmf = materialized
                .QuerySmfTrackChannelEventPages(exportTrackId)
                .SelectMany(static page => page.Items)
                .ToArray();
            CanonicalSmfTrackChannelEvent[] pagedSmf = paged
                .QuerySmfTrackChannelEventPages(exportTrackId)
                .SelectMany(static page => page.Items)
                .ToArray();
            Assert.Equal(materializedSmf, pagedSmf);
            Assert.Equal(
                boundary.Select(static value => value.Message),
                materializedSmf
                    .Where(static value => value.Tick == 10)
                    .Select(static value => value.Message));

            materializedProject.Dispose();
            pagedProject.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static (MidoraProject Project, MidiSegment Segment) CreateDirectOrderProject()
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 30,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            segment.Notes.Add(new(project)
            {
                StartTick = 0,
                LengthTicks = 10,
                Key = 60,
                NoteOnVelocity = 100,
                NoteOnOrder = 0,
                NoteOffOrder = long.MaxValue
            });
            segment.Notes.Add(new(project)
            {
                StartTick = 10,
                LengthTicks = 10,
                Key = 61,
                NoteOnVelocity = 100,
                NoteOnOrder = long.MaxValue,
                NoteOffOrder = long.MaxValue
            });
            segment.ChannelEvents.Add(new(project)
            {
                Tick = 10,
                Kind = DirectMidiChannelEventKind.NoteOff,
                Data1 = 62,
                Data2 = 7,
                Order = long.MaxValue
            });
            segment.ChannelEvents.Add(new(project)
            {
                Tick = 10,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11,
                Data2 = 64,
                Order = long.MaxValue
            });
            return (project, segment);
        }

        static CanonicalMidiEvent[] DirectEvents(CanonicalCompiledResult result) =>
            result.QueryEventPages(result.StartTick, result.EndTick)
                .SelectMany(static value => value.Items)
                .Where(static value => value.Role == CanonicalEventRole.DirectMidi
                    && value.Source.DirectMidiObjectId != default)
                .ToArray();
    }

    [Fact]
    public void NonzeroRangeDoesNotRetriggerActiveDirectNoteInEitherStorageMode()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "active-note.mpk");
        try
        {
            (MidoraProject materializedProject, MidiSegment materializedSegment) =
                CreateProject();
            (MidoraProject pagedProject, MidiSegment pagedSegment) = CreateProject();
            DirectMidiNote pagedNote = Assert.Single(pagedSegment.Notes);
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(pagedSegment.Id, new(
                pagedNote.Id,
                pagedNote.StartTick,
                pagedNote.LengthTicks,
                pagedNote.Key,
                pagedNote.NoteOnVelocity,
                pagedNote.NoteOffVelocity,
                pagedNote.NoteOnOrder,
                pagedNote.NoteOffOrder));
            DirectMidiChannelEvent pagedControl = Assert.Single(pagedSegment.ChannelEvents);
            writer.AddChannelEvent(pagedSegment.Id, new(
                pagedControl.Id,
                pagedControl.Tick,
                pagedControl.Kind,
                pagedControl.Data1,
                pagedControl.Data2,
                pagedControl.Order));
            using PureMidiContentPack pack = writer.Complete();
            pagedSegment.Notes.Clear();
            pagedSegment.ChannelEvents.Clear();
            pagedSegment.AttachPagedContent(pack.GetSegmentSource(pagedSegment.Id));
            CompilationRequest request = new()
            {
                Purpose = CompilationPurpose.Playback,
                StartTick = 10,
                EndTick = 30
            };

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult materialized = compiler.CompileFull(
                materializedProject,
                request);
            CanonicalCompiledResult paged = compiler.CompileFull(pagedProject, request);
            CanonicalMidiEvent[] materializedDirect = DirectEvents(materialized);
            CanonicalMidiEvent[] pagedDirect = DirectEvents(paged);

            Assert.Equal(materializedDirect, pagedDirect);
            CanonicalMidiEvent onlyEndpoint = Assert.Single(materializedDirect);
            Assert.Equal(20, onlyEndpoint.Tick);
            Assert.Equal(Midora.Midi.MidiMessageType.NoteOff, onlyEndpoint.Message.MessageType);
            Assert.DoesNotContain(materializedDirect, static value =>
                value.Role == CanonicalEventRole.RangeRestore
                && value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn);
            foreach (CanonicalCompiledResult result in new[] { materialized, paged })
            {
                Assert.Contains(
                    result.QueryEventPages(10, 30, includeStateAtStart: true)
                        .SelectMany(static page => page.Items),
                    static value => value.Tick == 10
                        && value.Role == CanonicalEventRole.RangeRestore
                        && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                        && value.Message.Byte1 == 11
                        && value.Message.Byte2 == 77);
            }
            CanonicalMidiRenderEvent[] pagedRender = paged
                .QueryMidiRenderEventPages(10, 30, includeStateAtStart: true)
                .SelectMany(static page => page.Items)
                .ToArray();
            Assert.DoesNotContain(pagedRender, static value =>
                value.Tick == 10
                && value.Role == CanonicalEventRole.RangeRestore
                && value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                && value.Message.Byte2 != 0);
            Assert.Contains(pagedRender, static value => value.Tick == 10
                && value.Role == CanonicalEventRole.RangeRestore
                && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                && value.Message.Byte1 == 11
                && value.Message.Byte2 == 77);

            materializedProject.Dispose();
            pagedProject.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static (MidoraProject Project, MidiSegment Segment) CreateProject()
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project) { LengthTicks = 30 };
            segment.Notes.Add(new(project)
            {
                StartTick = 0,
                LengthTicks = 20,
                Key = 60,
                NoteOnVelocity = 100,
                NoteOffVelocity = 12,
                NoteOnOrder = 1,
                NoteOffOrder = 2
            });
            segment.ChannelEvents.Add(new(project)
            {
                Tick = 5,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11,
                Data2 = 77,
                Order = 3
            });
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            return (project, segment);
        }

        static CanonicalMidiEvent[] DirectEvents(CanonicalCompiledResult result) =>
            result.QueryEventPages(
                    result.StartTick,
                    result.EndTick,
                    includeStateAtStart: true)
                .SelectMany(static page => page.Items)
                .Where(static value => value.Source.DirectMidiObjectId != default
                    && (value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                        || value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOff))
                .ToArray();
    }

    [Fact]
    public void FullCompileKeepsPagedDirectMidiDeferredAndRangeQueryable()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 384,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 0, 96, 60, 100, 17, 1, 2));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 192, 96, 64, 110, 0, 3, 4));
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult result = compiler.CompileFull(project);

            Assert.True(result.IsConsumable);
            Assert.True(result.HasPagedEvents);
            Assert.Equal(0, result.Events.Length);
            Assert.Equal(2, result.Statistics.NoteOnEventCount);
            Assert.Equal(result.Statistics.EventCount, result.TotalEventCount);
            CanonicalMidiEvent[] firstHalf = result.QueryEventPages(0, 192)
                .SelectMany(value => value.Items)
                .ToArray();
            Assert.Contains(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                && value.Message.Byte1 == 60);
            Assert.Contains(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOff
                && value.Message.Byte1 == 60
                && value.Message.Byte2 == 17);
            Assert.DoesNotContain(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                && value.Message.Byte1 == 64);
            CanonicalMidiRenderEvent[] renderEvents = result
                .QueryMidiRenderEventPages(0, 192)
                .SelectMany(value => value.Items)
                .ToArray();
            Assert.Equal(firstHalf.Length, renderEvents.Length);
            for (int index = 0; index < firstHalf.Length; index++)
            {
                CanonicalMidiEvent canonical = firstHalf[index];
                CanonicalMidiRenderEvent render = renderEvents[index];
                Assert.Equal(canonical.Tick, render.Tick);
                Assert.Equal(canonical.ZeroBasedPort, render.ZeroBasedPort);
                Assert.Equal(canonical.Message, render.Message);
                Assert.Equal(canonical.Source.TrackId, render.TrackId);
                MidoraId expectedMonitoringSource =
                    canonical.Source.Origin == SourceOrigin.MidiChannelRootLifecycle
                        ? canonical.Source.MidiChannelRootId
                        : canonical.Source.TrackId;
                Assert.Equal(expectedMonitoringSource, render.MonitoringSourceId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RangeStartChannelStateRestoresFromADeepEndpointCheckpoint()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state-checkpoint.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 21_000,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            using (PureMidiContentPackWriter writer = new(path))
            {
                for (int tick = 0; tick < 20_000; tick++)
                {
                    writer.AddChannelEvent(segment.Id, new(
                        project.AllocateStableId(),
                        tick,
                        DirectMidiChannelEventKind.ControlChange,
                        11,
                        tick % 128,
                        tick));
                }
                using PureMidiContentPack pack = writer.Complete();
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

                using MidoraCompiler compiler = new();
                CanonicalCompiledResult result = compiler.CompileFull(project);
                CanonicalMidiEvent restored = Assert.Single(
                    result.QueryEventPages(
                            19_500,
                            19_501,
                            includeStateAtStart: true)
                        .SelectMany(page => page.Items),
                    value =>
                        value.Role == CanonicalEventRole.RangeRestore
                        && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                        && value.Message.Byte1 == 11
                        && value.Source.DirectMidiObjectId != default);

                Assert.Equal(19_500, restored.Tick);
                Assert.Equal(19_499 % 128, restored.Message.Byte2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RangeStartRestoresPagedStateFromEndedSiblingTrackWithinActiveRoot()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cross-track-state.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Auto,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack notes = new(project)
            {
                Name = "Notes",
                MidiChannelRootId = root.Id
            };
            MidiSegment noteSegment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 300
            };
            notes.Segments.Add(noteSegment);
            PureMidiTrack state = new(project)
            {
                Name = "State",
                MidiChannelRootId = root.Id
            };
            MidiSegment stateSegment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 100
            };
            state.Segments.Add(stateSegment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(notes);
            project.PureMidiTracks.Add(state);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, notes.Id));
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, state.Id));

            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(noteSegment.Id, new(
                    project.AllocateStableId(), 200, 30, 60, 100, 0, 1, 2));
                writer.AddChannelEvent(stateSegment.Id, new(
                    project.AllocateStableId(),
                    20,
                    DirectMidiChannelEventKind.ControlChange,
                    11,
                    77,
                    1));
                using PureMidiContentPack pack = writer.Complete();
                noteSegment.AttachPagedContent(pack.GetSegmentSource(noteSegment.Id));
                stateSegment.AttachPagedContent(pack.GetSegmentSource(stateSegment.Id));

                using MidoraCompiler compiler = new();
                CanonicalCompiledResult result = compiler.CompileFull(
                    project,
                    new CompilationRequest
                    {
                        Purpose = CompilationPurpose.Playback,
                        StartTick = 150,
                        EndTick = 250
                    });

                Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
                CanonicalMidiEvent restored = Assert.Single(
                    result.QueryEventPages(150, 151, includeStateAtStart: true)
                        .SelectMany(page => page.Items),
                    value => value.Tick == 150
                        && value.Role == CanonicalEventRole.RangeRestore
                        && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                        && value.Message.Byte1 == 11
                        && value.Source.DirectMidiObjectId != default);
                Assert.Equal((byte)77, restored.Message.Byte2);
                Assert.Equal(state.Id, restored.Source.TrackId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
