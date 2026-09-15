namespace Midora.Domain;

/// <summary>
/// Defines the formal reset/default value for stateful Event Instrument MIDI targets.
/// </summary>
public static class MidiValueTargetDefaults
{
    public static int GetDefaultValue(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => GetControllerDefaultValue(target.Number),
        MidiValueKind.PitchBend => 0,
        MidiValueKind.PitchBendRangeSemitones => 2,
        MidiValueKind.PitchBendRangeCents => 0,
        _ => 0
    };

    public static int GetControllerDefaultValue(int controller) => controller switch
    {
        7 => 100,
        10 => 64,
        11 => 127,
        _ => 0
    };
}
