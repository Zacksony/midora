using Midora.Domain;

namespace Midora.Application;

/// <summary>Preserves independent inheritance; resolving a name never writes an override.</summary>
public readonly record struct InstrumentSelectionValues(int? BankMsb, int? BankLsb, int? Program)
{
    public static InstrumentSelectionValues From(MidiInitialState state) =>
        new(state.BankMsb, state.BankLsb, state.Program);

    public void Validate()
    {
        if (BankMsb is < 0 or > 127 || BankLsb is < 0 or > 127 || Program is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(InstrumentSelectionValues), "Bank and Program use 0–127.");
    }

    public InstrumentAddress Resolve(InstrumentAddress inherited)
    {
        Validate(); inherited.Validate();
        return new((byte)(BankMsb ?? inherited.BankMsb), (byte)(BankLsb ?? inherited.BankLsb),
            (byte)(Program ?? inherited.Program));
    }

    public static InstrumentSelectionValues Explicit(InstrumentAddress address)
    { address.Validate(); return new(address.BankMsb, address.BankLsb, address.Program); }

    public void ApplyTo(MidiInitialState state)
    { Validate(); state.BankMsb = BankMsb; state.BankLsb = BankLsb; state.Program = Program; }
}
