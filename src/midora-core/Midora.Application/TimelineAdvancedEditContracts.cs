namespace Midora.Application;

/// <summary>
/// An edit command whose successful Apply publishes the exact surviving
/// selection. Split commands include newly-created fragments; collision losers
/// are omitted. Undo publishes the original selection again.
/// </summary>
public interface ITimelineSelectionResultEditCommand : IProjectEditCommand
{
    IReadOnlyList<Midora.Domain.MidoraId> ResultSelectionIds { get; }
}

/// <summary>
/// Immutable before/after selection mapping frozen together with a detached
/// Timeline edit.  Consumers may project <see cref="ResultSelectionIds"/> into
/// their own optimized representation before the Project publication point.
/// </summary>
public sealed class PreparedTimelineSelection
{
    internal PreparedTimelineSelection(
        IReadOnlyList<Midora.Domain.MidoraId> originalSelectionIds,
        IReadOnlyList<Midora.Domain.MidoraId> resultSelectionIds)
    {
        OriginalSelectionIds = originalSelectionIds
            ?? throw new ArgumentNullException(nameof(originalSelectionIds));
        ResultSelectionIds = resultSelectionIds
            ?? throw new ArgumentNullException(nameof(resultSelectionIds));
    }

    public IReadOnlyList<Midora.Domain.MidoraId> OriginalSelectionIds { get; }
    public IReadOnlyList<Midora.Domain.MidoraId> ResultSelectionIds { get; }
}

/// <summary>
/// Internal prepared-edit facet used to carry the frozen selection mapping
/// through collision and document-session wrappers without re-materializing it.
/// </summary>
internal interface IPreparedTimelineSelectionEdit : IPreparedProjectEdit
{
    bool HasPreparedSelection => true;
    PreparedTimelineSelection PreparedSelection { get; }
}

/// <summary>
/// Internal pre-publication facet for prepared edits that depend on frozen
/// mutable Project state. Aggregate edits invoke every child gate before the
/// first child is allowed to publish, including children whose planned result
/// is otherwise a no-op.
/// </summary>
internal interface IPreparedProjectEditPublicationGate
{
    void ValidateForPublication(Midora.Domain.MidoraProject project);
}

/// <summary>
/// One homogeneous Logical Note selection owned by a single Segment.  The
/// multi-owner Stage 4 command overloads accept these descriptors so a future
/// cross-owner selection surface can still publish one Project history entry.
/// </summary>
public sealed record LogicalNoteOwnerSelection(
    Midora.Domain.MidoraId SegmentId,
    IReadOnlyCollection<Midora.Domain.MidoraId> NoteIds);

/// <summary>One homogeneous Direct MIDI Note selection in one MIDI Segment.</summary>
public sealed record DirectMidiNoteOwnerSelection(
    Midora.Domain.MidoraId SegmentId,
    IReadOnlyCollection<Midora.Domain.MidoraId> NoteIds);

/// <summary>One homogeneous Template Note selection in one SubVoice.</summary>
public sealed record TemplateNoteOwnerSelection(
    Midora.Domain.MidoraId EventInstrumentId,
    Midora.Domain.MidoraId SubVoiceId,
    IReadOnlyCollection<Midora.Domain.MidoraId> NoteIds);

/// <summary>Logical Parameter points selected from one Segment.</summary>
public sealed record LogicalParameterPointOwnerSelection(
    Midora.Domain.MidoraId SegmentId,
    IReadOnlyCollection<Midora.Domain.MidoraId> PointIds);

/// <summary>Direct MIDI channel events selected from one MIDI Segment.</summary>
public sealed record DirectMidiEventOwnerSelection(
    Midora.Domain.MidoraId SegmentId,
    IReadOnlyCollection<Midora.Domain.MidoraId> EventIds);

/// <summary>Template MIDI events selected from one SubVoice.</summary>
public sealed record TemplateEventOwnerSelection(
    Midora.Domain.MidoraId EventInstrumentId,
    Midora.Domain.MidoraId SubVoiceId,
    IReadOnlyCollection<Midora.Domain.MidoraId> EventIds);

