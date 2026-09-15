namespace Midora.Compiler;

/// <summary>A diagnostic capacity failure, not a musical tick/value overflow.</summary>
public sealed class DiagnosticCapacityExceededException : InvalidOperationException
{
    public DiagnosticCapacityExceededException()
        : base("The diagnostic count exceeds Midora's supported limit of 9,223,372,036,854,775,807. "
            + "Compilation was stopped without publishing incomplete diagnostics. "
            + "Reduce policy-constrained overlaps or revise the Event Instrument overlap settings.") { }

    internal static long Add(long current, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > long.MaxValue - current) throw new DiagnosticCapacityExceededException();
        return current + count;
    }
}
