using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateTemplateNote(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        long lengthTicks,
        int note,
        int velocity,
        bool followPitchDelta) =>
        UpdateTemplateEvent(
            "Change template note",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Note,
            current => current with
            {
                Tick = tick,
                LengthTicks = lengthTicks,
                Number = note,
                Value = velocity,
                FollowPitchDelta = followPitchDelta
            });

    public static IProjectEditCommand UpdateTemplateControlChange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int controller,
        int value) =>
        UpdateTemplateEvent(
            "Change template control change",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.ControlChange,
            current => current with { Tick = tick, Number = controller, Value = value });

    public static IProjectEditCommand UpdateTemplateBank(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int? bankMsb,
        int? bankLsb) =>
        UpdateTemplateEvent(
            "Change template bank",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Bank,
            current => current with
            {
                Tick = tick,
                Value = bankMsb ?? 0,
                SecondaryValue = bankLsb ?? 0,
                HasBankMsb = bankMsb.HasValue,
                HasBankLsb = bankLsb.HasValue
            });

    public static IProjectEditCommand UpdateTemplateProgram(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int program) =>
        UpdateTemplateEvent(
            "Change template program",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Program,
            current => current with { Tick = tick, Value = program });

    public static IProjectEditCommand UpdateTemplatePitchBend(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int value) =>
        UpdateTemplateEvent(
            "Change template pitch bend",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.PitchBend,
            current => current with { Tick = tick, Value = value });

    public static IProjectEditCommand UpdateTemplateRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateParameter(
            "Change template RPN",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.RegisteredParameter,
            tick,
            parameter,
            value);

    public static IProjectEditCommand UpdateTemplateNonRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateParameter(
            "Change template NRPN",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.NonRegisteredParameter,
            tick,
            parameter,
            value);

    public static IProjectEditCommand UpdateTemplatePitchBendRange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int semitones,
        int cents) =>
        UpdateTemplateEvent(
            "Change template pitch bend range",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.PitchBendRange,
            current => current with
            {
                Tick = tick,
                Value = semitones,
                SecondaryValue = cents
            });

    public static IProjectEditCommand DeleteTemplateEvent(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId) =>
        Command("Delete template event", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEvent templateEvent = FindTemplateEvent(voice, templateEventId);
            int originalIndex = voice.Events.IndexOf(templateEvent);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(voice.Events, templateEvent, "Template Event"),
                _ => InsertAt(voice.Events, originalIndex, templateEvent, "Template Event"));
        });

    private static IProjectEditCommand UpdateTemplateParameter(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        TemplateEventKind expectedKind,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateEvent(
            commandName,
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            expectedKind,
            current => current with { Tick = tick, Number = parameter, Value = value });

    private static IProjectEditCommand UpdateTemplateEvent(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        TemplateEventKind expectedKind,
        Func<TemplateEventValue, TemplateEventValue> update) =>
        Command(commandName, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return PrepareBoundedTemplateUpdate(project, instrument, voice, templateEventId, expectedKind, update);
            TemplateEvent templateEvent = FindTemplateEvent(voice, templateEventId);
            if (templateEvent.Kind != expectedKind)
            {
                throw new InvalidOperationException(
                    $"The Template Event is not a {expectedKind} event.");
            }
            TemplateEventValue old = CaptureTemplateEvent(templateEvent);
            TemplateEventValue replacement = update(old);
            ValidateTemplateEventEdit(templateEvent, replacement);
            HashSet<TemplateEventMappingTarget> oldTargets =
                EnumerateTemplateEventMappingTargets(old).ToHashSet();
            HashSet<TemplateEventMappingTarget> peerTargets = voice.Events
                .Where(value => !ReferenceEquals(value, templateEvent))
                .SelectMany(TemplateEventMappingTarget.Enumerate)
                .ToHashSet();
            TemplateEventMappingTarget[] optionalMappingTargetsToCreate =
                EnumerateTemplateEventMappingTargets(replacement)
                    .Where(target => target.EventKind != TemplateEventKind.Note
                        && !oldTargets.Contains(target)
                        && !peerTargets.Contains(target)
                        && voice.EventMappings.All(mapping => mapping.Target != target))
                    .ToArray();
            long requiredBoundary = replacement.Kind == TemplateEventKind.Note
                ? checked(replacement.Tick + replacement.LengthTicks)
                : checked(replacement.Tick + 1);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            SubVoiceEventMapping[]? createdMappings = null;
            IPreparedProjectEdit prepared = Prepared(
                old != replacement
                    || oldTemplateLength != replacementTemplateLength
                    || optionalMappingTargetsToCreate.Length != 0,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    RequireContains(voice.Events, templateEvent, "Template Event");
                    SetTemplateEvent(templateEvent, replacement);
                    createdMappings ??= optionalMappingTargetsToCreate
                        .Select(target => new SubVoiceEventMapping(project, target))
                        .ToArray();
                    RestoreNewTemplateEventMappings(voice, createdMappings);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    RequireContains(voice.Events, templateEvent, "Template Event");
                    foreach (SubVoiceEventMapping mapping in createdMappings ?? [])
                    {
                        RemoveRequired(
                            voice.EventMappings,
                            mapping,
                            "SubVoice event Mapping");
                    }
                    SetTemplateEvent(templateEvent, old);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
            return replacement.Kind == TemplateEventKind.Note
                ? ResolveTargetedExactTemplateNoteCollisions(
                    prepared,
                    [new(voice, replacement.Tick, replacement.Number)])
                : ResolveTargetedExactTemplateEventPointCollisions(
                    prepared,
                    CreateTemplateEventPointCollisionTargets(voice, replacement));
        });

    private static TemplateEvent FindTemplateEvent(SubVoice voice, MidoraId templateEventId) =>
        voice.Events.TryGetById(templateEventId, out TemplateEvent? value) && value is not null
            ? value
            : throw new ArgumentOutOfRangeException(nameof(templateEventId));

    private static void ValidateTemplateEventEdit(
        TemplateEvent templateEvent,
        TemplateEventValue value) => ValidateTemplateEventValue(new(
            templateEvent.Id, value.Kind, value.Tick, value.LengthTicks, value.Number, value.Value,
            value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta));

    internal static void ValidateTemplateEventValue(TemplateEventSnapshotValue value)
    {
        if (!Enum.IsDefined(value.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(value.Kind));
        }
        if (value.Tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.Tick));
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0
                    || value.Tick > long.MaxValue - value.LengthTicks)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.LengthTicks));
                }
                if (value.Number is < 0 or > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Number));
                }
                if (value.Value is < 1 or > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Value));
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 or 91 or 93)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Number));
                }
                ValidateSevenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.Bank:
                if (!value.HasBankMsb && !value.HasBankLsb)
                {
                    throw new ArgumentException("Bank must contain an MSB, an LSB, or both.");
                }
                if (value.HasBankMsb)
                {
                    ValidateSevenBit(value.Value, nameof(value.Value));
                }
                if (value.HasBankLsb)
                {
                    ValidateSevenBit(value.SecondaryValue, nameof(value.SecondaryValue));
                }
                break;
            case TemplateEventKind.Program:
                ValidateSevenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.PitchBend:
                if (value.Value is < -8192 or > 8191)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Value));
                }
                break;
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                ValidateFourteenBit(value.Number, nameof(value.Number));
                ValidateFourteenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.PitchBendRange:
                ValidateSevenBit(value.Value, nameof(value.Value));
                if (value.SecondaryValue is < 0 or > 99)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.SecondaryValue));
                }
                break;
        }
        if (value.Kind != TemplateEventKind.Note && value.Tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value.Tick),
                "An instantaneous Template Event requires a representable exclusive end boundary.");
        }
    }

    private static void ValidateSevenBit(int value, string parameterName)
    {
        if (value is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFourteenBit(int value, string parameterName)
    {
        if (value is < 0 or > 16_383)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static bool TemplateEventsConflict(
        TemplateEvent candidate,
        TemplateEventValue replacement)
    {
        if (candidate.Tick != replacement.Tick || candidate.Kind == TemplateEventKind.Note)
        {
            return false;
        }
        if (replacement.Kind == TemplateEventKind.Note)
        {
            return false;
        }
        return TemplateEventExactCollision.Conflicts(
            candidate.Kind,
            candidate.Number,
            candidate.HasBankMsb,
            candidate.HasBankLsb,
            replacement.Kind,
            replacement.Number,
            replacement.HasBankMsb,
            replacement.HasBankLsb);
    }

    private static TemplateEventValue CaptureTemplateEvent(TemplateEvent value) =>
        new(
            value.Kind,
            value.Tick,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            value.FollowPitchDelta);

    private static void SetTemplateEvent(TemplateEvent target, TemplateEventValue value)
    {
        target.SetValues(
            value.Kind,
            value.Tick,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            value.FollowPitchDelta);
        target.EnsureMappings();
    }

    private sealed record TemplateEventValue(
        TemplateEventKind Kind,
        long Tick,
        long LengthTicks,
        int Number,
        int Value,
        int SecondaryValue,
        bool HasBankMsb,
        bool HasBankLsb,
        bool FollowPitchDelta);

    private readonly record struct IndexedTemplateEvent(TemplateEvent Event, int Index);
}