public enum TimelineEditPreparationPhase
{
    ResolvingSelection,
    Planning,
    ResolvingCollisions,
    BuildingResult,
    Ready,
    PreparingIndex,
    ReadingSelection,
    Sorting,
    WritingStorage
}

/// <summary>
/// Bounded progress emitted while a detached Timeline edit is being prepared.
/// Completed and Total describe real work items in the current phase rather
/// than UI timer ticks. OverallFraction reserves fixed bands for the phases so
/// a phase-local counter remains monotonic when work changes shape.
/// </summary>
public readonly record struct TimelineEditPreparationProgress
{
    private readonly double? _overallFractionOverride;
    private TimelineEditPreparationProgress(TimelineEditPreparationProgress value, double overallFraction)
    {
        Phase = value.Phase; Completed = value.Completed; Total = value.Total;
        Detail = value.Detail;
        _overallFractionOverride = Math.Clamp(overallFraction, 0, 1);
    }
    public TimelineEditPreparationProgress InRange(double start, double length)
    {
        ValidateRange(start, length);
        return new(this, start + length * OverallFraction);
    }

    /// <summary>Maps this phase's actual record count, not the legacy edit
    /// phase bands, into a caller-owned portion of a larger task.</summary>
    public TimelineEditPreparationProgress InWorkRange(double start, double length)
    {
        ValidateRange(start, length);
        return new(this, start + length * (Total == 0 ? 0 : (double)Completed / Total));
    }

    private static void ValidateRange(double start, double length)
    {
        if (!double.IsFinite(start) || start < 0 || start > 1)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (!double.IsFinite(length) || length < 0 || start + length > 1 + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(length));
    }
    public TimelineEditPreparationProgress(
        TimelineEditPreparationPhase phase,
        long completed,
        long total)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (total != 0 && completed > total)
            throw new ArgumentOutOfRangeException(nameof(completed));
        Phase = phase;
        Completed = completed;
        Total = total;
    }

    public TimelineEditPreparationPhase Phase { get; }
    public long Completed { get; }
    public long Total { get; }
    /// <summary>Optional tool-specific work detail. It does not affect progress
    /// calculation, cancellation, or the meaning of the phase counters.</summary>
    public string? Detail { get; init; }
    public bool IsIndeterminate => Total == 0 && Phase != TimelineEditPreparationPhase.Ready;

    public double OverallFraction
    {
        get
        {
            if (_overallFractionOverride is double overall) return overall;
            (double start, double length) = Phase switch
            {
                TimelineEditPreparationPhase.ResolvingSelection => (0.00, 0.15),
                TimelineEditPreparationPhase.Planning => (0.15, 0.40),
                TimelineEditPreparationPhase.ResolvingCollisions => (0.55, 0.20),
                TimelineEditPreparationPhase.BuildingResult => (0.75, 0.24),
                TimelineEditPreparationPhase.Ready => (1.00, 0.00),
                TimelineEditPreparationPhase.PreparingIndex => (0.00, 0.15),
                TimelineEditPreparationPhase.ReadingSelection => (0.00, 1.00),
                TimelineEditPreparationPhase.Sorting => (0.55, 0.20),
                TimelineEditPreparationPhase.WritingStorage => (0.95, 0.04),
                _ => throw new ArgumentOutOfRangeException(nameof(Phase))
            };
            if (length == 0) return start;
            double phaseFraction = Total == 0
                ? 0
                : Math.Clamp((double)Completed / Total, 0, 1);
            return start + (length * phaseFraction);
        }
    }
}

/// <summary>
/// Optional extension used by long Timeline commands. Preparation remains
/// cancellable and detached; Apply/Undo are still atomic and non-cancellable.
/// </summary>
public interface IProgressReportingProjectEditCommand : ICancellableProjectEditCommand
{
    IPreparedProjectEdit Prepare(
        Midora.Domain.MidoraProject project,
        CancellationToken cancellationToken,
        IProgress<TimelineEditPreparationProgress>? progress);
}

