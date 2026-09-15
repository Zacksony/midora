using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateSubVoice(
        MidoraId eventInstrumentId,
        string? name = null,
        int? rootNoteOverride = null,
        int? insertionIndex = null) =>
        Command("Create SubVoice", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            EnsureSubVoiceCapacity(instrument);
            string? normalizedName = NormalizeOptionalShortText(name, nameof(name));
            ValidateRootNoteOverride(rootNoteOverride);
            int index = insertionIndex ?? instrument.SubVoices.Count;
            ValidateInsertionIndex(index, instrument.SubVoices.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                value =>
                {
                    SubVoice voice = new(value)
                    {
                        Name = normalizedName,
                        RootNoteOverride = rootNoteOverride
                    };
                    _ = SubVoiceMappingConventions.AddDefaultInstanceVelocityMapping(value, voice);
                    instrument.SubVoices.Insert(index, voice);
                    return voice;
                },
                (_, voice) => InsertAt(instrument.SubVoices, index, voice, "SubVoice"),
                (_, voice) => RemoveRequired(instrument.SubVoices, voice, "SubVoice"));
        });

    public static IProjectEditCommand DuplicateSubVoice(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        string? name = null) => new ProjectPresentationCloneCommand(
            DuplicateSubVoiceCore(eventInstrumentId, subVoiceId, name), PresentationCloneKind.SubVoice, subVoiceId, eventInstrumentId);

    private static IProjectEditCommand DuplicateSubVoiceCore(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        string? name) =>
        Command("Duplicate SubVoice", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice source = FindSubVoice(instrument, subVoiceId);
            EnsureSubVoiceCapacity(instrument);
            string? normalizedName = name is null
                ? source.Name
                : NormalizeOptionalShortText(name, nameof(name));
            int index = instrument.SubVoices.IndexOf(source) + 1;
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                value =>
                {
                    SubVoice copy = CloneSubVoice(value, source, normalizedName);
                    instrument.SubVoices.Insert(index, copy);
                    return copy;
                },
                (_, copy) => InsertAt(instrument.SubVoices, index, copy, "SubVoice"),
                (_, copy) => RemoveRequired(instrument.SubVoices, copy, "SubVoice"));
        });

    public static IProjectEditCommand CreateTemplateNote(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        long lengthTicks,
        int note,
        int velocity,
        bool followPitchDelta = true) =>
        CreateTemplateEvent(
            "Create template note",
            eventInstrumentId,
            subVoiceId,
            new(
                TemplateEventKind.Note,
                tick,
                lengthTicks,
                note,
                velocity,
                0,
                true,
                true,
                followPitchDelta));

    public static IProjectEditCommand CreateTemplateControlChange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int controller,
        int value) =>
        CreateTemplateEvent(
            "Create template control change",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.ControlChange, tick, 0, controller, value, 0, true, true, true));

    public static IProjectEditCommand CreateTemplateBank(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int? bankMsb,
        int? bankLsb) =>
        CreateTemplateEvent(
            "Create template bank",
            eventInstrumentId,
            subVoiceId,
            new(
                TemplateEventKind.Bank,
                tick,
                0,
                0,
                bankMsb ?? 0,
                bankLsb ?? 0,
                bankMsb.HasValue,
                bankLsb.HasValue,
                true));

    public static IProjectEditCommand CreateTemplateProgram(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int program) =>
        CreateTemplateEvent(
            "Create template program",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.Program, tick, 0, 0, program, 0, true, true, true));

    public static IProjectEditCommand CreateTemplatePitchBend(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int value) =>
        CreateTemplateEvent(
            "Create template pitch bend",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.PitchBend, tick, 0, 0, value, 0, true, true, true));

    public static IProjectEditCommand CreateTemplateRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int parameter,
        int value) =>
        CreateTemplateEvent(
            "Create template RPN",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.RegisteredParameter, tick, 0, parameter, value, 0, true, true, true));

    public static IProjectEditCommand CreateTemplateNonRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int parameter,
        int value) =>
        CreateTemplateEvent(
            "Create template NRPN",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.NonRegisteredParameter, tick, 0, parameter, value, 0, true, true, true));

    public static IProjectEditCommand CreateTemplatePitchBendRange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        long tick,
        int semitones,
        int cents) =>
        CreateTemplateEvent(
            "Create template pitch bend range",
            eventInstrumentId,
            subVoiceId,
            new(TemplateEventKind.PitchBendRange, tick, 0, 0, semitones, cents, true, true, true));

    public static IProjectEditCommand CreateValueCurve(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        MappingRounding rounding = MappingRounding.Round,
        MappingOverflow overflow = MappingOverflow.Fail,
        int? insertionIndex = null) =>
        Command("Create value curve", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            _ = ValueCurveTargetRange(target);
            ValidateIntegerTargetSettings(rounding, overflow);
            if (voice.Curves.Any(value => value.Target == target))
            {
                throw new InvalidOperationException(
                    "Only one Value Curve is allowed for a MIDI target in a SubVoice.");
            }
            int index = insertionIndex ?? voice.Curves.Count;
            ValidateInsertionIndex(index, voice.Curves.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                value =>
                {
                    ValueCurve curve = new(value) { Target = target };
                    curve.TargetSettings.Rounding = rounding;
                    curve.TargetSettings.Overflow = overflow;
                    voice.Curves.Insert(index, curve);
                    return curve;
                },
                (_, curve) => InsertAt(voice.Curves, index, curve, "Value Curve"),
                (_, curve) => RemoveRequired(voice.Curves, curve, "Value Curve"));
        });

    public static IProjectEditCommand CreateValueCurvePoint(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        long tick,
        double value,
        CurveInterpolation interpolation = CurveInterpolation.Linear) =>
        Command("Create value curve point", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            ValidateValueCurvePoint(curve, default, tick, value, interpolation);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(oldTemplateLength, checked(tick + 1));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    CurvePoint point = new(owner, tick, value, interpolation);
                    InsertValueCurvePoint(curve.Points, point);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                    return point;
                },
                (_, point) =>
                {
                    InsertValueCurvePoint(curve.Points, point);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                (_, point) =>
                {
                    RemoveRequired(curve.Points, point, "Value Curve point");
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

    public static IProjectEditCommand CreateLogicalParameter(
        MidoraId eventInstrumentId,
        string name,
        LogicalParameterType type,
        double minimum,
        double maximum,
        double displayMinimum,
        double displayMaximum,
        double defaultValue,
        bool usesExplicitEnumValues = false,
        int? insertionIndex = null,
        IReadOnlyList<LogicalParameterEnumItemDefinitionEdit>? enumItems = null)
    {
        LogicalParameterEnumItemDefinitionEdit[] frozenItems = enumItems?.ToArray() ?? [];
        return Command("Create logical parameter", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            string normalizedName = NormalizeUniqueLogicalParameterName(instrument, default, name);
            ValidateLogicalParameterDefinitionCreation(
                type,
                minimum,
                maximum,
                displayMinimum,
                displayMaximum,
                defaultValue);
            if (type != LogicalParameterType.Enum && frozenItems.Length != 0)
            {
                throw new InvalidOperationException(
                    "Only an Enum Logical Parameter can contain Enum items.");
            }
            if (type == LogicalParameterType.Enum
                && enumItems is not null
                && frozenItems.Length == 0)
            {
                throw new InvalidOperationException(
                    "An Enum Logical Parameter requires at least one Enum item.");
            }
            HashSet<string> enumNames = new(StringComparer.OrdinalIgnoreCase);
            HashSet<int> enumValues = [];
            (string Name, int Value)[] normalizedItems = frozenItems
                .Select((item, itemIndex) =>
                {
                    if (item.ExistingItemId.HasValue)
                    {
                        throw new InvalidOperationException(
                            "A newly created Logical Parameter cannot reuse an existing Enum item identity.");
                    }
                    string itemName = ProjectTextRules.NormalizeShortText(
                        item.Name,
                        allowEmpty: false,
                        nameof(enumItems));
                    if (!enumNames.Add(itemName))
                    {
                        throw new InvalidOperationException(
                            "Enum item names must be non-empty and unique ignoring case.");
                    }
                    int itemValue = usesExplicitEnumValues ? item.Value : itemIndex;
                    if (itemValue < minimum || itemValue > maximum)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(enumItems),
                            "Every Enum item value must be inside the legal range.");
                    }
                    if (!enumValues.Add(itemValue))
                    {
                        throw new InvalidOperationException("Enum item values must be unique.");
                    }
                    return (itemName, itemValue);
                })
                .ToArray();
            if (type == LogicalParameterType.Enum
                && enumItems is not null
                && (defaultValue < int.MinValue
                    || defaultValue > int.MaxValue
                    || !enumValues.Contains((int)defaultValue)))
            {
                throw new ArgumentException(
                    "An Enum default value must identify one of the Enum items.",
                    nameof(defaultValue));
            }
            int index = insertionIndex ?? instrument.LogicalParameters.Count;
            ValidateInsertionIndex(index, instrument.LogicalParameters.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    LogicalParameterDefinition parameter = new(owner)
                    {
                        Name = normalizedName,
                        Type = type,
                        Minimum = minimum,
                        Maximum = maximum,
                        DisplayMinimum = displayMinimum,
                        DisplayMaximum = displayMaximum,
                        DefaultValue = defaultValue,
                        UsesExplicitEnumValues = usesExplicitEnumValues
                    };
                    foreach ((string itemName, int itemValue) in normalizedItems)
                    {
                        parameter.EnumItems.Add(new(owner)
                        {
                            Name = itemName,
                            Value = itemValue
                        });
                    }
                    instrument.LogicalParameters.Insert(index, parameter);
                    return parameter;
                },
                (_, parameter) => InsertAt(
                    instrument.LogicalParameters,
                    index,
                    parameter,
                    "Logical Parameter"),
                (_, parameter) => RemoveRequired(
                    instrument.LogicalParameters,
                    parameter,
                    "Logical Parameter"));
        });
    }

    public static IProjectEditCommand CreateLogicalParameterEnumItem(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        string name,
        int? explicitValue = null,
        int? insertionIndex = null) =>
        Command("Create logical parameter enum item", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            if (parameter.Type != LogicalParameterType.Enum)
            {
                throw new InvalidOperationException(
                    "Enum items can only be created for an Enum Logical Parameter.");
            }
            string normalizedName = NormalizeUniqueEnumItemName(parameter, default, name);
            int index = insertionIndex ?? parameter.EnumItems.Count;
            ValidateInsertionIndex(index, parameter.EnumItems.Count, nameof(insertionIndex));
            if (!parameter.UsesExplicitEnumValues && index != parameter.EnumItems.Count)
            {
                throw new InvalidOperationException(
                    "Inserting an implicit Enum item before existing items changes their numeric identity; use the atomic Logical Parameter definition migration command.");
            }
            int storedValue = parameter.UsesExplicitEnumValues
                ? explicitValue ?? throw new ArgumentNullException(nameof(explicitValue))
                : index;
            if (!parameter.UsesExplicitEnumValues && explicitValue.HasValue)
            {
                throw new ArgumentException(
                    "Implicit Enum items do not accept explicit integer values.",
                    nameof(explicitValue));
            }
            if (parameter.UsesExplicitEnumValues
                && parameter.EnumItems.Any(value => value.Value == storedValue))
            {
                throw new InvalidOperationException(
                    "Explicit Enum item values must be unique in a Logical Parameter.");
            }
            if (storedValue < parameter.Minimum || storedValue > parameter.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(explicitValue));
            }
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    LogicalParameterEnumItem item = new(owner)
                    {
                        Name = normalizedName,
                        Value = storedValue
                    };
                    parameter.EnumItems.Insert(index, item);
                    NormalizeImplicitEnumItemValues(parameter);
                    return item;
                },
                (_, item) =>
                {
                    InsertAt(parameter.EnumItems, index, item, "Logical Parameter Enum item");
                    NormalizeImplicitEnumItemValues(parameter);
                },
                (_, item) =>
                {
                    RemoveRequired(parameter.EnumItems, item, "Logical Parameter Enum item");
                    NormalizeImplicitEnumItemValues(parameter);
                });
        });

    public static IProjectEditCommand CreateInstrumentEnvelope(
        MidoraId eventInstrumentId,
        string? name = null,
        long delayTicks = 0,
        long attackTicks = 0,
        long holdTicks = 0,
        long decayTicks = 0,
        double startValue = 0,
        double peakValue = 1,
        double sustainValue = 1,
        long releaseTicks = 0,
        double endValue = 0,
        int? insertionIndex = null) =>
        Command("Create envelope preset", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (!instrument.RequiresChannelIsolation)
            {
                throw new InvalidOperationException(
                    "Envelope Presets cannot be created while Per-Note Instance Isolation is disabled.");
            }
            string? normalizedName = NormalizeOptionalShortText(name, nameof(name));
            EnvelopeValue value = new(
                normalizedName,
                delayTicks,
                attackTicks,
                holdTicks,
                decayTicks,
                startValue,
                peakValue,
                sustainValue,
                releaseTicks,
                endValue);
            ValidateEnvelopeValue(value);
            int index = insertionIndex ?? instrument.Envelopes.Count;
            ValidateInsertionIndex(index, instrument.Envelopes.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    InstrumentEnvelope envelope = new(owner);
                    SetEnvelope(envelope, value);
                    instrument.Envelopes.Insert(index, envelope);
                    return envelope;
                },
                (_, envelope) => InsertAt(
                    instrument.Envelopes,
                    index,
                    envelope,
                    "Envelope Preset"),
                (_, envelope) => RemoveRequired(
                    instrument.Envelopes,
                    envelope,
                    "Envelope Preset"));
        });

    public static IProjectEditCommand CreateMappingFunction(
        MidoraId eventInstrumentId,
        string name,
        string body,
        IEnumerable<string> declaredContextFields,
        int? insertionIndex = null)
    {
        ArgumentNullException.ThrowIfNull(declaredContextFields);
        string[] frozenFields = declaredContextFields.ToArray();
        return Command("Create mapping function", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            string normalizedName = NormalizeUniqueMappingFunctionName(instrument, default, name);
            string validatedBody = ProjectTextRules.ValidateMappingBody(body, nameof(body));
            string[] normalizedFields = NormalizeContextFields(frozenFields);
            MappingFunctionValue value = new(
                normalizedName,
                validatedBody,
                MappingExpressionAbiV3.Version,
                normalizedFields);
            int index = insertionIndex ?? instrument.MappingFunctions.Count;
            ValidateInsertionIndex(index, instrument.MappingFunctions.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    CSharpMappingFunction function = new(owner)
                    {
                        Name = normalizedName,
                        Body = validatedBody
                    };
                    SetMappingFunction(function, value);
                    instrument.MappingFunctions.Insert(index, function);
                    return function;
                },
                (_, function) => InsertAt(
                    instrument.MappingFunctions,
                    index,
                    function,
                    "Mapping Function"),
                (_, function) => RemoveRequired(
                    instrument.MappingFunctions,
                    function,
                    "Mapping Function"));
        });
    }

    public static IProjectEditCommand DuplicateMappingFunction(
        MidoraId eventInstrumentId,
        MidoraId mappingFunctionId,
        string? name = null) =>
        Command("Duplicate mapping function", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            CSharpMappingFunction source = FindMappingFunction(instrument, mappingFunctionId);
            string normalizedName = name is null
                ? CreateUniqueMappingFunctionCopyName(instrument, source.Name)
                : NormalizeUniqueMappingFunctionName(instrument, default, name);
            MappingFunctionValue value = new(
                normalizedName,
                source.Body,
                source.AbiVersion,
                source.DeclaredContextFields.Order(StringComparer.Ordinal).ToArray());
            int index = instrument.MappingFunctions.IndexOf(source) + 1;
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    CSharpMappingFunction copy = new(owner)
                    {
                        Name = normalizedName,
                        Body = source.Body
                    };
                    SetMappingFunction(copy, value);
                    instrument.MappingFunctions.Insert(index, copy);
                    return copy;
                },
                (_, copy) => InsertAt(
                    instrument.MappingFunctions,
                    index,
                    copy,
                    "Mapping Function"),
                (_, copy) => RemoveRequired(
                    instrument.MappingFunctions,
                    copy,
                    "Mapping Function"));
        });

    public static IProjectEditCommand CreateLogicalParameterMapping(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        MappingRounding rounding = MappingRounding.Round,
        MappingOverflow overflow = MappingOverflow.Fail,
        int? insertionIndex = null) =>
        Command("Create logical parameter mapping", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            _ = FindLogicalParameter(instrument, parameterId);
            _ = FindSubVoice(instrument, subVoiceId);
            MidiStateValueRules.Validate(target, value: null);
            ValidateIntegerTargetSettings(rounding, overflow);
            LogicalParameterMapping[] peers = instrument.ParameterMappings
                .Where(value => value.SubVoiceId == subVoiceId && value.Target == target)
                .ToArray();
            if (peers.Length != 0)
            {
                IntegerTargetSettingsValue shared = GetSharedTargetSettings(peers);
                if (shared != new IntegerTargetSettingsValue(rounding, overflow))
                {
                    throw new InvalidOperationException(
                        "Mappings for the same SubVoice MIDI target must share target settings.");
                }
            }
            int index = insertionIndex ?? instrument.ParameterMappings.Count;
            ValidateInsertionIndex(index, instrument.ParameterMappings.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    LogicalParameterMapping mapping = new(owner)
                    {
                        ParameterId = parameterId,
                        SubVoiceId = subVoiceId,
                        Target = target
                    };
                    mapping.TargetSettings.Rounding = rounding;
                    mapping.TargetSettings.Overflow = overflow;
                    instrument.ParameterMappings.Insert(index, mapping);
                    return mapping;
                },
                (_, mapping) => InsertAt(
                    instrument.ParameterMappings,
                    index,
                    mapping,
                    "Logical Parameter Mapping"),
                (_, mapping) => RemoveRequired(
                    instrument.ParameterMappings,
                    mapping,
                    "Logical Parameter Mapping"));
        });

    public static IProjectEditCommand CreateMappingStep(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MappingSource source = MappingSource.CurrentValue,
        MappingOperation operation = MappingOperation.Add,
        MidoraId? logicalParameterId = null,
        MidoraId? envelopeId = null,
        MidoraId? mappingFunctionId = null,
        double constant = 0,
        double sourceMinimum = 0,
        double sourceMaximum = 1,
        double targetMinimum = 0,
        double targetMaximum = 127,
        MappingInputOverflow inputOverflow = MappingInputOverflow.Clamp,
        DivideByZeroPolicy divideByZero = DivideByZeroPolicy.TargetMaximum,
        bool isEnabled = true,
        int? insertionIndex = null) =>
        Command("Create mapping step", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            MappingStepValue value = NormalizeMappingStepValue(new(
                source,
                operation,
                logicalParameterId,
                envelopeId,
                mappingFunctionId,
                constant,
                sourceMinimum,
                sourceMaximum,
                targetMinimum,
                targetMaximum,
                inputOverflow,
                divideByZero));
            ValidateMappingStepValue(value);
            int index = insertionIndex ?? chain.Count;
            ValidateInsertionIndex(index, chain.Count, nameof(insertionIndex));
            return DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    ValueMappingStep step = new(owner);
                    SetMappingStep(step, value);
                    step.IsEnabled = isEnabled;
                    chain.Insert(index, step);
                    return step;
                },
                (_, step) => InsertMappingStepAt(chain, index, step),
                (_, step) => RemoveMappingStepRequired(chain, step));
        });

    public static IProjectEditCommand PasteMappingChain(
        MidoraId eventInstrumentId,
        MidoraId sourceMappingChainId,
        MidoraId targetMappingChainId,
        bool nonEmptyReplacementConfirmed) =>
        Command("Paste mapping chain", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain source = FindMappingChain(instrument, sourceMappingChainId);
            MappingChain target = FindMappingChain(instrument, targetMappingChainId);
            if (ReferenceEquals(source, target))
            {
                return Prepared(
                    hasChanges: false,
                    EventInstrumentChange(eventInstrumentId),
                    _ => { },
                    _ => { });
            }
            if (target.Count != 0 && !nonEmptyReplacementConfirmed)
            {
                throw new InvalidOperationException(
                    "Replacing a non-empty Mapping Chain requires explicit confirmation.");
            }
            MappingChain? replacement = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    if (replacement is null)
                    {
                        replacement = new MappingChain(owner);
                        CopyMappingChain(owner, source, replacement);
                    }
                    ReplaceMappingChain(instrument, target, replacement);
                },
                _ => ReplaceMappingChain(
                    instrument,
                    replacement ?? throw new InvalidOperationException(
                        "A pasted Mapping Chain cannot be undone before its first Apply."),
                    target));
        });

    private static IProjectEditCommand CreateTemplateEvent(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        TemplateEventValue replacement) =>
        Command(commandName, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValidateTemplateEventCreation(replacement);
            long requiredBoundary = replacement.Kind == TemplateEventKind.Note
                ? checked(replacement.Tick + replacement.LengthTicks)
                : checked(replacement.Tick + 1);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            // Use the paged target directory, not a full event enumeration when
            // creating one point in a large SubVoice. Existing targets without a
            // Mapping must stay unmapped; creation must not revive deleted owners.
            HashSet<TemplateEventMappingTarget> existingTargets = [];
            foreach (long key in voice.Events.CreateQuerySnapshot().DiscoveryKeys)
                if (TemplateEventMidiTargets.TryDecodeDiscoveryKey(key, out MidiValueTarget target))
                    existingTargets.Add(TemplateEventMidiTargets.ToMappingTarget(target));
            TemplateEventMappingTarget[] optionalMappingTargetsToCreate =
                EnumerateTemplateEventMappingTargets(replacement)
                    .Where(target => target.EventKind != TemplateEventKind.Note
                        && !existingTargets.Contains(target)
                        && voice.EventMappings.All(mapping => mapping.Target != target))
                    .ToArray();
            SubVoiceEventMapping[]? createdMappings = null;
            IPreparedProjectEdit prepared = DeferredCreate(
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    TemplateEvent created = new(owner);
                    SetTemplateEvent(created, replacement);
                    voice.Events.AddWithoutOptionalMappingCreation(created);
                    createdMappings ??= optionalMappingTargetsToCreate
                        .Select(target => new SubVoiceEventMapping(owner, target))
                        .ToArray();
                    RestoreNewTemplateEventMappings(voice, createdMappings);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                    return created;
                },
                (_, created) =>
                {
                    voice.Events.AddWithoutOptionalMappingCreation(created);
                    RestoreNewTemplateEventMappings(
                        voice,
                        createdMappings ?? throw new InvalidOperationException(
                            "Template Event mappings were not prepared before Redo."));
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                (_, created) =>
                {
                    RemoveRequired(voice.Events, created, "Template Event");
                    foreach (SubVoiceEventMapping mapping in createdMappings ?? [])
                    {
                        RemoveRequired(
                            voice.EventMappings,
                            mapping,
                            "SubVoice event Mapping");
                    }
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

    private static void RestoreNewTemplateEventMappings(
        SubVoice voice,
        IEnumerable<SubVoiceEventMapping> mappings)
    {
        foreach (SubVoiceEventMapping mapping in mappings)
        {
            if (voice.EventMappings.Any(value => value.Target == mapping.Target))
            {
                throw new InvalidOperationException(
                    "The SubVoice event Mapping target already exists.");
            }
            voice.EventMappings.Add(mapping);
        }
    }

    private static IEnumerable<TemplateEventMappingTarget> EnumerateTemplateEventMappingTargets(
        TemplateEventValue value)
    {
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                yield return TemplateEventMappingTarget.Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.Number);
                yield return TemplateEventMappingTarget.Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.ControlChange:
            case TemplateEventKind.Program:
            case TemplateEventKind.PitchBend:
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                yield return TemplateEventMappingTarget.Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb)
                {
                    yield return TemplateEventMappingTarget.Create(
                        value.Kind,
                        value.Number,
                        TemplateEventMappingParameter.Value);
                }
                if (value.HasBankLsb)
                {
                    yield return TemplateEventMappingTarget.Create(
                        value.Kind,
                        value.Number,
                        TemplateEventMappingParameter.SecondaryValue);
                }
                break;
            case TemplateEventKind.PitchBendRange:
                yield return TemplateEventMappingTarget.Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.Value);
                yield return TemplateEventMappingTarget.Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.SecondaryValue);
                break;
        }
    }

    private static void ValidateTemplateEventCreation(TemplateEventValue value)
    {
        if (!Enum.IsDefined(value.Kind) || value.Tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0 || value.Tick > long.MaxValue - value.LengthTicks)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                if (value.Number is < 0 or > 127 || value.Value is < 1 or > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 or 91 or 93)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                ValidateSevenBit(value.Value, nameof(value));
                break;
            case TemplateEventKind.Bank:
                if (!value.HasBankMsb && !value.HasBankLsb)
                {
                    throw new ArgumentException("Bank must contain an MSB, an LSB, or both.", nameof(value));
                }
                if (value.HasBankMsb) ValidateSevenBit(value.Value, nameof(value));
                if (value.HasBankLsb) ValidateSevenBit(value.SecondaryValue, nameof(value));
                break;
            case TemplateEventKind.Program:
                ValidateSevenBit(value.Value, nameof(value));
                break;
            case TemplateEventKind.PitchBend:
                if (value.Value is < -8192 or > 8191)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                break;
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                ValidateFourteenBit(value.Number, nameof(value));
                ValidateFourteenBit(value.Value, nameof(value));
                break;
            case TemplateEventKind.PitchBendRange:
                ValidateSevenBit(value.Value, nameof(value));
                if (value.SecondaryValue is < 0 or > 99)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                break;
        }
        if (value.Kind != TemplateEventKind.Note && value.Tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static SubVoice CloneSubVoice(
        MidoraProject project,
        SubVoice source,
        string? name)
    {
        SubVoice copy = new(project)
        {
            Name = name,
            RootNoteOverride = source.RootNoteOverride
        };
        CopyMidiInitialState(source.InitialState, copy.InitialState);
        foreach (SubVoiceEventMapping sourceMapping in source.EventMappings)
        {
            SubVoiceEventMapping mappingCopy = new(project, sourceMapping.Target);
            CopyMappingChain(project, sourceMapping.Steps, mappingCopy.Steps);
            SetTargetSettings(
                mappingCopy.TargetSettings,
                new(
                    sourceMapping.TargetSettings.Rounding,
                    sourceMapping.TargetSettings.Overflow));
            copy.EventMappings.Add(mappingCopy);
        }
        CopyBoundedSubVoiceTimeline(project, source, copy);
        return copy;
    }

    private static void CopyMidiInitialState(MidiInitialState source, MidiInitialState target)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach ((int key, int value) in source.Controllers) target.Controllers.Add(key, value);
        foreach ((int key, int value) in source.RegisteredParameters) target.RegisteredParameters.Add(key, value);
        foreach ((int key, int value) in source.NonRegisteredParameters) target.NonRegisteredParameters.Add(key, value);
    }

    private static void CopyMappingChain(
        MidoraProject project,
        MappingChain source,
        MappingChain target)
    {
        target.IsEnabled = source.IsEnabled;
        foreach (ValueMappingStep sourceStep in source)
        {
            ValueMappingStep step = new(project) { IsEnabled = sourceStep.IsEnabled };
            SetMappingStep(step, CaptureMappingStep(sourceStep));
            target.Add(step);
        }
    }

    private static void ReplaceMappingChain(
        EventInstrument instrument,
        MappingChain expected,
        MappingChain replacement)
    {
        foreach (SubVoice voice in instrument.SubVoices)
        {
            foreach (SubVoiceEventMapping mapping in voice.EventMappings)
            {
                if (ReferenceEquals(mapping.Steps, expected))
                {
                    mapping.Steps = replacement;
                    return;
                }
            }
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            if (ReferenceEquals(mapping.Steps, expected))
            {
                mapping.Steps = replacement;
                return;
            }
        }
        throw new InvalidOperationException(
            "The Mapping Chain is no longer attached to the Event Instrument.");
    }

    private static void InsertValueCurvePoint(CurvePointCollection points, CurvePoint point)
    {
        if (points.TryGetById(point.Id, out _)
            || points.CreateQuerySnapshot().QueryValues(point.Tick, checked(point.Tick + 1)).Any())
        {
            throw new InvalidOperationException(
                "The Value Curve point ID or tick is already present.");
        }
        points.Insert(FindCurvePointInsertionIndex(points, point.Tick, point.Id), point);
    }

    private static void ValidateLogicalParameterDefinitionCreation(
        LogicalParameterType type,
        double minimum,
        double maximum,
        double displayMinimum,
        double displayMaximum,
        double defaultValue)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
        ValidateFiniteOrderedRange(minimum, maximum, nameof(minimum));
        ValidateFiniteOrderedRange(displayMinimum, displayMaximum, nameof(displayMinimum));
        if (!double.IsFinite(defaultValue) || defaultValue < minimum || defaultValue > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }
        if (type is LogicalParameterType.Integer or LogicalParameterType.Enum
            && (minimum != Math.Truncate(minimum)
                || maximum != Math.Truncate(maximum)
                || defaultValue != Math.Truncate(defaultValue)))
        {
            throw new ArgumentException(
                "Integer and Enum Logical Parameters require integer legal range endpoints and default values.");
        }
    }

    private static string NormalizeUniqueEnumItemName(
        LogicalParameterDefinition parameter,
        MidoraId excludedId,
        string name)
    {
        string normalized = ProjectTextRules.NormalizeShortText(name, allowEmpty: false, nameof(name));
        if (parameter.EnumItems.Any(value =>
            value.Id != excludedId
            && string.Equals(value.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "An Enum item with the same name already exists in this Logical Parameter.");
        }
        return normalized;
    }

    private static void NormalizeImplicitEnumItemValues(LogicalParameterDefinition parameter)
    {
        if (parameter.UsesExplicitEnumValues)
        {
            return;
        }
        for (int index = 0; index < parameter.EnumItems.Count; index++)
        {
            parameter.EnumItems[index].Value = index;
        }
    }

    private static string CreateUniqueMappingFunctionCopyName(
        EventInstrument instrument,
        string sourceName)
    {
        string baseName = ProjectTextRules.NormalizeShortText(
            $"{sourceName} Copy",
            allowEmpty: false,
            nameof(sourceName));
        string candidate = baseName;
        for (int suffix = 2; ; suffix++)
        {
            if (!instrument.MappingFunctions.Any(value =>
                string.Equals(value.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
            candidate = ProjectTextRules.NormalizeShortText(
                $"{baseName} ({suffix})",
                allowEmpty: false,
                nameof(sourceName));
        }
    }

    private static string? NormalizeOptionalShortText(string? value, string parameterName) =>
        value is null
            ? null
            : ProjectTextRules.NormalizeShortText(value, allowEmpty: true, parameterName);

    private static void EnsureSubVoiceCapacity(EventInstrument instrument)
    {
        if (instrument.SubVoices.Count >= 256)
        {
            throw new InvalidOperationException(
                "An Event Instrument cannot contain more than 256 SubVoices.");
        }
    }

    private static void ValidateRootNoteOverride(int? rootNoteOverride)
    {
        if (rootNoteOverride is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(rootNoteOverride));
        }
    }
}
