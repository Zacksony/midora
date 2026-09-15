using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedPointCommandTests
{
    [Fact]
    public void LargeLogicalDrawPreservesExistingIdsAndInsertsNewPointsInFormalOrder()
    {
        using var f = new Fixture();
        var (track, segment, lane, _) = f.Logical();
        lane.Points.AddRange(Enumerable.Range(0, 4_100).Select(i => new CurvePoint(f.Project, i * 4, 0.5, CurveInterpolation.Step)));
        var original = lane.Points.CreateQuerySnapshot().EnumerateAll().ToArray();
        var input = Enumerable.Range(0, 8_200).Select(i => new LogicalParameterPointEdit(i * 2, 0.75)).ToArray();
        var edit = ProjectDomainEditCommands.UpsertLogicalParameterPoints(segment.Id, lane.Id, input).Prepare(f.Project);
        Assert.Same(segment, track.Segments[0]);
        edit.Apply(f.Project);
        var current = track.Segments[0].ParameterLanes[0].Points.CreateQuerySnapshot();
        Assert.Equal(8_200, current.Count);
        for (int i = 0; i < current.Count; i++)
        {
            var point = current.GetByOrdinal(i);
            Assert.Equal(i * 2, point.Tick); Assert.Equal(0.75, point.Value);
            if (i % 2 == 0) Assert.Equal(original[i / 2].Id, point.Id);
        }
        edit.Undo(f.Project); Assert.Same(segment, track.Segments[0]);
        Assert.Equal(original, lane.Points.CreateQuerySnapshot().EnumerateAll());
        edit.Apply(f.Project); Assert.Equal(8_200, track.Segments[0].ParameterLanes[0].Points.Count);
    }

    [Fact]
    public void LargeTemplateLaneDeletionRetainsTheOtherBankComponentAndUndoIdentity()
    {
        using var f = new Fixture();
        var instrument = EventInstrumentLibrary.Create(f.Project, "Instrument");
        var voice = instrument.SubVoices[0];
        voice.Events.AddRange(Enumerable.Range(0, 4_100).Select(i => TemplateEvent.Bank(f.Project, i, 3, 4)));
        var before = voice.Events.CreateQuerySnapshot().EnumerateAll().ToArray();
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.DeleteSubVoiceEventLane(instrument.Id, voice.Id, MidiValueTarget.BankMsb, false).Prepare(f.Project));
        var edit = ProjectDomainEditCommands.DeleteSubVoiceEventLane(instrument.Id, voice.Id, MidiValueTarget.BankMsb, true).Prepare(f.Project);
        edit.Apply(f.Project);
        Assert.Equal(before.Length, instrument.SubVoices[0].Events.Count);
        Assert.All(instrument.SubVoices[0].Events.CreateQuerySnapshot().EnumerateAll(), point =>
        {
            Assert.False(point.HasBankMsb); Assert.True(point.HasBankLsb); Assert.Equal(4, point.SecondaryValue);
        });
        Assert.DoesNotContain(instrument.SubVoices[0].EventMappings, m => m.Target == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.BankMsb));
        edit.Undo(f.Project); Assert.Same(voice, instrument.SubVoices[0]);
        Assert.Equal(before, voice.Events.CreateQuerySnapshot().EnumerateAll());
    }

    [Fact]
    public void SingleTemplatePointEditInLargeOwnerPreservesTheOtherBankValueAndCreatesANewTargetMapping()
    {
        using var f = new Fixture();
        var instrument = EventInstrumentLibrary.Create(f.Project, "Instrument");
        var voice = instrument.SubVoices[0];
        voice.Events.AddRange(Enumerable.Range(0, 4_100).Select(i => TemplateEvent.ControlChange(f.Project, i, 11, 50)));
        var source = voice.Events.CreateQuerySnapshot();
        var id = source.GetByOrdinal(0).Id;
        long allocator = f.Project.NextStableId;
        var edit = ProjectDomainEditCommands.UpdateTemplateControlChange(instrument.Id, voice.Id, id, 2, 1, 70).Prepare(f.Project);
        Assert.Equal(allocator, f.Project.NextStableId);
        edit.Apply(f.Project);
        var current = instrument.SubVoices[0];
        Assert.Equal(source.Count, current.Events.Count);
        Assert.True(current.Events.TryGetById(id, out var updated));
        Assert.Equal(1, updated!.Number); Assert.Equal(70, updated.Value);
        Assert.Contains(current.EventMappings, m => m.Target == TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.ControlChange(1)));
        edit.Undo(f.Project); Assert.Same(voice, instrument.SubVoices[0]);
    }

    [Fact]
    public void LargeSubVoiceMovePreservesUneditedNotesAndReplacesOnlyTargetedEvents()
    {
        using var f = new Fixture();
        var p = f.Project;
        EventInstrument instrument = new(p) { TemplateLengthTicks = 200_000, Name = "Instrument" };
        SubVoice voice = new(p) { Name = "SubVoice" };
        p.EventInstruments.Add(instrument); instrument.SubVoices.Add(voice);
        voice.Events.AddRange(Enumerable.Range(0, 4_101).Select(i => TemplateEvent.ControlChange(p, i * 10, 11, 50)));
        TemplateEvent note = TemplateEvent.Note(p, 10, 5, 60, 100);
        voice.Events.Add(note);
        var ids = voice.Events.Take(4_100).Select(static v => v.Id).ToArray();
        var before = voice.Events.CreateQuerySnapshot().EnumerateAll().ToArray();
        var command = ProjectDomainEditCommands.AdjustSubVoiceEventPoints(instrument.Id, voice.Id,
            ids, MidiValueTarget.ControlChange(11), 10, 20, false);
        var prepared = command.Prepare(p);
        Assert.Same(voice, instrument.SubVoices[0]);
        prepared.Apply(p);
        Assert.Equal(4_101, instrument.SubVoices[0].Events.Count);
        Assert.Equal(4_100, instrument.SubVoices[0].Events.Count(static e => e.Kind == TemplateEventKind.ControlChange));
        Assert.All(instrument.SubVoices[0].Events.Where(static e => e.Kind == TemplateEventKind.ControlChange),
            static e => Assert.Equal(70, e.Value));
        Assert.True(instrument.SubVoices[0].Events.TryGetById(note.Id, out _));
        Assert.Equal(before, voice.Events.CreateQuerySnapshot().EnumerateAll());
        prepared.Undo(p); Assert.Same(voice, instrument.SubVoices[0]);
        prepared.Apply(p); Assert.Equal(4_101, instrument.SubVoices[0].Events.Count);
        Assert.True(f.Resources.PeakResidentBytes <= f.Resources.Budget.MaximumResidentBytes);
        Assert.True(f.Resources.PeakWorkingBytes <= f.Resources.Budget.MaximumWorkingBytes);
    }

    [Fact]
    public void LargeLogicalPointMoveOverridesIncumbentAndUndoRestoresExactSourceRoot()
    {
        using var f = new Fixture();
        var (track, segment, first, _) = f.Logical();
        first.Points.AddRange(Enumerable.Range(0, 4_101).Select(i => new CurvePoint(f.Project, i * 10, 0.5, CurveInterpolation.Step)));
        var ids = first.Points.Take(4_100).Select(static p => p.Id).ToArray();
        var prepared = ProjectDomainEditCommands.MoveLogicalParameterPoints(segment.Id, first.Id, ids, 10).Prepare(f.Project);
        prepared.Apply(f.Project);
        Assert.Equal(4_100, track.Segments[0].ParameterLanes[0].Points.Count);
        Assert.Equal(ids, track.Segments[0].ParameterLanes[0].Points.Select(static p => p.Id));
        Assert.Equal(10, track.Segments[0].ParameterLanes[0].Points[0].Tick);
        prepared.Undo(f.Project); Assert.Same(segment, track.Segments[0]);
    }

    [Fact]
    public void LargeCrossLaneQuantizeUsesFormalLaterWinsAndDoesNotTouchUnrelatedDuplicates()
    {
        using var f = new Fixture();
        var (track, segment, first, second) = f.Logical();
        first.Points.AddRange(Enumerable.Range(0, 2_050).Select(i => new CurvePoint(f.Project, 11 + i * 20, 0.5, CurveInterpolation.Step)));
        second.Points.AddRange(Enumerable.Range(0, 2_050).Select(i => new CurvePoint(f.Project, 11 + i * 20, 0.5, CurveInterpolation.Step)));
        var ids = first.Points.Concat(second.Points).Select(static p => p.Id).ToArray();
        var incumbent = new CurvePoint(f.Project, 10, 0.8, CurveInterpolation.Step);
        first.Points.Add(incumbent);
        first.Points.AddRange([new(f.Project, 100_000, 0.2, CurveInterpolation.Step), new(f.Project, 100_000, 0.3, CurveInterpolation.Step)]);
        var command = ProjectDomainEditCommands.QuantizeLogicalParameterPoints(segment.Id, ids, TimelineQuantizeGrid.FromCustomTicks(10));
        var prepared = command.Prepare(f.Project); prepared.Apply(f.Project);
        var current = track.Segments[0];
        Assert.Equal(2_052, current.ParameterLanes[0].Points.Count);
        Assert.Equal(2_050, current.ParameterLanes[1].Points.Count);
        Assert.False(current.ParameterLanes[0].Points.TryGetById(ids[0], out _));
        Assert.True(current.ParameterLanes[0].Points.TryGetById(incumbent.Id, out _));
        Assert.Equal(4_099, command.ResultSelectionIds.Count);
        prepared.Undo(f.Project); Assert.Same(segment, track.Segments[0]);
    }

    [Fact]
    public void CancelDuringPointPlanningReleasesScratchAndKeepsSourceAndAllocator()
    {
        using var f = new Fixture();
        var (_, segment, first, _) = f.Logical();
        first.Points.AddRange(Enumerable.Range(0, 4_100).Select(i => new CurvePoint(f.Project, i, 0.5, CurveInterpolation.Step)));
        var ids = first.Points.Select(static p => p.Id).ToArray();
        long allocator = f.Project.NextStableId;
        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Phase == TimelineEditPreparationPhase.ResolvingCollisions) cancel.Cancel(); });
        var command = Assert.IsAssignableFrom<IProgressReportingProjectEditCommand>(
            ProjectDomainEditCommands.DuplicateLogicalParameterPoints(segment.Id, first.Id, ids, 100_000, 0));
        Assert.Throws<OperationCanceledException>(() => command.Prepare(f.Project, cancel.Token, progress));
        Assert.Equal(allocator, f.Project.NextStableId);
        Assert.Equal(4_100, first.Points.Count);
        // The completed, reusable ID-only source index may survive cancellation;
        // provisional musical/selection pages must not. It is not an edit result.
        Assert.Single(Directory.EnumerateDirectories(f.Path));
        Assert.Equal(4_100L * System.Runtime.CompilerServices.Unsafe.SizeOf<TimelineIdOrdinal>(), f.Resources.SpillBytes);
        Assert.True(first.Points.CreateQuerySnapshot().TryFindOrdinalById(ids[^1], out int ordinal));
        Assert.Equal(4_099, ordinal);
        Assert.Equal(0, f.Resources.ResidentBytes);
        Assert.Equal(0, f.Resources.WorkingBytes);
        f.Project.Dispose();
        Assert.Empty(Directory.EnumerateDirectories(f.Path));
        Assert.Equal(0, f.Resources.SpillBytes);
    }

    private sealed class CallbackProgress(Action<TimelineEditPreparationProgress> action) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => action(value); }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midora-bounded-points-" + Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480);
        public BoundedEditResources Resources { get; }
        private readonly BulkEditPreparationContext _scope;
        public Fixture()
        {
            Directory.CreateDirectory(Path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024, maximumWorkingBytes: 8 * 1024 * 1024), Path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
        }
        public (LogicalTrack Track, Segment Segment, LogicalParameterLane First, LogicalParameterLane Second) Logical()
        {
            EventInstrument instrument = EventInstrumentLibrary.Create(Project, "Instrument");
            LogicalTrack track = new(Project) { Name = "Track" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(Project, track, instrument.Id);
            Segment segment = new(Project) { LengthTicks = 200_000 }; track.Segments.Add(segment);
            LogicalParameterDefinition definition = new(Project) { Name = "Parameter", Type = LogicalParameterType.Double, Minimum = 0, Maximum = 1 };
            LogicalParameterDefinition other = new(Project) { Name = "Other", Type = LogicalParameterType.Double, Minimum = 0, Maximum = 1 };
            instrument.LogicalParameters.AddRange([definition, other]);
            LogicalParameterLane first = new(Project) { ParameterId = definition.Id }, second = new(Project) { ParameterId = other.Id };
            segment.ParameterLanes.AddRange([first, second]);
            return (track, segment, first, second);
        }
        public void Dispose() { _scope.Dispose(); Project.Dispose(); Directory.Delete(Path, true); }
    }
}
