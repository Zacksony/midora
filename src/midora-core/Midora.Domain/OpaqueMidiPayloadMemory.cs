using System.Runtime.InteropServices;

namespace Midora.Domain;

/// <summary>Counts retained array capacity, including a sliced array's hidden
/// prefix/suffix. Payloads provided by formal content sources are immutable.</summary>
public static class OpaqueMidiPayloadMemory
{
    public static long GetRetainedBytes(ReadOnlyMemory<byte> payload) =>
        MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is { } array
            ? array.LongLength : payload.Length;

    public static long GetRetainedAllocatedBytes(ReadOnlyMemory<byte> payload) =>
        24L + ((GetRetainedBytes(payload) + 7) & ~7L);

    public static long GetRetainedBytes(IEnumerable<OpaqueMidiEventValue> values)
        => CountArrays(values, includeArrayOverhead: false);

    /// <summary>Payload byte-array storage on the fixed x64 runtime: 24-byte
    /// header and 8-byte-aligned capacity. Shared backing arrays count once.</summary>
    public static long GetRetainedAllocatedBytes(IEnumerable<OpaqueMidiEventValue> values)
        => CountArrays(values, includeArrayOverhead: true);

    private static long CountArrays(IEnumerable<OpaqueMidiEventValue> values, bool includeArrayOverhead)
    {
        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (OpaqueMidiEventValue value in values)
        {
            if (MemoryMarshal.TryGetArray(value.Payload, out ArraySegment<byte> segment)
                && segment.Array is { } array)
            {
                if (arrays.Add(array)) bytes = checked(bytes + (includeArrayOverhead
                    ? 24L + ((array.LongLength + 7) & ~7L) : array.LongLength));
            }
            else bytes = checked(bytes + value.Payload.Length);
        }
        return bytes;
    }
}
