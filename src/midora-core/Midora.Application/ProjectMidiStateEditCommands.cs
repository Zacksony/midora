using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateProjectInitialStateValue(
        MidiValueTarget target,
        int? value) =>
        UpdateMidiStateValue(
            "Change project initial state",
            static project => project.GlobalInitialState,
            EverythingChange,
            target,
            value);

    public static IProjectEditCommand UpdateProjectResetDefaultValue(
        MidiValueTarget target,
        int? value) =>
        UpdateMidiStateValue(
            "Change project reset default",
            static project => project.GlobalResetDefaults,
            EverythingChange,
            target,
            value);

    public static IProjectEditCommand UpdateEventInstrumentInitialStateValue(
        MidoraId eventInstrumentId,
        MidiValueTarget target,
        int? value) =>
        UpdateMidiStateValue(
            "Change event instrument initial state",
            project => FindEventInstrument(project, eventInstrumentId).InitialState,
            () => EventInstrumentChange(eventInstrumentId),
            target,
            value);

    public static IProjectEditCommand UpdateSubVoiceInitialStateValue(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        int? value) =>
        UpdateMidiStateValue(
            "Change subvoice initial state",
            project => FindSubVoice(
                FindEventInstrument(project, eventInstrumentId),
                subVoiceId).InitialState,
            () => EventInstrumentChange(eventInstrumentId),
            target,
            value);

    private static IProjectEditCommand UpdateMidiStateValue(
        string commandName,
        Func<MidoraProject, MidiInitialState> selectState,
        Func<ProjectChangeSet> createChanges,
        MidiValueTarget target,
        int? value) =>
        Command(commandName, project =>
        {
            MidiStateValueRules.Validate(target, value);
            MidiInitialState state = selectState(project);
            int? oldValue = GetMidiStateValue(state, target);
            return Prepared(
                oldValue != value,
                createChanges(),
                _ => SetMidiStateValue(state, target, value),
                _ => SetMidiStateValue(state, target, oldValue));
        });

    private static int? GetMidiStateValue(MidiInitialState state, MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange => GetDictionaryValue(state.Controllers, target.Number),
            MidiValueKind.BankMsb => state.BankMsb,
            MidiValueKind.BankLsb => state.BankLsb,
            MidiValueKind.Program => state.Program,
            MidiValueKind.PitchBend => state.PitchBend,
            MidiValueKind.RegisteredParameter =>
                GetDictionaryValue(state.RegisteredParameters, target.Number),
            MidiValueKind.NonRegisteredParameter =>
                GetDictionaryValue(state.NonRegisteredParameters, target.Number),
            MidiValueKind.PitchBendRangeSemitones => state.PitchBendRangeSemitones,
            MidiValueKind.PitchBendRangeCents => state.PitchBendRangeCents,
            _ => throw new InvalidOperationException("The MIDI State target is unsupported.")
        };

    private static int? GetDictionaryValue(Dictionary<int, int> values, int key) =>
        values.TryGetValue(key, out int value) ? value : null;

    private static void SetMidiStateValue(
        MidiInitialState state,
        MidiValueTarget target,
        int? value)
    {
        switch (target.Kind)
        {
            case MidiValueKind.ControlChange:
                SetDictionaryValue(state.Controllers, target.Number, value);
                break;
            case MidiValueKind.BankMsb:
                state.BankMsb = value;
                break;
            case MidiValueKind.BankLsb:
                state.BankLsb = value;
                break;
            case MidiValueKind.Program:
                state.Program = value;
                break;
            case MidiValueKind.PitchBend:
                state.PitchBend = value;
                break;
            case MidiValueKind.RegisteredParameter:
                SetDictionaryValue(state.RegisteredParameters, target.Number, value);
                break;
            case MidiValueKind.NonRegisteredParameter:
                SetDictionaryValue(state.NonRegisteredParameters, target.Number, value);
                break;
            case MidiValueKind.PitchBendRangeSemitones:
                state.PitchBendRangeSemitones = value;
                break;
            case MidiValueKind.PitchBendRangeCents:
                state.PitchBendRangeCents = value;
                break;
            default:
                throw new InvalidOperationException("The MIDI State target is unsupported.");
        }
    }

    private static void SetDictionaryValue(
        Dictionary<int, int> values,
        int key,
        int? value)
    {
        if (value.HasValue)
        {
            values[key] = value.Value;
        }
        else
        {
            values.Remove(key);
        }
    }
}
