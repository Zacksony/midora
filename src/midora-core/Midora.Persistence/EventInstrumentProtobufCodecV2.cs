using Google.Protobuf.Reflection;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V2;

namespace Midora.Persistence;

internal static class EventInstrumentProtobufCodecV2
{
    public const string ObjectType = EventInstrumentProtobufCodecV1.ObjectType;

    public static byte[] Serialize(EventInstrument value)
    {
        using MemoryStream destination = new();
        Serialize(value, destination);
        return destination.ToArray();
    }

    public static void Serialize(EventInstrument value, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.PreRollTicks < 0 || value.PreRollTicks > value.TemplateLengthTicks)
            throw new InvalidDataException("Event Instrument preRollTicks must be within 0..templateLengthTicks.");
        new StreamingProtobufWriteContext(cancellationToken).Serialize(destination, writer =>
        {
            writer.Wire(new EventInstrumentV2
            {
                SchemaVersion = PersistenceContractV2.EventInstrumentSchemaVersion,
                ObjectType = ObjectType
            });
            writer.Message(3, value, nested => EventInstrumentProtobufCodecV1.Write(nested, value));
            writer.Int64(4, value.PreRollTicks);
        });
    }

    public static EventInstrument Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        using MemoryStream source = new(bytes.ToArray(), writable: false);
        return Restore(project, source);
    }

    public static EventInstrument Restore(MidoraProject project, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using MemoryStream source = new(bytes, writable: false);
        return Restore(project, source);
    }

    public static EventInstrument Restore(MidoraProject project, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        StreamingProtobufReader reader = new(source, cancellationToken);
        StreamingProtobufReader.Frame frame = reader.Root(EventInstrumentV2.Descriptor);
        EventInstrumentV2 header = new();
        EventInstrument? definition = null;
        long templateLengthTicks = 0;
        bool hasTemplateLengthTicks = false;
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 3)
                definition = EventInstrumentProtobufCodecV1.Read(project, reader, reader.Child(frame, field),
                    out templateLengthTicks, out hasTemplateLengthTicks, deferValidation: true);
            else reader.Scalar(header, frame, field);
        }
        ProtobufValueCodecV1.Require(header.HasSchemaVersion, "Event Instrument v2 schemaVersion");
        ProtobufValueCodecV1.Require(header.HasObjectType, "Event Instrument v2 objectType");
        ProtobufValueCodecV1.Require(header.HasPreRollTicks, "Event Instrument v2 preRollTicks");
        if (header.SchemaVersion != PersistenceContractV2.EventInstrumentSchemaVersion || header.ObjectType != ObjectType)
            throw new ProtobufObjectHeaderExceptionV1(
                "Event Instrument v2 schemaVersion or objectType is inconsistent with its manifest identity.");
        if ((frame.Seen & (1UL << 3)) == 0) throw new InvalidDataException("Event Instrument v2 definition is required.");
        if (header.PreRollTicks < 0) throw new InvalidDataException("Event Instrument preRollTicks must be non-negative.");
        ProtobufValueCodecV1.Require(hasTemplateLengthTicks, "Event Instrument v2 definition templateLengthTicks");
        if (header.PreRollTicks > templateLengthTicks)
            throw new InvalidDataException("Event Instrument preRollTicks cannot exceed templateLengthTicks.");
        reader.ThrowSemanticFailure();
        if (definition is null) throw new InvalidDataException("Event Instrument v2 definition is required.");
        definition.PreRollTicks = header.PreRollTicks;
        cancellationToken.ThrowIfCancellationRequested();
        return definition;
    }
}
