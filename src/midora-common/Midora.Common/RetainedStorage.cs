using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Midora.Common;

/// <summary>Preparation-time accounting only. Never called by an audio callback.</summary>
public interface IRetainedStorageSource
{
    void CollectRetainedStorage(RetainedStorageCollector collector);
}

public readonly record struct RetainedStoragePart(object Identity, long Bytes);

/// <summary>
/// Explicit ownership traversal, not a heap walker. Arrays use their real capacity;
/// object/dictionary headers use documented conservative x64 estimates. Shared
/// backing storage is charged once, without enumerating paged musical records.
/// </summary>
public sealed class RetainedStorageCollector
{
    private readonly Dictionary<object, long> _parts = new(ReferenceEqualityComparer.Instance);

    public bool Add(object? identity, long bytes)
    {
        if (identity is null) return false;
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return _parts.TryAdd(identity, checked((bytes + 7) & ~7L));
    }

    public bool Array<T>(T[] array) => Add(array,
        checked(24L + (long)array.Length * Unsafe.SizeOf<T>()));

    public void Text(string? text)
    {
        if (text is not null) Add(text, 24L + 2L * text.Length);
    }

    public void Bytes(ReadOnlyMemory<byte> bytes)
    {
        if (MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment))
        {
            if (segment.Array is { } array) Array(array);
        }
        else if (!bytes.IsEmpty)
        {
            // Current canonical opaque storage is array-backed. Keep custom
            // memory-owner implementations accounted rather than silently zero.
            Add(bytes, bytes.Length + 32L);
        }
    }

    public void Dictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        int capacity = dictionary is Dictionary<TKey, TValue> concrete
            ? concrete.EnsureCapacity(0) : checked(dictionary.Count * 2);
        Add(dictionary, 128L + (long)capacity *
            (16 + Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()));
    }

    public RetainedStoragePart[] ToArray() => _parts
        .Select(value => new RetainedStoragePart(value.Key, value.Value)).ToArray();
}
