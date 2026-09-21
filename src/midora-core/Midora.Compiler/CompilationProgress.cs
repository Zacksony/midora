namespace Midora.Compiler;

public enum CompilationPhase
{
    PreparingSnapshot, Validating, FreezingConductor, LogicalTracks, OverlapPolicies,
    MidiRoots, MidiMetadata, CheckingOverlaps, AllocatingChannels, LogicalInstances,
    SortingLogicalEvents, ApplyingRange, PublishingMidi, Fingerprinting, PreparingAudio
}

/// <summary>Percent is scoped to Phase, never an estimate of whole-compile time.</summary>
public sealed record CompilationProgressSnapshot(CompilationPhase Phase, int? Percent);

/// <summary>
/// Single producer, many readers. Holds only the latest phase/whole percent;
/// no callbacks, task queues, per-event objects or retained progress history.
/// Deliberately absent from compilation identity and canonical output.
/// </summary>
public sealed class CompilationProgress
{
    private CompilationProgressSnapshot _current = new(CompilationPhase.PreparingSnapshot, null);
    public CompilationProgressSnapshot Current => Volatile.Read(ref _current);
    public int PublicationCount { get; private set; }

    public void Report(CompilationPhase phase, long completed = 0, long? total = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        if (total is < 0 || total.HasValue && completed > total.Value) throw new ArgumentOutOfRangeException(nameof(total));
        int? percent = total is > 0 ? (int)((Int128)completed * 100 / total.Value) : null;
        var previous = _current;
        if (previous.Phase == phase && previous.Percent == percent) return;
        Volatile.Write(ref _current, new(phase, percent));
        PublicationCount++;
    }
}
