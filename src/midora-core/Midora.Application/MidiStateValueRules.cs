using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Defines the single authoritative numeric range used by editable MIDI
/// initial-state values. UI drafts may use this rule to clamp a syntactically
/// valid integer before submitting the formal Project command; the command
/// still validates the final value independently.
/// </summary>
public static class MidiStateValueRules
{
    public static (int Minimum, int Maximum) GetRange(MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange when target.Number is >= 0 and <= 119
                && target.Number is not 91 and not 93 => (0, 127),
            MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program
                when target.Number == 0 => (0, 127),
            MidiValueKind.PitchBend when target.Number == 0 => (-8192, 8191),
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter
                when target.Number is >= 0 and <= 16_383 => (0, 16_383),
            MidiValueKind.PitchBendRangeSemitones when target.Number == 0 => (0, 127),
            MidiValueKind.PitchBendRangeCents when target.Number == 0 => (0, 99),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                "The MIDI State target is unsupported or invalid.")
        };

    public static int Clamp(MidiValueTarget target, int value)
    {
        (int minimum, int maximum) = GetRange(target);
        return Math.Clamp(value, minimum, maximum);
    }

    public static void Validate(MidiValueTarget target, int? value)
    {
        (int minimum, int maximum) = GetRange(target);
        if (value is not null && (value < minimum || value > maximum))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
