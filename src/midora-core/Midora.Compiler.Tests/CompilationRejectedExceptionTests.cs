using System.Collections;

namespace Midora.Compiler.Tests;

public sealed class CompilationRejectedExceptionTests
{
    [Fact]
    public void LargeFailureRetainsCompleteSequenceWithoutFormattingEveryRow()
    {
        CountedDiagnostics diagnostics = new();
        CompilationRejectedException exception = new(diagnostics);
        Assert.Same(diagnostics, exception.Diagnostics);
        Assert.Equal(32, diagnostics.Reads);
        Assert.Contains("999968 additional diagnostics", exception.Message);
        Assert.Equal(1_000_000, exception.Diagnostics.Count);
    }

    [Fact]
    public void SmallFailureKeepsOriginalMessageFormat()
    {
        CompilerDiagnostic[] diagnostics =
        [
            new("TEST1", DiagnosticSeverity.Error, "First.", new()),
            new("TEST2", DiagnosticSeverity.Warning, "Second.", new())
        ];
        Assert.Equal($"TEST1: First.{Environment.NewLine}TEST2: Second.",
            new CompilationRejectedException(diagnostics).Message);
    }

    private sealed class CountedDiagnostics : ICompilerDiagnosticSequence
    {
        public int Reads { get; private set; }
        public long Count => 1_000_000;
        public CompilerDiagnostic this[long index]
        {
            get { Reads++; return new("TEST", DiagnosticSeverity.Error, "Repeated diagnostic.", new(Tick: index)); }
        }
        public IEnumerator<CompilerDiagnostic> GetEnumerator() => throw new InvalidOperationException("Do not enumerate the whole sequence.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
