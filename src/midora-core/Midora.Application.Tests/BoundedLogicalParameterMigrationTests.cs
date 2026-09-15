using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class BoundedLogicalParameterMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceMutationDuringPreparationIsRejectedBeforeAnyLaneRootIsPublished(bool changeAllocator)
    {
        using var fixture = new Fixture(3_000);
        Segment original = fixture.Project.Tracks[0].Segments[0];
        bool changed = false;
        using var scope = BulkEditPreparationContext.Enter(progress: new InlineProgress(value =>
        {
            if (changed || value.Completed == 0) return;
            if (changeAllocator) fixture.Project.AllocateStableId();
            else fixture.Parameter.Maximum = 9;
            changed = true;
        }), resources: fixture.Resources, project: fixture.Project);
        var prepared = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, fixture.Parameter.Id,
            new(LogicalParameterType.Integer, 0, 5, 0, 5, 0, false, []), LogicalParameterLaneRebindMode.Clamp, false).Prepare(fixture.Project);
        Assert.True(changed);
        Assert.Throws<InvalidOperationException>(() => prepared.Apply(fixture.Project));
        Assert.Same(original, fixture.Project.Tracks[0].Segments[0]);
        Assert.Equal(changeAllocator ? 10 : 9, fixture.Parameter.Maximum);
        Assert.Equal(LogicalParameterType.Double, fixture.Parameter.Type);
    }

    [Fact]
    public void DefinitionMigrationPreservesUnrelatedFormalSegmentOrder()
    {
        using var fixture = new Fixture(3_000);
        var track = fixture.Project.Tracks[0]; var source = track.Segments[0]; source.ProjectStartTick = 1_000;
        Segment earlier = new(fixture.Project) { ProjectStartTick = 0, LengthTicks = 500 }; track.Segments.Add(earlier);
        var edit = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, fixture.Parameter.Id,
            new(LogicalParameterType.Integer, 0, 5, 0, 5, 0, false, []), LogicalParameterLaneRebindMode.Clamp, false).Prepare(fixture.Project);
        edit.Apply(fixture.Project);
        Assert.Equal([source.Id, earlier.Id], track.Segments.Select(segment => segment.Id));
        Assert.Same(earlier, track.Segments[1]);
        edit.Undo(fixture.Project); Assert.Equal([source, earlier], track.Segments);
    }

    [Fact]
    public void LargeEnumMigrationReordersExistingIdsWithoutMutatingOldDefinitionAndAllocatesOnlyNewItems()
    {
        using var fixture = new Fixture(3_000);
        var parameter = fixture.Parameter;
        parameter.Type = LogicalParameterType.Enum; parameter.Minimum = parameter.DisplayMinimum = 0;
        parameter.Maximum = parameter.DisplayMaximum = 2;
        LogicalParameterEnumItem a = new(fixture.Project) { Name = "A", Value = 0 };
        LogicalParameterEnumItem b = new(fixture.Project) { Name = "B", Value = 1 };
        LogicalParameterEnumItem c = new(fixture.Project) { Name = "C", Value = 2 };
        parameter.EnumItems.AddRange([a, b, c]);
        foreach (var track in fixture.Project.Tracks)
        {
            var points = track.Segments[0].ParameterLanes[0].Points;
            for (int i = 0; i < points.Count; i++) points[i] = points[i] with { Value = points[i].Tick % 3 };
        }
        long nextId = fixture.Project.NextStableId;
        var edit = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, parameter.Id,
            new(LogicalParameterType.Enum, 0, 20, 0, 20, 10, true,
                [new(c.Id, "Third", 10), new(a.Id, "First", 0), new(null, "New", 20)]), LogicalParameterLaneRebindMode.Clamp, true).Prepare(fixture.Project);
        Assert.Equal(nextId, fixture.Project.NextStableId);
        edit.Apply(fixture.Project); var result = fixture.Instrument.LogicalParameters[0];
        Assert.Equal([c.Id, a.Id, new MidoraId(nextId)], result.EnumItems.Select(item => item.Id));
        Assert.Equal(["Third", "First", "New"], result.EnumItems.Select(item => item.Name));
        Assert.Equal(["A", "B", "C"], parameter.EnumItems.Select(item => item.Name));
        Assert.Equal(nextId + 1, fixture.Project.NextStableId);
        edit.Undo(fixture.Project); Assert.Same(parameter, fixture.Instrument.LogicalParameters[0]);
        edit.Apply(fixture.Project); Assert.Same(result, fixture.Instrument.LogicalParameters[0]);
    }

    [Fact]
    public void IdenticalLargeDefinitionDoesNotPublishAnyOwnerOrAllocateIds()
    {
        using var fixture = new Fixture(3_000);
        var oldSegments = fixture.Project.Tracks.Select(track => track.Segments[0]).ToArray();
        long nextId = fixture.Project.NextStableId;
        var edit = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, fixture.Parameter.Id,
            new(LogicalParameterType.Double, -10, 10, -10, 10, 0, false, []), LogicalParameterLaneRebindMode.Clamp, false).Prepare(fixture.Project);
        Assert.False(edit.HasChanges); edit.Apply(fixture.Project);
        Assert.Same(fixture.Parameter, fixture.Instrument.LogicalParameters[0]); Assert.Equal(nextId, fixture.Project.NextStableId);
        for (int i = 0; i < 2; i++) Assert.Same(oldSegments[i], fixture.Project.Tracks[i].Segments[0]);
    }

    [Fact]
    public void OversizedEnumMetadataIsRejectedBeforeFreezingOrEnumeratingInput()
    {
        var values = new OversizedEnumItems();
        var edit = new LogicalParameterDefinitionEdit(LogicalParameterType.Enum, 0, int.MaxValue, 0, int.MaxValue, 0, false, values);
        var error = Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
            new(1), new(2), edit, LogicalParameterLaneRebindMode.Clamp, true));
        Assert.Contains("working-memory budget", error.Message);
    }

    private sealed class OversizedEnumItems : IReadOnlyList<LogicalParameterEnumItemDefinitionEdit>
    {
        public int Count => int.MaxValue;
        public LogicalParameterEnumItemDefinitionEdit this[int index] => throw new InvalidOperationException("Input must not be read.");
        public IEnumerator<LogicalParameterEnumItemDefinitionEdit> GetEnumerator() => throw new InvalidOperationException("Input must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SixThousandPointDefinitionMigrationIsDetachedPreservesIdsAndRestoresRoots(bool useEnum, bool discard)
    {
        using var fixture = new Fixture(3_000);
        using var compilation = new ProjectCompilationSession(fixture.Project);
        // History owns the detached before/after roots. Release it before the
        // fixture removes its spill directory, as the real workspace does.
        using ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        LogicalParameterDefinition oldDefinition = fixture.Parameter;
        Segment[] oldSegments = fixture.Project.Tracks.Select(track => track.Segments[0]).ToArray();
        long nextId = fixture.Project.NextStableId;
        var edit = new LogicalParameterDefinitionEdit(useEnum ? LogicalParameterType.Enum : LogicalParameterType.Integer,
            0, useEnum ? 20 : 5, 0, useEnum ? 20 : 5, 0, useEnum,
            useEnum ? [new(null, "Low", 0), new(null, "Medium", 10), new(null, "High", 20)] : []);
        var command = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, fixture.Parameter.Id,
            edit, discard ? LogicalParameterLaneRebindMode.DiscardInvalidValues : LogicalParameterLaneRebindMode.Clamp,
            enumSemanticWarningAcknowledged: useEnum, name: "Migrated");
        document.Execute(command);
        LogicalParameterDefinition actualDefinition = fixture.Instrument.LogicalParameters[0];
        Assert.NotSame(oldDefinition, actualDefinition); Assert.Equal("Amount", oldDefinition.Name);
        Assert.Equal(LogicalParameterType.Double, oldDefinition.Type); Assert.Equal("Migrated", actualDefinition.Name);
        Assert.Equal(nextId + (useEnum ? 3 : 0), fixture.Project.NextStableId);
        for (int laneIndex = 0; laneIndex < 2; laneIndex++)
        {
            var oldValues = oldSegments[laneIndex].ParameterLanes[0].Points.CreateQuerySnapshot().EnumerateAll();
            var expected = oldValues.Select(value => (Value: value, Converted: Convert(value.Value)))
                .Where(entry => entry.Converted.HasValue).Select(entry => entry.Value with { Value = entry.Converted!.Value });
            Assert.Equal(expected, fixture.Project.Tracks[laneIndex].Segments[0].ParameterLanes[0].Points.CreateQuerySnapshot().EnumerateAll());
        }
        AssertMatchesFull(compilation);
        Segment[] results = fixture.Project.Tracks.Select(track => track.Segments[0]).ToArray();
        document.Undo(); Assert.Same(oldDefinition, fixture.Instrument.LogicalParameters[0]);
        for (int i = 0; i < 2; i++) Assert.Same(oldSegments[i], fixture.Project.Tracks[i].Segments[0]);
        AssertMatchesFull(compilation);
        document.Redo(); Assert.Same(actualDefinition, fixture.Instrument.LogicalParameters[0]);
        for (int i = 0; i < 2; i++) Assert.Same(results[i], fixture.Project.Tracks[i].Segments[0]);
        AssertMatchesFull(compilation);
        Assert.True(fixture.Resources.PeakResidentBytes <= fixture.Resources.Budget.MaximumResidentBytes);
        double? Convert(double value)
        {
            double integer = Math.Round(value, MidpointRounding.AwayFromZero);
            if (useEnum) return discard ? integer == 0 ? 0 : null : 0;
            return discard && (integer < 0 || integer > 5) ? null : Math.Clamp(integer, 0, 5);
        }
    }

    [Fact]
    public void LaneRebindStreamsWholeLaneAndUndoPreservesTheOldParameterAndPoints()
    {
        using var fixture = new Fixture(5_000);
        var track = fixture.Project.Tracks[0]; var source = track.Segments[0]; var lane = source.ParameterLanes[0];
        LogicalParameterDefinition target = new(fixture.Project) { Name = "Target", Type = LogicalParameterType.Integer,
            Minimum = 0, Maximum = 5, DisplayMinimum = 0, DisplayMaximum = 5 };
        fixture.Instrument.LogicalParameters.Add(target);
        var edit = ProjectDomainEditCommands.RebindLogicalParameterLane(source.Id, lane.Id, target.Id,
            LogicalParameterLaneRebindMode.DiscardInvalidValues, false).Prepare(fixture.Project);
        Assert.Same(source, track.Segments[0]); Assert.Equal(fixture.Parameter.Id, lane.ParameterId);
        edit.Apply(fixture.Project);
        Assert.Equal(target.Id, track.Segments[0].ParameterLanes[0].ParameterId);
        Assert.Equal(3_333, track.Segments[0].ParameterLanes[0].Points.Count);
        Assert.All(track.Segments[0].ParameterLanes[0].Points, value => Assert.True(value.Value is 1 or 5));
        edit.Undo(fixture.Project); Assert.Same(source, track.Segments[0]); Assert.Equal(5_000, lane.Points.Count);
    }

    [Fact]
    public void MigrationReportsRealPointProgressAndCancellationKeepsAllOwnersAndAllocator()
    {
        using var fixture = new Fixture(3_000);
        var originals = fixture.Project.Tracks.Select(track => track.Segments[0]).ToArray();
        long nextId = fixture.Project.NextStableId;
        using var cancellation = new CancellationTokenSource();
        List<TimelineEditPreparationProgress> reports = [];
        using var scope = BulkEditPreparationContext.Enter(cancellation.Token, new InlineProgress(value =>
        { reports.Add(value); if (value.Completed > 0 && value.Completed < value.Total) cancellation.Cancel(); }), fixture.Resources, fixture.Project);
        var command = ProjectDomainEditCommands.MigrateLogicalParameterDefinition(fixture.Instrument.Id, fixture.Parameter.Id,
            new(LogicalParameterType.Integer, 0, 5, 0, 5, 0, false, []), LogicalParameterLaneRebindMode.Clamp, false);
        Assert.Throws<OperationCanceledException>(() => command.Prepare(fixture.Project));
        Assert.Contains(reports, value => value.Total == 6_000 && value.Completed is > 0 and < 6_000);
        Assert.Same(fixture.Parameter, fixture.Instrument.LogicalParameters[0]); Assert.Equal(nextId, fixture.Project.NextStableId);
        for (int i = 0; i < 2; i++) Assert.Same(originals[i], fixture.Project.Tracks[i].Segments[0]);
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        var expected = compiler.CompileFull(compilation.Project);
        Assert.Equal(expected.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(expected.Events, compilation.LastAttempt.Events);
        Assert.Equal(expected.Diagnostics, compilation.LastAttempt.Diagnostics);
    }
    private sealed class InlineProgress(Action<TimelineEditPreparationProgress> report) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => report(value); }
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(AppContext.BaseDirectory, ".tmp", "migration-" + Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480);
        public EventInstrument Instrument { get; }
        public LogicalParameterDefinition Parameter { get; }
        public BoundedEditResources Resources { get; }
        private readonly BulkEditPreparationContext _scope;
        public Fixture(int count)
        {
            Directory.CreateDirectory(_path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024, maximumWorkingBytes: 8 * 1024 * 1024), _path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
            Instrument = EventInstrumentLibrary.Create(Project, "Instrument");
            Parameter = new(Project) { Name = "Amount", Type = LogicalParameterType.Double,
                Minimum = -10, Maximum = 10, DisplayMinimum = -10, DisplayMaximum = 10 };
            Instrument.LogicalParameters.Add(Parameter);
            for (int index = 0; index < 2; index++)
            {
                LogicalTrack track = new(Project) { Name = "Track " + index };
                ProjectGraphConstruction.AddIndependentLogicalTrack(Project, track, Instrument.Id);
                Segment segment = new(Project) { LengthTicks = 10_000 };
                LogicalParameterLane lane = new(Project) { ParameterId = Parameter.Id };
                lane.Points.AddRange(Enumerable.Range(0, count).Select(i => new CurvePoint(Project, i,
                    (i % 3) switch { 0 => -3.5, 1 => .5, _ => 4.9 }, CurveInterpolation.Step)));
                segment.ParameterLanes.Add(lane); track.Segments.Add(segment);
            }
        }
        public void Dispose()
        {
            _scope.Dispose(); Project.Dispose();
            // Weak runtime leases cannot reach sources already on the finalizer
            // queue. Finish that cleanup before deleting the test-owned parent;
            // no retry, suppressed IOException, or product-side GC is involved.
            GC.WaitForPendingFinalizers();
            Directory.Delete(_path, true);
        }
    }
}
