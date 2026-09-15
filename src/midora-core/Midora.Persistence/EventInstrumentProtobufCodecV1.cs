using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;
using WireCurveInterpolation = Midora.Persistence.Wire.Proto.V1.CurveInterpolationV1;
using WireDivideByZeroPolicy = Midora.Persistence.Wire.Proto.V1.DivideByZeroPolicyV1;
using WireLongLifecycle = Midora.Persistence.Wire.Proto.V1.EventInstrumentLongLifecycleV1;
using WireOverlapPolicy = Midora.Persistence.Wire.Proto.V1.EventInstrumentOverlapPolicyV1;
using WireOverlapScope = Midora.Persistence.Wire.Proto.V1.EventInstrumentOverlapScopeV1;
using WireLogicalParameterType = Midora.Persistence.Wire.Proto.V1.LogicalParameterTypeV1;
using WireMappingInputOverflow = Midora.Persistence.Wire.Proto.V1.MappingInputOverflowV1;
using WireMappingOperation = Midora.Persistence.Wire.Proto.V1.MappingOperationV1;
using WireMappingOverflow = Midora.Persistence.Wire.Proto.V1.MappingOverflowV1;
using WireMappingRounding = Midora.Persistence.Wire.Proto.V1.MappingRoundingV1;
using WireMappingSource = Midora.Persistence.Wire.Proto.V1.MappingSourceV1;
using WireMidiValueKind = Midora.Persistence.Wire.Proto.V1.MidiValueKindV1;
using WireShortLifecycle = Midora.Persistence.Wire.Proto.V1.EventInstrumentShortLifecycleV1;
using WireTemplateEventKind = Midora.Persistence.Wire.Proto.V1.TemplateEventKindV1;
using WireTemplateEventMappingParameter = Midora.Persistence.Wire.Proto.V1.TemplateEventMappingParameterV1;

namespace Midora.Persistence;

internal static partial class EventInstrumentProtobufCodecV1
{
    public const string ObjectType = "event-instrument";

    public static byte[] Serialize(EventInstrument value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using MemoryStream output = new();
        Serialize(value, output);
        return output.ToArray();
    }

