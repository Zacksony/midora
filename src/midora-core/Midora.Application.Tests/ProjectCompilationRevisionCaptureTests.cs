using System.Diagnostics;
using System.Collections.Immutable;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectCompilationRevisionCaptureTests
{
    [Fact]
    public void SixtyThousandPureMidiOverlayCaptureDoesNotCloneFormalValues()
    {
        const int noteCount = 60_000;
        MidoraProject source = new(480);
        MidiChannelRoot root = new(source)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(source)
        {
            Name = "Dense MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(source)
        {
            LengthTicks = noteCount
        };
        segment.Notes.AddRange(CreateDirectNotes(source, noteCount));
        track.Segments.Add(segment);
        source.MidiChannelRoots.Add(root);
        source.PureMidiTracks.Add(track);
        source.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

        MidoraProject mirror = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            source.Metadata.CreatedAtUtc);
        ProjectChangeSet changes = new();
        changes.PureMidiTrackIds.Add(track.Id);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProjectCompilationSnapshot.RevisionCapture capture =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        ProjectCompilationSnapshot.CapturedMidiSegment capturedSegment = Assert.Single(
            capture.PureMidiTracks.Replacements[track.Id].Segments);
        Console.WriteLine(
            $"60k Pure overlay gate capture: {stopwatch.Elapsed.TotalMilliseconds:F2} ms; "
            + $"allocated={allocated:N0} bytes.");

        Assert.Equal(noteCount, capturedSegment.Notes.Count);
        Assert.Equal(noteCount, capturedSegment.Notes.Added.Count);
        Assert.True(
            allocated < 1_000_000,
            $"Pure MIDI gate capture allocated {allocated:N0} bytes for {noteCount:N0} overlay notes.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Pure MIDI gate capture took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");

        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        MidoraProject materialized = ProjectCompilationSnapshot.MaterializeRevision(capture);
        stopwatch.Stop();
        long materializeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(
            $"60k Pure overlay compile-mirror materialization: "
            + $"{stopwatch.Elapsed.TotalMilliseconds:F2} ms; "
            + $"allocated={materializeAllocated:N0} bytes.");
        DirectMidiNoteCollection notes = Assert.Single(
            Assert.Single(materialized.PureMidiTracks).Segments).Notes;
        Assert.Equal(noteCount, notes.Count);
        Assert.Equal(0, notes[0].StartTick);
        Assert.Equal(noteCount - 1, notes[^1].StartTick);
        Assert.True(
            materializeAllocated < 48_000_000,
            $"Pure MIDI compile-mirror materialization allocated "
            + $"{materializeAllocated:N0} bytes for {noteCount:N0} overlay notes.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Pure MIDI compile-mirror materialization took "
            + $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void MillionValueFormalRootCaptureIsConstantSizeAndOldRootRemainsImmutable()
    {
        const int valueCount = 1_000_000;
        const int changedIndex = 600_000;
        var values = new DirectMidiNoteValue[valueCount];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = new(
                new MidoraId(index + 1L),
                index,
                1,
                index & 0x7f,
                100,
                0,
                index * 2L,
                index * 2L + 1);
        }
        PersistentFormalValueSequence<DirectMidiNoteValue> original =
            PersistentFormalValueSequence<DirectMidiNoteValue>.Create(
                values,
                static value => value);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        var captured = new DirectMidiNoteFormalSequenceSnapshot(
            null,
            false,
            ImmutableHashSet<MidoraId>.Empty,
            ImmutableDictionary<MidoraId, DirectMidiNoteValue>.Empty,
            original,
            valueCount,
            1);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(
            $"1m formal-root reference capture: {stopwatch.Elapsed.TotalMilliseconds:F3} ms; "
            + $"allocated={allocated:N0} bytes.");

        Assert.Same(original, captured.Added);
        Assert.True(
            allocated < 64_000,
            $"A million-value formal-root capture allocated {allocated:N0} bytes.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"A million-value formal-root capture took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");

        DirectMidiNoteValue changed = values[changedIndex] with { Key = 127 };
        PersistentFormalValueSequence<DirectMidiNoteValue> edited = original.ReplaceBatch(
            new Dictionary<int, DirectMidiNoteValue> { [changedIndex] = changed });
        Assert.Equal(
            values[changedIndex],
            original.Enumerate().ElementAt(changedIndex));
        Assert.Equal(changed, edited.Enumerate().ElementAt(changedIndex));

        const int removalFirst = 470_000;
        const int removalCount = 60_000;
        int[] removalIndices = Enumerable.Range(removalFirst, removalCount).ToArray();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        PersistentFormalValueSequence<DirectMidiNoteValue> removed =
            original.RemoveIndices(removalIndices);
        stopwatch.Stop();
        long removalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(
            $"1m root / 60k structural remove: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; "
            + $"allocated={removalAllocated:N0} bytes.");
        Assert.Equal(valueCount - removalCount, removed.Count);
        Assert.True(removalAllocated < 24_000_000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));

        KeyValuePair<int, DirectMidiNoteValue>[] insertions = removalIndices
            .Select(index => new KeyValuePair<int, DirectMidiNoteValue>(index, values[index]))
            .ToArray();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        PersistentFormalValueSequence<DirectMidiNoteValue> restored =
            removed.InsertAtFinalIndices(insertions);
        stopwatch.Stop();
        long restoreAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(
            $"940k root / 60k structural restore: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; "
            + $"allocated={restoreAllocated:N0} bytes.");
        Assert.Equal(valueCount, restored.Count);
        Assert.True(restoreAllocated < 24_000_000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(values, restored.Enumerate());
    }

    [Fact]
    public void PureMidiAddRangeValidationIsFailureAtomic()
    {
        MidoraProject project = new(480);
        MidiSegment segment = new(project);
        DirectMidiNote existingNote = new(project) { StartTick = 1, LengthTicks = 1, Key = 60 };
        DirectMidiChannelEvent existingEvent = new(project)
        {
            Tick = 1,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 2
        };
        OpaqueMidiEvent existingOpaque = new(project)
        {
            Tick = 1,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 1,
            Payload = [1]
        };
        segment.Notes.Add(existingNote);
        segment.ChannelEvents.Add(existingEvent);
        segment.OpaqueEvents.Add(existingOpaque);
        DirectMidiNote pendingNote = new(project) { StartTick = 2, LengthTicks = 1, Key = 61 };
        DirectMidiChannelEvent pendingEvent = new(project)
        {
            Tick = 2,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 2,
            Data2 = 3
        };
        OpaqueMidiEvent pendingOpaque = new(project)
        {
            Tick = 2,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 2,
            Payload = [2]
        };

        Assert.Throws<InvalidOperationException>(() =>
            segment.Notes.AddRange([pendingNote, existingNote]));
        Assert.Throws<InvalidOperationException>(() =>
            segment.ChannelEvents.AddRange([pendingEvent, existingEvent]));
        Assert.Throws<InvalidOperationException>(() =>
            segment.OpaqueEvents.AddRange([pendingOpaque, existingOpaque]));

        Assert.Equal([existingNote.Id], segment.Notes.Select(static value => value.Id));
        Assert.Equal([existingEvent.Id], segment.ChannelEvents.Select(static value => value.Id));
        Assert.Equal([existingOpaque.Id], segment.OpaqueEvents.Select(static value => value.Id));
        Assert.Equal(1, segment.Notes.CreateFormalSequenceSnapshot().Count);
        Assert.Equal(1, segment.ChannelEvents.CreateFormalSequenceSnapshot().Count);
        Assert.Equal(1, segment.OpaqueEvents.CreateFormalSequenceSnapshot().Count);
    }

    [Fact]
    public void PureMidiFormalRootsSurviveInterleavedUndoRedoAndPendingPointEdits()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project) { LengthTicks = 8_192 };
        DirectMidiNote[] notes = Enumerable.Range(0, 1_024)
            .Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 1,
                Key = index & 0x7f,
                NoteOnVelocity = 100,
                NoteOffVelocity = 0,
                NoteOnOrder = index * 4L,
                NoteOffOrder = index * 4L + 1
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        MidoraId[] originalOrder = notes.Select(static value => value.Id).ToArray();
        DirectMidiNote[] firstRemoval = [notes[5], notes[257], notes[512], notes[900]];
        DirectMidiNote[] secondRemoval = [notes[0], notes[256], notes[513], notes[1_023]];

        Action restoreFirst = segment.Notes.RemoveRangeWithUndo(firstRemoval);
        DirectMidiNote transient = new(project)
        {
            StartTick = 7_000,
            LengthTicks = 1,
            Key = 1,
            NoteOnVelocity = 1
        };
        segment.Notes.Insert(333, transient);
        Assert.True(segment.Notes.Remove(transient));
        Action restoreSecond = segment.Notes.RemoveRangeWithUndo(secondRemoval);
        MidoraId[] twiceRemovedOrder = segment.Notes
            .Select(static value => value.Id)
            .ToArray();
        AssertFormalNoteOrder(segment);

        // Project history is LIFO: undo the second edit, then the first. Redo
        // creates fresh removal tokens in the same order and must publish the
        // same immutable formal root without relying on a full tail rebuild.
        restoreSecond();
        restoreFirst();
        Assert.Equal(originalOrder, segment.Notes.Select(static value => value.Id));
        AssertFormalNoteOrder(segment);

        Action restoreFirstRedo = segment.Notes.RemoveRangeWithUndo(firstRemoval);
        Action restoreSecondRedo = segment.Notes.RemoveRangeWithUndo(secondRemoval);
        Assert.Equal(twiceRemovedOrder, segment.Notes.Select(static value => value.Id));
        AssertFormalNoteOrder(segment);
        restoreSecondRedo();
        restoreFirstRedo();
        Assert.Equal(originalOrder, segment.Notes.Select(static value => value.Id));
        AssertFormalNoteOrder(segment);

        DirectMidiNote insertedNote = new(project)
        {
            StartTick = 7_100,
            LengthTicks = 1,
            Key = 2,
            NoteOnVelocity = 2
        };
        using (segment.Notes.BeginBatchChange([]))
        {
            segment.Notes.Insert(700, insertedNote);
            insertedNote.Key = 119;
        }
        Assert.Equal(
            119,
            segment.Notes.CreateFormalSequenceSnapshot().Added
                .Enumerate()
                .Single(value => value.Id == insertedNote.Id)
                .Key);

        DirectMidiChannelEvent insertedEvent = new(project)
        {
            Tick = 100,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 1,
            Order = 1
        };
        using (segment.ChannelEvents.BeginBatchChange([]))
        {
            segment.ChannelEvents.Insert(0, insertedEvent);
            insertedEvent.Data2 = 117;
        }
        Assert.Equal(
            117,
            segment.ChannelEvents.CreateFormalSequenceSnapshot().Added
                .Enumerate()
                .Single(value => value.Id == insertedEvent.Id)
                .Data2);

        OpaqueMidiEvent insertedOpaque = new(project)
        {
            Tick = 100,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 1,
            Payload = [1],
            Order = 2
        };
        using (segment.OpaqueEvents.BeginBatchChange([]))
        {
            segment.OpaqueEvents.Insert(0, insertedOpaque);
            insertedOpaque.Payload = [9, 8, 7];
        }
        Assert.Equal(
            new byte[] { 9, 8, 7 },
            segment.OpaqueEvents.CreateFormalSequenceSnapshot().Added
                .Enumerate()
                .Single(value => value.Id == insertedOpaque.Id)
                .Payload.ToArray());
    }

    [Fact]
    public void PureMidiCapturePreservesOldFormalOrderAndOpaquePayload()
    {
        MidoraProject source = new(480);
        MidiChannelRoot root = new(source)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(source)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(source) { LengthTicks = 480 };
        DirectMidiNote first = new(source)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 90,
            NoteOffVelocity = 12,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        };
        DirectMidiNote second = new(source)
        {
            StartTick = 40,
            LengthTicks = 30,
            Key = 64,
            NoteOnVelocity = 100,
            NoteOffVelocity = 13,
            NoteOnOrder = 3,
            NoteOffOrder = 4
        };
        DirectMidiChannelEvent controller = new(source)
        {
            Tick = 5,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80,
            Order = 5
        };
        OpaqueMidiEvent opaque = new(source)
        {
            Tick = 6,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0xf0, 0x01, 0xf7],
            Order = 6
        };
        segment.Notes.AddRange([first, second]);
        segment.ChannelEvents.Add(controller);
        segment.OpaqueEvents.Add(opaque);
        track.Segments.Add(segment);
        source.MidiChannelRoots.Add(root);
        source.PureMidiTracks.Add(track);

        MidoraProject mirror = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            source.Metadata.CreatedAtUtc);
        ProjectChangeSet changes = new();
        changes.PureMidiTrackIds.Add(track.Id);
        ProjectCompilationSnapshot.RevisionCapture capture =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);

        segment.Notes.Insert(0, new DirectMidiNote(source)
        {
            StartTick = 1,
            LengthTicks = 1,
            Key = 1,
            NoteOnVelocity = 1
        });
        second.Key = 99;
        Assert.True(segment.Notes.Remove(first));
        controller.Data2 = 1;
        opaque.Payload[1] = 0x7f;

        MidoraProject materialized = ProjectCompilationSnapshot.MaterializeRevision(capture);
        MidiSegment capturedSegment = Assert.Single(
            Assert.Single(materialized.PureMidiTracks).Segments);
        Assert.Equal([first.Id, second.Id], capturedSegment.Notes.Select(static value => value.Id));
        Assert.Equal(64, capturedSegment.Notes[1].Key);
        Assert.Equal(80, Assert.Single(capturedSegment.ChannelEvents).Data2);
        Assert.Equal(
            new byte[] { 0xf0, 0x01, 0xf7 },
            Assert.Single(capturedSegment.OpaqueEvents).Payload);
    }

    [Fact]
    public void MillionLogicalNotesCaptureOnlyFreezesPageMetadata()
    {
        const int noteCount = 1_000_000;
        MidoraProject source = new(480);
        LogicalTrack track = new(source)
        {
            Name = "Million-note track"
        };
        Segment segment = new(source)
        {
            ProjectStartTick = 0,
            LengthTicks = noteCount,
            ContentOffsetTick = 0
        };
        track.Segments.Add(segment);
        source.Tracks.Add(track);
        segment.Notes.AddRange(CreateNotes(source, noteCount));

        // Publish the initial immutable pages once. A subsequent single-page edit
        // must not make source capture enumerate the other 999,999 objects.
        LogicalNoteQuerySnapshot baseline = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(noteCount, baseline.Count);
        segment.Notes[noteCount / 2].Velocity = 101;

        MidoraProject mirror = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            source.Metadata.CreatedAtUtc);
        mirror.Tracks.Add(new LogicalTrack(mirror, track.Id)
        {
            Name = track.Name
        });
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProjectCompilationSnapshot.RevisionCapture capture =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);
        stopwatch.Stop();
        Console.WriteLine(
            $"Million-note immutable capture: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; pages={segment.Notes.PageCount}.");

        Assert.Contains(track.Id, capture.LogicalTracks.Replacements.Keys);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Freezing one changed page in a million-note owner took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void CapturedLogicalAndSubVoicePagesRemainImmutableAfterSourceChanges()
    {
        MidoraProject source = new(480);
        LogicalTrack track = new(source) { Name = "Logical" };
        Segment segment = new(source)
        {
            ProjectStartTick = 0,
            LengthTicks = 480,
            ContentOffsetTick = 0
        };
        LogicalNote note = new(source)
        {
            StartTick = 24,
            LengthTicks = 120,
            Note = 60,
            Velocity = 90
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        source.Tracks.Add(track);

        EventInstrument instrument = new(source)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(source) { Name = "Voice" };
        TemplateEvent templateEvent = TemplateEvent.Note(source, 0, 120, 60, 80);
        voice.Events.Add(templateEvent);
        ValueCurve curve = new(source) { Target = MidiValueTarget.ControlChange(1) };
        CurvePoint point = new(source, 0, 0.25, CurveInterpolation.Step);
        curve.Points.Add(point);
        voice.Curves.Add(curve);
        instrument.SubVoices.Add(voice);
        source.EventInstruments.Add(instrument);

        MidoraProject mirror = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            source.Metadata.CreatedAtUtc);
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);
        changes.EventInstrumentIds.Add(instrument.Id);
        ProjectCompilationSnapshot.RevisionCapture capture =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);

        note.Velocity = 30;
        templateEvent.Value = 40;
        curve.Points[0] = new CurvePoint(
            source,
            point.Id,
            point.Tick,
            0.75,
            point.Interpolation);

        MidoraProject materialized = ProjectCompilationSnapshot.MaterializeRevision(capture);

        Assert.Equal(90, Assert.Single(Assert.Single(materialized.Tracks).Segments).Notes[0].Velocity);
        SubVoice materializedVoice = Assert.Single(
            Assert.Single(materialized.EventInstruments).SubVoices);
        Assert.Equal(80, Assert.Single(materializedVoice.Events).Value);
        Assert.Equal(0.25, Assert.Single(Assert.Single(materializedVoice.Curves).Points).Value);
    }

    [Fact]
    public void CanceledMaterializationDoesNotPartiallyCommitTheCompilerMirror()
    {
        MidoraProject source = new(480);
        LogicalTrack logicalTrack = new(source) { Name = "Logical" };
        Segment logicalSegment = new(source) { LengthTicks = 480 };
        LogicalNote logicalNote = new(source)
        {
            StartTick = 0,
            LengthTicks = 120,
            Note = 60,
            Velocity = 80
        };
        logicalSegment.Notes.Add(logicalNote);
        logicalTrack.Segments.Add(logicalSegment);
        source.Tracks.Add(logicalTrack);

        EventInstrument instrument = new(source)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(source) { Name = "Voice" };
        TemplateEvent templateEvent = TemplateEvent.Note(source, 0, 120, 60, 70);
        voice.Events.Add(templateEvent);
        instrument.SubVoices.Add(voice);
        source.EventInstruments.Add(instrument);

        MidiChannelRoot root = new(source) { Name = "Root" };
        PureMidiTrack pureTrack = new(source)
        {
            Name = "Pure",
            MidiChannelRootId = root.Id
        };
        MidiSegment midiSegment = new(source) { LengthTicks = 480 };
        DirectMidiNote directNote = new(source)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 64,
            NoteOnVelocity = 75
        };
        midiSegment.Notes.Add(directNote);
        pureTrack.Segments.Add(midiSegment);
        source.MidiChannelRoots.Add(root);
        source.PureMidiTracks.Add(pureTrack);

        MidoraProject mirror = ProjectCompilationSnapshot.Create(source);
        logicalNote.Velocity = 100;
        templateEvent.Value = 95;
        directNote.NoteOnVelocity = 90;
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(logicalTrack.Id);
        changes.EventInstrumentIds.Add(instrument.Id);
        changes.PureMidiTrackIds.Add(pureTrack.Id);
        ProjectCompilationSnapshot.RevisionCapture capture =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);
        using CancellationTokenSource cancellation = new();

        Assert.Throws<OperationCanceledException>(() =>
            ProjectCompilationSnapshot.MaterializeRevision(
                capture,
                cancellation.Token,
                () => cancellation.Cancel()));

        Assert.Equal(
            80,
            Assert.Single(Assert.Single(mirror.Tracks).Segments).Notes[0].Velocity);
        Assert.Equal(
            70,
            Assert.Single(Assert.Single(Assert.Single(mirror.EventInstruments).SubVoices).Events).Value);
        Assert.Equal(
            75,
            Assert.Single(Assert.Single(mirror.PureMidiTracks).Segments).Notes[0].NoteOnVelocity);

        ProjectCompilationSnapshot.RevisionCapture retry =
            ProjectCompilationSnapshot.CaptureRevision(mirror, source, changes);
        ProjectCompilationSnapshot.MaterializeRevision(retry);

        Assert.Equal(
            100,
            Assert.Single(Assert.Single(mirror.Tracks).Segments).Notes[0].Velocity);
        Assert.Equal(
            95,
            Assert.Single(Assert.Single(Assert.Single(mirror.EventInstruments).SubVoices).Events).Value);
        Assert.Equal(
            90,
            Assert.Single(Assert.Single(mirror.PureMidiTracks).Segments).Notes[0].NoteOnVelocity);
    }

    private static IEnumerable<LogicalNote> CreateNotes(MidoraProject project, int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new LogicalNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Note = index & 0x7f,
                Velocity = 100
            };
        }
    }

    private static IEnumerable<DirectMidiNote> CreateDirectNotes(
        MidoraProject project,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new DirectMidiNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Key = index & 0x7f,
                NoteOnVelocity = 100,
                NoteOffVelocity = 0,
                NoteOnOrder = index * 2L,
                NoteOffOrder = index * 2L + 1
            };
        }
    }

    private static void AssertFormalNoteOrder(MidiSegment segment)
    {
        DirectMidiNoteFormalSequenceSnapshot snapshot =
            segment.Notes.CreateFormalSequenceSnapshot();
        Assert.Equal(segment.Notes.Count, snapshot.Count);
        Assert.Equal(
            segment.Notes.Select(static value => value.Id),
            snapshot.Added.Enumerate().Select(static value => value.Id));
    }
}