public enum TimelineHumanizeMode
{
    Disabled,
    Add,
    Multiply,
    Override
}

public readonly record struct TimelineHumanizeField
{
    private TimelineHumanizeField(
        TimelineHumanizeMode mode,
        long integerMinimum,
        long integerMaximum,
        double factorMinimum,
        double factorMaximum)
    {
        Mode = mode;
        IntegerMinimum = integerMinimum;
        IntegerMaximum = integerMaximum;
        FactorMinimum = factorMinimum;
        FactorMaximum = factorMaximum;
    }

    public TimelineHumanizeMode Mode { get; }
    public long IntegerMinimum { get; }
    public long IntegerMaximum { get; }
    public double FactorMinimum { get; }
    public double FactorMaximum { get; }

    public static TimelineHumanizeField Disabled { get; } =
        new(TimelineHumanizeMode.Disabled, 0, 0, 0, 0);

    public static TimelineHumanizeField Add(long minimum, long maximum) =>
        new(TimelineHumanizeMode.Add, minimum, maximum, 0, 0);

    public static TimelineHumanizeField Multiply(double minimum, double maximum) =>
        new(TimelineHumanizeMode.Multiply, 0, 0, minimum, maximum);

    public static TimelineHumanizeField Override(long minimum, long maximum) =>
        new(TimelineHumanizeMode.Override, minimum, maximum, 0, 0);
}

public sealed record TimelineHumanizeOptions(
    TimelineHumanizeField Tick,
    TimelineHumanizeField Gate,
    TimelineHumanizeField Velocity,
    ulong? Seed = null);

public enum NoteSplitMode
{
    FixedPieceLength,
    MaximumPieceCount,
    Expression
}

public sealed record NoteSplitOptions
{
    public const int MaximumSupportedCuts = 16_777_216;
    public const int MaximumSupportedResultObjects = 100_000_000;

    public NoteSplitMode Mode { get; init; }
    public long FixedPieceLengthTicks { get; init; } = 1;
    public int MaximumPieceCount { get; init; } = 2;
    public Midora.Compiler.NoteSplitExpressionProgram? ExpressionProgram { get; init; }

    /// <summary>
    /// Safety stop for <see cref="NoteSplitMode.Expression"/> only. Fixed
    /// length and maximum-piece modes always plan their complete result and
    /// are governed by <see cref="MaximumSupportedCuts"/> instead.
    /// </summary>
    public int MaximumCuts { get; init; } = 65_535;
    public int MaximumResultObjects { get; init; } = MaximumSupportedResultObjects;
}

public sealed record NoteJoinOptions(long MaximumGapTicks = 0);

public enum TimelineQuantizeGridKind
{
    MusicalFraction,
    CustomTicks
}

/// <summary>
/// Quantization grid. Musical fractions are fractions of a whole note, so
/// 1/4 is one quarter note and 1/16 is one sixteenth note.
/// </summary>
public readonly record struct TimelineQuantizeGrid
{
    private TimelineQuantizeGrid(
        TimelineQuantizeGridKind kind,
        int numerator,
        int denominator,
        long customTicks)
    {
        Kind = kind;
        Numerator = numerator;
        Denominator = denominator;
        CustomTicks = customTicks;
    }

    public TimelineQuantizeGridKind Kind { get; }
    public int Numerator { get; }
    public int Denominator { get; }
    public long CustomTicks { get; }

    public static TimelineQuantizeGrid MusicalFraction(int numerator, int denominator) =>
        new(TimelineQuantizeGridKind.MusicalFraction, numerator, denominator, 0);

    public static TimelineQuantizeGrid FromCustomTicks(long ticks) =>
        new(TimelineQuantizeGridKind.CustomTicks, 0, 0, ticks);
}

public enum NoteQuantizeMode
{
    StartOnly,
    StartAndEnd
}

public sealed record NoteQuantizeOptions(
    TimelineQuantizeGrid Grid,
    NoteQuantizeMode Mode = NoteQuantizeMode.StartOnly);
