using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ConductorBatchEditCommandsTests
{
    [Fact]
    public void DrawUsesEnumerationOrderForLaterWinsAndPreservesTheRequiredInitialIdentity()
    {
        using var project = new MidoraProject(192);
        ConductorTrack before = project.Conductor;
        MidoraId initial = before.Tempos[0].Id;
        var command = ProjectDomainEditCommands.DrawTempoPoints(
            [new(96, 200.25m), new(0, 80.125m), new(96, 150.75m), new(48, 100m)]);
        var prepared = command.Prepare(project);
        Assert.Same(before, project.Conductor);
        prepared.Apply(project);
        Assert.Equal([0L, 48L, 96L], project.Conductor.Tempos.Select(static value => value.Tick));
        Assert.Equal([80.125m, 100m, 150.75m], project.Conductor.Tempos.Select(static value => value.BeatsPerMinute));
        Assert.Equal(initial, project.Conductor.Tempos[0].Id);
        Assert.Equal(project.Conductor.Tempos.Select(static value => value.Id), command.ResultSelectionIds);
        ConductorTrack after = project.Conductor;
        long allocated = project.NextStableId;
        prepared.Undo(project);
        Assert.Same(before, project.Conductor);
        prepared.Apply(project);
        Assert.Same(after, project.Conductor);
        Assert.Equal(allocated, project.NextStableId);
    }

    [Fact]
    public void MoveResolvesCollisionWithUnselectedEventAndUndoRestoresBoth()
    {
        using var project = new MidoraProject(192);
        var moved = new TempoChange(project, 96, 140m);
        var occupied = new TempoChange(project, 192, 90m);
        project.Conductor.Tempos.AddRange([moved, occupied]);
        var command = ProjectDomainEditCommands.MoveConductorEvents([moved.Id], 96, 2.5m);
        var edit = command.Prepare(project);
        edit.Apply(project);
        Assert.Equal(2, project.Conductor.Tempos.Count);
        var result = project.Conductor.Tempos.Single(static value => value.Tick == 192);
        Assert.Equal(moved.Id, result.Id);
        Assert.Equal(142.5m, result.BeatsPerMinute);
        Assert.Equal([moved.Id], command.ResultSelectionIds);
        edit.Undo(project);
        Assert.Equal([0L, 96L, 192L], project.Conductor.Tempos.Select(static value => value.Tick));
        Assert.Equal([moved.Id], command.ResultSelectionIds);
    }

    [Fact]
    public void CopyDoesNotMoveTheInitialStateAndPublishesOnlySurvivingCopies()
    {
        using var project = new MidoraProject(192);
        TempoChange first = project.Conductor.Tempos[0];
        TempoChange other = new(project, 48, 80m);
        project.Conductor.Tempos.Add(other);
        var command = ProjectDomainEditCommands.MoveConductorEvents([other.Id, first.Id], 48, 10m, duplicate: true);
        var edit = command.Prepare(project);
        edit.Apply(project);
        Assert.Equal(3, project.Conductor.Tempos.Count);
        Assert.Equal(first.Id, project.Conductor.Tempos[0].Id);
        Assert.Equal([120m, 130m, 90m], project.Conductor.Tempos.Select(static value => value.BeatsPerMinute));
        Assert.Equal(2, command.ResultSelectionIds.Count);
        Assert.DoesNotContain(first.Id, command.ResultSelectionIds);
        Assert.DoesNotContain(other.Id, command.ResultSelectionIds);
        edit.Undo(project);
        Assert.Equal([first.Id, other.Id], command.ResultSelectionIds);
        Assert.Equal([first.Id, other.Id], project.Conductor.Tempos.Select(static value => value.Id));
    }

    [Fact]
    public void RequiredInitialDeleteOrMoveRejectsTheWholeMixedSelection()
    {
        using var project = new MidoraProject(192);
        var marker = new ProjectMarker(project, 96, "Keep");
        project.Conductor.Markers.Add(marker);
        ConductorTrack source = project.Conductor;
        var ids = new[] { marker.Id, source.Tempos[0].Id, source.TimeSignatures[0].Id };
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.DeleteConductorEvents(ids).Prepare(project));
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.MoveConductorEvents(ids, 1).Prepare(project));
        Assert.Same(source, project.Conductor);
        Assert.Single(source.Markers);
    }

    [Fact]
    public void SameTickMarkersRetainIdentityTextAndStableFormalOrder()
    {
        using var project = new MidoraProject(192);
        var a = new ProjectMarker(project, 96, "同じ");
        var b = new ProjectMarker(project, 96, "同じ");
        var c = new ProjectMarker(project, 48, "");
        project.Conductor.Markers.AddRange([a, b, c]);
        var command = ProjectDomainEditCommands.MoveConductorEvents([b.Id, c.Id, a.Id], 24);
        var edit = command.Prepare(project);
        edit.Apply(project);
        Assert.Equal([c.Id, a.Id, b.Id], project.Conductor.Markers.Select(static value => value.Id));
        Assert.Equal(["", "同じ", "同じ"], project.Conductor.Markers.Select(static value => value.Name));
        Assert.Equal([c.Id, a.Id, b.Id], command.ResultSelectionIds);
        edit.Undo(project);
        Assert.Equal([48L, 96L, 96L], project.Conductor.Markers.Select(static value => value.Tick));
    }

    [Fact]
    public void AllInvalidInputAndRevisionRacesFailBeforePublication()
    {
        using var project = new MidoraProject(192);
        ConductorTrack source = project.Conductor;
        long next = project.NextStableId;
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectDomainEditCommands.DrawTempoPoints(
            [new(100, 120), new(101, 0)]).Prepare(project));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectDomainEditCommands.DrawTempoPoints(
            [new(100, 120), new(-1, 120)]).Prepare(project));
        Assert.Throws<ArgumentException>(() => ProjectDomainEditCommands.DrawTempoPoints([]).Prepare(project));
        Assert.Same(source, project.Conductor);
        Assert.Equal(next, project.NextStableId);
        var edit = ProjectDomainEditCommands.DrawTempoPoints([new(200, 180)]).Prepare(project);
        project.Conductor.Tempos.Add(new(project, 150, 100));
        Assert.Throws<InvalidOperationException>(() => edit.Apply(project));
        Assert.DoesNotContain(project.Conductor.Tempos, static value => value.Tick == 200);
        (edit as IDisposable)?.Dispose();
    }

    [Fact]
    public void CancellationWhileEnumeratingTempoTraceLeavesNoPartialEventsOrIds()
    {
        using var project = new MidoraProject(192);
        using var cancel = new CancellationTokenSource();
        ConductorTrack source = project.Conductor;
        long next = project.NextStableId;
        var command = (IProgressReportingProjectEditCommand)ProjectDomainEditCommands.DrawTempoPoints(Points());
        Assert.Throws<OperationCanceledException>(() => command.Prepare(project, cancel.Token, null));
        Assert.Same(source, project.Conductor);
        Assert.Equal(next, project.NextStableId);
        Assert.Single(source.Tempos);
        IEnumerable<ConductorTempoPoint> Points()
        {
            for (int i = 0; i < 10000; i++)
            {
                if (i == 5000) cancel.Cancel();
                yield return new(i, 100 + i % 100);
            }
        }
    }

    [Fact]
    public void DenseMoveCollisionMergeReplacesOnlyExactTargetsAndUndoRestoresTheOriginalRoot()
    {
        using var project = new MidoraProject(192);
        List<TempoChange> values = [];
        List<MidoraId> selected = [];
        for (int i = 1; i <= 600; i++)
        {
            TempoChange moved = new(project, i * 2, 123m);
            values.Add(moved);
            values.Add(new(project, i * 2 + 1, 89m));
            selected.Add(moved.Id);
        }
        project.Conductor.Tempos.AddRange(values);
        ConductorTrack before = project.Conductor;
        var command = ProjectDomainEditCommands.MoveConductorEvents(selected, 1);
        var edit = command.Prepare(project);
        edit.Apply(project);
        Assert.Equal(601, project.Conductor.Tempos.Count);
        for (int i = 1; i <= 600; i++)
        {
            Assert.Equal(i * 2 + 1, project.Conductor.Tempos[i].Tick);
            Assert.Equal(123m, project.Conductor.Tempos[i].BeatsPerMinute);
            Assert.Equal(selected[i - 1], project.Conductor.Tempos[i].Id);
        }
        edit.Undo(project);
        Assert.Same(before, project.Conductor);
        Assert.Equal(1201, project.Conductor.Tempos.Count);
        (edit as IDisposable)?.Dispose();
    }

    [Fact]
    public void SingleConductorEditsDoNotAllocateFullBulkPagesOrChangeTheSharedBudget()
    {
        using var project = new MidoraProject(192);
        var warm = ProjectDomainEditCommands.CreateProjectMarker(1, "Warm").Prepare(project);
        (warm as IDisposable)?.Dispose();
        var resources = new BoundedEditResources();
        using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var edit = ProjectDomainEditCommands.CreateProjectMarker(120, "Bookmark").Prepare(project);
        edit.Apply(project);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 1_000_000, $"A single Marker edit allocated {allocated:N0} bytes.");
        Assert.Equal(PagedEditResourceBudget.DefaultPageRecordCount, scope.PreferredPageRecordCount);
        Assert.Equal(PagedEditResourceBudget.DefaultPageRecordCount, resources.Budget.PageRecordCount);
        Assert.Equal(0, resources.WorkingBytes);
        edit.Undo(project);
        (edit as IDisposable)?.Dispose();
    }

    [Fact]
    public void TempoTraceFactoryReceivesCancellationAndReportsTheActualGeneratedCount()
    {
        using var project = new MidoraProject(192);
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var progress = new TraceProgress();
        var command = (IProgressReportingProjectEditCommand)ProjectDomainEditCommands.DrawTempoPoints(token =>
        {
            observed = token;
            return Enumerable.Range(1, 513).Select(static tick => new ConductorTempoPoint(tick, 120));
        });
        var edit = command.Prepare(project, cancellation.Token, progress);
        Assert.Equal(cancellation.Token, observed);
        Assert.Equal(513, progress.PlannedCandidates);
        Assert.Equal(513, progress.PlannedTotal);
        Assert.Equal(1, progress.LastFraction);
        (edit as IDisposable)?.Dispose();
    }

    [Fact]
    public void LazyTraceCandidatesCannotBypassTheConfiguredRecordLimit()
    {
        using var project = new MidoraProject(192);
        var resources = new BoundedEditResources(new PagedEditResourceBudget(maximumRecordCount: 512));
        using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
        ConductorTrack original = project.Conductor;
        long next = project.NextStableId;
        int visited = 0;
        var command = ProjectDomainEditCommands.DrawTempoPoints(Points());
        var error = Assert.Throws<InvalidOperationException>(() => command.Prepare(project));
        Assert.Contains("candidate-record limit", error.Message);
        Assert.Equal(513, visited);
        Assert.Same(original, project.Conductor);
        Assert.Equal(next, project.NextStableId);
        Assert.Equal(0, resources.WorkingBytes);

        IEnumerable<ConductorTempoPoint> Points()
        {
            while (true)
            {
                visited++;
                // Even every sample replacing the same tick is still a
                // candidate and must count against the bounded-work limit.
                yield return new(1, 100);
            }
        }
    }

    [Fact]
    public void LargeDrawMoveDeleteAndUndoUseBoundedPagesAndReachCompleteProgress()
    {
        using var project = new MidoraProject(192);
        var resources = new BoundedEditResources(new PagedEditResourceBudget(maximumResidentBytes: 1 << 20,
            maximumWorkingBytes: 8 << 20, pageRecordCount: 256));
        using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
        var progress = new ProgressLog();
        const int count = 20000;
        var draw = (IProgressReportingProjectEditCommand)ProjectDomainEditCommands.DrawTempoPoints(
            Enumerable.Range(1, count).Select(static i => new ConductorTempoPoint(i * 2L, 90 + i % 100)));
        var created = draw.Prepare(project, default, progress);
        created.Apply(project);
        Assert.Equal(count + 1, project.Conductor.Tempos.Count);
        Assert.Equal(1, progress.Last.OverallFraction);
        MidoraId[] ids = project.Conductor.Tempos.Skip(1).Select(static value => value.Id).ToArray();
        var moved = ProjectDomainEditCommands.MoveConductorEvents(ids, 1, .125m).Prepare(project);
        moved.Apply(project);
        Assert.Equal(3, project.Conductor.Tempos[1].Tick);
        var deleted = ProjectDomainEditCommands.DeleteConductorEvents(ids).Prepare(project);
        deleted.Apply(project);
        Assert.Single(project.Conductor.Tempos);
        deleted.Undo(project); moved.Undo(project); created.Undo(project);
        Assert.Single(project.Conductor.Tempos);
        Assert.True(resources.PeakResidentBytes <= 1 << 20);
        Assert.True(resources.PeakWorkingBytes <= 8 << 20);
        Assert.True(resources.PeakSpillBytes > 0);
        Assert.Equal(0, resources.WorkingBytes);
    }

    [Fact]
    public void IncrementalAndFullConductorResultsStayEquivalentAfterDrawingAndUndo()
    {
        using var project = new MidoraProject(192);
        using var compilation = new ProjectCompilationSession(project);
        using var compiler = new MidoraCompiler();
        var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(ProjectDomainEditCommands.DrawTempoPoints(
            Enumerable.Range(0, 500).Select(static i => new ConductorTempoPoint(i * 2, 100 + i % 97))));
        Assert.Equal(compiler.CompileFull(project).Fingerprint, compilation.LastAttempt.Fingerprint);
        document.Undo();
        Assert.Equal(compiler.CompileFull(project).Fingerprint, compilation.LastAttempt.Fingerprint);
        document.Redo();
        Assert.Equal(compiler.CompileFull(project).Fingerprint, compilation.LastAttempt.Fingerprint);
    }

    [Fact]
    public void UnchangedMarkerPropertiesDoNotPublishANewRootOrHistoryEntry()
    {
        using var project = new MidoraProject(192);
        var marker = new ProjectMarker(project, 10, "Marker");
        project.Conductor.Markers.Add(marker);
        ConductorTrack root = project.Conductor;
        var edit = ProjectDomainEditCommands.UpdateProjectMarker(marker.Id, 10, "Marker").Prepare(project);
        Assert.False(edit.HasChanges);
        Assert.Same(root, project.Conductor);
        (edit as IDisposable)?.Dispose();
    }

    [Fact]
    public void ChangedMarkerTextRemainsReadableAfterItsClipboardIsDisposed()
    {
        using var project = new MidoraProject(192);
        var marker = new ProjectMarker(project, 10, "日本語 — Marker");
        project.Conductor.Markers.Add(marker);
        using var compilation = new ProjectCompilationSession(project);
        var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
        var clipboard = ProjectObjectClipboard.CopyConductorEvents(document, [marker.Id]);
        var command = ProjectObjectClipboard.CreatePasteConductorEventsCommand(document, clipboard, 20);
        var edit = command.Prepare(project);
        edit.Apply(project);
        clipboard.Dispose();
        (edit as IDisposable)?.Dispose();
        Assert.Equal(["日本語 — Marker", "日本語 — Marker"], project.Conductor.Markers.Select(static value => value.Name));
    }

    private sealed class ProgressLog : IProgress<TimelineEditPreparationProgress>
    {
        public TimelineEditPreparationProgress Last { get; private set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            Assert.True(value.OverallFraction >= Last.OverallFraction);
            Last = value;
        }
    }

    private sealed class TraceProgress : IProgress<TimelineEditPreparationProgress>
    {
        public long PlannedCandidates { get; private set; }
        public long PlannedTotal { get; private set; }
        public double LastFraction { get; private set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            Assert.True(value.OverallFraction >= LastFraction);
            LastFraction = value.OverallFraction;
            if (value.Phase != TimelineEditPreparationPhase.Planning) return;
            PlannedCandidates = value.Completed;
            PlannedTotal = value.Total;
        }
    }
}
