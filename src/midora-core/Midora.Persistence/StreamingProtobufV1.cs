using System.Buffers.Binary;
using System.Collections;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Midora.Domain;

namespace Midora.Persistence;

/// <summary>Only container lengths and immutable value snapshots survive the sizing pass.</summary>
internal sealed class StreamingProtobufWriteContext(CancellationToken cancellationToken)
{
    private readonly Dictionary<object, int> _sizes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _snapshots = new(ReferenceEqualityComparer.Instance);
    public CancellationToken Token { get; } = cancellationToken;

    public T Snapshot<T>(object owner, Func<T> create) where T : class
    {
        if (_snapshots.TryGetValue(owner, out object? snapshot)) return (T)snapshot;
        T result = create();
        _snapshots.Add(owner, result);
        return result;
    }

    public void Serialize(Stream destination, Action<StreamingProtobufWriter> write)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Token.ThrowIfCancellationRequested();
        StreamingProtobufWriter counting = new(this, null);
        write(counting);
        Token.ThrowIfCancellationRequested();
        using CodedOutputStream output = new(destination, leaveOpen: true) { Deterministic = true };
        write(new(this, output));
        output.Flush();
        Token.ThrowIfCancellationRequested();
    }

    public int Measure(object key, Action<StreamingProtobufWriter> write)
    {
        if (_sizes.TryGetValue(key, out int length)) return length;
        StreamingProtobufWriter counter = new(this, null);
        write(counter);
        _sizes.Add(key, counter.Length);
        return counter.Length;
    }
}

internal sealed class StreamingProtobufWriter(StreamingProtobufWriteContext context, CodedOutputStream? output)
{
    public int Length { get; private set; }
    public CancellationToken Token => context.Token;
    public T Snapshot<T>(object owner, Func<T> create) where T : class => context.Snapshot(owner, create);
    public void Checkpoint() => Token.ThrowIfCancellationRequested();
    public void Wire(IMessage value)
    {
        Length = checked(Length + value.CalculateSize());
        if (output is not null) value.WriteTo(output);
    }
    public void Message(int field, object key, Action<StreamingProtobufWriter> write)
    {
        Checkpoint();
        int size = context.Measure(key, write);
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field)
            + CodedOutputStream.ComputeLengthSize(size) + size);
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteLength(size);
        write(new(context, output));
    }
    public void SmallMessage(int field, IMessage value)
    {
        int size = value.CalculateSize();
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field)
            + CodedOutputStream.ComputeLengthSize(size) + size);
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteMessage(value);
    }
    public void Int64(int field, long value)
    {
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeInt64Size(value));
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.Varint); output.WriteInt64(value);
    }
    public void String(int field, string value)
    {
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeStringSize(value));
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteString(value);
    }
    public void Note(int field, LogicalNoteSnapshotValue value)
    {
        _ = ProtobufValueCodecV1.ToWire(value.Id);
        int size = 5 + CodedOutputStream.ComputeInt64Size(value.Id.Value)
            + CodedOutputStream.ComputeInt64Size(value.StartTick) + CodedOutputStream.ComputeInt64Size(value.LengthTicks)
            + CodedOutputStream.ComputeInt32Size(value.Note) + CodedOutputStream.ComputeInt32Size(value.Velocity);
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeLengthSize(size) + size);
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteLength(size);
        output.WriteRawTag(8); output.WriteInt64(value.Id.Value);
        output.WriteRawTag(16); output.WriteInt64(value.StartTick);
        output.WriteRawTag(24); output.WriteInt64(value.LengthTicks);
        output.WriteRawTag(32); output.WriteInt32(value.Note);
        output.WriteRawTag(40); output.WriteInt32(value.Velocity);
    }
    public void Event(int field, TemplateEventSnapshotValue value)
    {
        _ = ProtobufValueCodecV1.ToWire(value.Id);
        if ((uint)value.Kind > 7) throw new InvalidDataException("Unknown Template Event kind.");
        int size = 13 + CodedOutputStream.ComputeInt64Size(value.Id.Value)
            + CodedOutputStream.ComputeEnumSize((int)value.Kind) + CodedOutputStream.ComputeInt64Size(value.Tick)
            + CodedOutputStream.ComputeInt64Size(value.LengthTicks) + CodedOutputStream.ComputeInt32Size(value.Number)
            + CodedOutputStream.ComputeInt32Size(value.Value) + CodedOutputStream.ComputeInt32Size(value.SecondaryValue);
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeLengthSize(size) + size);
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteLength(size);
        output.WriteRawTag(8); output.WriteInt64(value.Id.Value);
        output.WriteRawTag(16); output.WriteEnum((int)value.Kind);
        output.WriteRawTag(24); output.WriteInt64(value.Tick);
        output.WriteRawTag(32); output.WriteInt64(value.LengthTicks);
        output.WriteRawTag(40); output.WriteInt32(value.Number);
        output.WriteRawTag(48); output.WriteInt32(value.Value);
        output.WriteRawTag(56); output.WriteInt32(value.SecondaryValue);
        output.WriteRawTag(64); output.WriteBool(value.HasBankMsb);
        output.WriteRawTag(72); output.WriteBool(value.HasBankLsb);
        output.WriteRawTag(80); output.WriteBool(value.FollowPitchDelta);
    }
    public void Point(int field, CurvePointSnapshotValue value)
    {
        _ = ProtobufValueCodecV1.ToWire(value.Id);
        ProtobufValueCodecV1.RequireFinite(value.Value, "Curve Point value");
        if ((uint)value.Interpolation > 1) throw new InvalidDataException("Unknown Curve Point interpolation.");
        int size = 12 + CodedOutputStream.ComputeInt64Size(value.Id.Value)
            + CodedOutputStream.ComputeInt64Size(value.Tick) + CodedOutputStream.ComputeEnumSize((int)value.Interpolation);
        Length = checked(Length + CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeLengthSize(size) + size);
        if (output is null) return;
        output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteLength(size);
        output.WriteRawTag(8); output.WriteInt64(value.Id.Value);
        output.WriteRawTag(16); output.WriteInt64(value.Tick);
        output.WriteRawTag(25); output.WriteDouble(value.Value);
        output.WriteRawTag(32); output.WriteEnum((int)value.Interpolation);
    }
}

