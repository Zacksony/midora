using Midora.Compiler;

namespace Midora.Application;

public static class TimelineGenerationLimits
{
    public const int DefaultMaximumCandidates = 65_535;
    public const int MaximumCandidates = 16_777_216;

    internal static void Validate(long baseTick, int maximumCandidates, long? maximumRelativeStartTick)
    {
        if (baseTick < 0 || baseTick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(baseTick));
        if (maximumCandidates is < 1 or > MaximumCandidates) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        if (maximumRelativeStartTick < 0) throw new ArgumentOutOfRangeException(nameof(maximumRelativeStartTick));
    }

    internal static void Finite(params double[] values)
    {
        if (values.Any(static value => !double.IsFinite(value)))
            throw new ArgumentException("Generator initial values must be finite.");
    }
}

public sealed record NoteGenerationOptions
{
    public long BaseTick { get; init; }
    public int MaximumCandidates { get; init; } = TimelineGenerationLimits.DefaultMaximumCandidates;
    public long? MaximumRelativeStartTick { get; init; }
    public bool CreateFirstFromInitialValues { get; init; }
    public double InitialVelocity { get; init; } = 1;
    public double InitialKey { get; init; }
    public double InitialGate { get; init; } = 1;
    public double InitialTick { get; init; }
    public string? VelocityExpression { get; init; }
    public string? KeyExpression { get; init; }
    public string? GateExpression { get; init; }
    public string? TickExpression { get; init; }

    public GeneratorExpressionProgram Compile() => GeneratorExpressionProgram.Compile(new Dictionary<BatchEditField, string?>
    {
        [BatchEditField.Velocity] = VelocityExpression, [BatchEditField.KeyNumber] = KeyExpression,
        [BatchEditField.Gate] = GateExpression, [BatchEditField.Tick] = TickExpression
    });

    public NoteGenerationOptions Validate()
    {
        ValidateValues();
        using var program = Compile();
        return this;
    }

    internal void ValidateValues()
    {
        TimelineGenerationLimits.Validate(BaseTick, MaximumCandidates, MaximumRelativeStartTick);
        TimelineGenerationLimits.Finite(InitialVelocity, InitialKey, InitialGate, InitialTick);
    }
}

public sealed record EventGenerationOptions
{
    public long BaseTick { get; init; }
    public int MaximumCandidates { get; init; } = TimelineGenerationLimits.DefaultMaximumCandidates;
    public long? MaximumRelativeStartTick { get; init; }
    public bool CreateFirstFromInitialValues { get; init; }
    public double InitialValue { get; init; }
    public double InitialTick { get; init; }
    public string? ValueExpression { get; init; }
    public string? TickExpression { get; init; }

    public GeneratorExpressionProgram Compile() => GeneratorExpressionProgram.Compile(new Dictionary<BatchEditField, string?>
    {
        [BatchEditField.PointValue] = ValueExpression, [BatchEditField.Tick] = TickExpression
    });

    public EventGenerationOptions Validate()
    {
        ValidateValues();
        using var program = Compile();
        return this;
    }

    internal void ValidateValues()
    {
        TimelineGenerationLimits.Validate(BaseTick, MaximumCandidates, MaximumRelativeStartTick);
        TimelineGenerationLimits.Finite(InitialValue, InitialTick);
    }
}
