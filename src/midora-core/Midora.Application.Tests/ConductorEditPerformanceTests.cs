using System.Collections;
using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

/// <summary>Opt-in, real command preparation; no synthetic already-adopted root shortcut.</summary>
[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class ConductorEditPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void MeasureDrawMoveHistoryCancellationAndPartialCanonical(int count)
    {
        if (Environment.GetEnvironmentVariable("MIDORA_CONDUCTOR_EDIT_BENCHMARK") != "1") return;
        string scratch = Path.Combine(AppContext.BaseDirectory, ".tmp", "conductor-edit-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var project = new MidoraProject(480);
        try
        {
            var resources = new BoundedEditResources(temporaryRoot: scratch);
            using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
            long allocated = GC.GetTotalAllocatedBytes();
            long candidates = count + (count - 1) / 1024 + 1;
            var progress = new CountedProgress();
            var draw = ProjectDomainEditCommands.DrawTempoPoints(Trace, candidates);
            ConductorTrack original = project.Conductor;
            long nextId = project.NextStableId;
            var clock = Stopwatch.StartNew();
            var prepared = ((IProgressReportingProjectEditCommand)draw).Prepare(project, default, progress);
            double drawSeconds = clock.Elapsed.TotalSeconds;
            Assert.Same(original, project.Conductor);
            Assert.Equal(nextId, project.NextStableId);
            Assert.Equal(candidates, progress.Planned);
            Assert.Equal(1, progress.Fraction);
            clock.Restart(); prepared.Apply(project); double publishMs = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(count, project.Conductor.Tempos.Count);
            Assert.Equal(count, draw.ResultSelectionIds.Count);
            ConductorTrack drawn = project.Conductor;
            VerifySource(0);
            clock.Restart(); prepared.Undo(project); double undoMs = clock.Elapsed.TotalMilliseconds;
            Assert.Same(original, project.Conductor);
            clock.Restart(); prepared.Apply(project); double redoMs = clock.Elapsed.TotalMilliseconds;
            Assert.Same(drawn, project.Conductor);

            // Exercise real stable-ID selection, target collisions, bulk root
            // construction and both history transitions, without materializing
            // a million object/ID array in this benchmark harness.
            var movedIds = new SelectionWithoutInitial(draw.ResultSelectionIds, original.Tempos[0].Id);
            var move = ProjectDomainEditCommands.MoveConductorEvents(movedIds, 1, .125m);
            clock.Restart(); var moved = move.Prepare(project); double moveSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart(); moved.Apply(project); double movePublishMs = clock.Elapsed.TotalMilliseconds;
            VerifySource(1);
            clock.Restart(); moved.Undo(project); double moveUndoMs = clock.Elapsed.TotalMilliseconds;
            Assert.Same(drawn, project.Conductor);
            clock.Restart(); moved.Apply(project); double moveRedoMs = clock.Elapsed.TotalMilliseconds;
            VerifySource(1);

            // A single update inside a large source must use the same command
            // path; report this separately from the million-candidate edit.
            TempoChange single = project.Conductor.Tempos[count / 2];
            clock.Restart();
            var changed = ProjectDomainEditCommands.UpdateTempo(single.Id, single.Tick, single.BeatsPerMinute + 1).Prepare(project);
            double singleSeconds = clock.Elapsed.TotalSeconds;
            changed.Apply(project); changed.Undo(project);

            ConductorTrack beforeCancel = project.Conductor;
            long beforeCancelId = project.NextStableId;
            using var cancellation = new CancellationTokenSource();
            int visited = 0;
            var canceled = (IProgressReportingProjectEditCommand)ProjectDomainEditCommands.DrawTempoPoints(token => CanceledTrace(token));
            clock.Restart();
            Assert.Throws<OperationCanceledException>(() => canceled.Prepare(project, cancellation.Token, null));
            double cancelSeconds = clock.Elapsed.TotalSeconds;
            Assert.Same(beforeCancel, project.Conductor);
            Assert.Equal(beforeCancelId, project.NextStableId);
            Assert.Equal(Math.Min(count / 2, 100_000), visited);

            // Canonical range restoration must use the predecessor value, not
            // merely emit states whose source tick falls inside the request.
            using var compiler = new MidoraCompiler();
            long start = (count / 2) * 4L + 2;
            long end = start + 13;
            var request = new CompilationRequest { Purpose = CompilationPurpose.Range, StartTick = start, EndTick = end };
            clock.Restart();
            var partial = compiler.CompileFull(project, request);
            double partialSeconds = clock.Elapsed.TotalSeconds;
            Assert.True(partial.IsConsumable, string.Join("; ", partial.Diagnostics.Select(value => value.Message)));
            Assert.True(partial.Tempos[0].IsRangeRestore);
            Assert.Equal(start, partial.Tempos[0].Tick);
            Assert.Equal(Expected(count / 2) + .125m, partial.Tempos[0].BeatsPerMinute);
            for (int index = 1; index < partial.Tempos.Length; index++)
            {
                CanonicalTempo value = partial.Tempos[index];
                Assert.InRange(value.Tick, start, end - 1);
                Assert.Equal(Expected((int)((value.Tick - 1) / 4)) + .125m, value.BeatsPerMinute);
            }
            var incremental = compiler.CompileIncremental(project, ProjectChangeSet.Everything, request);
            Assert.Equal(partial.Fingerprint, incremental.Fingerprint);
            Assert.Equal(partial.Tempos.ToArray(), incremental.Tempos.ToArray());
            using var process = Process.GetCurrentProcess();
            output.WriteLine($"CONDUCTOR {count:N0} states / {candidates:N0} candidates: draw={drawSeconds:F3}s; publish={publishMs:F3}ms; Undo={undoMs:F3}ms; Redo={redoMs:F3}ms; move={moveSeconds:F3}s; move publish={movePublishMs:F3}ms; move Undo={moveUndoMs:F3}ms; move Redo={moveRedoMs:F3}ms; single prepare={singleSeconds:F4}s; cancel after {visited:N0}={cancelSeconds:F3}s; partial compile={partialSeconds:F3}s; working={resources.PeakWorkingBytes / 1048576d:F2}MiB; resident={resources.PeakResidentBytes / 1048576d:F2}MiB; spill={resources.PeakSpillBytes / 1048576d:F2}MiB; allocated={(GC.GetTotalAllocatedBytes() - allocated) / 1048576d:F2}MiB; managed={GC.GetTotalMemory(false) / 1048576d:F2}MiB; WS={process.WorkingSet64 / 1048576d:F2}MiB; process peak WS={process.PeakWorkingSet64 / 1048576d:F2}MiB.");
            Assert.True(resources.PeakWorkingBytes <= resources.Budget.MaximumWorkingBytes);
            Assert.True(resources.PeakResidentBytes <= resources.Budget.MaximumResidentBytes);
            Assert.True(resources.PeakSpillBytes <= resources.Budget.MaximumSpillBytes);
            Assert.Equal(0, resources.WorkingBytes);
            (changed as IDisposable)?.Dispose();
            (moved as IDisposable)?.Dispose();
            (prepared as IDisposable)?.Dispose();

            IEnumerable<ConductorTempoPoint> Trace(CancellationToken token)
            {
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    decimal value = 120m + (i % 997) / 10m;
                    yield return new(i * 4L, value);
                    if (i % 1024 == 0) yield return new(i * 4L, value + .25m);
                }
            }
            IEnumerable<ConductorTempoPoint> CanceledTrace(CancellationToken token)
            {
                while (true)
                {
                    if (++visited == Math.Min(count / 2, 100_000)) cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    yield return new(visited * 4L, 80);
                }
            }
            void VerifySource(int movedBy)
            {
                var snapshot = project.Conductor.Tempos.CaptureQuerySnapshot();
                foreach (int ordinal in new[] { 0, 1, count / 3, count / 2, count - 1 })
                {
                    var value = snapshot.GetByOrdinal(ordinal);
                    long tick = ordinal * 4L + (ordinal == 0 ? 0 : movedBy);
                    Assert.Equal(tick, value.Tick);
                    Assert.Equal(Expected(ordinal) + (ordinal == 0 ? 0 : movedBy * .125m), value.BeatsPerMinute);
                    Assert.True(snapshot.TryGetBeforeTick(tick + 1, out var previous));
                    Assert.Equal(value, previous);
                }
            }
        }
        finally
        {
            project.Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static decimal Expected(int ordinal) => 120m + ordinal % 997 / 10m + (ordinal % 1024 == 0 ? .25m : 0);

    private sealed class SelectionWithoutInitial(IReadOnlyCollection<MidoraId> source, MidoraId initial)
        : IReadOnlyCollection<MidoraId>
    {
        public int Count => source.Count - 1;
        public IEnumerator<MidoraId> GetEnumerator() => source.Where(id => id != initial).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountedProgress : IProgress<TimelineEditPreparationProgress>
    {
        public double Fraction { get; private set; }
        public long Planned { get; private set; }
        public void Report(TimelineEditPreparationProgress value)
        {
            Assert.True(value.OverallFraction >= Fraction);
            Fraction = value.OverallFraction;
            if (value.Phase == TimelineEditPreparationPhase.Planning) Planned = value.Completed;
        }
    }
}
