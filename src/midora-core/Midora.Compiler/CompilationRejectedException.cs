using System.Text;

namespace Midora.Compiler;

/// <summary>A bounded message with the complete, potentially virtual diagnostic sequence retained separately.</summary>
public sealed class CompilationRejectedException : InvalidOperationException
{
    public ICompilerDiagnosticSequence Diagnostics { get; }

    public CompilationRejectedException(IEnumerable<CompilerDiagnostic> diagnostics)
        : this(CompilerDiagnosticSequence.Wrap(diagnostics)) { }

    private CompilationRejectedException(ICompilerDiagnosticSequence diagnostics)
        : base(Format(diagnostics)) => Diagnostics = diagnostics;

    private static string Format(ICompilerDiagnosticSequence diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        const int maximumMessageRows = 32;
        StringBuilder text = new();
        for (int i = 0; i < Math.Min(maximumMessageRows, diagnostics.Count); i++)
        {
            if (i != 0) text.AppendLine();
            CompilerDiagnostic diagnostic = diagnostics[i];
            text.Append(diagnostic.Code).Append(": ").Append(diagnostic.Message);
        }
        if (diagnostics.Count > maximumMessageRows)
        {
            text.AppendLine().Append(diagnostics.Count - maximumMessageRows)
                .Append(" additional diagnostics. The complete diagnostic sequence is retained separately.");
        }
        return text.ToString();
    }
}
