using System.Text;

namespace Midora.MidiExport.Tests;

public sealed partial class MidiExportReadmeBuilderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void TimingInfoIsIndependentOfTheCompilerDiagnosticPrefix(int diagnosticCount)
    {
        CountingReadmeDiagnostics diagnostics = new(diagnosticCount);
        var request = Clone(CreateRequest(), diagnostics: diagnostics);
        var summary = new MidiExportPaddingSummary(3_000_000_000, 100_000, 3);
        using MemoryStream stream = new();
        MidiExportReadmeBuilder.WriteTo(stream, request, paddingSummary: summary);
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains(summary.Message, text);
        Assert.Equal(1, text.Split("## SMF Timing Compatibility (Export Info)").Length - 1);
        Assert.Equal(Math.Min(1000, diagnosticCount), diagnostics.ReadCount);
        Assert.Equal(Math.Min(1000, diagnosticCount), text.Split('\n').Count(line => line.StartsWith("- [Warning] W001:")));
        Assert.True(text.IndexOf("SMF Timing Compatibility", StringComparison.Ordinal) < text.IndexOf("## Diagnostics", StringComparison.Ordinal));
    }
}
