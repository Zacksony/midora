using System.Reflection;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class CompilationProgressLifetimeTests
{
    [Fact]
    public async Task OldGenerationProgressCannotSurviveNewEditCompletionFailureOrDisposal()
    {
        using var p = new MidoraProject(480);
        using var session = new ProjectCompilationSession(p, executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        session.CompilationStartingForTests = token => { started.Set(); release.Wait(token); };
        session.ApplyEdit(value => value.Conductor.Markers.Add(new(value, 100, "First")), new() { AffectsConductor = true });
        try
        {
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(10))));
            Assert.Equal(CompilationPhase.PreparingSnapshot, session.CurrentCompilationProgress!.Phase);
            var old = (CompilationProgress)typeof(ProjectCompilationSession).GetField("_activeCompilationProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            old.Report(CompilationPhase.LogicalInstances, 5, 10);
            Assert.Equal(50, session.CurrentCompilationProgress!.Percent);
            session.CompilationStartingForTests = null;
            session.ApplyEdit(value => value.Conductor.Markers.Add(new(value, 200, "Second")), new() { AffectsConductor = true });
            release.Set();
            var current = await session.EnsureCurrentCompilationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(current.IsConsumable);
            old.Report(CompilationPhase.Fingerprinting);
            Assert.Null(session.CurrentCompilationProgress);
            session.ApplyEdit(value => value.Conductor.Tempos.Clear(), new() { AffectsConductor = true });
            current = await session.EnsureCurrentCompilationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(current.IsConsumable); Assert.Null(session.CurrentCompilationProgress);
            session.Dispose(); old.Report(CompilationPhase.LogicalTracks, 1, 2);
            Assert.Null(session.CurrentCompilationProgress);
        }
        finally { release.Set(); }
    }
}
