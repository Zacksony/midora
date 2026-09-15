using Google.Protobuf.Reflection;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence;

internal static partial class EventInstrumentProtobufCodecV1
{
    public static void Serialize(EventInstrument value, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        new StreamingProtobufWriteContext(cancellationToken).Serialize(destination, writer => Write(writer, value));
    }

    internal static void Write(StreamingProtobufWriter writer, EventInstrument value)
    {
        EventInstrumentV1 header = ToWire(value, includeChildren: false);
        Validate(header);
        header.InitialState = null;
        writer.Wire(header);
        writer.Message(16, value.InitialState, nested => Write(nested, value.InitialState));
        foreach (LogicalParameterDefinition parameter in value.LogicalParameters)
            writer.Message(17, parameter, nested => Write(nested, parameter));
        foreach (SubVoice voice in value.SubVoices)
            writer.Message(18, voice, nested => Write(nested, voice));
        foreach (InstrumentEnvelope envelope in value.Envelopes)
        {
            writer.Checkpoint();
            InstrumentEnvelopeV1 wire = ToWire(envelope); Validate(wire); writer.SmallMessage(19, wire);
        }
        foreach (CSharpMappingFunction function in value.MappingFunctions)
            writer.Message(20, function, nested => Write(nested, function));
        foreach (LogicalParameterMapping mapping in value.ParameterMappings)
            writer.Message(21, mapping, nested => Write(nested, mapping));
    }

    private static void Write(StreamingProtobufWriter writer, LogicalParameterDefinition value)
    {
        LogicalParameterDefinitionV1 header = ToWire(value, includeChildren: false);
        Validate(header); writer.Wire(header);
        foreach (LogicalParameterEnumItem item in value.EnumItems)
        {
            writer.Checkpoint(); writer.SmallMessage(10, ToWire(item));
        }
    }

    private static void Write(StreamingProtobufWriter writer, SubVoice value)
    {
        SubVoiceV1 header = ToWire(value, includeChildren: false);
        Validate(header); header.InitialState = null; writer.Wire(header);
        writer.Message(4, value.InitialState, nested => Write(nested, value.InitialState));
        TemplateEventQuerySnapshot events = writer.Snapshot(value.Events, value.Events.CreateQuerySnapshot);
        bool hasNote = false;
        int ordinal = 0;
        foreach (TemplateEventSnapshotValue item in events.EnumerateAll())
        {
            if ((ordinal++ & 255) == 0) writer.Checkpoint();
            hasNote |= item.Kind == TemplateEventKind.Note;
            writer.Event(5, item);
        }
        foreach (ValueCurve curve in value.Curves)
            writer.Message(6, curve, nested => Write(nested, curve));
        HashSet<TemplateEventMappingTarget> targets = [];
        foreach (SubVoiceEventMapping mapping in value.EventMappings
            .OrderBy(item => item.Target.EventKind).ThenBy(item => item.Target.EventNumber)
            .ThenBy(item => item.Target.Parameter))
        {
            if (!targets.Add(mapping.Target)) throw new InvalidDataException("SubVoice eventMappings contain a duplicate target.");
            writer.Message(7, mapping, nested => Write(nested, mapping));
        }
        RequireNoteMappings(hasNote, targets);
    }

    private static void RequireNoteMappings(bool hasNote, HashSet<TemplateEventMappingTarget> targets)
    {
        if (hasNote && (!targets.Contains(new(TemplateEventKind.Note, 0, TemplateEventMappingParameter.Number))
            || !targets.Contains(new(TemplateEventKind.Note, 0, TemplateEventMappingParameter.Value))))
            throw new InvalidDataException("SubVoice eventMappings do not cover every mandatory Note Mapping target.");
    }

    private static void Write(StreamingProtobufWriter writer, ValueCurve value)
    {
        ValueCurveV1 header = ToWire(value, includeChildren: false);
        Validate(header); writer.Wire(header);
        CurvePointQuerySnapshot points = writer.Snapshot(value.Points, value.Points.CreateQuerySnapshot);
        int ordinal = 0;
        foreach (CurvePointSnapshotValue point in points.EnumerateAll())
        {
            if ((ordinal++ & 255) == 0) writer.Checkpoint();
            writer.Point(4, point);
        }
    }

