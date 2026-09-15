using System.Globalization;
using System.Text;
using Midora.Compiler;

namespace Midora.MidiExport.Tests;

public sealed partial class MidiExportReadmeBuilderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void ReadmeLimitIsExactAndDoesNotReadBeyondItsPrefix(int count)
    {
        CountingReadmeDiagnostics source = new(count);
        string result = Encoding.UTF8.GetString(MidiExportReadmeBuilder.Build(Clone(CreateRequest(), diagnostics: source)));
        Assert.Equal(Math.Min(count, 1000), source.ReadCount);
        Assert.Equal(Math.Min(count, 1000), result.Split('\n').Count(line => line.StartsWith("- [Warning] W001:")));
        if (count > 1000)
        {
            Assert.Contains($"of {count.ToString("N0", CultureInfo.InvariantCulture)} diagnostics.", result);
            Assert.Contains($"{(count - 1000).ToString("N0", CultureInfo.InvariantCulture)} additional diagnostics are omitted", result);
            Assert.True(result.IndexOf("Showing the first", StringComparison.Ordinal)
                < result.IndexOf("- [Warning]", StringComparison.Ordinal));
        }
        else Assert.DoesNotContain("diagnostics are omitted", result);
        Assert.InRange(Encoding.UTF8.GetByteCount(result), 1, 200_000);
    }

    [Fact]
    public void FreezeDoesNotMaterializeTheUnwrittenTailAndKeepsLongTotal()
    {
        CountingReadmeDiagnostics source = new(1_000);
        const long total = 2_147_516_416;
        MidiExportReadmeRequest request = Clone(CreateRequest(), diagnostics: source, totalDiagnosticCount: total);
        MidiExportReadmeRequest frozen = MidiExportReadmeBuilder.Freeze(request);
        Assert.Equal(1_000, source.ReadCount);
        Assert.Equal(1_000, frozen.Diagnostics.Count);
        Assert.Equal(total, frozen.TotalDiagnosticCount);
        string output = Encoding.UTF8.GetString(MidiExportReadmeBuilder.Build(frozen));
        Assert.Equal(1_000, source.ReadCount);
        Assert.Contains("2,147,516,416 diagnostics", output);
        Assert.Contains("2,147,515,416 additional diagnostics", output);

        CountingReadmeDiagnostics legacy = new(int.MaxValue);
        MidiExportReadmeRequest legacyFrozen = MidiExportReadmeBuilder.Freeze(Clone(CreateRequest(), diagnostics: legacy));
        Assert.Equal(1_000, legacy.ReadCount);
        Assert.Equal(int.MaxValue, legacyFrozen.TotalDiagnosticCount);
        Assert.Equal(1_000, legacyFrozen.Diagnostics.Count);
    }

    [Fact]
    public void CompactProjectionCountsBillionsAndPreservesOnlyTheFirstThousandEligibleRows()
    {
        const int count = 65_537;
        CompilerDiagnosticList.Builder builder = new();
        var sources = Enumerable.Range(0, count).Select(index =>
            new CompilerDiagnosticList.OverlapDiagnosticSourceValue(new(100_001), new(100_002),
                new(index + 1), new(100_003), index)).ToArray();
        int source = builder.AddOverlapSource(sources, DiagnosticSeverity.Warning);
        for (int index = 0; index < count - 1; index++) builder.AddRange(source, index + 1, count - index - 1);
        CompilerDiagnosticList all = CompilerDiagnosticList.Insert(
            [new("I0", DiagnosticSeverity.Info, "Prefix", new()),
             new("D0", DiagnosticSeverity.Debug, "Excluded", new()),
             new("I1", DiagnosticSeverity.Info, "Suffix", new())], 2, builder.Build());
        MidiExportReadmeDiagnosticProjection projected = MidiExportReadmeDiagnosticProjection.Create(all);
        Assert.Equal(2_147_516_418L, projected.TotalCount);
        Assert.Equal(1_000, projected.Count);
        Assert.Equal("I0", projected[0].Code);
        Assert.Equal("MIDORA2201", projected[^1].Code);
        Assert.DoesNotContain(projected, value => value.Code is "D0" or "I1");
        string text = Encoding.UTF8.GetString(MidiExportReadmeBuilder.Build(Clone(CreateRequest(), diagnostics: projected)));
        Assert.Contains("2,147,515,418 additional diagnostics", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 200_000);
        MidiExportReadmeRequest wrongTotal = Clone(CreateRequest(), diagnostics: projected, totalDiagnosticCount: 1000);
        Assert.Throws<ArgumentException>(() => MidiExportReadmeBuilder.Build(wrongTotal));
        Assert.Throws<ArgumentException>(() => MidiExportReadmeBuilder.Freeze(wrongTotal));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MidiExportReadmeDiagnosticProjection.Create(all, canceled.Token));
    }

    [Theory]
    [InlineData(1000, -1L)]
    [InlineData(1000, 999L)]
    [InlineData(999, 2000L)]
    [InlineData(2000, 1500L)]
    public void InconsistentTotalsCannotPublishMisleadingReadmes(int prefix, long total)
    {
        MidiExportReadmeRequest request = Clone(CreateRequest(),
            diagnostics: new CountingReadmeDiagnostics(prefix), totalDiagnosticCount: total);
        Assert.ThrowsAny<ArgumentException>(() => MidiExportReadmeBuilder.Build(request));
        Assert.ThrowsAny<ArgumentException>(() => MidiExportReadmeBuilder.Freeze(request));
    }

    [Fact]
    public void LongMaxValueTotalKeepsExactOmittedCount()
    {
        CountingReadmeDiagnostics source = new(1000);
        MidiExportReadmeRequest request = Clone(CreateRequest(), diagnostics: source, totalDiagnosticCount: long.MaxValue);
        string output = Encoding.UTF8.GetString(MidiExportReadmeBuilder.Build(request));
        Assert.Contains("9,223,372,036,854,775,807 diagnostics", output);
        Assert.Contains("9,223,372,036,854,774,807 additional diagnostics", output);
        Assert.Equal(1000, source.ReadCount);
    }
}
