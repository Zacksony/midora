using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Midora.Persistence;

/// <summary>Incremental token framing; only one compact source-generated record is materialized at a time.</summary>
internal sealed class ConductorJsonStreamReaderV1 : IDisposable
{
    internal const int InitialBufferBytes = 65_536;
    private readonly Stream _stream;
    private readonly CancellationToken _cancellationToken;
    private readonly ArrayBufferWriter<byte> _recordBytes = new(512);
    private readonly Utf8JsonWriter _recordWriter;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
    private int _offset, _length, _operations;
    private bool _final;
    private long _consumed;
    private JsonReaderState _state = new(new JsonReaderOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 100
    });

    public ConductorJsonStreamReaderV1(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("The JSON stream must be readable.", nameof(stream));
        _stream = stream;
        _cancellationToken = cancellationToken;
        _recordWriter = new Utf8JsonWriter(_recordBytes);
    }

    internal int PeakBufferBytes { get; private set; } = InitialBufferBytes;
    internal int PeakRecordBufferBytes { get; private set; } = 512;

    public JsonTokenType ReadToken(out string? text, out int integer)
    {
        CheckCancellation();
        if (_consumed == 0)
        {
            while (!_final && _length < 3) Refill();
            if (_length >= 3 && _buffer.AsSpan(0, 3).SequenceEqual("\uFEFF"u8))
                throw new InvalidDataException("Midora JSON must be UTF-8 without BOM.");
        }
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_offset, _length - _offset), _final, _state);
            if (reader.Read())
            {
                text = reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String ? ReadString(reader) : null;
                integer = 0;
                if (reader.TokenType == JsonTokenType.Number && !reader.TryGetInt32(out integer))
                    throw new JsonException("The Conductor schema version must be an Int32 integer.");
                JsonTokenType token = reader.TokenType;
                Commit(reader);
                return token;
            }
            Commit(reader);
            if (_final) { text = null; integer = 0; return JsonTokenType.None; }
            Refill();
        }
    }

    public JsonTokenType ReadToken() => ReadToken(out _, out _);

    public T? ReadRecord<T>(JsonTypeInfo<T> typeInfo, bool allowNull, out bool endArray) where T : class
    {
        CheckCancellation();
        bool started = false;
        int seen = 0;
        byte[][] names = RecordSchema<T>.Get(typeInfo);
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_offset, _length - _offset), _final, _state);
            if (!reader.Read())
            {
                Commit(reader);
                if (_final) throw new JsonException("Incomplete conductor JSON event.");
                Refill();
                continue;
            }
            endArray = !started && reader.TokenType == JsonTokenType.EndArray;
            if (endArray) { Commit(reader); return null; }
            if (!started)
            {
                if (allowNull && reader.TokenType == JsonTokenType.Null) { Commit(reader); return null; }
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("A Conductor event must be an object.");
                _recordBytes.Clear();
                _recordWriter.Reset(_recordBytes);
                _recordWriter.WriteStartObject();
                started = true;
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                _recordWriter.WriteEndObject();
                _recordWriter.Flush();
                PeakRecordBufferBytes = Math.Max(PeakRecordBufferBytes, _recordBytes.Capacity);
                Commit(reader);
                return JsonSerializer.Deserialize(_recordBytes.WrittenSpan, typeInfo)
                    ?? throw new JsonException("A Conductor event cannot be null.");
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                int index = 0;
                while (index < names.Length && !PropertyNameEquals(ref reader, names[index])) index++;
                if (index == names.Length) throw new JsonException($"Unknown Conductor event property '{ReadString(reader)}'.");
                int bit = 1 << index;
                if ((seen & bit) != 0) throw new InvalidDataException($"Duplicate JSON property '{ReadString(reader)}' in Conductor event.");
                seen |= bit;
                _recordWriter.WritePropertyName(names[index]);
            }
            else
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    throw new JsonException("Conductor event properties must be scalar values.");
                // Preserve lexical numeric/string representations for strict DTO
                // converters, but never retain intervening JSON whitespace.
                int start = checked((int)reader.TokenStartIndex);
                _recordWriter.WriteRawValue(_buffer.AsSpan(_offset + start,
                    checked((int)reader.BytesConsumed) - start), skipInputValidation: true);
            }
            Commit(reader);
        }
    }

    private static bool PropertyNameEquals(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        try { return reader.ValueTextEquals(name); }
        catch (InvalidOperationException exception)
        {
            // ValueTextEquals decodes escaped property names and can throw for an
            // unpaired surrogate before the source-generated converter sees it.
            throw new JsonException("Conductor JSON contains an invalid property name.", exception);
        }
    }

    private static string? ReadString(Utf8JsonReader reader)
    {
        try { return reader.GetString(); }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("Conductor JSON contains an invalid UTF-8 string.", exception);
        }
    }

    private void Commit(Utf8JsonReader reader)
    {
        _offset += checked((int)reader.BytesConsumed);
        _consumed += reader.BytesConsumed;
        _state = reader.CurrentState;
    }

    private void Refill()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        int remaining = _length - _offset;
        if (remaining == _buffer.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(checked(_buffer.Length * 2));
            _buffer.AsSpan(_offset, remaining).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
            PeakBufferBytes = Math.Max(PeakBufferBytes, _buffer.Length);
        }
        else if (_offset != 0) _buffer.AsSpan(_offset, remaining).CopyTo(_buffer);
        _offset = 0;
        _length = remaining;
        int read = _stream.Read(_buffer.AsSpan(_length));
        _length += read;
        _final = read == 0;
    }

    private void CheckCancellation()
    {
        if ((_operations++ & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        _recordWriter.Dispose();
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length != 0) ArrayPool<byte>.Shared.Return(buffer);
    }

    private static class RecordSchema<T>
    {
        private static byte[][]? _names;
        public static byte[][] Get(JsonTypeInfo<T> info) => _names ??= info.Properties
            .Select(property => Encoding.UTF8.GetBytes(property.Name)).ToArray();
    }
}
