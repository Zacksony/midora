using System.Globalization;

namespace Midora.Domain;

public readonly record struct MidoraId : IComparable<MidoraId>
{
    public MidoraId(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Stable IDs must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public static MidoraId FromSequence(long sequence) => new(sequence);

    public static bool TryParseCanonical(string? value, out MidoraId id)
    {
        id = default;
        if (string.IsNullOrEmpty(value) || value[0] is < '1' or > '9')
        {
            return false;
        }
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
        {
            return false;
        }

        id = FromSequence(sequence);
        return true;
    }

    public int CompareTo(MidoraId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct TickRange(long StartTick, long EndTick)
{
    public long Length => EndTick - StartTick;
    public bool IsValid => StartTick >= 0 && EndTick >= StartTick;
    public bool Contains(long tick) => tick >= StartTick && tick < EndTick;
    public bool Intersects(TickRange other) => StartTick < other.EndTick && other.StartTick < EndTick;
}

public readonly record struct MidoraColor(byte Red, byte Green, byte Blue)
{
    public static MidoraColor DefaultInstrument { get; } = new(0x6b, 0x72, 0x80);
}

public enum CurveInterpolation
{
    Step,
    Linear
}

public sealed record CurvePoint
{
    internal CurvePoint(CurvePointSnapshotValue value)
    {
        Id = value.Id;
        Tick = value.Tick;
        Value = value.Value;
        Interpolation = value.Interpolation;
    }

    public CurvePoint(
        MidoraProject project,
        long tick,
        double value,
        CurveInterpolation interpolation = CurveInterpolation.Linear)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        Value = value;
        Interpolation = interpolation;
    }

    internal CurvePoint(
        MidoraProject project,
        MidoraId preservedId,
        long tick,
        double value,
        CurveInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        Value = value;
        Interpolation = interpolation;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public double Value { get; init; }
    public CurveInterpolation Interpolation { get; init; }
}

public sealed class ValueCurve
{
    public ValueCurve(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        TargetSettings = new MidiIntegerTargetSettings();
    }

    internal ValueCurve(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        TargetSettings = new MidiIntegerTargetSettings();
    }

    public MidoraId Id { get; init; }
    public MidiValueTarget Target { get; set; }
    public MidiIntegerTargetSettings TargetSettings { get; }
    public CurvePointCollection Points { get; } = new();
}

public enum MidiValueKind
{
    ControlChange,
    BankMsb,
    BankLsb,
    Program,
    PitchBend,
    RegisteredParameter,
    NonRegisteredParameter,
    PitchBendRangeSemitones,
    PitchBendRangeCents
}

public readonly record struct MidiValueTarget(MidiValueKind Kind, int Number = 0)
{
    public static MidiValueTarget ControlChange(int controller) => new(MidiValueKind.ControlChange, controller);
    public static MidiValueTarget BankMsb => new(MidiValueKind.BankMsb);
    public static MidiValueTarget BankLsb => new(MidiValueKind.BankLsb);
    public static MidiValueTarget Program => new(MidiValueKind.Program);
    public static MidiValueTarget PitchBend => new(MidiValueKind.PitchBend);
    public static MidiValueTarget Rpn(int parameter) => new(MidiValueKind.RegisteredParameter, parameter);
    public static MidiValueTarget Nrpn(int parameter) => new(MidiValueKind.NonRegisteredParameter, parameter);
    public static MidiValueTarget PitchBendRange => PitchBendRangeSemitones;
    public static MidiValueTarget PitchBendRangeSemitones => new(MidiValueKind.PitchBendRangeSemitones);
    public static MidiValueTarget PitchBendRangeCents => new(MidiValueKind.PitchBendRangeCents);
}
