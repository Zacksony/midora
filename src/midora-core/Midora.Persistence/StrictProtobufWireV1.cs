using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Midora.Persistence;

internal static class StrictProtobufWireV1
{
    private const int MaximumMessageDepth = 100;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(ReadOnlySpan<byte> data, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateMessage(data, descriptor, descriptor.FullName, 0);
    }

    public static byte[] SerializeDeterministic(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using MemoryStream stream = new();
        using CodedOutputStream output = new(stream, leaveOpen: true) { Deterministic = true };
        message.WriteTo(output);
        output.Flush();
        return stream.ToArray();
    }

    private static void ValidateMessage(
        ReadOnlySpan<byte> data,
        MessageDescriptor descriptor,
        string path,
        int depth)
    {
        if (depth > MaximumMessageDepth)
        {
            throw new InvalidDataException($"Protobuf message depth exceeds {MaximumMessageDepth} at {path}.");
        }

        int offset = 0;
        ulong seen = 0;
        HashSet<int>? highFields = null;
        while (offset < data.Length)
        {
            ulong tag = ReadVarint(data, ref offset, path);
            ulong rawFieldNumber = tag >> 3;
            int wireType = (int)(tag & 7);
            if (rawFieldNumber is 0 or > 536_870_911)
            {
                throw new InvalidDataException($"Protobuf field number is invalid at {path}.");
            }
            int fieldNumber = (int)rawFieldNumber;
            FieldDescriptor? field = descriptor.FindFieldByNumber(fieldNumber);
            if (field is null)
            {
                throw new InvalidDataException($"Unknown protobuf field {fieldNumber} at {path}.");
            }
            if (!field.IsRepeated)
            {
                bool duplicate;
                if (fieldNumber < 64)
                {
                    ulong mask = 1UL << fieldNumber;
                    duplicate = (seen & mask) != 0;
                    seen |= mask;
                }
                else duplicate = !(highFields ??= []).Add(fieldNumber);
                if (duplicate) throw new InvalidDataException($"Duplicate protobuf field {fieldNumber} at {path}.");
            }

            int expectedWireType = field.IsRepeated && field.IsPacked && IsPackable(field.FieldType)
                ? 2
                : GetWireType(field.FieldType);
            if (wireType != expectedWireType)
            {
                throw new InvalidDataException(
                    $"Protobuf field {path}.{field.Name} uses wire type {wireType}; expected {expectedWireType}.");
            }
            ReadField(data, ref offset, field, path, depth);
        }
    }

    private static void ReadField(
        ReadOnlySpan<byte> data,
        ref int offset,
        FieldDescriptor field,
        string path,
        int depth)
    {
        // Descriptor names are already cached. Do not allocate a new full path
        // for every successfully validated scalar in a million-record source.
        string fieldPath = field.FullName;
        if (field.IsRepeated && field.IsPacked && IsPackable(field.FieldType))
        {
            ReadOnlySpan<byte> packed = ReadLengthDelimited(data, ref offset, fieldPath);
            ValidatePacked(packed, field, fieldPath);
            return;
        }

        switch (GetWireType(field.FieldType))
        {
            case 0:
                ulong value = ReadVarint(data, ref offset, fieldPath);
                ValidateVarintValue(value, field, fieldPath);
                break;
            case 1:
                if (field.FieldType == FieldType.Double)
                {
                    ValidateDouble(data, offset, fieldPath);
                }
                SkipFixed(data, ref offset, 8, fieldPath);
                break;
            case 2:
                ReadOnlySpan<byte> bytes = ReadLengthDelimited(data, ref offset, fieldPath);
                if (field.FieldType == FieldType.String)
                {
                    ValidateUtf8(bytes, fieldPath);
                }
                else if (field.FieldType == FieldType.Message)
                {
                    ValidateMessage(bytes, field.MessageType, fieldPath, depth + 1);
                }
                break;
            case 5:
                if (field.FieldType == FieldType.Float)
                {
                    ValidateFloat(data, offset, fieldPath);
                }
                SkipFixed(data, ref offset, 4, fieldPath);
                break;
            default:
                throw new InvalidDataException($"Groups are not supported by the Midora v1 protobuf contract at {fieldPath}.");
        }
    }

