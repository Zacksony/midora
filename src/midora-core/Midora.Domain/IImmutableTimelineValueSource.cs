namespace Midora.Domain;

/// <summary>
/// Replayable immutable values in formal ordinal order. A returned page is
/// valid while its ReadOnlyMemory is held; implementations may retain only a
/// bounded decoded-page cache and own the backing-store lifetime.
/// </summary>
public interface IImmutableTimelineValueSource<T> : IReadOnlyList<T>
{
    int PageCapacity { get; }
    ReadOnlyMemory<T> ReadPage(int pageIndex);
    bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> values)
    {
        values = default;
        return false;
    }
    bool TryReadCachedValue(int index, out T value)
    {
        if (TryReadCachedPage(index / PageCapacity, out ReadOnlyMemory<T> page))
        {
            value = page.Span[index % PageCapacity];
            return true;
        }
        value = default!;
        return false;
    }
}

public interface IIndexedImmutableTimelineValueSource<T> : IImmutableTimelineValueSource<T>
{
    bool TryFindOrdinalById(MidoraId id, out int ordinal);
    IImmutableTimelineIdIndex? DetachedIdIndex => null;
}

/// <summary>Stable-ID lookup independent of the musical value storage lifetime.</summary>
public interface IImmutableTimelineIdIndex
{
    bool TryFindOrdinalById(MidoraId id, out int ordinal);
    /// <summary>A completed cache attached to an already published source must
    /// outlive cancellation of the edit that first requested the cache.</summary>
    void RetainForSourceLifetime() { }
}

public readonly record struct TimelineIdOrdinal(MidoraId Id, int Ordinal);

/// <summary>Detached, independently owned metadata stores built within the active edit budget.</summary>
public interface IImmutableTimelineOrdinalIndexBuilder
{
    IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken cancellationToken);
}

public interface IImmutableTimelineEditAddressSource : IImmutableTimelineOrdinalIndexBuilder
{
    IImmutableTimelineValueSource<int> CreateDeletedOrdinals(CancellationToken cancellationToken);
}

/// <summary>A replacement or deletion at a frozen source formal ordinal.</summary>
public readonly record struct TimelineValueEdit<T>(int Ordinal, bool IsDeleted, T Replacement);

/// <summary>Replace one frozen source record by an ordinal range in a separate immutable value source.</summary>
public readonly record struct TimelineValueSplice(int Ordinal, int FirstValue, int Count);
