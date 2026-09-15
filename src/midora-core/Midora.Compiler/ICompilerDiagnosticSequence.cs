using System.Collections;

namespace Midora.Compiler;

/// <summary>
/// A complete diagnostic sequence. Logical ordinals are Int64; consumers that
/// need an Int32 collection must expose an explicit bounded window instead.
/// </summary>
public interface ICompilerDiagnosticSequence : IEnumerable<CompilerDiagnostic>
{
    long Count { get; }
    CompilerDiagnostic this[long index] { get; }
    CompilerDiagnostic this[int index] => this[(long)index];
    CompilerDiagnostic this[Index index] => this[index.IsFromEnd ? Count - index.Value : index.Value];
}

public static class CompilerDiagnosticSequence
{
    /// <summary>Adapts a caller-owned frozen list without enumerating its rows.</summary>
    public static ICompilerDiagnosticSequence Wrap(IEnumerable<CompilerDiagnostic> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source as ICompilerDiagnosticSequence
            ?? new ListAdapter(source as IReadOnlyList<CompilerDiagnostic> ?? source.ToArray());
    }

    private sealed class ListAdapter(IReadOnlyList<CompilerDiagnostic> source) : ICompilerDiagnosticSequence
    {
        public long Count => source.Count;
        public CompilerDiagnostic this[long index] => index >= 0 && index < Count
            ? source[(int)index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<CompilerDiagnostic> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