    private static void ValidatePacked(ReadOnlySpan<byte> data, FieldDescriptor field, string path)
    {
        int scalarWireType = GetWireType(field.FieldType);
        int offset = 0;
        switch (scalarWireType)
        {
            case 0:
                while (offset < data.Length)
                {
                    ValidateVarintValue(ReadVarint(data, ref offset, path), field, path);
                }
                break;
            case 1 when data.Length % 8 == 0:
                if (field.FieldType == FieldType.Double)
                {
                    for (int index = 0; index < data.Length; index += 8)
                    {
                        ValidateDouble(data, index, path);
                    }
                }
                break;
            case 5 when data.Length % 4 == 0:
                if (field.FieldType == FieldType.Float)
                {
                    for (int index = 0; index < data.Length; index += 4)
                    {
                        ValidateFloat(data, index, path);
                    }
                }
                break;
            default:
                throw new InvalidDataException($"Packed protobuf payload has invalid length or type at {path}.");
        }
    }

    private static void ValidateVarintValue(ulong value, FieldDescriptor field, string path)
    {
        if (field.FieldType is FieldType.UInt32 or FieldType.SInt32 && value > uint.MaxValue)
        {
            throw new InvalidDataException($"32-bit protobuf field is out of range at {path}.");
        }
        if (field.FieldType is FieldType.Int32 or FieldType.Enum
            && value > int.MaxValue
            && value < 0xffff_ffff_8000_0000UL)
        {
            throw new InvalidDataException($"Signed 32-bit protobuf field is out of range at {path}.");
        }
        if (field.FieldType == FieldType.Bool && value > 1)
        {
            throw new InvalidDataException($"Boolean protobuf field is not encoded as 0 or 1 at {path}.");
        }
        if (field.FieldType == FieldType.Enum
            && field.EnumType.FindValueByNumber(unchecked((int)value)) is null)
        {
            throw new InvalidDataException($"Unknown protobuf enum value {unchecked((int)value)} at {path}.");
        }
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> data, ref int offset, string path)
    {
        ulong rawLength = ReadVarint(data, ref offset, path);
        if (rawLength > int.MaxValue || rawLength > (ulong)(data.Length - offset))
        {
            throw new InvalidDataException($"Length-delimited protobuf field exceeds its input at {path}.");
        }
        int length = (int)rawLength;
        ReadOnlySpan<byte> result = data.Slice(offset, length);
        offset += length;
        return result;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset, string path)
    {
        ulong result = 0;
        for (int index = 0; index < 10; index++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException($"Truncated protobuf varint at {path}.");
            }
            byte value = data[offset++];
            if (index == 9 && value > 1)
            {
                throw new InvalidDataException($"Overflowing protobuf varint at {path}.");
            }
            result |= (ulong)(value & 0x7f) << (index * 7);
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }
        throw new InvalidDataException($"Overlong protobuf varint at {path}.");
    }

    private static void SkipFixed(ReadOnlySpan<byte> data, ref int offset, int count, string path)
    {
        if (offset > data.Length - count)
        {
            throw new InvalidDataException($"Truncated fixed-width protobuf field at {path}.");
        }
        offset += count;
    }

    private static void ValidateDouble(ReadOnlySpan<byte> data, int offset, string path)
    {
        if (offset > data.Length - 8)
        {
            throw new InvalidDataException($"Truncated double protobuf field at {path}.");
        }
        double value = BitConverter.Int64BitsToDouble(
            unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])));
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"Non-finite double protobuf field at {path}.");
        }
    }

    private static void ValidateFloat(ReadOnlySpan<byte> data, int offset, string path)
    {
        if (offset > data.Length - 4)
        {
            throw new InvalidDataException($"Truncated float protobuf field at {path}.");
        }
        float value = BitConverter.Int32BitsToSingle(
            unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])));
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException($"Non-finite float protobuf field at {path}.");
        }
    }

    private static void ValidateUtf8(ReadOnlySpan<byte> data, string path)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Invalid UTF-8 protobuf string at {path}.", exception);
        }
    }

    private static int GetWireType(FieldType fieldType) => fieldType switch
    {
        FieldType.Int32 or FieldType.Int64 or FieldType.UInt32 or FieldType.UInt64
            or FieldType.SInt32 or FieldType.SInt64 or FieldType.Bool or FieldType.Enum => 0,
        FieldType.Fixed64 or FieldType.SFixed64 or FieldType.Double => 1,
        FieldType.String or FieldType.Bytes or FieldType.Message => 2,
        FieldType.Group => 3,
        FieldType.Fixed32 or FieldType.SFixed32 or FieldType.Float => 5,
        _ => throw new InvalidDataException($"Unsupported protobuf field type {fieldType}.")
    };

    private static bool IsPackable(FieldType fieldType) => fieldType is not
        FieldType.String and not FieldType.Bytes and not FieldType.Message and not FieldType.Group;
}
