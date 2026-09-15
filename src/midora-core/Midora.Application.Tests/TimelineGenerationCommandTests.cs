using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class TimelineGenerationCommandTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void AllNoteOwnersNormalizeRecurrencePublishExactSelectionAndUndoRedo(string kind)
    {
        using var f = new Fixture();
        var command = f.Notes(kind, new()
        {
            BaseTick = 100, MaximumCandidates = 4, InitialTick = 5,
            InitialVelocity = 126, InitialKey = 60, InitialGate = 8,
            VelocityExpression = "=v0+1", KeyExpression = "=k0+1", TickExpression = "=tr+10",
            GateExpression = "=g0+0.5"
        });
        long allocator = f.Project.NextStableId;
        var edit = command.Prepare(f.Project);
        Assert.Empty(f.ReadNotes(kind));
        Assert.Equal(allocator, f.Project.NextStableId);
        edit.Apply(f.Project);
        var notes = f.ReadNotes(kind);
        Assert.Equal(new long[] { 115, 125, 135, 145 }, notes.Select(static value => value.Tick));
        Assert.Equal(new long[] { 9, 10, 11, 12 }, notes.Select(static value => value.Gate));
        Assert.Equal(new[] { 61, 62, 63, 64 }, notes.Select(static value => value.Key));
        Assert.All(notes, note => Assert.Equal(127, note.Velocity));
        Assert.Equal(notes.Select(static n => n.Id), Selection(edit).ResultSelectionIds);
        edit.Undo(f.Project);
        Assert.Empty(f.ReadNotes(kind));
        edit.Apply(f.Project);
        Assert.Equal(notes, f.ReadNotes(kind));
    }

    [Theory]
    [InlineData(false, 15, 5)]
    [InlineData(true, 10, 0)]
    public void InitialCandidateSwitchChangesIterationStartAndConsumesLimit(bool initial, long firstTick, int firstKey)
    {
        using var f = new Fixture();
        var edit = f.Notes("direct", new()
        {
            BaseTick = 10, MaximumCandidates = 3, CreateFirstFromInitialValues = initial,
            TickExpression = "=t0+5", KeyExpression = "=i+5"
        }).Prepare(f.Project);
        edit.Apply(f.Project);
        var notes = f.ReadNotes("direct");
        Assert.Equal(3, notes.Length);
        Assert.Equal(firstTick, notes[0].Tick); Assert.Equal(firstKey, notes[0].Key);
        Assert.Equal(7, notes[^1].Key);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void BackwardsTicksAndCandidateDuplicatesKeepExistingAndEarliestNote(string kind)
    {
        using var f = new Fixture();
        var seed = f.Notes(kind, new() { BaseTick = 2, MaximumCandidates = 1, InitialKey = 60, InitialVelocity = 99 });
        seed.Prepare(f.Project).Apply(f.Project);
        MidoraId original = f.ReadNotes(kind)[0].Id;
        var edit = f.Notes(kind, new()
        {
            MaximumCandidates = 6, InitialKey = 60, TickExpression = "=2-i%3", VelocityExpression = "=10+i"
        }).Prepare(f.Project);
        edit.Apply(f.Project);
        var notes = f.ReadNotes(kind).OrderBy(static n => n.Tick).ToArray();
        Assert.Equal(3, notes.Length);
        Assert.Equal(new[] { 12, 11, 99 }, notes.Select(static n => n.Velocity));
        Assert.Equal(original, notes[^1].Id);
        Assert.Equal(2, Selection(edit).ResultSelectionIds.Count);
        edit.Undo(f.Project); Assert.Equal(original, Assert.Single(f.ReadNotes(kind)).Id);
    }

    [Fact]
    public void MaximumTickStopsBeforeCreatingExceededCandidateAndNegativeTicksClamp()
    {
        using var f = new Fixture();
        var edit = f.Notes("direct", new()
        {
            MaximumCandidates = 100, MaximumRelativeStartTick = 6,
            TickExpression = "=i*3-3", KeyExpression = "=i"
        }).Prepare(f.Project);
        edit.Apply(f.Project);
        Assert.Equal(new long[] { 0, 0, 3, 6 }, f.ReadNotes("direct").Select(static n => n.Tick));
    }

    [Fact]
    public void GeneratedNotesUseFrozenSourceOrderRatherThanNewStableIds()
    {
        using var f = new Fixture();
        f.Midi.Notes.Add(new(f.Project) { StartTick = 100, LengthTicks = 1, Key = 12, NoteOnOrder = 900_000, NoteOffOrder = 900_100 });
        f.Midi.ChannelEvents.Add(new(f.Project) { Tick = 0, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 0, Order = 901_000 });
        var edit = f.Notes("direct", new() { MaximumCandidates = 2, TickExpression = "=i*10" }).Prepare(f.Project);
        edit.Apply(f.Project);
        var notes = f.MidiTrack.Segments[0].Notes.Skip(1).ToArray();
        Assert.Equal(new long[] { 901_001, 901_003 }, notes.Select(static n => n.NoteOnOrder));
        Assert.Equal(new long[] { 901_002, 901_004 }, notes.Select(static n => n.NoteOffOrder));
        Assert.All(notes, static n => Assert.Equal(0, n.NoteOffVelocity));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void EventGenerationOverwritesOnlyHitTargetsAndLastCandidateWins(string kind)
    {
        using var f = new Fixture();
        var seed = f.Events(kind, new() { MaximumCandidates = 1, InitialValue = 50 }).Prepare(f.Project);
        seed.Apply(f.Project);
        var edit = f.Events(kind, new()
        { MaximumCandidates = 6, ValueExpression = "=10+i", TickExpression = "=2-i%3" }).Prepare(f.Project);
        edit.Apply(f.Project);
        var points = f.ReadPoints(kind).OrderBy(static p => p.Tick).ToArray();
        Assert.Equal(new long[] { 0, 1, 2 }, points.Select(static p => p.Tick));
        Assert.Equal(new double[] { 15, 14, 13 }, points.Select(static p => p.Value));
        Assert.Equal(3, Selection(edit).ResultSelectionIds.Count);
        edit.Undo(f.Project); Assert.Equal(50, Assert.Single(f.ReadPoints(kind)).Value);
        edit.Apply(f.Project); Assert.Equal(points, f.ReadPoints(kind).OrderBy(static p => p.Tick));
    }

    [Fact]
    public void DirectEventsPreserveUnhitImportedDuplicatesAndRemoveEveryHitDuplicate()
    {
        using var f = new Fixture();
        foreach (long tick in new long[] { 0, 0, 10, 10 })
            f.Midi.ChannelEvents.Add(new(f.Project) { Tick = tick, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 50 });
        f.Midi.ChannelEvents.Add(new(f.Project) { Tick = 0, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 7, Data2 = 90 });
        var edit = f.Events("direct", new() { MaximumCandidates = 1, InitialValue = 25 }).Prepare(f.Project);
        edit.Apply(f.Project);
        var events = f.MidiTrack.Segments[0].ChannelEvents;
        Assert.Equal(4, events.Count);
        Assert.Equal(2, events.Count(static p => p.Tick == 10));
        Assert.Equal(25, Assert.Single(events, static p => p.Tick == 0 && p.Data1 == 11).Data2);
        Assert.Equal(90, Assert.Single(events, static p => p.Data1 == 7).Data2);
    }

    [Fact]
    public void LogicalDoubleAndSparseEnumNormalizeBeforeNextIteration()
    {
        using var f = new Fixture();
        f.Parameter.Type = LogicalParameterType.Double; f.Parameter.Maximum = 1;
        var edit = f.Events("logical", new()
        { MaximumCandidates = 3, InitialValue = 0.1, ValueExpression = "=p0+0.1", TickExpression = "=i" }).Prepare(f.Project);
        edit.Apply(f.Project);
        Assert.Equal(new[] { 0.2, 0.3, 0.4 }, f.ReadPoints("logical").Select(static p => Math.Round(p.Value, 5)));
        edit.Undo(f.Project);
        f.Parameter.Type = LogicalParameterType.Enum; f.Parameter.Maximum = 10; f.Parameter.UsesExplicitEnumValues = true;
        f.Parameter.EnumItems.AddRange([new(f.Project) { Name = "Low", Value = 0 }, new(f.Project) { Name = "High", Value = 10 }]);
        edit = f.Events("logical", new()
        { MaximumCandidates = 3, ValueExpression = "=p0+6", TickExpression = "=i" }).Prepare(f.Project);
        edit.Apply(f.Project);
        Assert.All(f.ReadPoints("logical"), static p => Assert.Equal(10, p.Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TemplateCompositeLanesPreserveTheOtherValue(bool bank)
    {
        using var f = new Fixture();
        var voice = f.Instrument.SubVoices[0];
        voice.Events.Add(bank ? TemplateEvent.Bank(f.Project, 0, 3, 4) : new TemplateEvent(f.Project)
        { Kind = TemplateEventKind.PitchBendRange, Tick = 0, Value = 12, SecondaryValue = 25 });
        var target = bank ? MidiValueTarget.BankLsb : MidiValueTarget.PitchBendRangeCents;
        var edit = ProjectDomainEditCommands.GenerateTemplateEventPoints(f.Instrument.Id, voice.Id, target,
            new() { MaximumCandidates = 1, InitialValue = 7 }).Prepare(f.Project);
        edit.Apply(f.Project);
        var value = Assert.Single(f.Instrument.SubVoices[0].Events);
        Assert.Equal(bank ? 3 : 12, value.Value);
        Assert.Equal(7, value.SecondaryValue);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void ArithmeticFailureIsAtomicForAllOwners(string kind)
    {
        using var f = new Fixture();
        long allocator = f.Project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => f.Notes(kind, new()
        { MaximumCandidates = 50, TickExpression = "=i", VelocityExpression = "=i==20 ? Sqrt(-1) : 64" }).Prepare(f.Project));
        Assert.Empty(f.ReadNotes(kind)); Assert.Equal(allocator, f.Project.NextStableId);
        Assert.Throws<OverflowException>(() => f.Notes(kind, new()
        { MaximumCandidates = 1, TickExpression = "=1e30" }).Prepare(f.Project));
        Assert.Empty(f.ReadNotes(kind)); Assert.Equal(allocator, f.Project.NextStableId);
    }

    [Fact]
    public void CancelDuringGenerationPublishesNothing()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        long allocator = f.Project.NextStableId;
        var command = Assert.IsAssignableFrom<IProgressReportingProjectEditCommand>(f.Notes("direct", new()
        { MaximumCandidates = 100_000, TickExpression = "=i*2" }));
        var progress = new ImmediateProgress(value => { if (value.Phase == TimelineEditPreparationPhase.Planning && value.Completed > 256) cancel.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => command.Prepare(f.Project, cancel.Token, progress));
        Assert.Empty(f.ReadNotes("direct")); Assert.Equal(allocator, f.Project.NextStableId);
    }

    [Fact]
    public void InvalidSafetyLimitsAndSourceRevisionRaceAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NoteGenerationOptions { MaximumCandidates = 16_777_217 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventGenerationOptions { MaximumCandidates = 0 }.Validate());
        using var f = new Fixture();
        var prepared = f.Notes("direct", new() { MaximumCandidates = 1 }).Prepare(f.Project);
        f.Midi.Notes.Add(new(f.Project) { StartTick = 20, LengthTicks = 1, Key = 55 });
        var gate = Assert.IsAssignableFrom<IPreparedProjectEditPublicationGate>(prepared);
        Assert.Throws<InvalidOperationException>(() => gate.ValidateForPublication(f.Project));
        Assert.Single(f.Midi.Notes);
    }

    [Fact]
    public void CheckedOverflowPrecedesMaximumTickEarlyStop()
    {
        using var f = new Fixture();
        long baseTick = long.MaxValue - 100;
        Assert.Throws<OverflowException>(() => f.Notes("direct", new()
        { BaseTick = baseTick, MaximumCandidates = 1, MaximumRelativeStartTick = 1, TickExpression = "=1024" }).Prepare(f.Project));
        Assert.Throws<OverflowException>(() => f.Events("direct", new()
        { BaseTick = baseTick, MaximumCandidates = 1, MaximumRelativeStartTick = 1, TickExpression = "=1024" }).Prepare(f.Project));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void HundredThousandBackwardsCandidatesUseBoundedMemoryAndRemainQueryable(string kind)
    {
        using var f = new Fixture();
        var resources = new BoundedEditResources(new PagedEditResourceBudget(maximumResidentBytes: 512 * 1024));
        using var scope = BulkEditPreparationContext.Enter(resources: resources, project: f.Project);
        var command = f.Notes(kind, new()
        { MaximumCandidates = 100_000, TickExpression = "=100000-i", KeyExpression = "=i%128", InitialGate = 1 });
        var prepared = command.Prepare(f.Project);
        prepared.Apply(f.Project);
        Assert.Equal(100_000, Selection(prepared).ResultSelectionIds.Count);
        Assert.True(resources.PeakResidentBytes <= resources.Budget.MaximumResidentBytes);
        Assert.True(resources.PeakWorkingBytes <= resources.Budget.MaximumWorkingBytes);
        Assert.True(resources.PeakSpillBytes > 0);
        Assert.True(resources.PeakSpillBytes <= resources.Budget.MaximumSpillBytes);
        int count = kind switch
        {
            "direct" => f.MidiTrack.Segments[0].Notes.QueryValues(99_900, 100_001).Count(),
            "logical" => f.LogicalTrack.Segments[0].Notes.CreateQuerySnapshot().EnumerateAll().Count(static n => n.StartTick >= 99_900),
            _ => f.Instrument.SubVoices[0].Events.CreateQuerySnapshot().EnumerateAll().Count(static n => n.Tick >= 99_900)
        };
        Assert.Equal(101, count);
        prepared.Undo(f.Project); Assert.Empty(f.ReadNotes(kind));
    }

    [Fact]
    public void ResultBudgetFailureAndEmptyResultDoNotPublishObjects()
    {
        using var f = new Fixture();
        var resources = new BoundedEditResources(new PagedEditResourceBudget(maximumRecordCount: 100));
        using (var scope = BulkEditPreparationContext.Enter(resources: resources, project: f.Project))
        {
            Assert.Throws<InvalidOperationException>(() => f.Notes("direct", new()
            { MaximumCandidates = 101, TickExpression = "=i" }).Prepare(f.Project));
            Assert.Empty(f.Midi.Notes);
        }
        foreach (string kind in new[] { "direct", "logical", "template" })
        {
            var prepared = f.Notes(kind, new()
            { MaximumCandidates = 1, MaximumRelativeStartTick = 0, TickExpression = "=10" }).Prepare(f.Project);
            Assert.Empty(Selection(prepared).ResultSelectionIds);
        }
    }

    [Fact]
    public void ProgressReportsAttemptedCandidatesAndExactRetainedCountWithoutPretendingBeforeReduction()
    {
        using var f = new Fixture();
        List<TimelineEditPreparationProgress> reports = [];
        var command = Assert.IsAssignableFrom<IProgressReportingProjectEditCommand>(f.Notes("direct",
            new() { MaximumCandidates = 32 }));
        var prepared = command.Prepare(f.Project, default, new ImmediateProgress(reports.Add));
        Assert.Contains(reports, static value => value.Detail is { } text && text.Contains("retained: pending", StringComparison.Ordinal));
        Assert.Equal("Candidates: 32; retained: 1", reports[^1].Detail);
        Assert.Equal(reports[^1].Detail, reports[^1].InRange(0, 0.95).Detail);
        Assert.Equal(reports[^1].Detail, reports[^1].InWorkRange(0, 0.95).Detail);
        Assert.Single(Selection(prepared).ResultSelectionIds);
        Assert.Null(new TimelineEditPreparationProgress(TimelineEditPreparationPhase.Planning, 0, 1).Detail);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void EntirelyDiscardedNoteCandidatesAreRevisionGatedNoOpNotAllocatorOrSourceChanges(string kind)
    {
        using var f = new Fixture();
        f.Notes(kind, new() { MaximumCandidates = 1, InitialKey = 60 }).Prepare(f.Project).Apply(f.Project);
        Note original = Assert.Single(f.ReadNotes(kind));
        long allocator = f.Project.NextStableId;
        var prepared = f.Notes(kind, new() { MaximumCandidates = 1000, InitialKey = 60 }).Prepare(f.Project);
        Assert.False(prepared.HasChanges);
        Assert.Empty(Selection(prepared).ResultSelectionIds);
        prepared.Apply(f.Project);
        Assert.Equal(allocator, f.Project.NextStableId);
        Assert.Equal([original], f.ReadNotes(kind));
        prepared.Undo(f.Project);
        Assert.Equal(allocator, f.Project.NextStableId);
        Assert.Equal([original], f.ReadNotes(kind));

        f.Notes(kind, new() { BaseTick = 10, MaximumCandidates = 1 }).Prepare(f.Project).Apply(f.Project);
        Assert.Throws<InvalidOperationException>(() =>
            Assert.IsAssignableFrom<IPreparedProjectEditPublicationGate>(prepared).ValidateForPublication(f.Project));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("logical")]
    [InlineData("template")]
    public void EarlyStoppedEventGenerationIsEmptyRevisionGatedNoOp(string kind)
    {
        using var f = new Fixture();
        long allocator = f.Project.NextStableId;
        var prepared = f.Events(kind, new()
        { MaximumCandidates = 20, MaximumRelativeStartTick = 5, TickExpression = "=10" }).Prepare(f.Project);
        Assert.False(prepared.HasChanges);
        Assert.Empty(Selection(prepared).ResultSelectionIds);
        prepared.Apply(f.Project);
        Assert.Empty(f.ReadPoints(kind));
        Assert.Equal(allocator, f.Project.NextStableId);
        f.Events(kind, new() { MaximumCandidates = 1 }).Prepare(f.Project).Apply(f.Project);
        Assert.Throws<InvalidOperationException>(() =>
            Assert.IsAssignableFrom<IPreparedProjectEditPublicationGate>(prepared).ValidateForPublication(f.Project));
    }

    private static PreparedTimelineSelection Selection(IPreparedProjectEdit edit) =>
        Assert.IsAssignableFrom<IPreparedTimelineSelectionEdit>(edit).PreparedSelection;
    private sealed class ImmediateProgress(Action<TimelineEditPreparationProgress> callback) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => callback(value); }
    private readonly record struct Note(MidoraId Id, long Tick, long Gate, int Key, int Velocity);
    private readonly record struct Point(MidoraId Id, long Tick, double Value);

    private sealed class Fixture : IDisposable
    {
        public MidoraProject Project { get; } = new(480);
        public EventInstrument Instrument { get; }
        public LogicalTrack LogicalTrack { get; }
        public Segment Logical { get; }
        public LogicalParameterDefinition Parameter { get; }
        public LogicalParameterLane Lane { get; }
        public PureMidiTrack MidiTrack { get; }
        public MidiSegment Midi { get; }
        public Fixture()
        {
            Instrument = EventInstrumentLibrary.Create(Project, "Instrument");
            LogicalTrack = new(Project) { Name = "Logical" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(Project, LogicalTrack, Instrument.Id);
            Logical = new(Project) { LengthTicks = 1_000_000 }; LogicalTrack.Segments.Add(Logical);
            Parameter = new(Project) { Name = "Parameter", Type = LogicalParameterType.Integer, Minimum = 0, Maximum = 127 };
            Instrument.LogicalParameters.Add(Parameter);
            Lane = new(Project) { ParameterId = Parameter.Id }; Logical.ParameterLanes.Add(Lane);
            MidiChannelRoot root = new(Project) { Name = "Root" }; Project.MidiChannelRoots.Add(root);
            MidiTrack = new(Project) { Name = "MIDI", MidiChannelRootId = root.Id }; Project.PureMidiTracks.Add(MidiTrack);
            Project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, MidiTrack.Id));
            Midi = new(Project) { LengthTicks = 1_000_000 }; MidiTrack.Segments.Add(Midi);
        }
        public IProjectEditCommand Notes(string kind, NoteGenerationOptions options) => kind switch
        {
            "direct" => ProjectDomainEditCommands.GenerateDirectMidiNotes(Midi.Id, options),
            "logical" => ProjectDomainEditCommands.GenerateLogicalNotes(Logical.Id, options),
            _ => ProjectDomainEditCommands.GenerateTemplateNotes(Instrument.Id, Instrument.SubVoices[0].Id, options)
        };
        public IProjectEditCommand Events(string kind, EventGenerationOptions options) => kind switch
        {
            "direct" => ProjectDomainEditCommands.GenerateDirectMidiEventPoints(Midi.Id, DirectMidiChannelEventKind.ControlChange, 11, options),
            "logical" => ProjectDomainEditCommands.GenerateLogicalParameterPoints(Logical.Id, Lane.Id, options),
            _ => ProjectDomainEditCommands.GenerateTemplateEventPoints(Instrument.Id, Instrument.SubVoices[0].Id, MidiValueTarget.ControlChange(11), options)
        };
        public Note[] ReadNotes(string kind) => kind switch
        {
            "direct" => MidiTrack.Segments[0].Notes.Select(static n => new Note(n.Id, n.StartTick, n.LengthTicks, n.Key, n.NoteOnVelocity)).ToArray(),
            "logical" => LogicalTrack.Segments[0].Notes.Select(static n => new Note(n.Id, n.StartTick, n.LengthTicks, n.Note, n.Velocity)).ToArray(),
            _ => Instrument.SubVoices[0].Events.Where(static e => e.Kind == TemplateEventKind.Note)
                .Select(static n => new Note(n.Id, n.Tick, n.LengthTicks, n.Number, n.Value)).ToArray()
        };
        public Point[] ReadPoints(string kind) => kind switch
        {
            "direct" => MidiTrack.Segments[0].ChannelEvents.Select(static p => new Point(p.Id, p.Tick, p.Data2)).ToArray(),
            "logical" => LogicalTrack.Segments[0].ParameterLanes[0].Points.Select(static p => new Point(p.Id, p.Tick, p.Value)).ToArray(),
            _ => Instrument.SubVoices[0].Events.Where(static e => e.Kind != TemplateEventKind.Note)
                .Select(static p => new Point(p.Id, p.Tick, p.Value)).ToArray()
        };
        public void Dispose() => Project.Dispose();
    }
}