/// <summary>Descriptor checks occur before values are accepted; no complete message byte buffer.</summary>
internal sealed class StreamingProtobufReader(Stream input, CancellationToken cancellationToken)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _buffer = new byte[8192];
    private int _next;
    private int _available;
    private int _fieldCount;
    private Exception? _semanticFailure;
    public long Position { get; private set; }
    public CancellationToken Token => cancellationToken;
    public T Materialize<T>(Func<T> create) where T : class
    {
        if (_semanticFailure is not null) return null!;
        try { return create(); }
        catch (InvalidDataException exception) { _semanticFailure = exception; return null!; }
    }
    public void ValidateSemantics(Action validate)
    {
        try { validate(); }
        catch (Exception exception) when (exception is InvalidDataException or ProtobufObjectHeaderExceptionV1)
        {
            if (_semanticFailure is null || exception is ProtobufObjectHeaderExceptionV1)
                _semanticFailure = exception;
        }
    }
    public void ThrowSemanticFailure()
    {
        if (_semanticFailure is not null) throw _semanticFailure;
    }
    private MidoraId StableId(long value, string name)
    {
        if (value > 0) return new MidoraId(value);
        _semanticFailure ??= new InvalidDataException($"{name} stable ID must be positive.");
        return default;
    }

    internal struct Frame(MessageDescriptor descriptor, long end, int depth)
    {
        public MessageDescriptor Descriptor = descriptor;
        public long End = end;
        public int Depth = depth;
        public ulong Seen;
    }
    public Frame Root(MessageDescriptor descriptor) => new(descriptor, long.MaxValue, 0);
    public Frame Child(Frame parent, FieldDescriptor field)
    {
        if (parent.Depth >= 100) throw Error(field, "Protobuf message depth exceeds 100");
        int length = Length(parent, field);
        return new(field.MessageType, checked(Position + length), parent.Depth + 1);
    }
    public FieldDescriptor? Field(ref Frame frame)
    {
        if ((_fieldCount++ & 255) == 0) Token.ThrowIfCancellationRequested();
        if (Position == frame.End) return null;
        int first = Byte(frame.End, allowEnd: frame.End == long.MaxValue);
        if (first < 0) return null;
        ulong tag = Varint(frame.End, first);
        ulong number = tag >> 3;
        if (number is 0 or > 536_870_911) throw new InvalidDataException("Protobuf field number is invalid.");
        FieldDescriptor field = frame.Descriptor.FindFieldByNumber((int)number)
            ?? throw new InvalidDataException($"Unknown protobuf field {number} at {frame.Descriptor.FullName}.");
        int expected = WireType(field.FieldType);
        if (field.IsRepeated && field.IsPacked) expected = 2;
        if ((int)(tag & 7) != expected) throw Error(field, "Wrong protobuf wire type");
        if (!field.IsRepeated)
        {
            // Every frozen Midora message has fewer than 64 fields.
            ulong mask = 1UL << field.FieldNumber;
            if ((frame.Seen & mask) != 0) throw Error(field, "Duplicate protobuf singular field");
            frame.Seen |= mask;
        }
        return field;
    }
    public long Integer(Frame frame, FieldDescriptor field)
    {
        ulong value = Varint(frame.End);
        if (field.FieldType is FieldType.UInt32 or FieldType.SInt32 && value > uint.MaxValue)
            throw Error(field, "32-bit protobuf field is out of range");
        if (field.FieldType is FieldType.Int32 or FieldType.Enum && value > int.MaxValue && value < 0xffff_ffff_8000_0000UL)
            throw Error(field, "Signed 32-bit protobuf field is out of range");
        if (field.FieldType == FieldType.Bool && value > 1) throw Error(field, "Protobuf boolean must be 0 or 1");
        if (field.FieldType == FieldType.Enum && field.EnumType.FindValueByNumber(unchecked((int)value)) is null)
            throw Error(field, "Unknown protobuf enum value");
        return unchecked((long)value);
    }
    public double Double(Frame frame, FieldDescriptor field)
    {
        Span<byte> bytes = stackalloc byte[8];
        Read(bytes, frame.End);
        double value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        if (!double.IsFinite(value)) throw Error(field, "Non-finite double protobuf field");
        return value;
    }
    public string Text(Frame frame, FieldDescriptor field)
    {
        int length = Length(frame, field);
        // The largest supported persisted string is a 65,536-scalar description.
        if (length > PersistenceContractV1.DescriptionMaximumScalars * 4)
        {
            // Still consume and strictly validate the wire, so a malformed later
            // field or a root header mismatch retains the frozen precedence.
            Decoder decoder = Utf8.GetDecoder();
            Span<byte> bytesBuffer = stackalloc byte[2048];
            Span<char> charsBuffer = stackalloc char[4096];
            try
            {
                int remaining = length;
                while (remaining != 0)
                {
                    int take = Math.Min(remaining, bytesBuffer.Length);
                    Read(bytesBuffer[..take], frame.End);
                    remaining -= take;
                    decoder.Convert(bytesBuffer[..take], charsBuffer, remaining == 0, out _, out _, out _);
                }
            }
            catch (DecoderFallbackException exception) { throw new InvalidDataException($"Invalid UTF-8 at {field.FullName}.", exception); }
            _semanticFailure ??= Error(field, "Protobuf string exceeds the maximum supported text size");
            return string.Empty;
        }
        byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            Read(bytes.AsSpan(0, length), frame.End);
            return Utf8.GetString(bytes.AsSpan(0, length));
        }
        catch (DecoderFallbackException exception) { throw new InvalidDataException($"Invalid UTF-8 at {field.FullName}.", exception); }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
    }
    public void Scalar(IMessage target, Frame frame, FieldDescriptor field)
    {
        object value = field.FieldType switch
        {
            FieldType.String => Text(frame, field),
            FieldType.Double => Double(frame, field),
            FieldType.Bool => Integer(frame, field) != 0,
            FieldType.Int32 or FieldType.Enum => unchecked((int)Integer(frame, field)),
            FieldType.UInt32 => unchecked((uint)Integer(frame, field)),
            FieldType.Int64 => Integer(frame, field),
            FieldType.Message => SmallMessage(Child(frame, field)),
            _ => throw Error(field, "Unsupported protobuf scalar type")
        };
        if (field.IsRepeated) ((IList)field.Accessor.GetValue(target)).Add(value);
        else field.Accessor.SetValue(target, value);
    }
    public IMessage SmallMessage(Frame frame)
    {
        IMessage result = frame.Descriptor.Parser.ParseFrom(Array.Empty<byte>());
        FieldDescriptor? field;
        while ((field = Field(ref frame)) is not null) Scalar(result, frame, field);
        return result;
    }
    public void Require(Frame frame, ulong required)
    {
        if ((frame.Seen & required) != required)
            _semanticFailure ??= new InvalidDataException($"Required protobuf field is missing at {frame.Descriptor.FullName}.");
    }
    public LogicalNoteSnapshotValue Note(Frame frame)
    {
        long id = 0, tick = 0, length = 0; int note = 0, velocity = 0;
        FieldDescriptor? field;
        while ((field = Field(ref frame)) is not null)
        {
            long value = Integer(frame, field);
            switch (field.FieldNumber) { case 1: id = value; break; case 2: tick = value; break;
                case 3: length = value; break; case 4: note = (int)value; break; case 5: velocity = (int)value; break; }
        }
        Require(frame, 0x3e);
        return new(StableId(id, "Logical Note ID"), tick, length, note, velocity);
    }
    public TemplateEventSnapshotValue Event(Frame frame)
    {
        long id = 0, tick = 0, length = 0; int kind = 0, number = 0, value = 0, secondary = 0;
        bool msb = false, lsb = false, follow = false;
        FieldDescriptor? field;
        while ((field = Field(ref frame)) is not null)
        {
            long scalar = Integer(frame, field);
            switch (field.FieldNumber) { case 1: id = scalar; break; case 2: kind = (int)scalar; break;
                case 3: tick = scalar; break; case 4: length = scalar; break; case 5: number = (int)scalar; break;
                case 6: value = (int)scalar; break; case 7: secondary = (int)scalar; break;
                case 8: msb = scalar != 0; break; case 9: lsb = scalar != 0; break; case 10: follow = scalar != 0; break; }
        }
        Require(frame, 0x7fe);
        return new(StableId(id, "Template Event ID"), (TemplateEventKind)kind,
            tick, length, number, value, secondary, msb, lsb, follow);
    }
    public CurvePointSnapshotValue Point(Frame frame)
    {
        long id = 0, tick = 0; double value = 0; int interpolation = 0;
        FieldDescriptor? field;
        while ((field = Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 3) value = Double(frame, field);
            else { long scalar = Integer(frame, field); switch (field.FieldNumber)
                { case 1: id = scalar; break; case 2: tick = scalar; break; case 4: interpolation = (int)scalar; break; } }
        }
        Require(frame, 0x1e);
        return new(StableId(id, "Curve Point ID"), tick, value, (CurveInterpolation)interpolation);
    }
    private int Length(Frame frame, FieldDescriptor field)
    {
        ulong length = Varint(frame.End);
        if (length > int.MaxValue || length > (ulong)(frame.End - Position))
            throw Error(field, "Length-delimited protobuf field exceeds its input");
        return (int)length;
    }
    private ulong Varint(long end, int first = -1)
    {
        ulong result = 0;
        for (int index = 0; index < 10; index++)
        {
            int value = index == 0 && first >= 0 ? first : Byte(end, false);
            if (index == 9 && value > 1) throw new InvalidDataException("Overflowing protobuf varint.");
            result |= (ulong)(value & 127) << (index * 7);
            if ((value & 128) == 0) return result;
        }
        throw new InvalidDataException("Overlong protobuf varint.");
    }
    private int Byte(long end, bool allowEnd)
    {
        if (Position >= end) throw new InvalidDataException("Truncated protobuf message.");
        if (_next == _available)
        {
            Token.ThrowIfCancellationRequested();
            _available = input.Read(_buffer); _next = 0;
            if (_available == 0)
            {
                if (allowEnd) return -1;
                throw new InvalidDataException("Truncated protobuf input.");
            }
        }
        Position++;
        return _buffer[_next++];
    }
    private void Read(Span<byte> destination, long end)
    {
        if (destination.Length > end - Position) throw new InvalidDataException("Truncated protobuf field.");
        for (int index = 0; index < destination.Length; index++) destination[index] = (byte)Byte(end, false);
    }
    private static InvalidDataException Error(FieldDescriptor field, string message) => new($"{message} at {field.FullName}.");
    private static int WireType(FieldType type) => type switch
    {
        FieldType.Int32 or FieldType.Int64 or FieldType.UInt32 or FieldType.UInt64 or FieldType.SInt32
            or FieldType.SInt64 or FieldType.Bool or FieldType.Enum => 0,
        FieldType.Fixed64 or FieldType.SFixed64 or FieldType.Double => 1,
        FieldType.String or FieldType.Bytes or FieldType.Message => 2,
        FieldType.Fixed32 or FieldType.SFixed32 or FieldType.Float => 5,
        _ => throw new InvalidDataException("Protobuf groups are not supported.")
    };
}

