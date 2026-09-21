using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Compiler.Tests;

public sealed class CompilationProgressTests(ITestOutputHelper output)
{
    [Fact]
    public void ProgressIsBoundedAndUnknownTotalsNeverInventPercent()
    {
        var progress = new CompilationProgress();
        Assert.Null(progress.Current.Percent);
        for (long i = 0; i <= 1000000; i++) progress.Report(CompilationPhase.LogicalInstances, i, 1000000);
        Assert.Equal(101, progress.PublicationCount);
        Assert.Equal(100, progress.Current.Percent);
        progress.Report(CompilationPhase.SortingLogicalEvents);
        Assert.Null(progress.Current.Percent);
        progress.Report(CompilationPhase.LogicalInstances, long.MaxValue - 1, long.MaxValue);
        Assert.Equal(99, progress.Current.Percent);
        progress.Report(CompilationPhase.LogicalInstances, long.MaxValue, long.MaxValue);
        Assert.Equal(100, progress.Current.Percent);
    }

    [Fact]
    public void OptionalProgressPreservesFullIncrementalAndFailureResults()
    {
        var (p, track, segment, instrument, voice) = CompilerTestProject.Create();
        voice.Events.Add(new(p) { Kind = TemplateEventKind.Note, Number = 60, Value = 100, LengthTicks = 480 });
        var note = CompilerTestProject.AddNote(segment, instrument, 0, 480);
        using var baseline = new MidoraCompiler(); using var observed = new MidoraCompiler();
        var progress = new CompilationProgress(); var request = new CompilationRequest { Progress = progress };
        Equal(baseline.CompileFull(p), observed.CompileFull(p, request));
        var changes = new ProjectChangeSet(); changes.TrackIds.Add(track.Id);
        note.Velocity = 77;
        Equal(baseline.CompileFull(p), observed.CompileIncremental(p, changes, request));
        Equal(baseline.CompileFull(p), observed.CompileIncremental(p, new(), request));
        Assert.InRange(progress.PublicationCount, 1, 100);
        instrument.TemplateLengthTicks = 0;
        Equal(baseline.CompileFull(p), observed.CompileIncremental(p, ProjectChangeSet.Everything, request));
        Assert.Null(progress.Current.Percent);
    }

    [Fact]
    public void CompareLogicalCompilationWithAndWithoutProgress()
    {
        // Alternate AB/BA order; same warmed project and exact output. This is a
        // reporting benchmark, not a noisy wall-clock ratio assertion in CI.
        var (p, _, segment, instrument, voice) = CompilerTestProject.Create(segmentLength: 192000);
        instrument.TemplateLengthTicks = 96;
        voice.Events.AddRange(Enumerable.Range(0, 512).Select(i => new TemplateEvent(p)
        { Kind = TemplateEventKind.Note, Tick = i / 128, LengthTicks = 4, Number = i % 128, Value = 100 }));
        for (int i = 0; i < 2000; i++) CompilerTestProject.AddNote(segment, instrument, i * 96L, 96);
        using var compiler = new MidoraCompiler();
        var warm = compiler.CompileFull(p); Assert.True(warm.IsConsumable);
        List<double> off = [], on = []; List<long> offBytes = [], onBytes = [];
        for (int round = 0; round < 6; round++)
        {
            bool enabled = round % 4 is 1 or 2;
            var progress = enabled ? new CompilationProgress() : null;
            long bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            var result = compiler.CompileFull(p, new() { Progress = progress });
            clock.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.Equal(warm.Fingerprint, result.Fingerprint);
            Assert.Equal(warm.Statistics.NoteOnEventCount, result.Statistics.NoteOnEventCount);
            (enabled ? on : off).Add(clock.Elapsed.TotalMilliseconds);
            (enabled ? onBytes : offBytes).Add(bytes);
            output.WriteLine($"progress={enabled}: {clock.Elapsed.TotalMilliseconds:F2} ms, {bytes:N0} bytes, publications={progress?.PublicationCount ?? 0}");
        }
        output.WriteLine($"notes={warm.Statistics.NoteOnEventCount:N0}; median off={off.Order().ElementAt(off.Count / 2):F2} ms, on={on.Order().ElementAt(on.Count / 2):F2} ms; allocation off={offBytes.Min():N0}, on={onBytes.Min():N0}");
    }

    private static void Equal(CanonicalCompiledResult a, CanonicalCompiledResult b)
    {
        Assert.Equal(a.Fingerprint, b.Fingerprint); Assert.Equal(a.IsConsumable, b.IsConsumable);
        Assert.Equal(a.Events.ToArray(), b.Events.ToArray()); Assert.Equal(a.Diagnostics.ToArray(), b.Diagnostics.ToArray());
        Assert.Equal(a.Statistics, b.Statistics); Assert.Equal(a.SmfTracks.ToArray(), b.SmfTracks.ToArray());
    }
}