    public static EventInstrument Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        using MemoryStream input = new(bytes.ToArray(), writable: false);
        return Restore(project, input);
    }

    public static EventInstrument Restore(MidoraProject project, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using MemoryStream input = new(bytes, writable: false);
        return Restore(project, input);
    }

    private static EventInstrumentV1 ToWire(EventInstrument value, bool includeChildren = true)
    {
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Event Instrument name");
        EventInstrumentV1 result = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ObjectType = ObjectType,
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            Color = ProtobufValueCodecV1.ToWire(value.Color),
            RootNote = value.RootNote,
            TemplateLengthTicks = value.TemplateLengthTicks,
            RequiresChannelIsolation = value.RequiresChannelIsolation,
            OverlapPolicy = (WireOverlapPolicy)(int)value.OverlapPolicy,
            OverlapScope = (WireOverlapScope)(int)value.OverlapScope,
            ShortLifecycle = (WireShortLifecycle)(int)value.ShortLifecycle,
            LongLifecycle = (WireLongLifecycle)(int)value.LongLifecycle,
            InitialState = includeChildren ? ToWire(value.InitialState) : new MidiInitialStateV1()
        };
        if (value.Description is not null)
        {
            PersistenceValueValidationV1.ValidateDescription(value.Description, "Event Instrument description");
            result.Description = value.Description;
        }
        if (value.LoopStartTick.HasValue) result.LoopStartTick = value.LoopStartTick.Value;
        if (value.LoopEndTick.HasValue) result.LoopEndTick = value.LoopEndTick.Value;
        if (includeChildren)
        {
            result.LogicalParameters.Add(value.LogicalParameters.Select(item => ToWire(item)));
            result.SubVoices.Add(value.SubVoices.Select(item => ToWire(item)));
            result.Envelopes.Add(value.Envelopes.Select(ToWire));
            result.MappingFunctions.Add(value.MappingFunctions.Select(ToWire));
            result.ParameterMappings.Add(value.ParameterMappings.Select(ToWire));
        }
        return result;
    }

    private static EventInstrument FromWire(MidoraProject project, EventInstrumentV1 value)
    {
        EventInstrument result = new(project, ProtobufValueCodecV1.FromWire(value.Id, "Event Instrument ID"))
        {
            Name = value.Name,
            Description = value.HasDescription ? value.Description : null,
            Color = ProtobufValueCodecV1.FromWire(value.Color, "Event Instrument color"),
            RootNote = value.RootNote,
            TemplateLengthTicks = value.TemplateLengthTicks,
            RequiresChannelIsolation = value.RequiresChannelIsolation,
            OverlapPolicy = (OverlapPolicy)(int)value.OverlapPolicy,
            OverlapScope = (OverlapScope)(int)value.OverlapScope,
            ShortLifecycle = (ShortNoteLifecycle)(int)value.ShortLifecycle,
            LongLifecycle = (LongNoteLifecycle)(int)value.LongLifecycle,
            LoopStartTick = value.HasLoopStartTick ? value.LoopStartTick : null,
            LoopEndTick = value.HasLoopEndTick ? value.LoopEndTick : null
        };
        Restore(result.InitialState, value.InitialState!);
        result.LogicalParameters.AddRange(value.LogicalParameters.Select(item => FromWire(project, item)));
        result.SubVoices.AddRange(value.SubVoices.Select(item => FromWire(project, item)));
        result.Envelopes.AddRange(value.Envelopes.Select(item => FromWire(project, item)));
        result.MappingFunctions.AddRange(value.MappingFunctions.Select(item => FromWire(project, item)));
        result.ParameterMappings.AddRange(value.ParameterMappings.Select(item => FromWire(project, item)));
        return result;
    }

    private static MidiInitialStateV1 ToWire(MidiInitialState value, bool includeEntries = true)
    {
        MidiInitialStateV1 result = new();
        if (value.BankMsb.HasValue) result.BankMsb = value.BankMsb.Value;
        if (value.BankLsb.HasValue) result.BankLsb = value.BankLsb.Value;
        if (value.Program.HasValue) result.Program = value.Program.Value;
        if (value.PitchBend.HasValue) result.PitchBend = value.PitchBend.Value;
        if (value.PitchBendRangeSemitones.HasValue)
        {
            result.PitchBendRangeSemitones = value.PitchBendRangeSemitones.Value;
        }
        if (value.PitchBendRangeCents.HasValue) result.PitchBendRangeCents = value.PitchBendRangeCents.Value;
        if (includeEntries)
        {
            result.Controllers.Add(ToEntries(value.Controllers));
            result.RegisteredParameters.Add(ToEntries(value.RegisteredParameters));
            result.NonRegisteredParameters.Add(ToEntries(value.NonRegisteredParameters));
        }
        return result;
    }

    private static IEnumerable<MidiStateEntryV1> ToEntries(IReadOnlyDictionary<int, int> values) => values
        .OrderBy(item => item.Key)
        .Select(item => new MidiStateEntryV1 { Number = item.Key, Value = item.Value });

    private static void Restore(MidiInitialState target, MidiInitialStateV1 value)
    {
        target.BankMsb = value.HasBankMsb ? value.BankMsb : null;
        target.BankLsb = value.HasBankLsb ? value.BankLsb : null;
        target.Program = value.HasProgram ? value.Program : null;
        target.PitchBend = value.HasPitchBend ? value.PitchBend : null;
        target.PitchBendRangeSemitones = value.HasPitchBendRangeSemitones
            ? value.PitchBendRangeSemitones
            : null;
        target.PitchBendRangeCents = value.HasPitchBendRangeCents ? value.PitchBendRangeCents : null;
        AddEntries(target.Controllers, value.Controllers, "controllers");
        AddEntries(target.RegisteredParameters, value.RegisteredParameters, "registered parameters");
        AddEntries(target.NonRegisteredParameters, value.NonRegisteredParameters, "non-registered parameters");
    }

    private static void AddEntries(
        IDictionary<int, int> target,
        IEnumerable<MidiStateEntryV1> values,
        string fieldName)
    {
        foreach (MidiStateEntryV1 item in values)
        {
            if (!target.TryAdd(item.Number, item.Value))
            {
                throw new InvalidDataException($"Event Instrument {fieldName} contain a duplicate number.");
            }
        }
    }

    private static LogicalParameterDefinitionV1 ToWire(LogicalParameterDefinition value, bool includeChildren = true)
    {
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Logical Parameter name");
        ProtobufValueCodecV1.RequireFinite(value.Minimum, "Logical Parameter minimum");
        ProtobufValueCodecV1.RequireFinite(value.Maximum, "Logical Parameter maximum");
        ProtobufValueCodecV1.RequireFinite(value.DisplayMinimum, "Logical Parameter display minimum");
        ProtobufValueCodecV1.RequireFinite(value.DisplayMaximum, "Logical Parameter display maximum");
        ProtobufValueCodecV1.RequireFinite(value.DefaultValue, "Logical Parameter default value");
        LogicalParameterDefinitionV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            Type = (WireLogicalParameterType)(int)value.Type,
            Minimum = value.Minimum,
            Maximum = value.Maximum,
            DisplayMinimum = value.DisplayMinimum,
            DisplayMaximum = value.DisplayMaximum,
            DefaultValue = value.DefaultValue,
            UsesExplicitEnumValues = value.UsesExplicitEnumValues
        };
        if (includeChildren) result.EnumItems.Add(value.EnumItems.Select(ToWire));
        return result;
    }

    private static LogicalParameterDefinition FromWire(MidoraProject project, LogicalParameterDefinitionV1 value)
    {
        LogicalParameterDefinition result = new(
            project,
            ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter ID"))
        {
            Name = value.Name,
            Type = (LogicalParameterType)(int)value.Type,
            Minimum = value.Minimum,
            Maximum = value.Maximum,
            DisplayMinimum = value.DisplayMinimum,
            DisplayMaximum = value.DisplayMaximum,
            DefaultValue = value.DefaultValue,
            UsesExplicitEnumValues = value.UsesExplicitEnumValues
        };
        result.EnumItems.AddRange(value.EnumItems.Select(item => FromWire(project, item)));
        return result;
    }

    private static LogicalParameterEnumItemV1 ToWire(LogicalParameterEnumItem value)
    {
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Logical Parameter enum item name");
        return new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            Value = value.Value
        };
    }

    private static LogicalParameterEnumItem FromWire(MidoraProject project, LogicalParameterEnumItemV1 value) =>
        new(project, ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter enum item ID"))
        {
            Name = value.Name,
            Value = value.Value
        };

    private static SubVoiceV1 ToWire(SubVoice value, bool includeChildren = true)
    {
        SubVoiceV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            InitialState = includeChildren ? ToWire(value.InitialState) : new MidiInitialStateV1()
        };
        if (value.Name is not null)
        {
            PersistenceValueValidationV1.ValidateShortText(value.Name, "SubVoice name");
            result.Name = value.Name;
        }
        if (value.RootNoteOverride.HasValue) result.RootNoteOverride = value.RootNoteOverride.Value;
        if (includeChildren)
        {
        result.EventMappings.Add(value.EventMappings
            .OrderBy(item => item.Target.EventKind)
            .ThenBy(item => item.Target.EventNumber)
            .ThenBy(item => item.Target.Parameter)
            .Select(ToWire));
        result.Events.Add(value.Events.Select(ToWire));
        result.Curves.Add(value.Curves.Select(item => ToWire(item)));
        }
        return result;
    }

    private static SubVoice FromWire(MidoraProject project, SubVoiceV1 value)
    {
        SubVoice result = new(project, ProtobufValueCodecV1.FromWire(value.Id, "SubVoice ID"))
        {
            Name = value.HasName ? value.Name : null,
            RootNoteOverride = value.HasRootNoteOverride ? value.RootNoteOverride : null
        };
        Restore(result.InitialState, value.InitialState!);
        result.EventMappings.AddRange(value.EventMappings.Select(item => FromWire(project, item)));
        result.Events.AddRangeWithoutOptionalMappingCreation(
            value.Events.Select(item => FromWire(project, item)));
        result.Curves.AddRange(value.Curves.Select(item => FromWire(project, item)));
        return result;
    }

    private static TemplateEventV1 ToWire(TemplateEvent value) => new()
    {
        Id = ProtobufValueCodecV1.ToWire(value.Id),
        Kind = (WireTemplateEventKind)(int)value.Kind,
        Tick = value.Tick,
        LengthTicks = value.LengthTicks,
        Number = value.Number,
        Value = value.Value,
        SecondaryValue = value.SecondaryValue,
        HasBankMsb = value.HasBankMsb,
        HasBankLsb = value.HasBankLsb,
        FollowPitchDelta = value.FollowPitchDelta
    };

    private static TemplateEvent FromWire(MidoraProject project, TemplateEventV1 value) =>
        new(project, ProtobufValueCodecV1.FromWire(value.Id, "Template Event ID"))
        {
            Kind = (TemplateEventKind)(int)value.Kind,
            Tick = value.Tick,
            LengthTicks = value.LengthTicks,
            Number = value.Number,
            Value = value.Value,
            SecondaryValue = value.SecondaryValue,
            HasBankMsb = value.HasBankMsb,
            HasBankLsb = value.HasBankLsb,
            FollowPitchDelta = value.FollowPitchDelta
        };

    private static SubVoiceEventMappingV1 ToWire(SubVoiceEventMapping value) => new()
    {
        EventKind = (WireTemplateEventKind)(int)value.Target.EventKind,
        EventNumber = value.Target.EventNumber,
        Parameter = (WireTemplateEventMappingParameter)(int)value.Target.Parameter,
        Steps = ToWire(value.Steps),
        TargetSettings = ToWire(value.TargetSettings)
    };

    private static SubVoiceEventMapping FromWire(
        MidoraProject project,
        SubVoiceEventMappingV1 value)
    {
        TemplateEventMappingTarget target = new(
            (TemplateEventKind)(int)value.EventKind,
            value.EventNumber,
            (TemplateEventMappingParameter)(int)value.Parameter);
        SubVoiceEventMapping result = new(
            project,
            target,
            ProtobufValueCodecV1.FromWire(
                value.Steps!.Id,
                "SubVoice Event Mapping Chain ID"));
        Restore(project, result.Steps, value.Steps);
        Restore(result.TargetSettings, value.TargetSettings!);
        return result;
    }

    private static ValueCurveV1 ToWire(ValueCurve value, bool includeChildren = true)
    {
        ValueCurveV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Target = ToWire(value.Target),
            TargetSettings = ToWire(value.TargetSettings)
        };
        if (includeChildren) result.Points.Add(value.Points.Select(ToWire));
        return result;
    }

    private static ValueCurve FromWire(MidoraProject project, ValueCurveV1 value)
    {
        ValueCurve result = new(project, ProtobufValueCodecV1.FromWire(value.Id, "Value Curve ID"))
        {
            Target = FromWire(value.Target!)
        };
        Restore(result.TargetSettings, value.TargetSettings!);
        result.Points.AddRange(value.Points.Select(item => FromWire(project, item)));
        return result;
    }

    internal static CurvePointV1 ToWire(CurvePoint value)
    {
        ProtobufValueCodecV1.RequireFinite(value.Value, "Curve Point value");
        return new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Tick = value.Tick,
            Value = value.Value,
            Interpolation = (WireCurveInterpolation)(int)value.Interpolation
        };
    }

    internal static CurvePoint FromWire(MidoraProject project, CurvePointV1 value) => new(
        project,
        ProtobufValueCodecV1.FromWire(value.Id, "Curve Point ID"),
        value.Tick,
        value.Value,
        (CurveInterpolation)(int)value.Interpolation);

    private static InstrumentEnvelopeV1 ToWire(InstrumentEnvelope value)
    {
        ProtobufValueCodecV1.RequireFinite(value.StartValue, "Envelope start value");
        ProtobufValueCodecV1.RequireFinite(value.PeakValue, "Envelope peak value");
        ProtobufValueCodecV1.RequireFinite(value.SustainValue, "Envelope sustain value");
        ProtobufValueCodecV1.RequireFinite(value.EndValue, "Envelope end value");
        InstrumentEnvelopeV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            DelayTicks = value.DelayTicks,
            AttackTicks = value.AttackTicks,
            HoldTicks = value.HoldTicks,
            DecayTicks = value.DecayTicks,
            StartValue = value.StartValue,
            PeakValue = value.PeakValue,
            SustainValue = value.SustainValue,
            ReleaseTicks = value.ReleaseTicks,
            EndValue = value.EndValue
        };
        if (value.Name is not null)
        {
            PersistenceValueValidationV1.ValidateShortText(value.Name, "Envelope name");
            result.Name = value.Name;
        }
        return result;
    }

    private static InstrumentEnvelope FromWire(MidoraProject project, InstrumentEnvelopeV1 value) => new(
        project,
        ProtobufValueCodecV1.FromWire(value.Id, "Envelope ID"))
    {
        Name = value.HasName ? value.Name : null,
        DelayTicks = value.DelayTicks,
        AttackTicks = value.AttackTicks,
        HoldTicks = value.HoldTicks,
        DecayTicks = value.DecayTicks,
        StartValue = value.StartValue,
        PeakValue = value.PeakValue,
        SustainValue = value.SustainValue,
        ReleaseTicks = value.ReleaseTicks,
        EndValue = value.EndValue
    };

    private static CSharpMappingFunctionV1 ToWire(CSharpMappingFunction value)
    {
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Mapping Function name");
        PersistenceValueValidationV1.ValidateMappingBody(value.Body, "Mapping Function body");
        CSharpMappingFunctionV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            Body = value.Body,
            AbiVersion = value.AbiVersion
        };
        result.DeclaredContextFields.Add(value.DeclaredContextFields.Order(StringComparer.Ordinal));
        return result;
    }

    private static CSharpMappingFunction FromWire(MidoraProject project, CSharpMappingFunctionV1 value)
    {
        CSharpMappingFunction result = new(
            project,
            ProtobufValueCodecV1.FromWire(value.Id, "Mapping Function ID"))
        {
            Name = value.Name,
            Body = value.Body,
            AbiVersion = value.AbiVersion
        };
        foreach (string field in value.DeclaredContextFields)
        {
            if (!result.DeclaredContextFields.Add(field))
            {
                throw new InvalidDataException("Mapping Function declared Context fields contain a duplicate.");
            }
        }
        return result;
    }

    private static LogicalParameterMappingV1 ToWire(LogicalParameterMapping value) => new()
    {
        Id = ProtobufValueCodecV1.ToWire(value.Id),
        ParameterId = ProtobufValueCodecV1.ToWire(value.ParameterId),
        SubVoiceId = ProtobufValueCodecV1.ToWire(value.SubVoiceId),
        Target = ToWire(value.Target),
        Steps = ToWire(value.Steps),
        TargetSettings = ToWire(value.TargetSettings)
    };

    private static LogicalParameterMapping FromWire(MidoraProject project, LogicalParameterMappingV1 value)
    {
        LogicalParameterMapping result = new(
            project,
            ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter Mapping ID"),
            ProtobufValueCodecV1.FromWire(value.Steps!.Id, "Logical Parameter Mapping Chain ID"))
        {
            ParameterId = ProtobufValueCodecV1.FromWire(value.ParameterId, "Logical Parameter Mapping parameter ID"),
            SubVoiceId = ProtobufValueCodecV1.FromWire(value.SubVoiceId, "Logical Parameter Mapping SubVoice ID"),
            Target = FromWire(value.Target!)
        };
        Restore(project, result.Steps, value.Steps);
        Restore(result.TargetSettings, value.TargetSettings!);
        return result;
    }

    private static MappingChainV1 ToWire(MappingChain value)
    {
        MappingChainV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            IsEnabled = value.IsEnabled
        };
        result.Steps.Add(value.Select(ToWire));
        return result;
    }

    private static void Restore(MidoraProject project, MappingChain target, MappingChainV1 value)
    {
        target.IsEnabled = value.IsEnabled;
        foreach (ValueMappingStepV1 step in value.Steps) target.Add(FromWire(project, step));
    }

    private static ValueMappingStep FromWire(MidoraProject project, ValueMappingStepV1 value) => new(
        project,
        ProtobufValueCodecV1.FromWire(value.Id, "Mapping Step ID"))
    {
        IsEnabled = value.IsEnabled,
        Source = (MappingSource)(int)value.Source,
        Operation = (MappingOperation)(int)value.Operation,
        LogicalParameterId = !value.HasLogicalParameterId
            ? null
            : ProtobufValueCodecV1.FromWire(value.LogicalParameterId, "Mapping Step Logical Parameter ID"),
        EnvelopeId = !value.HasEnvelopeId
            ? null
            : ProtobufValueCodecV1.FromWire(value.EnvelopeId, "Mapping Step Envelope ID"),
        MappingFunctionId = !value.HasMappingFunctionId
            ? null
            : ProtobufValueCodecV1.FromWire(value.MappingFunctionId, "Mapping Step Function ID"),
        Constant = value.Constant,
        SourceMinimum = value.SourceMinimum,
        SourceMaximum = value.SourceMaximum,
        TargetMinimum = value.TargetMinimum,
        TargetMaximum = value.TargetMaximum,
        InputOverflow = (MappingInputOverflow)(int)value.InputOverflow,
        DivideByZero = (DivideByZeroPolicy)(int)value.DivideByZero
    };

    private static ValueMappingStepV1 ToWire(ValueMappingStep value)
    {
        ProtobufValueCodecV1.RequireFinite(value.Constant, "Mapping Step constant");
        ProtobufValueCodecV1.RequireFinite(value.SourceMinimum, "Mapping Step source minimum");
        ProtobufValueCodecV1.RequireFinite(value.SourceMaximum, "Mapping Step source maximum");
        ProtobufValueCodecV1.RequireFinite(value.TargetMinimum, "Mapping Step target minimum");
        ProtobufValueCodecV1.RequireFinite(value.TargetMaximum, "Mapping Step target maximum");
        ValueMappingStepV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            IsEnabled = value.IsEnabled,
            Source = (WireMappingSource)(int)value.Source,
            Operation = (WireMappingOperation)(int)value.Operation,
            Constant = value.Constant,
            SourceMinimum = value.SourceMinimum,
            SourceMaximum = value.SourceMaximum,
            TargetMinimum = value.TargetMinimum,
            TargetMaximum = value.TargetMaximum,
            InputOverflow = (WireMappingInputOverflow)(int)value.InputOverflow,
            DivideByZero = (WireDivideByZeroPolicy)(int)value.DivideByZero
        };
        if (value.LogicalParameterId.HasValue)
        {
            result.LogicalParameterId = ProtobufValueCodecV1.ToWire(value.LogicalParameterId.Value);
        }
        if (value.EnvelopeId.HasValue) result.EnvelopeId = ProtobufValueCodecV1.ToWire(value.EnvelopeId.Value);
        if (value.MappingFunctionId.HasValue)
        {
            result.MappingFunctionId = ProtobufValueCodecV1.ToWire(value.MappingFunctionId.Value);
        }
        return result;
    }

    private static MidiValueTargetV1 ToWire(MidiValueTarget value) => new()
    {
        Kind = (WireMidiValueKind)(int)value.Kind,
        Number = value.Number
    };

    private static MidiValueTarget FromWire(MidiValueTargetV1 value) =>
        new((MidiValueKind)(int)value.Kind, value.Number);

    private static MidiIntegerTargetSettingsV1 ToWire(MidiIntegerTargetSettings value) => new()
    {
        Rounding = (WireMappingRounding)(int)value.Rounding,
        Overflow = (WireMappingOverflow)(int)value.Overflow
    };

    private static void Restore(MidiIntegerTargetSettings target, MidiIntegerTargetSettingsV1 value)
    {
        target.Rounding = (MappingRounding)(int)value.Rounding;
        target.Overflow = (MappingOverflow)(int)value.Overflow;
    }

    private static void Validate(EventInstrumentV1 value)
    {
        ProtobufValueCodecV1.Require(value.HasSchemaVersion, "Event Instrument schemaVersion");
        ProtobufValueCodecV1.Require(value.HasObjectType, "Event Instrument objectType");
        ProtobufValueCodecV1.Require(value.HasName, "Event Instrument name");
        ProtobufValueCodecV1.Require(value.HasRootNote, "Event Instrument rootNote");
        ProtobufValueCodecV1.Require(value.HasTemplateLengthTicks, "Event Instrument templateLengthTicks");
        ProtobufValueCodecV1.Require(value.HasRequiresChannelIsolation, "Event Instrument isolation setting");
        ProtobufValueCodecV1.Require(value.HasOverlapPolicy, "Event Instrument overlapPolicy");
        ProtobufValueCodecV1.Require(value.HasOverlapScope, "Event Instrument overlapScope");
        ProtobufValueCodecV1.Require(value.HasShortLifecycle, "Event Instrument shortLifecycle");
        ProtobufValueCodecV1.Require(value.HasLongLifecycle, "Event Instrument longLifecycle");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "Event Instrument schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Event Instrument ID");
        _ = ProtobufValueCodecV1.FromWire(value.Color, "Event Instrument color");
        if (value.InitialState is null) throw new InvalidDataException("Event Instrument initialState is required.");
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Event Instrument name");
        if (value.HasDescription)
        {
            PersistenceValueValidationV1.ValidateDescription(value.Description, "Event Instrument description");
        }
        Validate(value.InitialState, "Event Instrument initialState");
        foreach (LogicalParameterDefinitionV1 item in value.LogicalParameters) Validate(item);
        foreach (SubVoiceV1 item in value.SubVoices) Validate(item);
        foreach (InstrumentEnvelopeV1 item in value.Envelopes) Validate(item);
        foreach (CSharpMappingFunctionV1 item in value.MappingFunctions) Validate(item);
        foreach (LogicalParameterMappingV1 item in value.ParameterMappings) Validate(item);
    }

    private static void Validate(MidiInitialStateV1 value, string fieldName)
    {
        ValidateEntries(value.Controllers, $"{fieldName}.controllers");
        ValidateEntries(value.RegisteredParameters, $"{fieldName}.registeredParameters");
        ValidateEntries(value.NonRegisteredParameters, $"{fieldName}.nonRegisteredParameters");
    }

    private static void ValidateEntries(IEnumerable<MidiStateEntryV1> values, string fieldName)
    {
        HashSet<int> numbers = [];
        foreach (MidiStateEntryV1 item in values)
        {
            if (!item.HasNumber) throw new InvalidDataException($"{fieldName}.number is required.");
            if (!item.HasValue) throw new InvalidDataException($"{fieldName}.value is required.");
            if (!numbers.Add(item.Number)) throw new InvalidDataException($"{fieldName} contains a duplicate number.");
        }
    }

    private static void Validate(LogicalParameterDefinitionV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter ID");
        ProtobufValueCodecV1.Require(value.HasName, "Logical Parameter name");
        ProtobufValueCodecV1.Require(value.HasType, "Logical Parameter type");
        ProtobufValueCodecV1.Require(value.HasMinimum, "Logical Parameter minimum");
        ProtobufValueCodecV1.Require(value.HasMaximum, "Logical Parameter maximum");
        ProtobufValueCodecV1.Require(value.HasDisplayMinimum, "Logical Parameter display minimum");
        ProtobufValueCodecV1.Require(value.HasDisplayMaximum, "Logical Parameter display maximum");
        ProtobufValueCodecV1.Require(value.HasDefaultValue, "Logical Parameter default value");
        ProtobufValueCodecV1.Require(
            value.HasUsesExplicitEnumValues,
            "Logical Parameter usesExplicitEnumValues");
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Logical Parameter name");
        foreach (double item in new[]
        {
            value.Minimum, value.Maximum, value.DisplayMinimum, value.DisplayMaximum, value.DefaultValue
        }) ProtobufValueCodecV1.RequireFinite(item, "Logical Parameter numeric value");
        foreach (LogicalParameterEnumItemV1 item in value.EnumItems)
        {
            _ = ProtobufValueCodecV1.FromWire(item.Id, "Logical Parameter enum item ID");
            ProtobufValueCodecV1.Require(item.HasName, "Logical Parameter enum item name");
            ProtobufValueCodecV1.Require(item.HasValue, "Logical Parameter enum item value");
            PersistenceValueValidationV1.ValidateShortText(item.Name, "Logical Parameter enum item name");
        }
    }

    private static void Validate(SubVoiceV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "SubVoice ID");
        if (value.InitialState is null) throw new InvalidDataException("SubVoice initialState is required.");
        if (value.HasName) PersistenceValueValidationV1.ValidateShortText(value.Name, "SubVoice name");
        Validate(value.InitialState, "SubVoice initialState");
        HashSet<TemplateEventMappingTarget> targets = [];
        foreach (SubVoiceEventMappingV1 item in value.EventMappings)
        {
            TemplateEventMappingTarget target = Validate(item);
            if (!targets.Add(target))
            {
                throw new InvalidDataException(
                    "SubVoice eventMappings contain a duplicate target.");
            }
        }
        foreach (TemplateEventV1 item in value.Events)
        {
            Validate(item);
            if (EnumerateRequiredEventMappingTargets(item).Any(target => !targets.Contains(target)))
            {
                throw new InvalidDataException(
                    "SubVoice eventMappings do not cover every mandatory Note Mapping target.");
            }
        }
        foreach (ValueCurveV1 item in value.Curves) Validate(item);
    }

    private static IEnumerable<TemplateEventMappingTarget> EnumerateRequiredEventMappingTargets(
        TemplateEventV1 value)
    {
        TemplateEventKind kind = (TemplateEventKind)(int)value.Kind;
        if (kind == TemplateEventKind.Note)
        {
            yield return TemplateEventMappingTarget.Create(
                kind,
                value.Number,
                TemplateEventMappingParameter.Number);
            yield return TemplateEventMappingTarget.Create(
                kind,
                value.Number,
                TemplateEventMappingParameter.Value);
        }
    }

    private static void Validate(TemplateEventV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Template Event ID");
        ProtobufValueCodecV1.Require(value.HasKind, "Template Event kind");
        ProtobufValueCodecV1.Require(value.HasTick, "Template Event tick");
        ProtobufValueCodecV1.Require(value.HasLengthTicks, "Template Event lengthTicks");
        ProtobufValueCodecV1.Require(value.HasNumber, "Template Event number");
        ProtobufValueCodecV1.Require(value.HasValue, "Template Event value");
        ProtobufValueCodecV1.Require(value.HasSecondaryValue, "Template Event secondaryValue");
        ProtobufValueCodecV1.Require(value.HasHasBankMsb, "Template Event hasBankMsb");
        ProtobufValueCodecV1.Require(value.HasHasBankLsb, "Template Event hasBankLsb");
        ProtobufValueCodecV1.Require(value.HasFollowPitchDelta, "Template Event followPitchDelta");
    }

    private static TemplateEventMappingTarget Validate(SubVoiceEventMappingV1 value)
    {
        ProtobufValueCodecV1.Require(value.HasEventKind, "SubVoice Event Mapping eventKind");
        ProtobufValueCodecV1.Require(value.HasEventNumber, "SubVoice Event Mapping eventNumber");
        ProtobufValueCodecV1.Require(value.HasParameter, "SubVoice Event Mapping parameter");
        TemplateEventMappingTarget target = new(
            (TemplateEventKind)(int)value.EventKind,
            value.EventNumber,
            (TemplateEventMappingParameter)(int)value.Parameter);
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new InvalidDataException("SubVoice Event Mapping target is invalid.");
        }
        Validate(value.Steps, "SubVoice Event Mapping steps");
        Validate(value.TargetSettings, "SubVoice Event Mapping targetSettings");
        return target;
    }

    private static void Validate(ValueCurveV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Value Curve ID");
        Validate(value.Target, "Value Curve target");
        Validate(value.TargetSettings, "Value Curve targetSettings");
        foreach (CurvePointV1 point in value.Points) Validate(point);
    }

    internal static void Validate(CurvePointV1? value)
    {
        if (value is null) throw new InvalidDataException("Curve Point is required.");
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Curve Point ID");
        ProtobufValueCodecV1.Require(value.HasTick, "Curve Point tick");
        ProtobufValueCodecV1.Require(value.HasValue, "Curve Point value");
        ProtobufValueCodecV1.Require(value.HasInterpolation, "Curve Point interpolation");
        ProtobufValueCodecV1.RequireFinite(value.Value, "Curve Point value");
    }

    private static void Validate(InstrumentEnvelopeV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Envelope ID");
        if (value.HasName) PersistenceValueValidationV1.ValidateShortText(value.Name, "Envelope name");
        ProtobufValueCodecV1.Require(value.HasDelayTicks, "Envelope delayTicks");
        ProtobufValueCodecV1.Require(value.HasAttackTicks, "Envelope attackTicks");
        ProtobufValueCodecV1.Require(value.HasHoldTicks, "Envelope holdTicks");
        ProtobufValueCodecV1.Require(value.HasDecayTicks, "Envelope decayTicks");
        ProtobufValueCodecV1.Require(value.HasStartValue, "Envelope startValue");
        ProtobufValueCodecV1.Require(value.HasPeakValue, "Envelope peakValue");
        ProtobufValueCodecV1.Require(value.HasSustainValue, "Envelope sustainValue");
        ProtobufValueCodecV1.Require(value.HasReleaseTicks, "Envelope releaseTicks");
        ProtobufValueCodecV1.Require(value.HasEndValue, "Envelope endValue");
        foreach (double item in new[] { value.StartValue, value.PeakValue, value.SustainValue, value.EndValue })
        {
            ProtobufValueCodecV1.RequireFinite(item, "Envelope numeric value");
        }
    }

    private static void Validate(CSharpMappingFunctionV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Mapping Function ID");
        ProtobufValueCodecV1.Require(value.HasName, "Mapping Function name");
        ProtobufValueCodecV1.Require(value.HasBody, "Mapping Function body");
        ProtobufValueCodecV1.Require(value.HasAbiVersion, "Mapping Function abiVersion");
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Mapping Function name");
        PersistenceValueValidationV1.ValidateMappingBody(value.Body, "Mapping Function body");
        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (string field in value.DeclaredContextFields)
        {
            PersistenceValueValidationV1.ValidateShortText(field, "Mapping Function Context field", allowEmpty: false);
            if (!fields.Add(field))
            {
                throw new InvalidDataException("Mapping Function Context fields contain a duplicate.");
            }
        }
    }

    private static void Validate(LogicalParameterMappingV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter Mapping ID");
        _ = ProtobufValueCodecV1.FromWire(value.ParameterId, "Logical Parameter Mapping parameter ID");
        _ = ProtobufValueCodecV1.FromWire(value.SubVoiceId, "Logical Parameter Mapping SubVoice ID");
        Validate(value.Target, "Logical Parameter Mapping target");
        Validate(value.Steps, "Logical Parameter Mapping steps");
        Validate(value.TargetSettings, "Logical Parameter Mapping targetSettings");
    }

    private static void Validate(MappingChainV1? value, string fieldName)
    {
        if (value is null) throw new InvalidDataException($"{fieldName} is required.");
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Mapping Chain ID");
        if (!value.HasIsEnabled) throw new InvalidDataException($"{fieldName}.isEnabled is required.");
        foreach (ValueMappingStepV1 step in value.Steps)
        {
            _ = ProtobufValueCodecV1.FromWire(step.Id, "Mapping Step ID");
            ProtobufValueCodecV1.Require(step.HasIsEnabled, "Mapping Step isEnabled");
            ProtobufValueCodecV1.Require(step.HasSource, "Mapping Step source");
            ProtobufValueCodecV1.Require(step.HasOperation, "Mapping Step operation");
            ProtobufValueCodecV1.Require(step.HasConstant, "Mapping Step constant");
            ProtobufValueCodecV1.Require(step.HasSourceMinimum, "Mapping Step sourceMinimum");
            ProtobufValueCodecV1.Require(step.HasSourceMaximum, "Mapping Step sourceMaximum");
            ProtobufValueCodecV1.Require(step.HasTargetMinimum, "Mapping Step targetMinimum");
            ProtobufValueCodecV1.Require(step.HasTargetMaximum, "Mapping Step targetMaximum");
            ProtobufValueCodecV1.Require(step.HasInputOverflow, "Mapping Step inputOverflow");
            ProtobufValueCodecV1.Require(step.HasDivideByZero, "Mapping Step divideByZero");
            foreach (double item in new[]
            {
                step.Constant, step.SourceMinimum, step.SourceMaximum, step.TargetMinimum, step.TargetMaximum
            }) ProtobufValueCodecV1.RequireFinite(item, "Mapping Step numeric value");
            if (step.HasLogicalParameterId)
            {
                _ = ProtobufValueCodecV1.FromWire(step.LogicalParameterId, "Mapping Step Logical Parameter ID");
            }
            if (step.HasEnvelopeId)
            {
                _ = ProtobufValueCodecV1.FromWire(step.EnvelopeId, "Mapping Step Envelope ID");
            }
            if (step.HasMappingFunctionId)
            {
                _ = ProtobufValueCodecV1.FromWire(step.MappingFunctionId, "Mapping Step Function ID");
            }
        }
    }

    private static void Validate(MidiValueTargetV1? value, string fieldName)
    {
        if (value is null) throw new InvalidDataException($"{fieldName} is required.");
        if (!value.HasKind) throw new InvalidDataException($"{fieldName}.kind is required.");
        if (!value.HasNumber) throw new InvalidDataException($"{fieldName}.number is required.");
    }

    private static void Validate(MidiIntegerTargetSettingsV1? value, string fieldName)
    {
        if (value is null) throw new InvalidDataException($"{fieldName} is required.");
        if (!value.HasRounding) throw new InvalidDataException($"{fieldName}.rounding is required.");
        if (!value.HasOverflow) throw new InvalidDataException($"{fieldName}.overflow is required.");
    }
}