    private static void Write(StreamingProtobufWriter writer, SubVoiceEventMapping value)
    {
        if (!TemplateEventMappingTarget.IsSupported(value.Target))
            throw new InvalidDataException("SubVoice Event Mapping target is invalid.");
        writer.Wire(new SubVoiceEventMappingV1
        {
            EventKind = (Wire.Proto.V1.TemplateEventKindV1)(int)value.Target.EventKind,
            EventNumber = value.Target.EventNumber,
            Parameter = (Wire.Proto.V1.TemplateEventMappingParameterV1)(int)value.Target.Parameter
        });
        writer.Message(4, value.Steps, nested => Write(nested, value.Steps));
        writer.SmallMessage(5, ToWire(value.TargetSettings));
    }

    private static void Write(StreamingProtobufWriter writer, LogicalParameterMapping value)
    {
        writer.Wire(new LogicalParameterMappingV1
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            ParameterId = ProtobufValueCodecV1.ToWire(value.ParameterId),
            SubVoiceId = ProtobufValueCodecV1.ToWire(value.SubVoiceId),
            Target = ToWire(value.Target)
        });
        writer.Message(5, value.Steps, nested => Write(nested, value.Steps));
        writer.SmallMessage(6, ToWire(value.TargetSettings));
    }

    private static void Write(StreamingProtobufWriter writer, MappingChain value)
    {
        writer.Wire(new MappingChainV1 { Id = ProtobufValueCodecV1.ToWire(value.Id), IsEnabled = value.IsEnabled });
        foreach (ValueMappingStep step in value)
        {
            writer.Checkpoint(); writer.SmallMessage(3, ToWire(step));
        }
    }

    private static void Write(StreamingProtobufWriter writer, CSharpMappingFunction value)
    {
        CSharpMappingFunctionV1 header = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id), Name = value.Name,
            Body = value.Body, AbiVersion = value.AbiVersion
        };
        Validate(header); writer.Wire(header);
        foreach (string field in value.DeclaredContextFields.Order(StringComparer.Ordinal))
        {
            writer.Checkpoint();
            PersistenceValueValidationV1.ValidateShortText(field, "Mapping Function Context field", allowEmpty: false);
            writer.String(5, field);
        }
    }

    private static void Write(StreamingProtobufWriter writer, MidiInitialState value)
    {
        writer.Wire(ToWire(value, includeEntries: false));
        WriteEntries(writer, 7, value.Controllers);
        WriteEntries(writer, 8, value.RegisteredParameters);
        WriteEntries(writer, 9, value.NonRegisteredParameters);
    }

    private static void WriteEntries(StreamingProtobufWriter writer, int field, IReadOnlyDictionary<int, int> values)
    {
        foreach (KeyValuePair<int, int> entry in values.OrderBy(item => item.Key))
        {
            writer.Checkpoint();
            writer.SmallMessage(field, new MidiStateEntryV1 { Number = entry.Key, Value = entry.Value });
        }
    }

    public static EventInstrument Restore(MidoraProject project, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        StreamingProtobufReader reader = new(source, cancellationToken);
        return Read(project, reader, reader.Root(EventInstrumentV1.Descriptor), out _, out _);
    }

    internal static EventInstrument Read(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame,
        out long templateLengthTicks, out bool hasTemplateLengthTicks, bool deferValidation = false)
    {
        EventInstrumentV1 header = new();
        List<LogicalParameterDefinition> parameters = [];
        List<SubVoice> voices = [];
        List<InstrumentEnvelope> envelopes = [];
        List<CSharpMappingFunction> functions = [];
        List<LogicalParameterMapping> mappings = [];
        MidiInitialState? initialState = null;
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            switch (field.FieldNumber)
            {
                case 16:
                    initialState = ReadInitialState(reader, reader.Child(frame, field));
                    header.InitialState = new(); break;
                case 17: parameters.Add(ReadParameter(project, reader, reader.Child(frame, field))); break;
                case 18: voices.Add(ReadVoice(project, reader, reader.Child(frame, field))); break;
                case 19:
                {
                    InstrumentEnvelopeV1 wire = (InstrumentEnvelopeV1)reader.SmallMessage(reader.Child(frame, field));
                    envelopes.Add(reader.Materialize(() => { Validate(wire); return FromWire(project, wire); })); break;
                }
                case 20:
                {
                    functions.Add(ReadFunction(project, reader, reader.Child(frame, field))); break;
                }
                case 21: mappings.Add(ReadParameterMapping(project, reader, reader.Child(frame, field))); break;
                default: reader.Scalar(header, frame, field); break;
            }
        }
        templateLengthTicks = header.TemplateLengthTicks;
        hasTemplateLengthTicks = header.HasTemplateLengthTicks;
        if (deferValidation) reader.ValidateSemantics(() => Validate(header));
        else
        {
            Validate(header);
            reader.ThrowSemanticFailure();
        }
        return reader.Materialize(() =>
        {
            EventInstrument result = FromWire(project, header);
            CopyState(initialState!, result.InitialState);
            foreach (LogicalParameterDefinition parameter in parameters) result.LogicalParameters.Add(parameter);
            foreach (SubVoice voice in voices) result.SubVoices.Add(voice);
            foreach (InstrumentEnvelope envelope in envelopes) result.Envelopes.Add(envelope);
            foreach (CSharpMappingFunction function in functions) result.MappingFunctions.Add(function);
            foreach (LogicalParameterMapping mapping in mappings) result.ParameterMappings.Add(mapping);
            reader.Token.ThrowIfCancellationRequested();
            return result;
        });
    }

    private static LogicalParameterDefinition ReadParameter(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        LogicalParameterDefinitionV1 header = new();
        List<LogicalParameterEnumItem> items = [];
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber != 10) { reader.Scalar(header, frame, field); continue; }
            LogicalParameterEnumItemV1 wire = (LogicalParameterEnumItemV1)reader.SmallMessage(reader.Child(frame, field));
            items.Add(reader.Materialize(() =>
            {
                ProtobufValueCodecV1.Require(wire.HasName, "Logical Parameter enum item name");
                ProtobufValueCodecV1.Require(wire.HasValue, "Logical Parameter enum item value");
                PersistenceValueValidationV1.ValidateShortText(wire.Name, "Logical Parameter enum item name");
                return FromWire(project, wire);
            }));
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            LogicalParameterDefinition result = FromWire(project, header);
            foreach (LogicalParameterEnumItem item in items) result.EnumItems.Add(item);
            return result;
        });
    }

    private static SubVoice ReadVoice(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        SubVoiceV1 header = new();
        ProtobufValuePages<TemplateEventSnapshotValue> events = new();
        List<ValueCurve> curves = [];
        List<SubVoiceEventMapping> mappings = [];
        MidiInitialState? initialState = null;
        bool hasNote = false;
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            switch (field.FieldNumber)
            {
                case 4:
                    initialState = ReadInitialState(reader, reader.Child(frame, field));
                    header.InitialState = new(); break;
                case 5:
                    TemplateEventSnapshotValue value = reader.Event(reader.Child(frame, field));
                    hasNote |= value.Kind == TemplateEventKind.Note; events.Add(value); break;
                case 6: curves.Add(ReadCurve(project, reader, reader.Child(frame, field))); break;
                case 7: mappings.Add(ReadEventMapping(project, reader, reader.Child(frame, field))); break;
                default: reader.Scalar(header, frame, field); break;
            }
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            HashSet<TemplateEventMappingTarget> targets = [];
            foreach (SubVoiceEventMapping mapping in mappings)
                if (!targets.Add(mapping.Target)) throw new InvalidDataException("SubVoice eventMappings contain a duplicate target.");
            RequireNoteMappings(hasNote, targets);
            SubVoice result = FromWire(project, header);
            CopyState(initialState!, result.InitialState);
            foreach (SubVoiceEventMapping mapping in mappings) result.EventMappings.Add(mapping);
            result.Events.AdoptSource(project, events.Seal(), reader.Token);
            foreach (ValueCurve curve in curves) result.Curves.Add(curve);
            return result;
        });
    }

    private static ValueCurve ReadCurve(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        ValueCurveV1 header = new();
        ProtobufValuePages<CurvePointSnapshotValue> points = new();
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 4) points.Add(reader.Point(reader.Child(frame, field)));
            else reader.Scalar(header, frame, field);
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            ValueCurve result = FromWire(project, header);
            result.Points.AdoptSource(project, points.Seal(), reader.Token);
            return result;
        });
    }

    private static SubVoiceEventMapping ReadEventMapping(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        SubVoiceEventMappingV1 header = new();
        List<ValueMappingStep> steps = [];
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 4) header.Steps = ReadChain(project, reader, reader.Child(frame, field), steps);
            else reader.Scalar(header, frame, field);
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            SubVoiceEventMapping result = FromWire(project, header);
            foreach (ValueMappingStep step in steps) result.Steps.Add(step);
            return result;
        });
    }

    private static LogicalParameterMapping ReadParameterMapping(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        LogicalParameterMappingV1 header = new();
        List<ValueMappingStep> steps = [];
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 5) header.Steps = ReadChain(project, reader, reader.Child(frame, field), steps);
            else reader.Scalar(header, frame, field);
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            LogicalParameterMapping result = FromWire(project, header);
            foreach (ValueMappingStep step in steps) result.Steps.Add(step);
            return result;
        });
    }

    private static MappingChainV1 ReadChain(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame,
        List<ValueMappingStep> steps)
    {
        MappingChainV1 header = new();
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber != 3) { reader.Scalar(header, frame, field); continue; }
            ValueMappingStepV1 wire = (ValueMappingStepV1)reader.SmallMessage(reader.Child(frame, field));
            // Reuse the frozen presence/value validator with only this one step resident.
            steps.Add(reader.Materialize(() =>
            {
                MappingChainV1 single = new() { Id = 1, IsEnabled = true };
                single.Steps.Add(wire); Validate(single, "Mapping Chain");
                return FromWire(project, wire);
            }));
        }
        reader.ValidateSemantics(() => Validate(header, "Mapping Chain"));
        return header;
    }

    private static CSharpMappingFunction ReadFunction(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        CSharpMappingFunctionV1 header = new();
        HashSet<string> fields = new(StringComparer.Ordinal);
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber != 5) { reader.Scalar(header, frame, field); continue; }
            string name = reader.Text(frame, field);
            reader.ValidateSemantics(() =>
            {
                PersistenceValueValidationV1.ValidateShortText(name, "Mapping Function Context field", allowEmpty: false);
                if (!fields.Add(name)) throw new InvalidDataException("Mapping Function Context fields contain a duplicate.");
            });
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            CSharpMappingFunction result = FromWire(project, header);
            foreach (string name in fields) result.DeclaredContextFields.Add(name);
            return result;
        });
    }

    private static MidiInitialState ReadInitialState(StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        MidiInitialStateV1 header = new();
        MidiInitialState result = new();
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber < 7) { reader.Scalar(header, frame, field); continue; }
            MidiStateEntryV1 entry = (MidiStateEntryV1)reader.SmallMessage(reader.Child(frame, field));
            int number = field.FieldNumber;
            reader.ValidateSemantics(() =>
            {
                ProtobufValueCodecV1.Require(entry.HasNumber, "MIDI State Entry number");
                ProtobufValueCodecV1.Require(entry.HasValue, "MIDI State Entry value");
                IDictionary<int, int> target = number switch
                { 7 => result.Controllers, 8 => result.RegisteredParameters, _ => result.NonRegisteredParameters };
                if (!target.TryAdd(entry.Number, entry.Value)) throw new InvalidDataException("MIDI Initial State contains a duplicate number.");
            });
        }
        Restore(result, header);
        return result;
    }

    private static void CopyState(MidiInitialState source, MidiInitialState target)
    {
        target.BankMsb = source.BankMsb; target.BankLsb = source.BankLsb; target.Program = source.Program;
        target.PitchBend = source.PitchBend; target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach (KeyValuePair<int, int> entry in source.Controllers) target.Controllers.Add(entry.Key, entry.Value);
        foreach (KeyValuePair<int, int> entry in source.RegisteredParameters) target.RegisteredParameters.Add(entry.Key, entry.Value);
        foreach (KeyValuePair<int, int> entry in source.NonRegisteredParameters) target.NonRegisteredParameters.Add(entry.Key, entry.Value);
    }
}
