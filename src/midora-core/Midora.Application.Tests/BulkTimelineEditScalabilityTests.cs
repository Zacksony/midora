using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class BulkTimelineEditScalabilityTests(ITestOutputHelper output)
{
    [Fact]
    public void SixtyThousandPublishedLogicalAndSubVoiceAppendsAvoidPerItemRootPublication()
    {
        const int count = 60_000;
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 1_000_000 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 1,
            Note = 60,
            Velocity = 100
        });
        _ = segment.Notes.CreateQuerySnapshot();
        LogicalNote[] logical = Enumerable.Range(0, count)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index + 1,
                LengthTicks = 1,
                Note = index & 127,
                Velocity = 100
            })
            .ToArray();

        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 1, 60, 100));
        _ = voice.Events.CreateQuerySnapshot();
        TemplateEvent[] template = Enumerable.Range(0, count)
            .Select(index => TemplateEvent.Note(project, index + 1, 1, index & 127, 100))
            .ToArray();

        (TimeSpan logicalTime, long logicalBytes) = Measure(() => segment.Notes.AddRange(logical));
        (TimeSpan templateTime, long templateBytes) = Measure(() => voice.Events.AddRange(template));

        Assert.Equal(count + 1, segment.Notes.Count);
        Assert.Equal(count + 1, voice.Events.Count);
        // This guards cumulative allocation in collection publication. It is
        // intentionally not the detached transaction's separate 64 MiB live
        // resident-memory contract, which requires a production root owner.
        Assert.True(logicalBytes < 256L * 1024 * 1024,
            $"Logical append allocated {logicalBytes / 1048576d:F1} MiB.");
        Assert.True(templateBytes < 256L * 1024 * 1024,
            $"SubVoice append allocated {templateBytes / 1048576d:F1} MiB.");
        Assert.True(logicalTime < TimeSpan.FromSeconds(20),
            $"Logical append took {logicalTime}.");
        Assert.True(templateTime < TimeSpan.FromSeconds(20),
            $"SubVoice append took {templateTime}.");
        output.WriteLine(
            $"count={count}; logical={logicalTime}/{logicalBytes / 1048576d:F1} MiB; "
            + $"subVoice={templateTime}/{templateBytes / 1048576d:F1} MiB");

        static (TimeSpan Time, long Bytes) Measure(Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch elapsed = Stopwatch.StartNew();
            action();
            elapsed.Stop();
            return (elapsed.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void MillionLogicalNotesKeepSparseAndFarRegionSnapshotsBounded()
    {
        using MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 20_000_000 };
        track.Segments.Add(segment);
        const int total = 1_000_000;
        const int selectedCount = 60_000;
        LogicalNote[] notes = Enumerable.Range(0, total)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        LogicalNoteQuerySnapshot original = segment.Notes.CreateQuerySnapshot();

        LogicalNote[] sparse = Enumerable.Range(0, selectedCount)
            .Select(index => notes[index * 16])
            .ToArray();
        Exercise("sparse", sparse);

        Stopwatch collisionPrepare = Stopwatch.StartNew();
        IPreparedProjectEdit collisionEdit = ExactTimelineCollisionPolicy.Wrap(
            project,
            ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segment.Id,
                    sparse.Select(static value => value.Id).ToArray(),
                    startDelta: 0,
                    endDelta: 1)
                .Prepare(project));
        collisionPrepare.Stop();
        Stopwatch collisionApply = Stopwatch.StartNew();
        collisionEdit.Apply(project);
        collisionApply.Stop();
        Stopwatch collisionUndo = Stopwatch.StartNew();
        collisionEdit.Undo(project);
        collisionUndo.Stop();
        Assert.Equal(total, segment.Notes.Count);
        Assert.True(collisionApply.Elapsed < TimeSpan.FromSeconds(20),
            $"Sparse exact-key collision Apply took {collisionApply.Elapsed}.");
        output.WriteLine(
            $"million/sparseCollision: prepare={collisionPrepare.Elapsed}; "
            + $"apply={collisionApply.Elapsed}; undo={collisionUndo.Elapsed}");

        LogicalNote[] far = notes.AsSpan(total - selectedCount, selectedCount).ToArray();
        Exercise("far", far);

        Assert.Equal(2, Assert.Single(original.QueryValues(0, 1, 0, 0)).LengthTicks);

        void Exercise(string label, LogicalNote[] selected)
        {
            (TimeSpan apply, TimeSpan snapshot, long allocation, LogicalNoteQuerySnapshot applied) =
                MutateAndSnapshot(selected, lengthDelta: 1);
            Assert.Equal(3, applied.ResolveByIds([selected[0].Id]).Single().LengthTicks);

            (TimeSpan undo, TimeSpan undoSnapshot, long undoAllocation, LogicalNoteQuerySnapshot undone) =
                MutateAndSnapshot(selected, lengthDelta: -1);
            Assert.Equal(2, undone.ResolveByIds([selected[0].Id]).Single().LengthTicks);

            (TimeSpan redo, TimeSpan redoSnapshot, long redoAllocation, _) =
                MutateAndSnapshot(selected, lengthDelta: 1);
            (TimeSpan finalUndo, TimeSpan finalSnapshot, long finalAllocation, _) =
                MutateAndSnapshot(selected, lengthDelta: -1);

            Assert.True(snapshot < TimeSpan.FromMilliseconds(250),
                $"{label} snapshot took {snapshot}.");
            Assert.True(undoSnapshot < TimeSpan.FromMilliseconds(250),
                $"{label} undo snapshot took {undoSnapshot}.");
            Assert.True(allocation < 1_048_576, $"{label} snapshot allocated {allocation} bytes.");
            Assert.True(undoAllocation < 1_048_576,
                $"{label} undo snapshot allocated {undoAllocation} bytes.");
            output.WriteLine(
                $"million/{label}: apply={apply}; snapshot={snapshot}; snapshotBytes={allocation}; "
                + $"undo={undo}; undoSnapshot={undoSnapshot}; undoSnapshotBytes={undoAllocation}; "
                + $"redo={redo}; redoSnapshot={redoSnapshot}; redoSnapshotBytes={redoAllocation}; "
                + $"finalUndo={finalUndo}; finalSnapshot={finalSnapshot}; "
                + $"finalSnapshotBytes={finalAllocation}");
        }

        (TimeSpan Mutation, TimeSpan Snapshot, long Allocation, LogicalNoteQuerySnapshot Capture)
            MutateAndSnapshot(LogicalNote[] selected, long lengthDelta)
        {
            Stopwatch mutation = Stopwatch.StartNew();
            using (segment.Notes.BeginBatchChange())
            {
                foreach (LogicalNote note in selected)
                {
                    note.SetValues(
                        note.StartTick,
                        checked(note.LengthTicks + lengthDelta),
                        note.Note,
                        note.Velocity);
                }
            }
            mutation.Stop();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch snapshot = Stopwatch.StartNew();
            LogicalNoteQuerySnapshot result = segment.Notes.CreateQuerySnapshot();
            snapshot.Stop();
            long allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            return (mutation.Elapsed, snapshot.Elapsed, allocation, result);
        }
    }

    [Fact]
    public void SixtyThousandDirectNotesUseLinearColdAndWarmResizeUndoRedo()
    {
        using MidoraProject project = new(192);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

        const int count = 60_000;
        DirectMidiNote[] notes = Enumerable.Range(0, count)
            .Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Key = index % 128,
                NoteOnVelocity = 100,
                NoteOffVelocity = 64,
                NoteOnOrder = index * 2L,
                NoteOffOrder = index * 2L + 1
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        MidoraId[] ids = notes.Select(static note => note.Id).ToArray();

        Stopwatch prepare = Stopwatch.StartNew();
        IPreparedProjectEdit source = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                segment.Id,
                ids,
                startDelta: 1,
                endDelta: 0,
                minimumLengthTicks: 1)
            .Prepare(project);
        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(project, source);
        prepare.Stop();

        Stopwatch coldApply = Stopwatch.StartNew();
        edit.Apply(project);
        coldApply.Stop();
        Assert.Equal(1, track.Segments[0].Notes[0].StartTick);
        Assert.Equal(1, track.Segments[0].Notes[^1].StartTick - ((count - 1) * 4L));

        Stopwatch coldUndo = Stopwatch.StartNew();
        edit.Undo(project);
        coldUndo.Stop();
        Assert.Equal(0, track.Segments[0].Notes[0].StartTick);

        Stopwatch warmRedo = Stopwatch.StartNew();
        edit.Apply(project);
        warmRedo.Stop();
        Assert.Equal(1, track.Segments[0].Notes[0].StartTick);

        Stopwatch warmUndo = Stopwatch.StartNew();
        edit.Undo(project);
        warmUndo.Stop();
        Assert.Equal(0, track.Segments[0].Notes[0].StartTick);
        Assert.Equal(count, track.Segments[0].Notes.Count);

        output.WriteLine(
            $"count={count}; prepare={prepare.Elapsed}; coldApply={coldApply.Elapsed}; "
            + $"coldUndo={coldUndo.Elapsed}; warmRedo={warmRedo.Elapsed}; warmUndo={warmUndo.Elapsed}; "
            + $"managedMiB={GC.GetTotalMemory(false) / 1048576d:F1}");
    }

    [Fact]
    public void RandomizedDirectResizeMatchesScalarOracleAcrossUndoRedo()
    {
        using MidoraProject project = new(192);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "Track", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

        Random random = new(0x4d494449);
        DirectMidiNote[] notes = Enumerable.Range(0, 4_096)
            .Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 16L,
                LengthTicks = random.Next(2, 16),
                Key = random.Next(128),
                NoteOnVelocity = random.Next(1, 128),
                NoteOffVelocity = random.Next(128),
                NoteOnOrder = index * 2L,
                NoteOffOrder = index * 2L + 1
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        DirectMidiNote[] selected = notes.Where((_, index) => index % 3 != 0).ToArray();
        Dictionary<MidoraId, (long Start, long Length)> before = selected.ToDictionary(
            static note => note.Id,
            static note => (note.StartTick, note.LengthTicks));

        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(
            project,
            ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segment.Id,
                    selected.Select(static note => note.Id).ToArray(),
                    startDelta: 0,
                    endDelta: 7,
                    minimumLengthTicks: 1)
                .Prepare(project));
        edit.Apply(project);
        foreach (DirectMidiNote note in selected)
            Assert.Equal(before[note.Id].Length + 7, note.LengthTicks);
        edit.Undo(project);
        foreach (DirectMidiNote note in selected)
            Assert.Equal(before[note.Id], (note.StartTick, note.LengthTicks));
        edit.Apply(project);
        foreach (DirectMidiNote note in selected)
            Assert.Equal(before[note.Id].Length + 7, note.LengthTicks);
        edit.Undo(project);
    }

    [Fact]
    public void SixtyThousandLogicalAndSubVoiceNotesKeepColdSnapshotsLinearAcrossResizeUndoRedo()
    {
        using MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 1_000_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);

        const int count = 60_000;
        LogicalNote[] logical = Enumerable.Range(0, count)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        TemplateEvent[] template = Enumerable.Range(0, count)
            .Select(index => TemplateEvent.Note(
                project,
                index * 4L,
                lengthTicks: 2,
                note: index % 128,
                velocity: 100))
            .ToArray();
        segment.Notes.AddRange(logical);
        voice.Events.AddRange(template);

        TimeSpan logicalElapsed = Exercise(
            ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                segment.Id,
                logical.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1),
            () => CurrentSegment().Notes.CreateQuerySnapshot().GetRangeFingerprint(0, 1_000_000),
            () => CurrentSegment().Notes.CreateQuerySnapshot().GetByOrdinal(0).LengthTicks,
            expectedApplied: 3,
            expectedUndone: 2);
        TimeSpan templateElapsed = Exercise(
            ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                instrument.Id,
                voice.Id,
                template.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1),
            () => CurrentVoice().Events.CreateQuerySnapshot().GetNoteRangeFingerprint(0, 1_000_000),
            () => CurrentVoice().Events.CreateQuerySnapshot().GetByOrdinal(0).LengthTicks,
            expectedApplied: 3,
            expectedUndone: 2);

        Assert.Equal(count, segment.Notes.Count);
        Assert.Equal(count, voice.Events.Count);
        Assert.True(logicalElapsed < TimeSpan.FromSeconds(20), $"Logical resize cycle took {logicalElapsed}.");
        Assert.True(templateElapsed < TimeSpan.FromSeconds(20), $"SubVoice resize cycle took {templateElapsed}.");
        output.WriteLine(
            $"count={count}; logicalResizeSnapshotCycle={logicalElapsed}; "
            + $"subVoiceResizeSnapshotCycle={templateElapsed}; "
            + $"managedMiB={GC.GetTotalMemory(false) / 1048576d:F1}");

        Segment CurrentSegment() => track.Segments.Single(value => value.Id == segment.Id);
        SubVoice CurrentVoice() => instrument.SubVoices.Single(value => value.Id == voice.Id);

        TimeSpan Exercise(
            IProjectEditCommand command,
            Func<ulong> snapshot,
            Func<long> currentLength,
            long expectedApplied,
            long expectedUndone)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            IPreparedProjectEdit edit = command.Prepare(project);
            edit.Apply(project);
            _ = snapshot();
            Assert.Equal(expectedApplied, currentLength());
            edit.Undo(project);
            _ = snapshot();
            Assert.Equal(expectedUndone, currentLength());
            edit.Apply(project);
            _ = snapshot();
            Assert.Equal(expectedApplied, currentLength());
            edit.Undo(project);
            _ = snapshot();
            Assert.Equal(expectedUndone, currentLength());
            elapsed.Stop();
            return elapsed.Elapsed;
        }
    }

    [Fact]
    public void SixtyThousandDirectAndOpaquePointsUseFrozenLocalOverlaySnapshots()
    {
        using MidoraProject project = new(192);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

        const int count = 60_000;
        DirectMidiChannelEvent[] channel = Enumerable.Range(0, count)
            .Select(index => new DirectMidiChannelEvent(project)
            {
                Tick = index * 4L,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index % 120,
                Data2 = index % 128,
                Order = index
            })
            .ToArray();
        OpaqueMidiEvent[] opaque = Enumerable.Range(0, count)
            .Select(index => new OpaqueMidiEvent(project)
            {
                Tick = index * 4L,
                Kind = OpaqueMidiEventKind.Meta,
                MetaType = 0x7f,
                Payload = [(byte)(index & 0xff)],
                Order = index
            })
            .ToArray();
        segment.ChannelEvents.AddRange(channel);
        segment.OpaqueEvents.AddRange(opaque);

        Stopwatch channelTime = Stopwatch.StartNew();
        IPreparedProjectEdit channelEdit = ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
                segment.Id,
                channel.Select(static value => value.Id).ToArray(),
                tickDelta: 1,
                data1Delta: 0,
                data2Delta: 0,
                duplicate: false)
            .Prepare(project);
        channelEdit.Apply(project);
        DirectMidiChannelEventQuerySnapshot channelSnapshot = track.Segments[0].ChannelEvents.CreateQuerySnapshot();
        Assert.Single(channelSnapshot.QueryValues(1, 2));
        channelEdit.Undo(project);
        _ = track.Segments[0].ChannelEvents.CreateQuerySnapshot();
        channelTime.Stop();

        Stopwatch opaqueTime = Stopwatch.StartNew();
        IPreparedProjectEdit opaqueEdit = ProjectDomainEditCommands.AdjustOpaqueMidiEvents(
                segment.Id,
                opaque.Select(static value => value.Id).ToArray(),
                tickDelta: 1,
                duplicate: false)
            .Prepare(project);
        opaqueEdit.Apply(project);
        OpaqueMidiEventQuerySnapshot opaqueSnapshot = track.Segments[0].OpaqueEvents.CreateQuerySnapshot();
        Assert.Single(opaqueSnapshot.QueryValues(1, 2));
        opaqueEdit.Undo(project);
        _ = track.Segments[0].OpaqueEvents.CreateQuerySnapshot();
        opaqueTime.Stop();

        Assert.Equal(0, channel[0].Tick);
        Assert.Equal(0, opaque[0].Tick);
        Assert.True(channelTime.Elapsed < TimeSpan.FromSeconds(20), $"Channel Event cycle took {channelTime.Elapsed}.");
        Assert.True(opaqueTime.Elapsed < TimeSpan.FromSeconds(20), $"Opaque Event cycle took {opaqueTime.Elapsed}.");
        output.WriteLine(
            $"count={count}; channelMoveSnapshotUndo={channelTime.Elapsed}; "
            + $"opaqueMoveSnapshotUndo={opaqueTime.Elapsed}; "
            + $"managedMiB={GC.GetTotalMemory(false) / 1048576d:F1}");
    }

    [Fact]
    public void SixtyThousandColdContentPackNotesUseCompactResizeUndoOverlay()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-cold-bulk-edit-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cold.mpk");
        try
        {
            using MidoraProject project = new(192);
            MidiChannelRoot root = new(project) { Name = "Root" };
            PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
            MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

            const int count = 60_000;
            MidoraId[] ids = new MidoraId[count];
            using PureMidiContentPackWriter writer = new(path);
            for (int index = 0; index < count; index++)
            {
                MidoraId id = project.AllocateStableId();
                ids[index] = id;
                writer.AddNote(segment.Id, new(
                    id,
                    index * 4L,
                    2,
                    index % 128,
                    100,
                    64,
                    index * 2L,
                    index * 2L + 1));
            }
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            Assert.Equal(0, pack.PageCacheMissCount);

            Stopwatch elapsed = Stopwatch.StartNew();
            IPreparedProjectEdit edit = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segment.Id,
                    ids,
                    startDelta: 0,
                    endDelta: 1)
                .Prepare(project);
            edit.Apply(project);
            DirectMidiNoteQuerySnapshot applied = track.Segments[0].Notes.CreateQuerySnapshot();
            Assert.Equal(3, Assert.Single(applied.QueryValues(0, 1, 0, 0)).LengthTicks);
            edit.Undo(project);
            DirectMidiNoteQuerySnapshot undone = track.Segments[0].Notes.CreateQuerySnapshot();
            Assert.Equal(2, Assert.Single(undone.QueryValues(0, 1, 0, 0)).LengthTicks);
            edit.Apply(project);
            _ = track.Segments[0].Notes.CreateQuerySnapshot();
            edit.Undo(project);
            _ = track.Segments[0].Notes.CreateQuerySnapshot();
            elapsed.Stop();

            Assert.Equal(count, segment.Notes.Count);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"Cold Content Pack cycle took {elapsed.Elapsed}.");
            output.WriteLine(
                $"count={count}; coldContentPackResizeSnapshotCycle={elapsed.Elapsed}; "
                + $"packMisses={pack.PageCacheMissCount}; "
                + $"managedMiB={GC.GetTotalMemory(false) / 1048576d:F1}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BulkRemovalTokensRemainReusableAcrossApplyUndoRedoCycles()
    {
        using MidoraProject project = new(192);

        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack midiTrack = new(project)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment midiSegment = new(project) { LengthTicks = 200_000 };
        midiTrack.Segments.Add(midiSegment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(midiTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midiTrack.Id));

        DirectMidiNote[] directNotes = Enumerable.Range(0, 8_193)
            .Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Key = index % 128,
                NoteOnVelocity = 100,
                NoteOffVelocity = 64,
                NoteOnOrder = index * 3L,
                NoteOffOrder = index * 3L + 1
            })
            .ToArray();
        midiSegment.Notes.AddRange(directNotes);
        DirectMidiChannelEvent[] directEvents = Enumerable.Range(0, 4_097)
            .Select(index => new DirectMidiChannelEvent(project)
            {
                Tick = index * 8L,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index % 120,
                Data2 = index % 128,
                Order = index * 3L + 2
            })
            .ToArray();
        midiSegment.ChannelEvents.AddRange(directEvents);

        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 200_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent[] templateEvents = Enumerable.Range(0, 8_193)
            .Select(index => TemplateEvent.Note(
                project,
                index * 4L,
                lengthTicks: 2,
                note: index % 128,
                velocity: 100))
            .ToArray();
        voice.Events.AddRange(templateEvents);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project) { LengthTicks = 200_000 };
        LogicalNote[] logicalNotes = Enumerable.Range(0, 8_193)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        logicalSegment.Notes.AddRange(logicalNotes);
        logicalTrack.Segments.Add(logicalSegment);

        DirectMidiNote[] removedDirectNotes = directNotes.Where((_, index) => index % 3 == 0).ToArray();
        DirectMidiChannelEvent[] removedDirectEvents = directEvents.Where((_, index) => index % 3 == 1).ToArray();
        LogicalNote[] removedLogicalNotes = logicalNotes.Where((_, index) => index % 3 == 2).ToArray();
        TemplateEvent[] removedTemplateEvents = templateEvents.Where((_, index) => index % 4 == 0).ToArray();

        VerifyCycle(
            ProjectDomainEditCommands.DeleteDirectMidiNotes(
                midiSegment.Id,
                removedDirectNotes.Select(static value => value.Id).ToArray()),
            () => project.PureMidiTracks.Single(value => value.Id == midiTrack.Id).Segments.Single(value => value.Id == midiSegment.Id).Notes.ToArray(),
            directNotes,
            removedDirectNotes);
        VerifyCycle(
            ProjectDomainEditCommands.DeleteDirectMidiEvents(
                midiSegment.Id,
                removedDirectEvents.Select(static value => value.Id).ToArray()),
            () => project.PureMidiTracks.Single(value => value.Id == midiTrack.Id).Segments.Single(value => value.Id == midiSegment.Id).ChannelEvents.ToArray(),
            directEvents,
            removedDirectEvents);
        VerifyCycle(
            ProjectDomainEditCommands.DeleteLogicalNotes(
                logicalSegment.Id,
                removedLogicalNotes.Select(static value => value.Id).ToArray()),
            () => project.Tracks.Single(value => value.Id == logicalTrack.Id).Segments.Single(value => value.Id == logicalSegment.Id).Notes.ToArray(),
            logicalNotes,
            removedLogicalNotes);
        VerifyCycle(
            ProjectDomainEditCommands.DeleteTemplateEvents(
                instrument.Id,
                voice.Id,
                removedTemplateEvents.Select(static value => value.Id).ToArray()),
            () => project.EventInstruments.Single(value => value.Id == instrument.Id).SubVoices.Single(value => value.Id == voice.Id).Events.ToArray(),
            templateEvents,
            removedTemplateEvents);

        void VerifyCycle<T>(
            IProjectEditCommand command,
            Func<T[]> read,
            T[] original,
            T[] removed)
            where T : class
        {
            IPreparedProjectEdit edit = command.Prepare(project);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                edit.Apply(project);
                HashSet<T> removedSet = new(removed, ReferenceEqualityComparer.Instance);
                Assert.Equal(
                    original.Where(value => !removedSet.Contains(value)).Select(Scalar),
                    read().Select(Scalar));
                edit.Undo(project);
                Assert.Equal(original.Select(Scalar), read().Select(Scalar));
            }
        }
        static object Scalar(object value) => value switch
        {
            DirectMidiNote note => new DirectMidiNoteValue(note.Id, note.StartTick, note.LengthTicks, note.Key,
                note.NoteOnVelocity, note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder),
            DirectMidiChannelEvent point => new DirectMidiChannelEventValue(point.Id, point.Tick, point.Kind, point.Data1, point.Data2, point.Order),
            LogicalNote note => new LogicalNoteSnapshotValue(note.Id, note.StartTick, note.LengthTicks, note.Note, note.Velocity),
            TemplateEvent item => new TemplateEventSnapshotValue(item.Id, item.Kind, item.Tick, item.LengthTicks,
                item.Number, item.Value, item.SecondaryValue, item.HasBankMsb, item.HasBankLsb, item.FollowPitchDelta),
            _ => throw new InvalidOperationException("Unexpected timeline object type.")
        };
    }
}
