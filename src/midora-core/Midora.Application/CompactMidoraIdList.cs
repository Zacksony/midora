using System.Collections;
using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Immutable, order-preserving stable-ID sequence used by large edit-result
/// selections. Consecutive IDs are represented as runs; other values are
/// stored in small delta-encoded blocks. Duplicate and out-of-order IDs are
/// intentionally preserved because this type is a sequence, not a set.
/// </summary>
internal sealed class CompactMidoraIdList : IReadOnlyList<MidoraId>
{
    private const int LiteralBlockCapacity = 256;
    private const int MinimumRunLength = 3;

    private static readonly CompactMidoraIdList EmptyInstance =
        new([], [], 0);

    private readonly Segment[] _segments;
    private readonly byte[] _encodedDeltas;

    private CompactMidoraIdList(
        Segment[] segments,
        byte[] encodedDeltas,
        int count)
    {
        _segments = segments;
        _encodedDeltas = encodedDeltas;
        Count = count;
    }

    public int Count { get; }

    internal int SegmentCount => _segments.Length;
    internal int EncodedDeltaByteCount => _encodedDeltas.Length;

    public MidoraId this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));

            Segment segment = FindSegment(index);
            int relativeIndex = index - segment.StartIndex;
            long value = segment.IsRun
                ? checked(segment.FirstValue + (segment.Step * relativeIndex))
                : DecodeLiteralValue(segment, relativeIndex);
            return FromRawValue(value);
        }
    }

    internal static IReadOnlyList<MidoraId> Freeze(IReadOnlyList<MidoraId> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is BoundedImmutableValueSource<MidoraId> or ConcatenatedList) return values;
        if (values is CompactMidoraIdList compact) return compact;
        if (values.Count == 0) return EmptyInstance;

        List<Segment> segments = [];
        long encodedByteCount = 0;
        int index = 0;
        while (index < values.Count)
        {
            if (TryMeasureRun(values, index, out long step, out int runLength))
            {
                segments.Add(new(
                    index,
                    runLength,
                    values[index].Value,
                    step,
                    PayloadOffset: 0,
                    PayloadLength: 0,
                    IsRun: true));
                index += runLength;
                continue;
            }

            int literalStart = index++;
            while (index < values.Count
                && index - literalStart < LiteralBlockCapacity
                && !TryMeasureRun(values, index, out _, out _))
            {
                index++;
            }

            int literalLength = index - literalStart;
            long blockPayloadLength = MeasureLiteralPayload(
                values,
                literalStart,
                literalLength);
            if (encodedByteCount > int.MaxValue - blockPayloadLength)
            {
                throw new InvalidOperationException(
                    "The compact selection exceeds the supported encoded size.");
            }

            segments.Add(new(
                literalStart,
                literalLength,
                values[literalStart].Value,
                Step: 0,
                PayloadOffset: (int)encodedByteCount,
                PayloadLength: (int)blockPayloadLength,
                IsRun: false));
            encodedByteCount += blockPayloadLength;
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>((int)encodedByteCount);
        foreach (Segment segment in segments)
        {
            if (segment.IsRun || segment.Count == 1) continue;
            EncodeLiteralPayload(values, segment, payload);
        }

        return new CompactMidoraIdList(segments.ToArray(), payload, values.Count);
    }

    internal static IReadOnlyList<MidoraId> FreezeConcatenated(
        IEnumerable<IReadOnlyList<MidoraId>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<MidoraId>[] parts = values
            .Where(static value => value.Count != 0)
            .ToArray();
        return parts.Length switch
        {
            0 => EmptyInstance,
            1 => Freeze(parts[0]),
            _ when parts.All(static value => value is CompactMidoraIdList) =>
                ConcatenateCompact(parts.Cast<CompactMidoraIdList>().ToArray()),
            _ => new ConcatenatedList(parts.Select(Freeze).ToArray())
        };
    }

    private static CompactMidoraIdList ConcatenateCompact(
        IReadOnlyList<CompactMidoraIdList> parts)
    {
        int totalCount = 0;
        int totalSegmentCount = 0;
        int totalPayloadLength = 0;
        foreach (CompactMidoraIdList part in parts)
        {
            totalCount = checked(totalCount + part.Count);
            totalSegmentCount = checked(totalSegmentCount + part._segments.Length);
            totalPayloadLength = checked(totalPayloadLength + part._encodedDeltas.Length);
        }

        Segment[] segments = new Segment[totalSegmentCount];
        byte[] payload = GC.AllocateUninitializedArray<byte>(totalPayloadLength);
        int countOffset = 0;
        int segmentOffset = 0;
        int payloadOffset = 0;
        foreach (CompactMidoraIdList part in parts)
        {
            foreach (Segment segment in part._segments)
            {
                segments[segmentOffset++] = segment with
                {
                    StartIndex = checked(segment.StartIndex + countOffset),
                    PayloadOffset = segment.IsRun
                        ? 0
                        : checked(segment.PayloadOffset + payloadOffset)
                };
            }
            part._encodedDeltas.CopyTo(payload, payloadOffset);
            countOffset = checked(countOffset + part.Count);
            payloadOffset = checked(payloadOffset + part._encodedDeltas.Length);
        }
        if (segmentOffset != segments.Length
            || countOffset != totalCount
            || payloadOffset != payload.Length)
        {
            throw new InvalidOperationException(
                "The compact selection concatenation plan is inconsistent.");
        }
        return new(segments, payload, totalCount);
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<MidoraId> IEnumerable<MidoraId>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ConcatenatedList : IReadOnlyList<MidoraId>
    {
        private readonly IReadOnlyList<MidoraId>[] _parts;
        private readonly int[] _ends;

        public ConcatenatedList(IReadOnlyList<MidoraId>[] parts)
        {
            _parts = parts;
            _ends = new int[parts.Length];
            int count = 0;
            for (int index = 0; index < parts.Length; index++)
            {
                count = checked(count + parts[index].Count);
                _ends[index] = count;
            }
            Count = count;
        }

        public int Count { get; }

        public MidoraId this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                int partIndex = Array.BinarySearch(_ends, checked(index + 1));
                if (partIndex < 0) partIndex = ~partIndex;
                int start = partIndex == 0 ? 0 : _ends[partIndex - 1];
                return _parts[partIndex][index - start];
            }
        }

        public IEnumerator<MidoraId> GetEnumerator()
        {
            foreach (IReadOnlyList<MidoraId> part in _parts)
            {
                for (int index = 0; index < part.Count; index++)
                    yield return part[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private Segment FindSegment(int index)
    {
        int low = 0;
        int high = _segments.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            Segment candidate = _segments[middle];
            if (index < candidate.StartIndex)
            {
                high = middle - 1;
            }
            else if (index >= candidate.StartIndex + candidate.Count)
            {
                low = middle + 1;
            }
            else
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The compact selection index is not covered.");
    }

    private long DecodeLiteralValue(Segment segment, int relativeIndex)
    {
        long value = segment.FirstValue;
        int offset = segment.PayloadOffset;
        for (int index = 1; index <= relativeIndex; index++)
            value = checked(value + DecodeSignedVarInt(_encodedDeltas, ref offset));
        return value;
    }

    private static bool TryMeasureRun(
        IReadOnlyList<MidoraId> values,
        int startIndex,
        out long step,
        out int length)
    {
        step = 0;
        length = 0;
        if (values.Count - startIndex < MinimumRunLength) return false;

        step = checked(values[startIndex + 1].Value - values[startIndex].Value);
        if (step is < -1 or > 1) return false;
        if (checked(values[startIndex + 2].Value - values[startIndex + 1].Value) != step)
            return false;

        length = MinimumRunLength;
        while (startIndex + length < values.Count
            && checked(values[startIndex + length].Value
                - values[startIndex + length - 1].Value) == step)
        {
            length++;
        }
        return true;
    }

    private static long MeasureLiteralPayload(
        IReadOnlyList<MidoraId> values,
        int startIndex,
        int count)
    {
        long result = 0;
        long previous = values[startIndex].Value;
        for (int index = 1; index < count; index++)
        {
            long current = values[startIndex + index].Value;
            result = checked(result + GetVarIntLength(ZigZagEncode(
                checked(current - previous))));
            previous = current;
        }
        return result;
    }

    private static void EncodeLiteralPayload(
        IReadOnlyList<MidoraId> values,
        Segment segment,
        Span<byte> destination)
    {
        int offset = segment.PayloadOffset;
        long previous = values[segment.StartIndex].Value;
        for (int index = 1; index < segment.Count; index++)
        {
            long current = values[segment.StartIndex + index].Value;
            WriteVarInt(
                destination,
                ref offset,
                ZigZagEncode(checked(current - previous)));
            previous = current;
        }
        if (offset != segment.PayloadOffset + segment.PayloadLength)
            throw new InvalidOperationException("The compact selection payload size changed.");
    }

    private static int GetVarIntLength(ulong value)
    {
        int result = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            result++;
        }
        return result;
    }

    private static void WriteVarInt(Span<byte> destination, ref int offset, ulong value)
    {
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[offset++] = (byte)value;
    }

    private static long DecodeSignedVarInt(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if ((uint)offset >= (uint)source.Length || shift >= 70)
                throw new InvalidOperationException("The compact selection payload is invalid.");
            byte current = source[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) break;
            shift += 7;
        }
        return (long)(value >> 1) ^ -((long)value & 1);
    }

    private static ulong ZigZagEncode(long value) =>
        unchecked(((ulong)value << 1) ^ (ulong)(value >> 63));

    private static MidoraId FromRawValue(long value) =>
        value == 0 ? default : MidoraId.FromSequence(value);

    private readonly record struct Segment(
        int StartIndex,
        int Count,
        long FirstValue,
        long Step,
        int PayloadOffset,
        int PayloadLength,
        bool IsRun);

    public struct Enumerator : IEnumerator<MidoraId>
    {
        private readonly CompactMidoraIdList _owner;
        private int _segmentIndex;
        private int _relativeIndex;
        private int _payloadOffset;
        private long _currentValue;
        private bool _hasCurrent;

        internal Enumerator(CompactMidoraIdList owner)
        {
            _owner = owner;
            _segmentIndex = -1;
            _relativeIndex = -1;
            _payloadOffset = 0;
            _currentValue = 0;
            _hasCurrent = false;
        }

        public MidoraId Current => _hasCurrent
            ? FromRawValue(_currentValue)
            : throw new InvalidOperationException();

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_segmentIndex < 0)
            {
                if (_owner._segments.Length == 0) return false;
                StartSegment(0);
                return true;
            }

            Segment segment = _owner._segments[_segmentIndex];
            if (_relativeIndex + 1 < segment.Count)
            {
                _relativeIndex++;
                _currentValue = segment.IsRun
                    ? checked(_currentValue + segment.Step)
                    : checked(_currentValue
                        + DecodeSignedVarInt(_owner._encodedDeltas, ref _payloadOffset));
                _hasCurrent = true;
                return true;
            }

            int nextSegment = _segmentIndex + 1;
            if (nextSegment >= _owner._segments.Length)
            {
                _hasCurrent = false;
                return false;
            }
            StartSegment(nextSegment);
            return true;
        }

        public void Reset()
        {
            _segmentIndex = -1;
            _relativeIndex = -1;
            _payloadOffset = 0;
            _currentValue = 0;
            _hasCurrent = false;
        }

        public void Dispose()
        {
        }

        private void StartSegment(int segmentIndex)
        {
            Segment segment = _owner._segments[segmentIndex];
            _segmentIndex = segmentIndex;
            _relativeIndex = 0;
            _payloadOffset = segment.PayloadOffset;
            _currentValue = segment.FirstValue;
            _hasCurrent = true;
        }
    }
}
