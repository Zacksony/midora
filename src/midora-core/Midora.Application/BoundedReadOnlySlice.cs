using System.Collections;

namespace Midora.Application;

/// <summary>Immutable indexed view; never copies the selected owner-sized payload.</summary>
internal sealed class BoundedReadOnlySlice<T>(IReadOnlyList<T> source, int start, int count) : IReadOnlyList<T>
{
    public int Count { get; } = count;
    public T this[int index] => (uint)index < (uint)Count ? source[checked(start + index)]
        : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
