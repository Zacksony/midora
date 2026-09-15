using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

/// <summary>
/// Cross-cutting regression gates for edit paths whose correctness previously depended on
/// whole-owner enumeration or on publishing one collection revision per edited value.
/// These tests intentionally execute prepared edits directly so compilation scheduling does
/// not hide the source-collection cost being measured.
/// </summary>
public sealed class ExtremeTimelineEditCoverageTests(ITestOutputHelper output)
{
    [Fact]
    public void SixtyThousandPianoRollNotesHaveBoundedFirstApplyUndoRedoPhases()
    {
        using MidoraProject project = new(192);
        const int count = 60_000;
        const long extent = count * 8L + 64;

        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = extent
        };
        SubVoice voice = new(project) { Name = "SubVoice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project) { LengthTicks = extent };
        logicalTrack.Segments.Add(logicalSegment);
        MidiSegment midiSegment = AddMidiTrack(project, extent);

        LogicalNote[] logical = new LogicalNote[count];
        TemplateEvent[] template = new TemplateEvent[count];
        DirectMidiNote[] direct = new DirectMidiNote[count];
        for (int index = 0; index < count; index++)
        {
            long tick = index * 8L;
            int key = index % 128;
            logical[index] = new(project)
            {
                StartTick = tick,
                LengthTicks = 2,
                Note = key,
                Velocity = 100
            };
            template[index] = TemplateEvent.Note(project, tick, 2, key, 100);
            direct[index] = new(project)
            {
                StartTick = tick,
                LengthTicks = 2,
                Key = key,
                NoteOnVelocity = 100,
                NoteOffVelocity = 64,
                NoteOnOrder = index * 2L,
                NoteOffOrder = index * 2L + 1
            };
        }
        logicalSegment.Notes.AddRange(logical);
        voice.Events.AddRange(template);
        midiSegment.Notes.AddRange(direct);

        Exercise(
            "Logical",
            ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                logicalSegment.Id,
                logical.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1),
            () => CurrentLogical().Notes.CreateQuerySnapshot().GetRangeFingerprint(0, extent),
            () => CurrentLogical().Notes.CreateQuerySnapshot().GetByOrdinal(0).LengthTicks);
        Exercise(
            "SubVoice",
            ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                instrument.Id,
                voice.Id,
                template.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1),
            () => CurrentVoice().Events.CreateQuerySnapshot().GetNoteRangeFingerprint(0, extent),
            () => CurrentVoice().Events.CreateQuerySnapshot().GetByOrdinal(0).LengthTicks);
        Exercise(
            "Direct MIDI",
            ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                midiSegment.Id,
                direct.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1),
            () => CurrentMidi().Notes.CreateQuerySnapshot().GetRangeFingerprint(0, extent, 0, 127),
            () => CurrentMidi().Notes.CreateObjectSource().GetByOrdinal(0).LengthTicks);

        Segment CurrentLogical() => project.Tracks.Single(value => value.Id == logicalTrack.Id).Segments.Single(value => value.Id == logicalSegment.Id);
        SubVoice CurrentVoice() => project.EventInstruments.Single(value => value.Id == instrument.Id).SubVoices.Single(value => value.Id == voice.Id);
        MidiSegment CurrentMidi() => project.PureMidiTracks.SelectMany(static value => value.Segments).Single(value => value.Id == midiSegment.Id);

        void Exercise(
            string label,
            IProjectEditCommand command,
            Func<ulong> fingerprint,
            Func<long> firstLength)
        {
            ulong beforeFingerprint = fingerprint();
            Stopwatch prepare = Stopwatch.StartNew();
            IPreparedProjectEdit edit = command.Prepare(project);
            prepare.Stop();
            TimeSpan apply = Measure(() => edit.Apply(project));
            Assert.Equal(3, firstLength());
            TimeSpan undo = Measure(() => edit.Undo(project));
            Assert.Equal(2, firstLength());
            Assert.Equal(beforeFingerprint, fingerprint());
            TimeSpan redo = Measure(() => edit.Apply(project));
            Assert.Equal(3, firstLength());
            TimeSpan finalUndo = Measure(() => edit.Undo(project));
            Assert.Equal(2, firstLength());
            Assert.Equal(beforeFingerprint, fingerprint());

            TimeSpan maximumPhase = new[] { apply, undo, redo, finalUndo }.Max();
            Assert.True(maximumPhase < TimeSpan.FromSeconds(5),
                $"{label} 60,000-note edit phase took {maximumPhase}; "
                + $"apply={apply}, undo={undo}, redo={redo}, finalUndo={finalUndo}.");
            output.WriteLine(
                $"{label}: prepare={prepare.Elapsed}; apply={apply}; undo={undo}; "
                + $"redo={redo}; finalUndo={finalUndo}");
        }

        static TimeSpan Measure(Action action)
        {
            Stopwatch clock = Stopwatch.StartNew();
            action();
            clock.Stop();
            return clock.Elapsed;
        }
    }

    [Fact]
    public void SixtyThousandLogicalAndSubVoicePointsHaveBoundedFirstEditCycle()
    {
        using MidoraProject project = new(192);
        const int count = 60_000;
        const long extent = count * 4L + 64;
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = extent
        };
        SubVoice voice = new(project) { Name = "SubVoice" };
        instrument.SubVoices.Add(voice);
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Parameter",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 127,
            DisplayMinimum = 0,
            DisplayMaximum = 127,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = extent };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);

        CurvePoint[] logical = Enumerable.Range(0, count)
            .Select(index => new CurvePoint(
                project,
                index * 4L,
                64,
                CurveInterpolation.Step))
            .ToArray();
        TemplateEvent[] template = Enumerable.Range(0, count)
            .Select(index => TemplateEvent.ControlChange(project, index * 4L, 11, 64))
            .ToArray();
        lane.Points.AddRange(logical);
        voice.Events.AddRange(template);

        Exercise(
            "Logical Parameter",
            ProjectDomainEditCommands.AdjustLogicalParameterPoints(
                segment.Id,
                lane.Id,
                logical.Select(static value => value.Id).ToArray(),
                tickDelta: 1,
                valueDelta: 1),
            () => CurrentLane().Points.CreateQuerySnapshot().GetRangeFingerprint(0, extent),
            () =>
            {
                Assert.True(CurrentLane().Points.TryGetById(logical[0].Id, out CurvePoint? current));
                Assert.NotNull(current);
                return (current.Tick, current.Value);
            });
        Exercise(
            "SubVoice Event",
            ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                instrument.Id,
                voice.Id,
                template.Select(static value => value.Id).ToArray(),
                MidiValueTarget.ControlChange(11),
                tickDelta: 1,
                valueDelta: 1,
                duplicate: false),
            () => CurrentVoice().Events.CreateQuerySnapshot().GetEventRangeFingerprint(0, extent),
            () =>
            {
                Assert.True(CurrentVoice().Events.TryGetById(template[0].Id, out TemplateEvent? current));
                Assert.NotNull(current);
                return (current.Tick, (double)current.Value);
            });

        LogicalParameterLane CurrentLane() => track.Segments.Single(s => s.Id == segment.Id)
            .ParameterLanes.Single(p => p.Id == lane.Id);
        SubVoice CurrentVoice() => instrument.SubVoices.Single(s => s.Id == voice.Id);

        void Exercise(
            string label,
            IProjectEditCommand command,
            Func<ulong> fingerprint,
            Func<(long Tick, double Value)> first)
        {
            ulong beforeFingerprint = fingerprint();
            IPreparedProjectEdit edit = command.Prepare(project);
            TimeSpan apply = Measure(() => edit.Apply(project));
            Assert.Equal((1L, 65d), first());
            TimeSpan undo = Measure(() => edit.Undo(project));
            Assert.Equal((0L, 64d), first());
            Assert.Equal(beforeFingerprint, fingerprint());
            TimeSpan redo = Measure(() => edit.Apply(project));
            Assert.Equal((1L, 65d), first());
            TimeSpan finalUndo = Measure(() => edit.Undo(project));
            Assert.Equal((0L, 64d), first());
            Assert.Equal(beforeFingerprint, fingerprint());
            TimeSpan maximumPhase = new[] { apply, undo, redo, finalUndo }.Max();
            Assert.True(maximumPhase < TimeSpan.FromSeconds(5),
                $"{label} 60,000-point edit phase took {maximumPhase}; "
                + $"apply={apply}, undo={undo}, redo={redo}, finalUndo={finalUndo}.");
            output.WriteLine(
                $"{label}: apply={apply}; undo={undo}; redo={redo}; finalUndo={finalUndo}");
        }

        static TimeSpan Measure(Action action)
        {
            Stopwatch clock = Stopwatch.StartNew();
            action();
            clock.Stop();
            return clock.Elapsed;
        }
    }

    [Fact]
    public void RandomizedPianoRollEditsMatchIndependentOracleAcrossUndoRedo()
    {
        using MidoraProject project = new(192);
        const int count = 1_024;
        const long extent = count * 128L;
        Random random = new(0x4d49444f);

        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = extent
        };
        SubVoice voice = new(project) { Name = "SubVoice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project) { LengthTicks = extent };
        logicalTrack.Segments.Add(logicalSegment);
        MidiSegment midiSegment = AddMidiTrack(project, extent);

        LogicalNote[] logical = new LogicalNote[count];
        TemplateEvent[] template = new TemplateEvent[count];
        DirectMidiNote[] direct = new DirectMidiNote[count];
        for (int index = 0; index < count; index++)
        {
            long tick = index * 64L;
            long length = random.Next(2, 32);
            int key = random.Next(0, 127);
            int velocity = random.Next(1, 128);
            logical[index] = new(project)
            {
                StartTick = tick,
                LengthTicks = length,
                Note = key,
                Velocity = velocity
            };
            template[index] = TemplateEvent.Note(project, tick, length, key, velocity);
            direct[index] = new(project)
            {
                StartTick = tick,
                LengthTicks = length,
                Key = key,
                NoteOnVelocity = velocity,
                NoteOffVelocity = index % 128,
                NoteOnOrder = index * 2L,
                NoteOffOrder = index * 2L + 1
            };
        }
        logicalSegment.Notes.AddRange(logical);
        voice.Events.AddRange(template);
        midiSegment.Notes.AddRange(direct);
        MidoraId[] logicalIds = logical.Select(static value => value.Id).ToArray();
        MidoraId[] templateIds = template.Select(static value => value.Id).ToArray();
        MidoraId[] directIds = direct.Select(static value => value.Id).ToArray();

        ExerciseModel(
            logicalIds,
            () => logical.Select(static value => new NoteOracleValue(
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity,
                0)).ToArray(),
            () => logicalSegment.Notes.CreateQuerySnapshot().GetRangeFingerprint(0, extent),
            (ids, tick, key) => ProjectDomainEditCommands.MoveLogicalNotes(
                logicalSegment.Id, ids, tick, key),
            (ids, start, end) => ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                logicalSegment.Id, ids, start, end),
            values => ProjectDomainEditCommands.PaintLogicalNoteVelocities(
                logicalSegment.Id,
                logical.ToDictionary(static value => value.Id, value => values[value.Id])),
            ids => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(logicalSegment.Id, ids),
            ids => ProjectDomainEditCommands.FlipLogicalNotesVertical(logicalSegment.Id, ids),
            (ids, factor) => ProjectDomainEditCommands.ScaleLogicalNotes(logicalSegment.Id, ids, factor),
            (ids, delta) => ProjectDomainEditCommands.TransposeLogicalNotes(logicalSegment.Id, ids, delta));

        ExerciseModel(
            templateIds,
            () => template.Select(static value => new NoteOracleValue(
                value.Tick,
                value.LengthTicks,
                value.Number,
                value.Value,
                0)).ToArray(),
            () => voice.Events.CreateQuerySnapshot().GetNoteRangeFingerprint(0, extent),
            (ids, tick, key) => ProjectDomainEditCommands.MoveTemplateNotes(
                instrument.Id, voice.Id, ids, tick, key),
            (ids, start, end) => ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                instrument.Id, voice.Id, ids, start, end),
            values => ProjectDomainEditCommands.PaintTemplateNoteVelocities(
                instrument.Id,
                voice.Id,
                template.ToDictionary(static value => value.Id, value => values[value.Id])),
            ids => ProjectDomainEditCommands.FlipTemplateNotesHorizontal(
                instrument.Id, voice.Id, ids),
            ids => ProjectDomainEditCommands.FlipTemplateNotesVertical(
                instrument.Id, voice.Id, ids),
            (ids, factor) => ProjectDomainEditCommands.ScaleTemplateNotes(
                instrument.Id, voice.Id, ids, factor),
            (ids, delta) => ProjectDomainEditCommands.TransposeTemplateNotes(
                instrument.Id, voice.Id, ids, delta));

        ExerciseModel(
            directIds,
            () => direct.Select(static value => new NoteOracleValue(
                value.StartTick,
                value.LengthTicks,
                value.Key,
                value.NoteOnVelocity,
                value.NoteOffVelocity)).ToArray(),
            () => midiSegment.Notes.CreateQuerySnapshot().GetRangeFingerprint(0, extent, 0, 127),
            (ids, tick, key) => ProjectDomainEditCommands.MoveDirectMidiNotes(
                midiSegment.Id, ids, tick, key),
            (ids, start, end) => ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                midiSegment.Id, ids, start, end),
            values => ProjectDomainEditCommands.PaintDirectMidiNoteVelocities(
                midiSegment.Id,
                direct.ToDictionary(static value => value.Id, value => values[value.Id])),
            ids => ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(midiSegment.Id, ids),
            ids => ProjectDomainEditCommands.FlipDirectMidiNotesVertical(midiSegment.Id, ids),
            (ids, factor) => ProjectDomainEditCommands.ScaleDirectMidiNotes(midiSegment.Id, ids, factor),
            (ids, delta) => ProjectDomainEditCommands.TransposeDirectMidiNotes(midiSegment.Id, ids, delta));

        void ExerciseModel(
            MidoraId[] ids,
            Func<NoteOracleValue[]> capture,
            Func<ulong> fingerprint,
            Func<IReadOnlyCollection<MidoraId>, long, int, IProjectEditCommand> move,
            Func<IReadOnlyCollection<MidoraId>, long, long, IProjectEditCommand> resize,
            Func<IReadOnlyDictionary<MidoraId, int>, IProjectEditCommand> velocity,
            Func<IReadOnlyCollection<MidoraId>, IProjectEditCommand> flipHorizontal,
            Func<IReadOnlyCollection<MidoraId>, IProjectEditCommand> flipVertical,
            Func<IReadOnlyCollection<MidoraId>, double, IProjectEditCommand> scale,
            Func<IReadOnlyCollection<MidoraId>, int, IProjectEditCommand> transpose)
        {
            VerifyCycle(move(ids, 7, 1), capture, fingerprint,
                values => values.Select(value => value with
                {
                    Tick = value.Tick + 7,
                    Key = value.Key + 1
                }).ToArray());
            VerifyCycle(resize(ids, 0, -20), capture, fingerprint,
                values => values.Select(value => value with
                {
                    Length = Math.Max(1, value.Length - 20)
                }).ToArray());
            Dictionary<MidoraId, int> velocities = ids
                .Select((id, index) => (id, value: (index * 37 % 127) + 1))
                .ToDictionary(static value => value.id, static value => value.value);
            VerifyCycle(velocity(velocities), capture, fingerprint,
                values => values.Select((value, index) => value with
                {
                    Velocity = velocities[ids[index]]
                }).ToArray());
            VerifyCycle(flipHorizontal(ids), capture, fingerprint, values =>
            {
                long left = values.Min(static value => value.Tick);
                long right = values.Max(static value => value.Tick + value.Length);
                return values.Select(value => value with
                {
                    Tick = left + right - (value.Tick + value.Length)
                }).ToArray();
            });
            VerifyCycle(flipVertical(ids), capture, fingerprint, values =>
            {
                int low = values.Min(static value => value.Key);
                int high = values.Max(static value => value.Key);
                return values.Select(value => value with { Key = low + high - value.Key }).ToArray();
            });
            VerifyCycle(scale(ids, 1.5), capture, fingerprint, values =>
            {
                long origin = values.Min(static value => value.Tick);
                return values.Select(value => value with
                {
                    Tick = origin + (long)Math.Round(
                        (value.Tick - origin) * 1.5,
                        MidpointRounding.AwayFromZero),
                    Length = Math.Max(1, (long)Math.Round(
                        value.Length * 1.5,
                        MidpointRounding.AwayFromZero))
                }).ToArray();
            });
            VerifyCycle(transpose(ids, 1), capture, fingerprint,
                values => values.Select(value => value with { Key = value.Key + 1 }).ToArray());
        }

        void VerifyCycle(
            IProjectEditCommand command,
            Func<NoteOracleValue[]> capture,
            Func<ulong> fingerprint,
            Func<NoteOracleValue[], NoteOracleValue[]> transform)
        {
            NoteOracleValue[] before = capture();
            NoteOracleValue[] expected = transform(before);
            ulong beforeFingerprint = fingerprint();
            IPreparedProjectEdit edit = command.Prepare(project);
            edit.Apply(project);
            Assert.Equal(expected, capture());
            edit.Undo(project);
            Assert.Equal(before, capture());
            Assert.Equal(beforeFingerprint, fingerprint());
            edit.Apply(project);
            Assert.Equal(expected, capture());
            edit.Undo(project);
            Assert.Equal(before, capture());
            Assert.Equal(beforeFingerprint, fingerprint());
        }
    }

    [Fact]
    public void DirectEventSelectionTransformPublishesOneRevisionAndUndoRestoresFingerprint()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = AddMidiTrack(project, lengthTicks: 1_000_000);
        const int count = 60_000;
        DirectMidiChannelEvent[] events = Enumerable.Range(0, count)
            .Select(index => new DirectMidiChannelEvent(project)
            {
                Tick = index * 4L,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11,
                Data2 = index % 128,
                Order = index
            })
            .ToArray();
        segment.ChannelEvents.AddRange(events);
        MidoraId[] ids = events.Select(static value => value.Id).ToArray();

        DirectMidiChannelEventQuerySnapshot before = segment.ChannelEvents.CreateQuerySnapshot();
        ulong beforeFingerprint = before.GetRangeFingerprint(0, segment.LengthTicks);
        IPreparedProjectEdit edit = ProjectDomainEditCommands.ScaleDirectMidiEventPoints(
                segment.Id,
                ids,
                factor: 2)
            .Prepare(project);

        Stopwatch apply = Stopwatch.StartNew();
        edit.Apply(project);
        apply.Stop();
        DirectMidiChannelEventQuerySnapshot applied = Current().ChannelEvents.CreateQuerySnapshot();
        Assert.Equal(before.Generation + 1, applied.Generation);
        Assert.NotEqual(beforeFingerprint, applied.GetRangeFingerprint(0, segment.LengthTicks));
        Assert.Equal(0, Current().ChannelEvents.CreateObjectSource().GetByOrdinal(0).Tick);
        Assert.Equal((count - 1) * 8L, Current().ChannelEvents.CreateObjectSource().GetByOrdinal(count - 1).Tick);

        Stopwatch undo = Stopwatch.StartNew();
        edit.Undo(project);
        undo.Stop();
        DirectMidiChannelEventQuerySnapshot undone = Current().ChannelEvents.CreateQuerySnapshot();
        // Undo restores the immutable owner root, including its exact local generation.
        Assert.Equal(before.Generation, undone.Generation);
        Assert.Equal(beforeFingerprint, undone.GetRangeFingerprint(0, segment.LengthTicks));
        Assert.Equal((count - 1) * 4L, Current().ChannelEvents.CreateObjectSource().GetByOrdinal(count - 1).Tick);

        edit.Apply(project);
        Assert.Equal((count - 1) * 8L, Current().ChannelEvents.CreateObjectSource().GetByOrdinal(count - 1).Tick);
        edit.Undo(project);
        Assert.Equal(beforeFingerprint, Current().ChannelEvents
            .CreateQuerySnapshot()
            .GetRangeFingerprint(0, segment.LengthTicks));

        Assert.True(apply.Elapsed < TimeSpan.FromSeconds(10),
            $"60,000-point Direct MIDI transform apply took {apply.Elapsed}.");
        Assert.True(undo.Elapsed < TimeSpan.FromSeconds(10),
            $"60,000-point Direct MIDI transform undo took {undo.Elapsed}.");
        output.WriteLine($"directEvents={count}; apply={apply.Elapsed}; undo={undo.Elapsed}");
        MidiSegment Current() => project.PureMidiTracks.SelectMany(static value => value.Segments).Single(value => value.Id == segment.Id);
    }

    [Fact]
    public void PagedMidiSegmentExposedTransformsDoNotDecodeHiddenPages()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-exposed-transform-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "content.mpk");
        try
        {
            using MidoraProject project = new(192);
            MidiSegment segment = AddMidiTrack(project, lengthTicks: 64);
            segment.ContentOffsetTick = 0;
            const int count = 200_000;
            using PureMidiContentPackWriter writer = new(path);
            for (int index = 0; index < count; index++)
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(),
                    index * 4L,
                    2,
                    index % 128,
                    100,
                    0,
                    index * 2L,
                    index * 2L + 1));
            }
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            Assert.True(pack.PageCount > 4, $"Expected multiple source pages, got {pack.PageCount}.");

            ulong beforeFingerprint = segment.Notes
                .CreateQuerySnapshot()
                .GetRangeFingerprint(0, 64, 0, 127);
            long missesBefore = pack.PageCacheMissCount;
            IPreparedProjectEdit edit = ProjectDomainEditCommands.FlipMidiSegmentsHorizontal(
                    [segment.Id],
                    SegmentSelectionTransformScope.ExposedContentOnly)
                .Prepare(project);
            long missesAfterPrepare = pack.PageCacheMissCount;

            edit.Apply(project);
            edit.Undo(project);
            ulong undoneFingerprint = segment.Notes
                .CreateQuerySnapshot()
                .GetRangeFingerprint(0, 64, 0, 127);
            Assert.Equal(beforeFingerprint, undoneFingerprint);
            Assert.Equal(count, segment.Notes.Count);

            long decodedForActiveWindow = missesAfterPrepare - missesBefore;
            Assert.InRange(decodedForActiveWindow, 1, 3);
            Assert.True(decodedForActiveWindow < pack.PageCount,
                $"The exposed transform decoded {decodedForActiveWindow} of {pack.PageCount} pages.");

            DirectMidiNoteValue far = Assert.Single(segment.Notes
                .CreateQuerySnapshot()
                .QueryValues((count - 1) * 4L, (count - 1) * 4L + 1, 0, 127));
            Assert.Equal((count - 1) * 4L, far.StartTick);
            output.WriteLine(
                $"notes={count}; pages={pack.PageCount}; activeWindowMisses={decodedForActiveWindow}");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static MidiSegment AddMidiTrack(MidoraProject project, long lengthTicks)
    {
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project) { LengthTicks = lengthTicks };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return segment;
    }

    private readonly record struct NoteOracleValue(
        long Tick,
        long Length,
        int Key,
        int Velocity,
        int ReleaseVelocity);
}
