using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// The outside editor/tool value domain, never a Project or Mapping value domain.
/// A delta is unchanged by this translation; decode only absolute results.
/// </summary>
public static class MidiEditingValueDomain
{
    public const int NumericContractVersion = 2;

    public static int ControllerOffset(int controller) => controller is 10 or >= 71 and <= 78 ? -64 : 0;
    public static int Offset(MidiValueTarget target) =>
        target.Kind == MidiValueKind.ControlChange ? ControllerOffset(target.Number) : 0;
    public static int Offset(DirectMidiChannelEventKind kind, int number) =>
        kind == DirectMidiChannelEventKind.ControlChange ? ControllerOffset(number) : 0;
    public static int Offset(TemplateEventKind kind, int number) =>
        kind == TemplateEventKind.ControlChange ? ControllerOffset(number) : 0;

    public static int ControllerDisplay(int controller, int raw) => checked(raw + ControllerOffset(controller));
    public static int ControllerRaw(int controller, int display) => checked(display - ControllerOffset(controller));
    public static int ClampInitialState(MidiValueTarget target, int display)
    {
        int offset = Offset(target);
        // Clamp before adding the offset, including int.MinValue/MaxValue input.
        return offset == 0 ? MidiStateValueRules.Clamp(target, display) : Math.Clamp(display, -64, 63) + 64;
    }
}
