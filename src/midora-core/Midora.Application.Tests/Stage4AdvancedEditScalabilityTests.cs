using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class Stage4AdvancedEditScalabilityCollection
{
    public const string CollectionName = "Stage 4 advanced edit scalability";
}

[Collection(Stage4AdvancedEditScalabilityCollection.CollectionName)]
public sealed class Stage4AdvancedEditScalabilityTests(ITestOutputHelper output)
{
    private const int NoteCount = 60_000;
    private const long MaximumPrepareAllocationBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumPublicationAllocationBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan MaximumPrepareTime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumPublicationTime = TimeSpan.FromSeconds(2);

    [Fact]
    public void SixtyThousandDirectNotesHumanizeWithDetachedConstantTimePublication()
    {
        DirectScenario scenario = CreateDirectScenario(
            static index => (index * 4L, 2L, index & 127, 70 + (index % 40), index & 127));
        using MidoraProject project = scenario.Project;
        DirectMidiNote first = scenario.Notes[0];
        DirectMidiNote last = scenario.Notes[^1];
        DirectNoteState firstBefore = Capture(first);
        DirectNoteState lastBefore = Capture(last);
        ulong originalFingerprint = DirectFingerprint(scenario.Segment);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                scenario.Segment.Id,
                scenario.Ids,
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(96, 96),
                    Seed: 0x4d49444f5241));

        ExerciseDirectRootSwap(
            "Direct/Humanize",
            scenario,
            command,
            () =>
            {
                MidiSegment active = Active(scenario.Track);
                Assert.Equal(NoteCount, active.Notes.Count);
                Assert.Equal(96, ResolveDirect(active, first.Id).NoteOnVelocity);
                Assert.Equal(96, ResolveDirect(active, last.Id).NoteOnVelocity);
                Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
            },
            () =>
            {
                Assert.Equal(NoteCount, scenario.Segment.Notes.Count);
                Assert.Equal(
                    originalFingerprint,
                    DirectFingerprint(scenario.Segment));
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            });
    }

    [Fact]
    public void SixtyThousandDirectNotesJoinWithDetachedConstantTimePublication()
    {
        DirectScenario scenario = CreateDirectScenario(
            static index => (index * 2L, 2L, 60, 80 + (index % 30), index & 127));
        using MidoraProject project = scenario.Project;
        DirectMidiNote first = scenario.Notes[0];
        DirectMidiNote last = scenario.Notes[^1];
        DirectNoteState firstBefore = Capture(first);
        DirectNoteState lastBefore = Capture(last);
        ulong originalFingerprint = DirectFingerprint(scenario.Segment);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.JoinDirectMidiNotes(
                scenario.Segment.Id,
                scenario.Ids,
                new(MaximumGapTicks: 0));

        ExerciseDirectRootSwap(
            "Direct/Join",
            scenario,
            command,
            () =>
            {
                DirectMidiNote joined = Assert.Single(Active(scenario.Track).Notes);
                Assert.Equal(first.Id, joined.Id);
                Assert.Equal((0L, NoteCount * 2L, 60),
                    (joined.StartTick, joined.LengthTicks, joined.Key));
                Assert.Equal(first.NoteOnVelocity, joined.NoteOnVelocity);
                Assert.Equal(last.NoteOffVelocity, joined.NoteOffVelocity);
                Assert.Equal([first.Id], command.ResultSelectionIds);
            },
            () =>
            {
                Assert.Equal(NoteCount, scenario.Segment.Notes.Count);
                Assert.Equal(
                    originalFingerprint,
                    DirectFingerprint(scenario.Segment));
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            });
    }

    [Fact]
    public void SixtyThousandDirectNotesQuantizeWithDetachedConstantTimePublication()
    {
        DirectScenario scenario = CreateDirectScenario(
            static index => ((index * 4L) + 1, 2L, index & 127, 90, index & 127));
        using MidoraProject project = scenario.Project;
        DirectMidiNote first = scenario.Notes[0];
        DirectMidiNote last = scenario.Notes[^1];
        DirectNoteState firstBefore = Capture(first);
        DirectNoteState lastBefore = Capture(last);
        ulong originalFingerprint = DirectFingerprint(scenario.Segment);
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                scenario.Segment.Id,
                scenario.Ids,
                new(
                    TimelineQuantizeGrid.FromCustomTicks(4),
                    NoteQuantizeMode.StartOnly));

        ExerciseDirectRootSwap(
            "Direct/Quantize",
            scenario,
            command,
            () =>
            {
                MidiSegment active = Active(scenario.Track);
                Assert.Equal(NoteCount, active.Notes.Count);
                Assert.Equal(0, ResolveDirect(active, first.Id).StartTick);
                Assert.Equal((NoteCount - 1) * 4L, ResolveDirect(active, last.Id).StartTick);
                Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
            },
            () =>
            {
                Assert.Equal(NoteCount, scenario.Segment.Notes.Count);
                Assert.Equal(
                    originalFingerprint,
                    DirectFingerprint(scenario.Segment));
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            });
    }

    [Fact]
    public void SixtyThousandDirectNotesSplitToOneHundredTwentyThousandWithDetachedConstantTimePublication()
    {
        DirectScenario scenario = CreateDirectScenario(
            static index => (index * 4L, 2L, index & 127, 100, index & 127));
        using MidoraProject project = scenario.Project;
        DirectMidiNote first = scenario.Notes[0];
        DirectMidiNote last = scenario.Notes[^1];
        DirectNoteState firstBefore = Capture(first);
        DirectNoteState lastBefore = Capture(last);
        ulong originalFingerprint = DirectFingerprint(scenario.Segment);
        long stableIdBeforePrepare = project.NextStableId;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitDirectMidiNotes(
                scenario.Segment.Id,
                scenario.Ids,
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 1,
                    MaximumResultObjects = NoteCount * 2
                });

        ExerciseDirectRootSwap(
            "Direct/Split",
            scenario,
            command,
            () =>
            {
                MidiSegment active = Active(scenario.Track);
                Assert.Equal(NoteCount * 2, active.Notes.Count);
                DirectMidiNote[] firstPair = active.Notes
                    .Where(value => value.StartTick is 0 or 1)
                    .OrderBy(static value => value.StartTick)
                    .ToArray();
                Assert.Equal(2, firstPair.Length);
                Assert.Equal(first.Id, firstPair[0].Id);
                Assert.All(firstPair, static value => Assert.Equal(1, value.LengthTicks));
                DirectMidiNote final = active.Notes.Last();
                Assert.Equal(((NoteCount - 1) * 4L) + 1, final.StartTick);
                Assert.Equal(1, final.LengthTicks);
                Assert.Equal(NoteCount * 2, command.ResultSelectionIds.Count);
                Assert.Equal(stableIdBeforePrepare + NoteCount, project.NextStableId);
            },
            () =>
            {
                Assert.Equal(NoteCount, scenario.Segment.Notes.Count);
                Assert.Equal(
                    originalFingerprint,
                    DirectFingerprint(scenario.Segment));
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            },
            assertAfterPrepare: () => Assert.Equal(stableIdBeforePrepare, project.NextStableId),
            assertAllocatorAfterUndo: () =>
                Assert.Equal(stableIdBeforePrepare + NoteCount, project.NextStableId));
    }

    [Fact]
    public void SixtyThousandLogicalNotesHumanizeWithDetachedConstantTimePublication()
    {
        using MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);
        LogicalNote[] notes = Enumerable.Range(0, NoteCount)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Note = index & 127,
                Velocity = 80
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        MidoraId[] ids = notes.Select(static value => value.Id).ToArray();
        LogicalNote first = notes[0];
        LogicalNote last = notes[^1];
        LogicalNoteState firstBefore = Capture(first);
        LogicalNoteState lastBefore = Capture(last);
        ulong originalFingerprint = segment.Notes.CreateQuerySnapshot().ContentFingerprint;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                segment.Id,
                ids,
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(101, 101),
                    Seed: 0x4c4f474943414c));

        ExerciseRootSwap(
            "Logical/Humanize",
            project,
            command,
            () => Assert.Single(track.Segments),
            segment,
            active =>
            {
                Assert.Equal(NoteCount, active.Notes.Count);
                LogicalNoteQuerySnapshot snapshot = active.Notes.CreateQuerySnapshot();
                Assert.Equal(101, snapshot.ResolveByIds([first.Id]).Single().Velocity);
                Assert.Equal(101, snapshot.ResolveByIds([last.Id]).Single().Velocity);
                Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
            },
            () =>
            {
                Assert.Equal(NoteCount, segment.Notes.Count);
                Assert.Equal(
                    originalFingerprint,
                    segment.Notes.CreateQuerySnapshot().ContentFingerprint);
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            });
    }

    [Fact]
    public void SixtyThousandSubVoiceNotesHumanizeWithDetachedConstantTimePublication()
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
        TemplateEvent[] notes = Enumerable.Range(0, NoteCount)
            .Select(index => TemplateEvent.Note(
                project,
                index * 4L,
                lengthTicks: 2,
                note: index & 127,
                velocity: 75))
            .ToArray();
        voice.Events.AddRange(notes);
        MidoraId[] ids = notes.Select(static value => value.Id).ToArray();
        TemplateEvent first = notes[0];
        TemplateEvent last = notes[^1];
        TemplateNoteState firstBefore = Capture(first);
        TemplateNoteState lastBefore = Capture(last);
        ulong originalFingerprint = voice.Events.CreateQuerySnapshot().ContentFingerprint;
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeTemplateNotes(
                instrument.Id,
                voice.Id,
                ids,
                new(
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Override(103, 103),
                    Seed: 0x535542564f494345));

        ExerciseRootSwap(
            "SubVoice/Humanize",
            project,
            command,
            () => Assert.Single(instrument.SubVoices),
            voice,
            active =>
            {
                Assert.Equal(NoteCount, active.Events.Count);
                TemplateEventQuerySnapshot snapshot = active.Events.CreateQuerySnapshot();
                Assert.Equal(103, snapshot.ResolveByIds([first.Id]).Single().Value);
                Assert.Equal(103, snapshot.ResolveByIds([last.Id]).Single().Value);
                Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
            },
            () =>
            {
                Assert.Equal(NoteCount, voice.Events.Count);
                Assert.Equal(
                    originalFingerprint,
                    voice.Events.CreateQuerySnapshot().ContentFingerprint);
                Assert.Equal(firstBefore, Capture(first));
                Assert.Equal(lastBefore, Capture(last));
            });
    }

    private void ExerciseDirectRootSwap(
        string label,
        DirectScenario scenario,
        ITimelineSelectionResultEditCommand command,
        Action assertApplied,
        Action assertOriginal,
        Action? assertAfterPrepare = null,
        Action? assertAllocatorAfterUndo = null) =>
        ExerciseRootSwap(
            label,
            scenario.Project,
            command,
            () => Active(scenario.Track),
            scenario.Segment,
            _ => assertApplied(),
            assertOriginal,
            assertAfterPrepare,
            assertAllocatorAfterUndo);

    private void ExerciseRootSwap<TRoot>(
        string label,
        MidoraProject project,
        ITimelineSelectionResultEditCommand command,
        Func<TRoot> activeRoot,
        TRoot originalRoot,
        Action<TRoot> assertApplied,
        Action assertOriginal,
        Action? assertAfterPrepare = null,
        Action? assertAllocatorAfterUndo = null)
        where TRoot : class
    {
        ForceFullCollection();
        PhaseMeasurement<IPreparedProjectEdit> prepare = Measure(() => command.Prepare(project));
        AssertWithinPrepareBudget(label, prepare.Metrics);
        Assert.Same(originalRoot, activeRoot());
        assertOriginal();
        Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
        assertAfterPrepare?.Invoke();

        PhaseMeasurement<object?> apply = Measure(() =>
        {
            prepare.Result.Apply(project);
            return (object?)null;
        });
        AssertWithinPublicationBudget(label, "Apply", apply.Metrics);
        TRoot replacementRoot = activeRoot();
        Assert.NotSame(originalRoot, replacementRoot);
        assertOriginal();
        assertApplied(replacementRoot);

        PhaseMeasurement<object?> undo = Measure(() =>
        {
            prepare.Result.Undo(project);
            return (object?)null;
        });
        AssertWithinPublicationBudget(label, "Undo", undo.Metrics);
        Assert.Same(originalRoot, activeRoot());
        assertOriginal();
        Assert.Equal(NoteCount, command.ResultSelectionIds.Count);
        assertAllocatorAfterUndo?.Invoke();

        PhaseMeasurement<object?> redo = Measure(() =>
        {
            prepare.Result.Apply(project);
            return (object?)null;
        });
        AssertWithinPublicationBudget(label, "Redo", redo.Metrics);
        Assert.Same(replacementRoot, activeRoot());
        assertApplied(replacementRoot);

        PhaseMeasurement<object?> finalUndo = Measure(() =>
        {
            prepare.Result.Undo(project);
            return (object?)null;
        });
        AssertWithinPublicationBudget(label, "FinalUndo", finalUndo.Metrics);
        Assert.Same(originalRoot, activeRoot());
        assertOriginal();
        Assert.Equal(NoteCount, command.ResultSelectionIds.Count);

        WriteMeasurements(
            label,
            prepare.Metrics,
            apply.Metrics,
            undo.Metrics,
            redo.Metrics,
            finalUndo.Metrics);
    }

    private static DirectScenario CreateDirectScenario(
        Func<int, (long Tick, long Gate, int Key, int Velocity, int NoteOffVelocity)> values)
    {
        MidoraProject project = new(192);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project) { LengthTicks = 1_000_000 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        DirectMidiNote[] notes = Enumerable.Range(0, NoteCount)
            .Select(index =>
            {
                (long tick, long gate, int key, int velocity, int noteOffVelocity) = values(index);
                return new DirectMidiNote(project)
                {
                    StartTick = tick,
                    LengthTicks = gate,
                    Key = key,
                    NoteOnVelocity = velocity,
                    NoteOffVelocity = noteOffVelocity,
                    NoteOnOrder = index * 2L,
                    NoteOffOrder = (index * 2L) + 1
                };
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        return new(
            project,
            track,
            segment,
            notes,
            notes.Select(static value => value.Id).ToArray());
    }

    private static MidiSegment Active(PureMidiTrack track) => Assert.Single(track.Segments);

    private static ulong DirectFingerprint(MidiSegment segment) =>
        segment.Notes.CreateQuerySnapshot().GetRangeFingerprint(
            0,
            segment.LengthTicks,
            0,
            127);

    private static DirectMidiNote ResolveDirect(MidiSegment segment, MidoraId id) =>
        segment.Notes.ResolveByIds([id]).Single().Value;

    private static DirectNoteState Capture(DirectMidiNote value) => new(
        value.StartTick,
        value.LengthTicks,
        value.Key,
        value.NoteOnVelocity,
        value.NoteOffVelocity,
        value.NoteOnOrder,
        value.NoteOffOrder);

    private static LogicalNoteState Capture(LogicalNote value) => new(
        value.StartTick,
        value.LengthTicks,
        value.Note,
        value.Velocity);

    private static TemplateNoteState Capture(TemplateEvent value) => new(
        value.Kind,
        value.Tick,
        value.LengthTicks,
        value.Number,
        value.Value,
        value.SecondaryValue);

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static PhaseMeasurement<T> Measure<T>(Func<T> action)
    {
        long managedBefore = GC.GetTotalMemory(false);
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T result = action();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        long managedAfter = GC.GetTotalMemory(false);
        return new(result, new(elapsed, allocated, managedBefore, managedAfter));
    }

    private static void AssertWithinPrepareBudget(string label, PhaseMetrics measurement)
    {
        Assert.True(
            measurement.Elapsed < MaximumPrepareTime,
            $"{label} Prepare took {measurement.Elapsed}; expected a bounded linear preparation.");
        Assert.True(
            measurement.CurrentThreadAllocatedBytes < MaximumPrepareAllocationBytes,
            $"{label} Prepare allocated {measurement.CurrentThreadAllocatedBytes / 1048576d:F1} MiB on its thread.");
    }

    private static void AssertWithinPublicationBudget(
        string label,
        string phase,
        PhaseMetrics measurement)
    {
        Assert.True(
            measurement.Elapsed < MaximumPublicationTime,
            $"{label} {phase} took {measurement.Elapsed}; root publication must not scale with note count.");
        Assert.True(
            measurement.CurrentThreadAllocatedBytes < MaximumPublicationAllocationBytes,
            $"{label} {phase} allocated {measurement.CurrentThreadAllocatedBytes / 1048576d:F2} MiB; "
            + "root publication must not rebuild the edited collection.");
    }

    private void WriteMeasurements(
        string label,
        PhaseMetrics prepare,
        PhaseMetrics apply,
        PhaseMetrics undo,
        PhaseMetrics redo,
        PhaseMetrics finalUndo)
    {
        output.WriteLine(
            $"{label}: Prepare={Format(prepare)}; Apply={Format(apply)}; "
            + $"Undo={Format(undo)}; Redo={Format(redo)}; FinalUndo={Format(finalUndo)}");

        static string Format(PhaseMetrics value) =>
            $"{value.Elapsed.TotalMilliseconds:F1} ms/"
            + $"{value.CurrentThreadAllocatedBytes / 1048576d:F2} MiB thread/"
            + $"{value.ManagedBeforeBytes / 1048576d:F1}->{value.ManagedAfterBytes / 1048576d:F1} MiB managed";
    }

    private sealed record DirectScenario(
        MidoraProject Project,
        PureMidiTrack Track,
        MidiSegment Segment,
        DirectMidiNote[] Notes,
        MidoraId[] Ids);

    private readonly record struct PhaseMeasurement<T>(T Result, PhaseMetrics Metrics);

    private readonly record struct PhaseMetrics(
        TimeSpan Elapsed,
        long CurrentThreadAllocatedBytes,
        long ManagedBeforeBytes,
        long ManagedAfterBytes);

    private readonly record struct DirectNoteState(
        long Tick,
        long Gate,
        int Key,
        int Velocity,
        int NoteOffVelocity,
        long NoteOnOrder,
        long NoteOffOrder);

    private readonly record struct LogicalNoteState(
        long Tick,
        long Gate,
        int Key,
        int Velocity);

    private readonly record struct TemplateNoteState(
        TemplateEventKind Kind,
        long Tick,
        long Gate,
        int Number,
        int Value,
        int SecondaryValue);
}
