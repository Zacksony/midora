using Midora.Common;
using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class CompactDiagnosticTests
{
    [Fact]
    public void RangesKeepEveryRepeatedDiagnosticInOriginalOrder()
    {
        CompilerDiagnosticList.Builder builder = new();
        int source = builder.AddOverlapSource(CreateSources(6), DiagnosticSeverity.Warning);
        builder.AddRange(source, 0, 4);
        builder.AddRange(source, 2, 3);
        builder.AddRange(source, 0, 1);
        CompilerDiagnosticList result = builder.Build();

        long[] expected = [1, 2, 3, 4, 3, 4, 5, 1];
        Assert.Equal(expected, result.Select(value => value.Source.LogicalNoteId.Value));
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], result[index].Source.LogicalNoteId.Value);
        Assert.Equal(expected.Length, result.CountSeverity(DiagnosticSeverity.Warning));
        Assert.Equal(0, result.CountSeverity(DiagnosticSeverity.Error));
        Assert.Throws<ArgumentOutOfRangeException>(() => result[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => result[result.Count]);
    }

    [Fact]
    public void InsertKeepsOrdinaryPrefixOverlapAndOrdinarySuffix()
    {
        CompilerDiagnostic[] ordinary = Enumerable.Range(0, 4)
            .Select(index => new CompilerDiagnostic($"TEST{index}", DiagnosticSeverity.Info,
                "Ordinary diagnostic.", new(Tick: index))).ToArray();
        CompilerDiagnosticList.Builder builder = new();
        int source = builder.AddOverlapSource(CreateSources(3), DiagnosticSeverity.Error);
        builder.AddRange(source, 1, 2);
        builder.AddRange(source, 2, 1);
        CompilerDiagnosticList overlap = builder.Build();
        CompilerDiagnosticList combined = CompilerDiagnosticList.Insert(ordinary, 2, overlap);

        Assert.Equal(ordinary.Take(2).Concat(overlap).Concat(ordinary.Skip(2)), combined);
        Assert.Equal(3, combined.CountSeverity(DiagnosticSeverity.Error));
        Assert.Equal(4, combined.CountSeverity(DiagnosticSeverity.Info));
        Assert.Same(ordinary[0], combined[0]);
    }

    [Fact]
    public void FiftyMillionDiagnosticsRetainOnlySourcesAndRangesAndFilterOncePerSource()
    {
        const int sourceCount = 10_000;
        CompilerDiagnosticList.Builder builder = new();
        int source = builder.AddOverlapSource(CreateSources(sourceCount), DiagnosticSeverity.Error);
        for (int index = 0; index < sourceCount - 1; index++)
            builder.AddRange(source, index + 1, sourceCount - index - 1);
        CompilerDiagnosticList result = builder.Build();

        Assert.Equal(49_995_000, result.Count);
        Assert.Equal(result.Count, result.CountSeverity(DiagnosticSeverity.Error));
        Assert.Equal(2, result[0].Source.LogicalNoteId.Value);
        Assert.Equal(sourceCount, result[^1].Source.LogicalNoteId.Value);
        RetainedStorageCollector storage = new();
        result.CollectRetainedStorage(storage);
        Assert.InRange(storage.ToArray().Sum(part => part.Bytes), 1, 1_000_000);

        int evaluations = 0;
        CompilerDiagnosticList filtered = result.Filter(value =>
        {
            evaluations++;
            return value.Source.LogicalNoteId.Value == sourceCount;
        });
        Assert.Equal(sourceCount, evaluations);
        Assert.Equal(sourceCount - 1, filtered.Count);
        Assert.All(filtered, value => Assert.Equal(sourceCount, value.Source.LogicalNoteId.Value));
        RetainedStorageCollector sharedStorage = new();
        result.CollectRetainedStorage(sharedStorage);
        filtered.CollectRetainedStorage(sharedStorage);
        Assert.InRange(sharedStorage.ToArray().Sum(part => part.Bytes), 1, 1_200_000);
    }

    [Fact]
    public void FiltersKeepNestedRangeOrderAndHonorCancellation()
    {
        CompilerDiagnosticList.Builder builder = new();
        int source = builder.AddOverlapSource(CreateSources(2_000), DiagnosticSeverity.Warning);
        builder.AddRange(source, 0, 1_000);
        builder.AddRange(source, 100, 1_500);
        CompilerDiagnosticList result = builder.Build();
        CompilerDiagnosticList filtered = result.Filter(value => value.Source.LogicalNoteId.Value % 7 == 0);
        Assert.Equal(result.Where(value => value.Source.LogicalNoteId.Value % 7 == 0), filtered);
        Assert.Equal(filtered.Where(value => value.Source.Tick > 900),
            filtered.Filter(value => value.Source.Tick > 900));
        using CancellationTokenSource cancellation = new();
        int evaluated = 0;
        Assert.Throws<OperationCanceledException>(() => result.Filter(_ =>
        {
            if (++evaluated == 17) cancellation.Cancel();
            return true;
        }, cancellation.Token));
        Assert.InRange(evaluated, 17, 256);
    }

    [Theory]
    [InlineData(OverlapPolicy.Reject, OverlapScope.SamePitch)]
    [InlineData(OverlapPolicy.Reject, OverlapScope.AnyPitch)]
    [InlineData(OverlapPolicy.Warn, OverlapScope.SamePitch)]
    [InlineData(OverlapPolicy.Warn, OverlapScope.AnyPitch)]
    public void FullAndIncrementalOverlapMatchPairwiseReference(
        OverlapPolicy policy, OverlapScope scope)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 2_000);
        fixture.Instrument.OverlapPolicy = policy;
        fixture.Instrument.OverlapScope = scope;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        LogicalNote[] notes =
        [
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 35, 300, 62),
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 150, 60),
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 35, 75, 60),
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 200, 62),
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 150, 80, 60),
            CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 335, 80, 62)
        ];
        LogicalNote[] ordered = notes.OrderBy(value => value.StartTick).ToArray();
        List<CompilerDiagnostic> expected = [];
        for (int left = 0; left < ordered.Length; left++)
            for (int right = left + 1; right < ordered.Length
                && ordered[right].StartTick < ordered[left].StartTick + ordered[left].LengthTicks; right++)
                if (scope == OverlapScope.AnyPitch || ordered[left].Note == ordered[right].Note)
                    expected.Add(new("MIDORA2201", policy == OverlapPolicy.Reject
                        ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                        "An Event Instrument Instance has a policy-constrained overlap.",
                        new(fixture.Track.Id, fixture.Segment.Id, ordered[right].Id,
                            fixture.Instrument.Id, Tick: ordered[right].StartTick)));
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult full = compiler.CompileFull(fixture.Project);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project, ProjectChangeSet.Everything);
        Assert.Equal(expected, full.Diagnostics.Where(value => value.Code == "MIDORA2201"));
        Assert.Equal(full.Diagnostics, incremental.Diagnostics);
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
    }

    private static CompilerDiagnosticList.OverlapDiagnosticSourceValue[] CreateSources(int count) =>
        Enumerable.Range(0, count).Select(index => new CompilerDiagnosticList.OverlapDiagnosticSourceValue(
            new MidoraId(10_001), new MidoraId(10_002), new MidoraId(index + 1),
            new MidoraId(10_003), index)).ToArray();
}