/// <summary>Completed pages are the final source, not a staging DTO copy.</summary>
internal sealed class ProtobufValuePages<T> : IImmutableTimelineValueSource<T> where T : struct
{
    private readonly List<T[]> _pages = [];
    private bool _sealed;
    public int Count { get; private set; }
    public int PageCapacity => 4096;
    public T this[int index] => index >= 0 && index < Count ? _pages[index / PageCapacity][index % PageCapacity]
        : throw new ArgumentOutOfRangeException(nameof(index));
    public void Add(T value)
    {
        if (_sealed) throw new InvalidOperationException("The protobuf value pages are sealed.");
        if (Count == int.MaxValue) throw new InvalidDataException("Protobuf source exceeds the supported Int32 ordinal range.");
        if (Count % PageCapacity == 0) _pages.Add(new T[PageCapacity]);
        _pages[Count / PageCapacity][Count % PageCapacity] = value;
        Count++;
    }
    public ProtobufValuePages<T> Seal()
    {
        if (!_sealed && Count % PageCapacity != 0)
        {
            T[] tail = _pages[^1];
            Array.Resize(ref tail, Count % PageCapacity);
            _pages[^1] = tail;
        }
        _sealed = true; return this;
    }
    public ReadOnlyMemory<T> ReadPage(int pageIndex)
    {
        if (!_sealed) throw new InvalidOperationException("The protobuf source is not sealed.");
        T[] page = _pages[pageIndex];
        return page.AsMemory(0, Math.Min(PageCapacity, Count - pageIndex * PageCapacity));
    }
    public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
    {
        values = ReadPage(pageIndex); return true;
    }
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
