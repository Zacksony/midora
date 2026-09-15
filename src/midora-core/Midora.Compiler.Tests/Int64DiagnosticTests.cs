using Midora.Common;
using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class Int64DiagnosticTests
{
    [Fact]
    public void TriangularDiagnosticsPreserveEveryPairAcrossInt32WithoutMaterialization()
    {
        const int count = 65_537;
        CompilerDiagnosticList.Builder builder = new();
        var sources = Enumerable.Range(0, count).Select(index =>
            new CompilerDiagnosticList.OverlapDiagnosticSourceValue(new(100_001), new(100_002),
                new(index + 1), new(100_003), index)).ToArray();
        int source = builder.AddOverlapSource(sources, DiagnosticSeverity.Warning);
        for (int index = 0; index < count - 1; index++) builder.AddRange(source, index + 1, count - index - 1);
        CompilerDiagnosticList result = builder.Build();
        const long expected = 2_147_516_416;
        Assert.Equal(expected, result.Count);
        Assert.Equal(expected, result.CountSeverity(DiagnosticSeverity.Warning));
        Assert.Equal(0, result.CountSeverity(DiagnosticSeverity.Error));
        foreach (long ordinal in new long[] { 0, 65_535, int.MaxValue - 1L, int.MaxValue, expected - 1 })
        {
            int left = 0, right = count - 1;
            while (left < right)
            {
                int middle = left + (right - left + 1) / 2;
                if (Prefix(middle) <= ordinal) left = middle;
                else right = middle - 1;
            }
            Assert.Equal(left + 2 + ordinal - Prefix(left), result[ordinal].Source.LogicalNoteId.Value);
        }
        Assert.Equal(result[expected - 1], result[^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => result[-1L]);
        Assert.Throws<ArgumentOutOfRangeException>(() => result[expected]);
        Assert.Throws<ArgumentOutOfRangeException>(() => result[long.MaxValue]);
        int filterReads = 0;
        CompilerDiagnosticList filtered = result.Filter(diagnostic =>
        {
            filterReads++;
            return diagnostic.Source.LogicalNoteId.Value == count;
        });
        Assert.Equal(count, filterReads);
        Assert.Equal(count - 1L, filtered.Count);
        Assert.All(filtered, value => Assert.Equal(count, value.Source.LogicalNoteId.Value));
        RetainedStorageCollector storage = new();
        result.CollectRetainedStorage(storage);
        Assert.InRange(storage.ToArray().Sum(value => value.Bytes), 1, 8 * 1024 * 1024);

        CompilerDiagnostic marker = new("TEST", DiagnosticSeverity.Info, "Marker", new());
        CompilerDiagnosticList inserted = CompilerDiagnosticList.Insert([marker, marker], 1, result);
        Assert.Equal(expected + 2, inserted.Count);
        Assert.Same(marker, inserted[0]);
        Assert.Same(marker, inserted[^1]);
        Assert.Equal(result[int.MaxValue], inserted[int.MaxValue + 1L]);
        Assert.Equal(2, inserted.CountSeverity(DiagnosticSeverity.Info));
        CompilationRejectedException exception = new(result);
        Assert.Same(result, exception.Diagnostics);
        Assert.Contains($"{expected - 32} additional diagnostics", exception.Message);
        return;
        static long Prefix(int row) => (long)row * (2L * count - row - 1) / 2;
    }

    [Fact]
    public void CountLimitHasAnExplicitFailureAndDoesNotWrap()
    {
        Assert.Equal(long.MaxValue, DiagnosticCapacityExceededException.Add(long.MaxValue - 7, 7));
        DiagnosticCapacityExceededException failure = Assert.Throws<DiagnosticCapacityExceededException>(
            () => DiagnosticCapacityExceededException.Add(long.MaxValue - 7, 8));
        Assert.Contains("9,223,372,036,854,775,807", failure.Message);
        Assert.Contains("without publishing incomplete diagnostics", failure.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticCapacityExceededException.Add(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticCapacityExceededException.Add(0, -1));
    }

    [Theory]
    [InlineData(OverlapPolicy.Reject, false, false)]
    [InlineData(OverlapPolicy.Warn, false, true)]
    [InlineData(OverlapPolicy.Warn, true, false)]
    public void RealCompilerKeepsLargeDiagnosticsAndOriginalWarningPolicy(
        OverlapPolicy policy, bool warningsAsErrors, bool consumable)
    {
        const int count = 65_537;
        var fixture = CompilerTestProject.Create(segmentLength: count * 2L);
        using var project = fixture.Project;
        fixture.Instrument.OverlapPolicy = policy;
        fixture.Instrument.OverlapScope = OverlapScope.SamePitch;
        fixture.Voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        for (int index = 0; index < count; index++)
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, index, count);
        using MidoraCompiler compiler = new();
        CompilationRequest request = new() { TreatWarningsAsErrors = warningsAsErrors };
        CanonicalCompiledResult full = compiler.CompileFull(project, request);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(project, ProjectChangeSet.Everything, request);
        DiagnosticSeverity severity = policy == OverlapPolicy.Reject ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        const long pairs = 2_147_516_416;
        Assert.Equal(pairs, CompilerDiagnosticList.CountSeverity(full.Diagnostics, severity));
        Assert.Equal(full.Diagnostics.Count, incremental.Diagnostics.Count);
        Assert.Equal(consumable, full.IsConsumable);
        Assert.Equal(full.IsConsumable, incremental.IsConsumable);
        Assert.Equal(full.FailureStage, incremental.FailureStage);
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(full.TotalEventCount, incremental.TotalEventCount);
        if (full.TotalEventCount != 0)
            Assert.Equal(
                full.QueryEventPages(full.StartTick, full.EndTick).SelectMany(page => page.Items),
                incremental.QueryEventPages(incremental.StartTick, incremental.EndTick).SelectMany(page => page.Items));
        foreach (long ordinal in new[] { 0L, int.MaxValue, full.Diagnostics.Count - 1 })
            Assert.Equal(full.Diagnostics[ordinal], incremental.Diagnostics[ordinal]);
    }
}
