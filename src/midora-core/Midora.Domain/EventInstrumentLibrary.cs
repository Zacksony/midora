namespace Midora.Domain;

public static class EventInstrumentLibrary
{
    public static EventInstrument Create(MidoraProject project, string? requestedName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        string name = requestedName is null
            ? GenerateUniqueName(project, "Event Instrument")
            : ValidateUniqueName(project, requestedName, default);
        EventInstrument result = new(project)
        {
            Name = name,
            TemplateLengthTicks = project.TicksPerQuarterNote
        };
        result.SubVoices.Add(new SubVoice(project));
        project.EventInstruments.Add(result);
        return result;
    }

    public static void Rename(MidoraProject project, MidoraId instrumentId, string name)
    {
        EventInstrument instrument = Find(project, instrumentId);
        instrument.Name = ValidateUniqueName(project, name, instrumentId);
    }

    public static EventInstrument Duplicate(MidoraProject project, MidoraId instrumentId, string? requestedName = null)
    {
        EventInstrument source = Find(project, instrumentId);
        return CopyInto(project, source, requestedName);
    }

    public static EventInstrument CopyInto(
        MidoraProject project,
        EventInstrument source,
        string? requestedName = null) => CopyInto(project, source, requestedName, null, default);

    internal static EventInstrument CopyInto(MidoraProject project, EventInstrument source,
        string? requestedName, Action<MidoraProject, SubVoice, SubVoice>? copyTimeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        string name = requestedName is null
            ? GenerateUniqueName(project, $"{source.Name} Copy")
            : ValidateUniqueName(project, requestedName, default);
        Dictionary<MidoraId, MidoraId> parameters = [];
        Dictionary<MidoraId, MidoraId> voices = [];
        Dictionary<MidoraId, MidoraId> functions = [];
        Dictionary<MidoraId, MidoraId> envelopes = [];
        EventInstrument result = new(project)
        {
            Name = name,
            Description = source.Description,
            Color = source.Color,
            RootNote = source.RootNote,
            TemplateLengthTicks = source.TemplateLengthTicks,
            PreRollTicks = source.PreRollTicks,
            RequiresChannelIsolation = source.RequiresChannelIsolation,
            OverlapPolicy = source.OverlapPolicy,
            OverlapScope = source.OverlapScope,
            ShortLifecycle = source.ShortLifecycle,
            LongLifecycle = source.LongLifecycle,
            LoopStartTick = source.LoopStartTick,
            LoopEndTick = source.LoopEndTick
        };
        CopyState(source.InitialState, result.InitialState);

        foreach (LogicalParameterDefinition definition in source.LogicalParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterDefinition copy = new(project)
            {
                Name = definition.Name,
                Type = definition.Type,
                Minimum = definition.Minimum,
                Maximum = definition.Maximum,
                DisplayMinimum = definition.DisplayMinimum,
                DisplayMaximum = definition.DisplayMaximum,
                DefaultValue = definition.DefaultValue,
                UsesExplicitEnumValues = definition.UsesExplicitEnumValues
            };
            foreach (LogicalParameterEnumItem item in definition.EnumItems)
            {
                copy.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = item.Name, Value = item.Value });
            }
            parameters.Add(definition.Id, copy.Id);
            result.LogicalParameters.Add(copy);
        }
        foreach (CSharpMappingFunction function in source.MappingFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CSharpMappingFunction copy = new(project)
            {
                Name = function.Name,
                Body = function.Body,
                AbiVersion = function.AbiVersion
            };
            copy.DeclaredContextFields.UnionWith(function.DeclaredContextFields);
            functions.Add(function.Id, copy.Id);
            result.MappingFunctions.Add(copy);
        }
        foreach (InstrumentEnvelope envelope in source.Envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstrumentEnvelope copy = new(project)
            {
                Name = envelope.Name,
                DelayTicks = envelope.DelayTicks,
                AttackTicks = envelope.AttackTicks,
                HoldTicks = envelope.HoldTicks,
                DecayTicks = envelope.DecayTicks,
                StartValue = envelope.StartValue,
                PeakValue = envelope.PeakValue,
                SustainValue = envelope.SustainValue,
                ReleaseTicks = envelope.ReleaseTicks,
                EndValue = envelope.EndValue
            };
            envelopes.Add(envelope.Id, copy.Id);
            result.Envelopes.Add(copy);
        }
        foreach (SubVoice voice in source.SubVoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubVoice copy = new(project) { Name = voice.Name, RootNoteOverride = voice.RootNoteOverride };
            CopyState(voice.InitialState, copy.InitialState);
            voices.Add(voice.Id, copy.Id);
            foreach (SubVoiceEventMapping mapping in voice.EventMappings)
            {
                SubVoiceEventMapping mappingCopy = new(project, mapping.Target);
                CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
                CopyChain(
                    project,
                    mapping.Steps,
                    mappingCopy.Steps,
                    parameters,
                    functions,
                    envelopes);
                copy.EventMappings.Add(mappingCopy);
            }
            if (copyTimeline is not null) copyTimeline(project, voice, copy);
            else
            {
                Dictionary<MidoraId, MidoraId> instrumentMembers = [];
                foreach (TemplateEvent value in voice.Events)
                {
                    TemplateEvent eventCopy = new(project)
                    {
                        Kind = value.Kind,
                        Tick = value.Tick,
                        LengthTicks = value.LengthTicks,
                        Number = value.Number,
                        Value = value.Value,
                        SecondaryValue = value.SecondaryValue,
                        HasBankMsb = value.HasBankMsb,
                        HasBankLsb = value.HasBankLsb,
                        FollowPitchDelta = value.FollowPitchDelta
                    };
                    copy.Events.Add(eventCopy);
                    if (voice.InstrumentChanges.TryGetByMember(value.Id, out _))
                        instrumentMembers.Add(value.Id, eventCopy.Id);
                }
                foreach (var group in voice.InstrumentChanges.Values)
                    copy.InstrumentChanges = copy.InstrumentChanges.Add(new(project.AllocateStableId(),
                        instrumentMembers[group.BankEventId], null, instrumentMembers[group.ProgramEventId]), false);
                foreach (ValueCurve curve in voice.Curves)
                {
                    ValueCurve curveCopy = new(project) { Target = curve.Target };
                    CopyTargetSettings(curve.TargetSettings, curveCopy.TargetSettings);
                    foreach (CurvePoint point in curve.Points)
                    {
                        curveCopy.Points.Add(new(project, point.Tick, point.Value, point.Interpolation));
                    }
                    copy.Curves.Add(curveCopy);
                }
            }
            result.SubVoices.Add(copy);
        }
        foreach (LogicalParameterMapping mapping in source.ParameterMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterMapping copy = new(project)
            {
                ParameterId = Remap(parameters, mapping.ParameterId),
                SubVoiceId = Remap(voices, mapping.SubVoiceId),
                Target = mapping.Target
            };
            CopyTargetSettings(mapping.TargetSettings, copy.TargetSettings);
            CopyChain(project, mapping.Steps, copy.Steps, parameters, functions, envelopes);
            result.ParameterMappings.Add(copy);
        }

        project.EventInstruments.Add(result);
        return result;
    }

    public static IReadOnlyList<LogicalTrack> Delete(
        MidoraProject project,
        MidoraId instrumentId,
        bool referencedDeletionConfirmed)
    {
        EventInstrument instrument = Find(project, instrumentId);
        EventInstrumentUsage[] usages = project.EventInstrumentUsages
            .Where(value => value.EventInstrumentId == instrumentId)
            .ToArray();
        if (usages.Length != 0)
        {
            throw new InvalidOperationException(
                "A referenced Event Instrument Definition cannot be deleted. Remove or rebind all usages first.");
        }
        _ = project.EventInstruments.Remove(instrument);
        return [];
    }

    private static EventInstrument Find(MidoraProject project, MidoraId id) =>
        project.EventInstruments.FirstOrDefault(value => value.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id));

    internal static string ValidateUniqueName(MidoraProject project, string name, MidoraId excludedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        string normalized = ProjectTextRules.NormalizeShortText(
            name,
            allowEmpty: false,
            nameof(name));
        if (project.EventInstruments.Any(value => value.Id != excludedId
                && string.Equals(value.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Event Instrument names must be non-empty and unique ignoring case.", nameof(name));
        }
        return normalized;
    }

    private static string GenerateUniqueName(MidoraProject project, string stem)
    {
        for (int index = 1; ; index++)
        {
            string candidate = $"{stem} {index}";
            if (!project.EventInstruments.Any(value =>
                string.Equals(value.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static void CopyChain(
        MidoraProject project,
        MappingChain source,
        MappingChain target,
        IReadOnlyDictionary<MidoraId, MidoraId> parameters,
        IReadOnlyDictionary<MidoraId, MidoraId> functions,
        IReadOnlyDictionary<MidoraId, MidoraId> envelopes)
    {
        target.IsEnabled = source.IsEnabled;
        foreach (ValueMappingStep step in source)
        {
            target.Add(new ValueMappingStep(project)
            {
                IsEnabled = step.IsEnabled,
                Source = step.Source,
                Operation = step.Operation,
                LogicalParameterId = Remap(parameters, step.LogicalParameterId),
                EnvelopeId = Remap(envelopes, step.EnvelopeId),
                MappingFunctionId = Remap(functions, step.MappingFunctionId),
                Constant = step.Constant,
                SourceMinimum = step.SourceMinimum,
                SourceMaximum = step.SourceMaximum,
                TargetMinimum = step.TargetMinimum,
                TargetMaximum = step.TargetMaximum,
                InputOverflow = step.InputOverflow,
                DivideByZero = step.DivideByZero
            });
        }
    }

    private static void CopyTargetSettings(MidiIntegerTargetSettings source, MidiIntegerTargetSettings target)
    {
        target.Rounding = source.Rounding;
        target.Overflow = source.Overflow;
    }

    private static MidoraId Remap(IReadOnlyDictionary<MidoraId, MidoraId> map, MidoraId id) =>
        map.TryGetValue(id, out MidoraId result) ? result : id;

    private static MidoraId? Remap(IReadOnlyDictionary<MidoraId, MidoraId> map, MidoraId? id) =>
        id.HasValue ? Remap(map, id.Value) : null;

    private static void CopyState(MidiInitialState source, MidiInitialState target)
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
}
