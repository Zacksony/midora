using System.Collections;
using System.Collections.Immutable;
using System.Numerics;

namespace Midora.Domain;

public readonly record struct DirectMidiNoteValue(
    MidoraId Id,
    long StartTick,
    long LengthTicks,
    int Key,
    int NoteOnVelocity,
    int NoteOffVelocity,
    long NoteOnOrder,
    long NoteOffOrder);

public readonly record struct DirectMidiChannelEventValue(
    MidoraId Id,
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1,
    int Data2,
    long Order);

public readonly record struct OpaqueMidiEventValue(
    MidoraId Id,
    long Tick,
    OpaqueMidiEventKind Kind,
    byte MetaType,
    ReadOnlyMemory<byte> Payload,
    long Order);

/// <summary>
/// Persistent fixed-size chunks for the formal IList tail of an edited Pure
/// MIDI collection. A point edit copies one small chunk plus the affected path
/// through the persistent chunk tree; a batch updates every touched chunk once.
/// Structural insert/remove shares untouched chunks and only repacks chunks
/// intersected by the splice, so source capture never has to clone the tail.
/// </summary>
internal sealed class PersistentFormalValueSequence<T>
{
    // Keep a point edit bounded without paying one immutable-tree node per
    // imported record. 256 values are small enough to copy for an interactive
    // edit, while a million-value overlay still needs fewer than four thousand
    // persistent tree leaves.
    private const int ChunkCapacity = 256;
    private readonly ImmutableList<ImmutableArray<T>> _chunks;
    private readonly Lazy<int[]> _chunkStarts;

    private PersistentFormalValueSequence(
        ImmutableList<ImmutableArray<T>> chunks,
        int count)
    {
        _chunks = chunks;
        Count = count;
        _chunkStarts = new(() =>
        {
            int[] starts = new int[chunks.Count];
            int next = 0;
            for (int index = 0; index < starts.Length; index++)
            {
                starts[index] = next;
                next = checked(next + chunks[index].Length);
            }
            return starts;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static PersistentFormalValueSequence<T> Empty { get; } =
        new(ImmutableList<ImmutableArray<T>>.Empty, 0);

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int chunkIndex = FindChunk(index);
            int localIndex = index - _chunkStarts.Value[chunkIndex];
            return _chunks[chunkIndex][localIndex];
        }
    }

    public T[] CopyRange(int firstIndex, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (firstIndex > Count - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return [];
        T[] result = new T[count];
        int source = firstIndex;
        int destination = 0;
        while (destination < result.Length)
        {
            int chunkIndex = FindChunk(source);
            int localIndex = source - _chunkStarts.Value[chunkIndex];
            ImmutableArray<T> chunk = _chunks[chunkIndex];
            int take = Math.Min(chunk.Length - localIndex, result.Length - destination);
            chunk.AsSpan(localIndex, take).CopyTo(result.AsSpan(destination, take));
            source += take;
            destination += take;
        }
        return result;
    }

    private int FindChunk(int index)
    {
        int found = Array.BinarySearch(_chunkStarts.Value, index);
        return found < 0 ? ~found - 1 : found;
    }

    public static PersistentFormalValueSequence<T> Create<TSource>(
        IReadOnlyList<TSource> source,
        Func<TSource, T> convert)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(convert);
        if (source.Count == 0) return Empty;
        var chunks = ImmutableList.CreateBuilder<ImmutableArray<T>>();
        for (int first = 0; first < source.Count; first += ChunkCapacity)
        {
            int count = Math.Min(ChunkCapacity, source.Count - first);
            var chunk = ImmutableArray.CreateBuilder<T>(count);
            for (int offset = 0; offset < count; offset++)
                chunk.Add(convert(source[first + offset]));
            chunks.Add(chunk.MoveToImmutable());
        }
        return new(chunks.ToImmutable(), source.Count);
    }

    public PersistentFormalValueSequence<T> AppendRange<TSource>(
        IReadOnlyList<TSource> source,
        int firstIndex,
        Func<TSource, T> convert)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(convert);
        if ((uint)firstIndex > (uint)source.Count)
            throw new ArgumentOutOfRangeException(nameof(firstIndex));
        if (firstIndex == source.Count) return this;

        var chunks = _chunks.ToBuilder();
        int index = firstIndex;
        if (chunks.Count != 0 && chunks[^1].Length < ChunkCapacity)
        {
            ImmutableArray<T> previous = chunks[^1];
            int take = Math.Min(ChunkCapacity - previous.Length, source.Count - index);
            var merged = ImmutableArray.CreateBuilder<T>(previous.Length + take);
            merged.AddRange(previous);
            for (int offset = 0; offset < take; offset++)
                merged.Add(convert(source[index++]));
            chunks[^1] = merged.MoveToImmutable();
        }
        while (index < source.Count)
        {
            int take = Math.Min(ChunkCapacity, source.Count - index);
            var chunk = ImmutableArray.CreateBuilder<T>(take);
            for (int offset = 0; offset < take; offset++)
                chunk.Add(convert(source[index++]));
            chunks.Add(chunk.MoveToImmutable());
        }
        return new(chunks.ToImmutable(), checked(Count + source.Count - firstIndex));
    }

    public PersistentFormalValueSequence<T> Append(T value)
    {
        var chunks = _chunks.ToBuilder();
        if (chunks.Count != 0 && chunks[^1].Length < ChunkCapacity)
        {
            ImmutableArray<T> previous = chunks[^1];
            var merged = ImmutableArray.CreateBuilder<T>(previous.Length + 1);
            merged.AddRange(previous);
            merged.Add(value);
            chunks[^1] = merged.MoveToImmutable();
        }
        else
        {
            chunks.Add(ImmutableArray.Create(value));
        }
        return new(chunks.ToImmutable(), checked(Count + 1));
    }

    public PersistentFormalValueSequence<T> ReplaceBatch(
        IReadOnlyDictionary<int, T> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return this;
        KeyValuePair<int, T>[] ordered = replacements
            .OrderBy(static pair => pair.Key)
            .ToArray();
        foreach ((int index, _) in ordered)
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(replacements));
        }

        var chunks = _chunks.ToBuilder();
        int replacementIndex = 0;
        int first = 0;
        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            ImmutableArray<T> source = chunks[chunkIndex];
            int end = checked(first + source.Length);
            if (replacementIndex < ordered.Length && ordered[replacementIndex].Key < end)
            {
                var changed = source.ToBuilder();
                while (replacementIndex < ordered.Length
                    && ordered[replacementIndex].Key < end)
                {
                    KeyValuePair<int, T> replacement = ordered[replacementIndex++];
                    changed[replacement.Key - first] = replacement.Value;
                }
                chunks[chunkIndex] = changed.MoveToImmutable();
            }
            first = end;
        }
        if (replacementIndex != ordered.Length)
            throw new InvalidOperationException("The formal sequence chunk index is inconsistent.");
        return new(chunks.ToImmutable(), Count);
    }

    public PersistentFormalValueSequence<T> Insert(int index, T value) =>
        InsertAtFinalIndices([new KeyValuePair<int, T>(index, value)]);

    public PersistentFormalValueSequence<T> RemoveIndices(
        IReadOnlyCollection<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count == 0) return this;
        int[] ordered = [.. indices.Order()];
        for (int index = 0; index < ordered.Length; index++)
        {
            if ((uint)ordered[index] >= (uint)Count
                || index != 0 && ordered[index - 1] == ordered[index])
            {
                throw new ArgumentOutOfRangeException(nameof(indices));
            }
        }

        var chunks = ImmutableList.CreateBuilder<ImmutableArray<T>>();
        int removalIndex = 0;
        int first = 0;
        foreach (ImmutableArray<T> source in _chunks)
        {
            int end = checked(first + source.Length);
            if (removalIndex >= ordered.Length || ordered[removalIndex] >= end)
            {
                chunks.Add(source);
            }
            else
            {
                var retained = ImmutableArray.CreateBuilder<T>(source.Length);
                for (int offset = 0; offset < source.Length; offset++)
                {
                    int absoluteIndex = first + offset;
                    if (removalIndex < ordered.Length
                        && ordered[removalIndex] == absoluteIndex)
                    {
                        removalIndex++;
                    }
                    else
                    {
                        retained.Add(source[offset]);
                    }
                }
                if (retained.Count != 0) chunks.Add(retained.ToImmutable());
            }
            first = end;
        }
        if (removalIndex != ordered.Length)
            throw new InvalidOperationException("The formal sequence removal index is inconsistent.");
        return new(chunks.ToImmutable(), checked(Count - ordered.Length));
    }

    /// <summary>
    /// Inserts values at their indexes in the final sequence. Unaffected chunks
    /// remain shared; only chunks cut by an insertion are repacked.
    /// </summary>
    public PersistentFormalValueSequence<T> InsertAtFinalIndices(
        IReadOnlyList<KeyValuePair<int, T>> insertions)
    {
        ArgumentNullException.ThrowIfNull(insertions);
        if (insertions.Count == 0) return this;
        KeyValuePair<int, T>[] ordered = insertions as KeyValuePair<int, T>[]
            ?? [.. insertions];
        bool alreadyOrdered = true;
        for (int index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Key <= ordered[index].Key) continue;
            alreadyOrdered = false;
            break;
        }
        if (!alreadyOrdered)
        {
            if (ReferenceEquals(ordered, insertions)) ordered = [.. ordered];
            Array.Sort(ordered, static (left, right) => left.Key.CompareTo(right.Key));
        }
        int finalCount = checked(Count + ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            if ((uint)ordered[index].Key >= (uint)finalCount
                || index != 0 && ordered[index - 1].Key == ordered[index].Key)
            {
                throw new ArgumentOutOfRangeException(nameof(insertions));
            }
        }

        var chunks = ImmutableList.CreateBuilder<ImmutableArray<T>>();
        int insertionIndex = 0;
        int finalIndex = 0;
        foreach (ImmutableArray<T> source in _chunks)
        {
            int nextInsertion = insertionIndex < ordered.Length
                ? ordered[insertionIndex].Key
                : int.MaxValue;
            if (nextInsertion >= checked(finalIndex + source.Length))
            {
                chunks.Add(source);
                finalIndex += source.Length;
                continue;
            }

            List<T> merged = new(source.Length + Math.Min(ordered.Length - insertionIndex, ChunkCapacity));
            foreach (T value in source)
            {
                while (insertionIndex < ordered.Length
                    && ordered[insertionIndex].Key == finalIndex)
                {
                    merged.Add(ordered[insertionIndex++].Value);
                    finalIndex++;
                }
                if (insertionIndex < ordered.Length
                    && ordered[insertionIndex].Key < finalIndex)
                {
                    throw new InvalidOperationException("The formal sequence insertion order is inconsistent.");
                }
                merged.Add(value);
                finalIndex++;
            }
            AppendPacked(chunks, merged);
        }

        if (insertionIndex < ordered.Length)
        {
            List<T> tail = new(ordered.Length - insertionIndex);
            while (insertionIndex < ordered.Length)
            {
                if (ordered[insertionIndex].Key != finalIndex)
                    throw new InvalidOperationException("The formal sequence insertion index is inconsistent.");
                tail.Add(ordered[insertionIndex++].Value);
                finalIndex++;
            }
            AppendPacked(chunks, tail);
        }
        if (finalIndex != finalCount)
            throw new InvalidOperationException("The formal sequence final count is inconsistent.");
        return new(chunks.ToImmutable(), finalCount);
    }

    public IEnumerable<T> Enumerate(CancellationToken cancellationToken = default)
    {
        int visited = 0;
        foreach (ImmutableArray<T> chunk in _chunks)
        {
            foreach (T value in chunk)
            {
                if ((visited++ & 0xff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
    }

    private static void AppendPacked(
        ImmutableList<ImmutableArray<T>>.Builder destination,
        IReadOnlyList<T> values)
    {
        for (int first = 0; first < values.Count; first += ChunkCapacity)
        {
            int count = Math.Min(ChunkCapacity, values.Count - first);
            var chunk = ImmutableArray.CreateBuilder<T>(count);
            for (int offset = 0; offset < count; offset++)
                chunk.Add(values[first + offset]);
            destination.Add(chunk.MoveToImmutable());
        }
    }
}

internal sealed record DirectMidiNoteFormalSequenceSnapshot(
    IPureMidiSegmentContentSource? Source,
    bool ClearsSource,
    ImmutableHashSet<MidoraId> RemovedSourceIds,
    ImmutableDictionary<MidoraId, DirectMidiNoteValue> Replacements,
    PersistentFormalValueSequence<DirectMidiNoteValue> Added,
    int Count,
    long Generation)
{
    public ImmutableDictionary<MidoraId, int> AddedIndices { get; init; } = ImmutableDictionary<MidoraId, int>.Empty;
}

internal sealed record DirectMidiChannelEventFormalSequenceSnapshot(
    IPureMidiSegmentContentSource? Source,
    bool ClearsSource,
    ImmutableHashSet<MidoraId> RemovedSourceIds,
    ImmutableDictionary<MidoraId, DirectMidiChannelEventValue> Replacements,
    PersistentFormalValueSequence<DirectMidiChannelEventValue> Added,
    int Count,
    long Generation)
{
    public ImmutableDictionary<MidoraId, int> AddedIndices { get; init; } = ImmutableDictionary<MidoraId, int>.Empty;
}

internal sealed record OpaqueMidiEventFormalSequenceSnapshot(
    IPureMidiSegmentContentSource? Source,
    bool ClearsSource,
    ImmutableHashSet<MidoraId> RemovedSourceIds,
    ImmutableDictionary<MidoraId, OpaqueMidiEventValue> Replacements,
    PersistentFormalValueSequence<OpaqueMidiEventValue> Added,
    int Count,
    long Generation)
{
    public ImmutableDictionary<MidoraId, int> AddedIndices { get; init; } = ImmutableDictionary<MidoraId, int>.Empty;
}

public readonly record struct DirectMidiNoteSourceMatch(
    int Index,
    DirectMidiNoteValue Value);

public readonly record struct DirectMidiChannelEventSourceMatch(
    int Index,
    DirectMidiChannelEventValue Value);

public readonly record struct OpaqueMidiEventSourceMatch(
    int Index,
    OpaqueMidiEventValue Value);

public readonly record struct DirectMidiNoteMatch(
    int Index,
    DirectMidiNote Value);

public readonly record struct DirectMidiNoteStartKey(long Tick, int Key);

public readonly record struct DirectMidiEventStartKey(
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1);

public readonly record struct DirectMidiChannelEventMatch(
    int Index,
    DirectMidiChannelEvent Value);

public readonly record struct OpaqueMidiEventMatch(
    int Index,
    OpaqueMidiEvent Value);

public readonly record struct PureMidiContentRangeSummary(
    long MinimumTick,
    long MaximumTick,
    int RecordCount,
    int MinimumKey = 0,
    int MaximumKey = 127);

public interface IPureMidiContentOverviewSource
{
    IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries();

    IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries() => [];

    IEnumerable<PureMidiContentRangeSummary> GetOpaqueEventRangeSummaries() => [];

    bool TryAccumulateNoteStartColumns(
        long extent,
        Span<byte> destination,
        IReadOnlySet<MidoraId>? excludedIds) => false;

    bool TryAccumulateNoteRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumKey,
        int maximumKey,
        Span<TimelineRasterColumnSummary> destination,
        IReadOnlySet<MidoraId>? excludedIds,
        out int sourceWorkCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceWorkCount = 0;
        return false;
    }

    bool TryAccumulateChannelEventColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns,
        IReadOnlySet<MidoraId>? excludedIds) => false;

    bool TryAccumulateOpaqueEventColumns(
        long extent,
        Span<byte> destination,
        IReadOnlySet<MidoraId>? excludedIds) => false;
}

/// <summary>
/// Optional immutable bounds supplied by an out-of-core Pure MIDI source.
/// Consumers use these bounds to reject empty range queries without walking
/// every page descriptor. Values are conservative exclusive upper bounds.
/// </summary>
public interface IPureMidiContentBoundsSource
{
    long MaximumNoteEndTick { get; }
}

/// <summary>
/// Optional metadata-only fingerprints for immutable Pure MIDI page ranges.
/// Implementations must not decode page payloads. Presentation caches use these
/// values on the UI thread to identify a local tile without enumerating millions
/// of MIDI records.
/// </summary>
public interface IPureMidiContentRangeFingerprintSource
{
    ulong GetNoteRangeFingerprint(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127);

    ulong GetChannelEventRangeFingerprint(long startTick, long endTick);

    ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick);
}

/// <summary>
/// Optional cache-only access to an immutable out-of-core source. Implementations
/// must return <see langword="false"/> without modifying the destination when any
/// required page is not decoded. Prefetch methods may perform file IO, decompression,
/// and checksum validation and therefore must only be called from a background thread.
/// </summary>
public interface IPureMidiCachedContentSource
{
    bool TryQueryCachedNotes(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        List<DirectMidiNoteValue> destination);

    bool TryQueryCachedChannelEvents(
        long startTick,
        long endTick,
        List<DirectMidiChannelEventValue> destination);

    bool TryQueryCachedOpaqueEvents(
        long startTick,
        long endTick,
        List<OpaqueMidiEventValue> destination);

    bool TryQueryCachedNotesByIds(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiNoteSourceMatch> destination);

    bool TryQueryCachedChannelEventsByIds(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiChannelEventSourceMatch> destination);

    bool TryQueryCachedOpaqueEventsByIds(
        IReadOnlySet<MidoraId> ids,
        List<OpaqueMidiEventSourceMatch> destination);

    void PrefetchNotes(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        CancellationToken cancellationToken);

    void PrefetchChannelEvents(
        long startTick,
        long endTick,
        CancellationToken cancellationToken);

    void PrefetchOpaqueEvents(
        long startTick,
        long endTick,
        CancellationToken cancellationToken);

    void PrefetchNotesByIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken);

    void PrefetchChannelEventsByIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken);

    void PrefetchOpaqueEventsByIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken);
}

internal static class PureMidiOverviewProjection
{
    public static void Validate(long extent, Span<byte> destination)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (destination.IsEmpty) return;
    }

    public static void Validate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        Validate(extent, noteStartColumns);
        if (noteStartColumns.Length != eventColumns.Length)
        {
            throw new ArgumentException(
                "Pure MIDI overview channels must have equal widths.");
        }
    }

    public static int Column(long tick, long extent, int width)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (tick <= 0) return 0;
        double projected = tick / (double)extent * width;
        return projected >= width ? width - 1 : (int)projected;
    }

    public static void Mark(Span<byte> destination, long tick, long extent)
    {
        if (destination.IsEmpty) return;
        destination[Column(tick, extent, destination.Length)] = 1;
    }

    public static void Mark(
        DirectMidiChannelEventKind kind,
        int data2,
        long tick,
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (kind == DirectMidiChannelEventKind.NoteOn && data2 > 0)
        {
            Mark(noteStartColumns, tick, extent);
        }
        else if (kind is not (DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff))
        {
            Mark(eventColumns, tick, extent);
        }
    }
}

public interface IPureMidiSegmentContentSource
{
    int NoteCount { get; }
    int ChannelEventCount { get; }
    int OpaqueEventCount { get; }
    string ContentFingerprint { get; }

    DirectMidiNoteValue GetNote(int index);
    DirectMidiChannelEventValue GetChannelEvent(int index);
    OpaqueMidiEventValue GetOpaqueEvent(int index);
    int FindNoteIndex(MidoraId id);
    int FindChannelEventIndex(MidoraId id);
    int FindOpaqueEventIndex(MidoraId id);

    IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindNoteIndex(id);
            if (index >= 0) yield return new(index, GetNote(index));
        }
    }

    IEnumerable<DirectMidiNoteSourceMatch> QueryNotesAtStarts(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (DirectMidiNoteStartKey key in keys)
        {
            long endTick = key.Tick == long.MaxValue ? long.MaxValue : key.Tick + 1;
            foreach (DirectMidiNoteValue value in QueryNotes(
                key.Tick,
                endTick,
                key.Key,
                key.Key))
            {
                if (value.StartTick == key.Tick)
                {
                    int index = FindNoteIndex(value.Id);
                    if (index >= 0) yield return new(index, value);
                }
            }
        }
    }

    IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindChannelEventIndex(id);
            if (index >= 0) yield return new(index, GetChannelEvent(index));
        }
    }

    IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsAtStarts(
        IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (DirectMidiEventStartKey key in keys)
        {
            IEnumerable<DirectMidiChannelEventValue> candidates = key.Tick == long.MaxValue
                ? Enumerable.Range(0, ChannelEventCount).Select(GetChannelEvent)
                : QueryChannelEvents(key.Tick, key.Tick + 1);
            foreach (DirectMidiChannelEventValue value in candidates)
            {
                int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                    or DirectMidiChannelEventKind.PolyphonicKeyPressure
                    or DirectMidiChannelEventKind.NoteOn
                    or DirectMidiChannelEventKind.NoteOff
                        ? value.Data1
                        : 0;
                if (value.Tick == key.Tick && value.Kind == key.Kind && selector == key.Data1)
                {
                    int index = FindChannelEventIndex(value.Id);
                    if (index >= 0) yield return new(index, value);
                }
            }
        }
    }

    IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindOpaqueEventIndex(id);
            if (index >= 0) yield return new(index, GetOpaqueEvent(index));
        }
    }

    IEnumerable<DirectMidiNoteValue> QueryNotes(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127);

    IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
        long startTick,
        long endTick);

    IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(
        long startTick,
        long endTick);
}

public interface IPureMidiPlaybackEndpointSource
{
    long CountNoteStarts(long startTick, long endTick, CancellationToken cancellationToken = default)
    {
        long count = 0;
        foreach (DirectMidiNoteValue value in QueryNoteStarts(startTick, endTick))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }

    IEnumerable<DirectMidiNoteValue> QueryNoteStarts(
        long startTick,
        long endTick);

    IEnumerable<DirectMidiNoteValue> QueryNoteEnds(
        long startTick,
        long endTick);

    IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick);

    IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(
        long startTick,
        long endTick);
}

internal interface IDirectMidiNoteChangeSink
{
    void OnChanged(DirectMidiNote value);
}

internal interface IDirectMidiChannelEventChangeSink
{
    void OnChanged(DirectMidiChannelEvent value);
}

internal interface IOpaqueMidiEventChangeSink
{
    void OnChanged(OpaqueMidiEvent value);
}

/// <summary>
/// Immutable union view over the two formal source-exclusion roots. Reusing
/// their persistent roots avoids copying every removed/replaced source ID for
/// each presentation snapshot.
/// </summary>
internal interface IPureMidiIdRangeSet
{
    bool MayContain(MidoraId minimumId, MidoraId maximumId);
}

internal interface IPureMidiOrdinalRangeSet
{
    bool HasUnknownOrdinals { get; }

    bool MayContainOrdinalRange(int firstOrdinal, int count);

    bool ContainsAllOrdinals(int firstOrdinal, int count);

    ArraySegment<int> GetOrdinalsInRange(int firstOrdinal, int count);

    bool ContainsUnknownOrdinalId(MidoraId id) =>
        HasUnknownOrdinals && ((IReadOnlySet<MidoraId>)this).Contains(id);
}

/// <summary>
/// A source-page exclusion mask that can be read without decoding or reading
/// spill data on the caller. False means Pending, never an empty exclusion set.
/// </summary>
internal interface IPureMidiCachedOrdinalRangeSet : IPureMidiOrdinalRangeSet
{
    bool TryGetOrdinalsInRange(int firstOrdinal, int count, out ArraySegment<int> ordinals);
}

internal interface IPureMidiNoteExclusionAwareSource
{
    IEnumerable<DirectMidiNoteValue> QueryNotesExcluding(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        IReadOnlySet<MidoraId> excludedIds);

    bool TryQueryCachedNotesExcluding(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        IReadOnlySet<MidoraId> excludedIds,
        List<DirectMidiNoteValue> destination);

    void PrefetchNotesExcluding(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        IReadOnlySet<MidoraId> excludedIds,
        CancellationToken cancellationToken);
}

internal sealed class PureMidiSourceExclusionSet<TValue> :
    IReadOnlySet<MidoraId>,
    IPureMidiIdRangeSet,
    IPureMidiOrdinalRangeSet
{
    private readonly ImmutableHashSet<MidoraId> _removed;
    private readonly ImmutableDictionary<MidoraId, TValue> _replacements;
    private readonly Lazy<long[]> _sortedIds;
    private readonly int[] _sortedOrdinals;
    private readonly bool _hasUnknownOrdinals;

    public PureMidiSourceExclusionSet(
        ImmutableHashSet<MidoraId> removed,
        ImmutableDictionary<MidoraId, TValue> replacements,
        IReadOnlyDictionary<MidoraId, int>? sourceIndices = null)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(replacements);
        _removed = removed;
        _replacements = replacements;
        _sortedIds = new(
            CreateSortedIds,
            LazyThreadSafetyMode.ExecutionAndPublication);
        (_sortedOrdinals, _hasUnknownOrdinals) = CreateSortedOrdinals(sourceIndices);
    }

    public int Count => checked(_removed.Count + _replacements.Count);

    public bool HasUnknownOrdinals => _hasUnknownOrdinals;

    public bool Contains(MidoraId item) =>
        _removed.Contains(item) || _replacements.ContainsKey(item);

    public bool MayContain(MidoraId minimumId, MidoraId maximumId)
    {
        if (Count == 0 || minimumId.CompareTo(maximumId) > 0) return false;

        long[] sortedIds = _sortedIds.Value;
        int index = Array.BinarySearch(sortedIds, minimumId.Value);
        if (index < 0) index = ~index;
        return index < sortedIds.Length && sortedIds[index] <= maximumId.Value;
    }

    public bool MayContainOrdinalRange(int firstOrdinal, int count)
    {
        if (firstOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(firstOrdinal));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return false;
        return _hasUnknownOrdinals || CountOrdinals(firstOrdinal, count) != 0;
    }

    public bool ContainsAllOrdinals(int firstOrdinal, int count)
    {
        if (firstOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(firstOrdinal));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return count != 0 && CountOrdinals(firstOrdinal, count) == count;
    }

    public ArraySegment<int> GetOrdinalsInRange(int firstOrdinal, int count)
    {
        if (firstOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(firstOrdinal));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int endOrdinal = checked(firstOrdinal + count);
        int first = LowerBound(_sortedOrdinals, firstOrdinal);
        int end = LowerBound(_sortedOrdinals, endOrdinal);
        return new(_sortedOrdinals, first, end - first);
    }

    public IEnumerator<MidoraId> GetEnumerator()
    {
        foreach (MidoraId id in _removed) yield return id;
        foreach (MidoraId id in _replacements.Keys)
        {
            if (!_removed.Contains(id)) yield return id;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<MidoraId> other) =>
        Materialize().IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<MidoraId> other) =>
        Materialize().IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<MidoraId> other) => Materialize().IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<MidoraId> other) => Materialize().IsSupersetOf(other);

    public bool Overlaps(IEnumerable<MidoraId> other) => other.Any(Contains);

    public bool SetEquals(IEnumerable<MidoraId> other) => Materialize().SetEquals(other);

    private HashSet<MidoraId> Materialize() => [.. this];

    private long[] CreateSortedIds()
    {
        long[] result = new long[Count];
        int index = 0;
        foreach (MidoraId id in _removed) result[index++] = id.Value;
        foreach (MidoraId id in _replacements.Keys)
        {
            if (!_removed.Contains(id)) result[index++] = id.Value;
        }
        if (index != result.Length) Array.Resize(ref result, index);
        Array.Sort(result);
        return result;
    }

    private (int[] Ordinals, bool HasUnknown) CreateSortedOrdinals(
        IReadOnlyDictionary<MidoraId, int>? sourceIndices)
    {
        if (sourceIndices is null) return ([], Count != 0);
        int[] result = new int[Count];
        int index = 0;
        bool unknown = false;
        foreach (MidoraId id in this)
        {
            if (sourceIndices.TryGetValue(id, out int ordinal) && ordinal >= 0)
                result[index++] = ordinal;
            else
                unknown = true;
        }
        if (index != result.Length) Array.Resize(ref result, index);
        Array.Sort(result);
        if (result.Length > 1)
        {
            int write = 1;
            for (int read = 1; read < result.Length; read++)
            {
                if (result[read] != result[write - 1]) result[write++] = result[read];
            }
            if (write != result.Length) Array.Resize(ref result, write);
        }
        return (result, unknown);
    }

    private int CountOrdinals(int firstOrdinal, int count)
    {
        int endOrdinal = checked(firstOrdinal + count);
        return LowerBound(_sortedOrdinals, endOrdinal)
            - LowerBound(_sortedOrdinals, firstOrdinal);
    }

    private static int LowerBound(int[] values, int value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

}

internal interface IPureMidiContentPackSegmentSource
{
    PureMidiContentPack Owner { get; }
}

/// <summary>
/// Immutable range-query view over the source pages and the current copy-on-write
/// Note overlay. The overlay interval index is built once per presentation revision,
/// so each visible tile only visits edited Notes intersecting that tile.
/// </summary>
public sealed class DirectMidiNoteQuerySnapshot
{
    private const ulong FingerprintOffset = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;
    private readonly IPureMidiSegmentContentSource? _source;
    private readonly IReadOnlySet<MidoraId>? _sourceExclusions;
    private readonly DirectMidiNoteOverlayIndex _overlayIndex;
    private readonly PureMidiSourceIdResolutionCache<DirectMidiNoteSourceMatch> _sourceIdCache;
    private readonly FingerprintIndex _excludedFingerprintIndex;
    private readonly ulong _unknownExclusionFingerprint;
    private long _sourceMaximumEndTickCache = -1;
    private long _sourceMaximumEndTick
    {
        get
        {
            long value = Volatile.Read(ref _sourceMaximumEndTickCache);
            if (value >= 0) return value;
            value = GetSourceMaximumEndTick(_source);
            Interlocked.CompareExchange(ref _sourceMaximumEndTickCache, value, -1);
            return value;
        }
    }

    internal DirectMidiNoteQuerySnapshot(
        IPureMidiSegmentContentSource? source,
        bool clearSource,
        IReadOnlySet<MidoraId>? sourceExclusions,
        IReadOnlyDictionary<MidoraId, DirectMidiNoteValue> materializedSourceValues,
        DirectMidiNoteOverlayIndex overlayIndex,
        PureMidiSourceIdResolutionCache<DirectMidiNoteSourceMatch> sourceIdCache,
        int count,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(materializedSourceValues);
        ArgumentNullException.ThrowIfNull(overlayIndex);
        _source = clearSource ? null : source;
        _overlayIndex = overlayIndex;
        _sourceIdCache = sourceIdCache;
        _sourceExclusions = _source is not null && sourceExclusions?.Count != 0
            ? sourceExclusions
            : null;
        DirectMidiNoteValue[] excludedValues = _sourceExclusions is null
            ? []
            : _sourceExclusions
                .Where(materializedSourceValues.ContainsKey)
                .Select(id => materializedSourceValues[id])
                .ToArray();
        Array.Sort(excludedValues, static (left, right) =>
        {
            int key = left.Key.CompareTo(right.Key);
            if (key != 0) return key;
            int tick = left.StartTick.CompareTo(right.StartTick);
            return tick != 0 ? tick : left.Id.CompareTo(right.Id);
        });
        _excludedFingerprintIndex = new(excludedValues);
        FingerprintAggregate unknownExclusions = default;
        if (_sourceExclusions is not null)
        {
            foreach (MidoraId id in _sourceExclusions)
            {
                if (!materializedSourceValues.ContainsKey(id))
                    unknownExclusions.Add(unchecked((ulong)id.Value));
            }
        }
        _unknownExclusionFingerprint = unknownExclusions.ToFingerprint();
        // Exact-key edits do not need a full source extent. Legacy sources may
        // derive their bounds by enumeration, so only query consumers which
        // actually need the extent may request this lazy value.
        Count = count;
        Generation = generation;
    }

    public int Count { get; }
    public long Generation { get; }
    public long MaximumEndTick => Math.Max(_sourceMaximumEndTick, _overlayIndex.MaximumEndTick);

    public long CountStarts(long startTick, long endTick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endTick <= startTick) return 0;
        if (startTick == 0 && endTick >= MaximumEndTick) return Count;
        long count = 0;
        if (_source is IPureMidiPlaybackEndpointSource endpoints)
            count = endpoints.CountNoteStarts(startTick, endTick, cancellationToken);
        else if (_source is not null)
            foreach (DirectMidiNoteValue value in _source.QueryNotes(startTick, endTick))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.StartTick >= startTick && value.StartTick < endTick) count++;
            }
        if (_source is not null && _sourceExclusions is not null)
            foreach (DirectMidiNoteSourceMatch match in ResolveSourceMatches(_sourceExclusions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (match.Value.StartTick >= startTick && match.Value.StartTick < endTick) count--;
            }
        foreach (DirectMidiNoteValue value in _overlayIndex.Query(startTick, endTick, 0, 127, cancellationToken))
            if (value.StartTick >= startTick && value.StartTick < endTick) count++;
        return count;
    }

    internal IEnumerable<DirectMidiNoteSourceMatch> ResolveSourceMatches(IReadOnlySet<MidoraId> ids) =>
        _source is null ? [] : _sourceIdCache.Resolve(ids, _source.QueryNotesByIds);

    /// <summary>
    /// Returns a content-derived local fingerprint without decoding immutable
    /// source pages or enumerating edited Notes in the requested range.
    /// </summary>
    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
        if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
        if (maximumKey < minimumKey) throw new ArgumentOutOfRangeException(nameof(maximumKey));

        ulong fingerprint = FingerprintOffset;
        if (_source is IPureMidiContentRangeFingerprintSource ranged)
        {
            AddFingerprint(
                ref fingerprint,
                ranged.GetNoteRangeFingerprint(startTick, endTick, minimumKey, maximumKey));
        }
        else if (_source is not null)
        {
            AddFingerprint(ref fingerprint, HashText(_source.ContentFingerprint));
        }
        AddFingerprint(
            ref fingerprint,
            _excludedFingerprintIndex.GetFingerprint(
                startTick,
                endTick,
                minimumKey,
                maximumKey));
        AddFingerprint(ref fingerprint, _unknownExclusionFingerprint);
        AddFingerprint(
            ref fingerprint,
            _overlayIndex.GetRangeFingerprint(
                startTick,
                endTick,
                minimumKey,
                maximumKey));
        return fingerprint;
    }

    public bool TryAccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumKey,
        int maximumKey,
        Span<TimelineRasterColumnSummary> destination,
        out int sourceWorkCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination.Clear();
        sourceWorkCount = 0;
        if (destination.IsEmpty || maximumKey < minimumKey)
            return true;
        if (_source is not null)
        {
            if (_source is not IPureMidiContentOverviewSource overview
                || !overview.TryAccumulateNoteRasterColumns(
                    projection,
                    minimumKey,
                    maximumKey,
                    destination,
                    _sourceExclusions,
                    out sourceWorkCount,
                    cancellationToken))
            {
                return false;
            }
        }
        int overlayWork = _overlayIndex.AccumulateRasterColumns(
            projection,
            minimumKey,
            maximumKey,
            destination,
            cancellationToken);
        sourceWorkCount = sourceWorkCount > int.MaxValue - overlayWork
            ? int.MaxValue
            : sourceWorkCount + overlayWork;
        return true;
    }

    internal IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (_source is IPureMidiContentOverviewSource overview)
            foreach (var summary in overview.GetNoteRangeSummaries()) yield return summary;
        else if (_source is not null && _source.NoteCount != 0)
            yield return new(0, _sourceMaximumEndTick, _source.NoteCount);
        if (_overlayIndex.Count != 0)
            yield return new(0, _overlayIndex.MaximumEndTick, _overlayIndex.Count);
    }

    internal bool TryAccumulateRasterColumnsExcluding(TimelineRasterColumnProjection projection,
        int minimumKey, int maximumKey, Span<TimelineRasterColumnSummary> destination,
        IReadOnlySet<MidoraId> excludedIds, out int work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        work = 0;
        if (_source is not null)
        {
            var exclusions = _sourceExclusions is null ? excludedIds
                : new PureMidiUnionIdSet(_sourceExclusions, excludedIds);
            if (_source is not IPureMidiContentOverviewSource overview
                || !overview.TryAccumulateNoteRasterColumns(projection, minimumKey, maximumKey,
                    destination, exclusions, out work, cancellationToken)) return false;
        }
        int scanned = 0;
        foreach (var value in _overlayIndex.Query(projection.StartTick, projection.EndTick, minimumKey, maximumKey, cancellationToken))
        {
            if ((scanned++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (excludedIds.Contains(value.Id)) continue;
            ulong low = value.Key < 64 ? 1UL << value.Key : 0;
            ulong high = value.Key >= 64 ? 1UL << (value.Key - 64) : 0;
            PagedTimelineRasterProjection.IncludeExact(destination, projection, value.StartTick,
                checked(value.StartTick + value.LengthTicks), low, high,
                value.NoteOnVelocity / 127d, value.NoteOnVelocity / 127d, 1);
            if (work != int.MaxValue) work++;
        }
        return true;
    }

    internal bool TryAccumulateNoteStartColumnsExcluding(long extent, Span<byte> destination,
        IReadOnlySet<MidoraId> excludedIds)
    {
        if (_source is not null)
        {
            var exclusions = _sourceExclusions is null ? excludedIds
                : new PureMidiUnionIdSet(_sourceExclusions, excludedIds);
            if (_source is not IPureMidiContentOverviewSource overview
                || !overview.TryAccumulateNoteStartColumns(extent, destination, exclusions)) return false;
        }
        foreach (var value in _overlayIndex.Query(0, long.MaxValue, 0, 127))
            if (!excludedIds.Contains(value.Id)) PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
        return true;
    }

    public IEnumerable<DirectMidiNoteValue> QueryValues(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (endTick <= startTick
            || maximumKey < minimumKey
            || startTick >= MaximumEndTick)
        {
            yield break;
        }

        if (_source is not null && startTick < _sourceMaximumEndTick)
        {
            long sourceEnd = Math.Min(endTick, _sourceMaximumEndTick);
            bool filteredBySource = _sourceExclusions is not null
                && _source is IPureMidiNoteExclusionAwareSource;
            IEnumerable<DirectMidiNoteValue> sourceValues = filteredBySource
                ? ((IPureMidiNoteExclusionAwareSource)_source).QueryNotesExcluding(
                    startTick,
                    sourceEnd,
                    minimumKey,
                    maximumKey,
                    _sourceExclusions!)
                : _source.QueryNotes(
                    startTick,
                    sourceEnd,
                    minimumKey,
                    maximumKey);
            foreach (DirectMidiNoteValue value in sourceValues)
            {
                if (filteredBySource || _sourceExclusions?.Contains(value.Id) != true)
                    yield return value;
            }
        }

        foreach (DirectMidiNoteValue value in _overlayIndex.Query(
            startTick,
            endTick,
            minimumKey,
            maximumKey))
            yield return value;
    }

    /// <summary>Allows a detached edit root to retain the original source's
    /// exclusion-aware page skipping without materializing an additional hash set.</summary>
    internal IEnumerable<DirectMidiNoteValue> QueryValuesExcluding(
        long startTick, long endTick, int minimumKey, int maximumKey,
        IReadOnlySet<MidoraId> excludedIds)
    {
        if (endTick <= startTick || maximumKey < minimumKey || startTick >= MaximumEndTick)
            yield break;
        IReadOnlySet<MidoraId> all = _sourceExclusions is null ? excludedIds
            : new PureMidiUnionIdSet(_sourceExclusions, excludedIds);
        if (_source is not null && startTick < _sourceMaximumEndTick)
        {
            IEnumerable<DirectMidiNoteValue> values = _source is IPureMidiNoteExclusionAwareSource aware
                ? aware.QueryNotesExcluding(startTick, Math.Min(endTick, _sourceMaximumEndTick), minimumKey, maximumKey, all)
                : _source.QueryNotes(startTick, Math.Min(endTick, _sourceMaximumEndTick), minimumKey, maximumKey);
            foreach (DirectMidiNoteValue value in values)
                if (!all.Contains(value.Id)) yield return value;
        }
        foreach (DirectMidiNoteValue value in _overlayIndex.Query(startTick, endTick, minimumKey, maximumKey))
            if (!excludedIds.Contains(value.Id)) yield return value;
    }

    internal IEnumerable<DirectMidiNoteValue> QueryStartKeys(IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        if (_source is not null)
            foreach (var match in _source.QueryNotesAtStarts(keys))
                if (_sourceExclusions?.Contains(match.Value.Id) != true) yield return match.Value;
        foreach (var value in _overlayIndex.QueryStartKeys(keys)) yield return value;
    }

    internal bool TryQueryValuesCachedExcluding(long startTick, long endTick, int minimumKey,
        int maximumKey, IReadOnlySet<MidoraId> excludedIds, List<DirectMidiNoteValue> destination)
    {
        if (endTick <= startTick || maximumKey < minimumKey || startTick >= MaximumEndTick) return true;
        int initial = destination.Count;
        IReadOnlySet<MidoraId> all = _sourceExclusions is null ? excludedIds
            : new PureMidiUnionIdSet(_sourceExclusions, excludedIds);
        if (_source is not null && startTick < _sourceMaximumEndTick)
        {
            long sourceEnd = Math.Min(endTick, _sourceMaximumEndTick);
            if (_source is IPureMidiNoteExclusionAwareSource aware)
            {
                if (!aware.TryQueryCachedNotesExcluding(startTick, sourceEnd, minimumKey, maximumKey, all, destination))
                    return false;
            }
            else
            {
                if (_source is IPureMidiCachedContentSource cached)
                {
                    if (!cached.TryQueryCachedNotes(startTick, sourceEnd, minimumKey, maximumKey, destination)) return false;
                }
                else destination.AddRange(_source.QueryNotes(startTick, sourceEnd, minimumKey, maximumKey));
                int write = initial;
                for (int index = initial; index < destination.Count; index++)
                    if (!all.Contains(destination[index].Id)) destination[write++] = destination[index];
                destination.RemoveRange(write, destination.Count - write);
            }
        }
        foreach (var value in _overlayIndex.Query(startTick, endTick, minimumKey, maximumKey))
            if (!excludedIds.Contains(value.Id)) destination.Add(value);
        return true;
    }

    public IEnumerable<DirectMidiNoteValue> QueryStartValues(
        long startTick,
        long endTick) => QueryEndpointValues(startTick, endTick, noteOn: true);

    public IEnumerable<DirectMidiNoteValue> QueryEndValues(
        long startTick,
        long endTick) => QueryEndpointValues(startTick, endTick, noteOn: false);

    public IEnumerable<DirectMidiNoteValue> QueryActiveValues(long tick)
    {
        if (tick < 0) yield break;
        IEnumerable<DirectMidiNoteValue> source = [];
        if (_source is not null)
        {
            source = _source is IPureMidiPlaybackEndpointSource endpoints
                ? endpoints.QueryActiveNotes(tick)
                : _source.QueryNotes(tick, tick == long.MaxValue ? tick : tick + 1);
        }
        foreach (DirectMidiNoteValue value in source)
        {
            if (_sourceExclusions?.Contains(value.Id) == true) continue;
            if (IsActive(value, tick)) yield return value;
        }
        long endTick = tick == long.MaxValue ? tick : tick + 1;
        foreach (DirectMidiNoteValue value in _overlayIndex.Query(tick, endTick, 0, 127))
            if (IsActive(value, tick)) yield return value;
    }

    private IEnumerable<DirectMidiNoteValue> QueryEndpointValues(
        long startTick,
        long endTick,
        bool noteOn)
    {
        if (endTick <= startTick) yield break;
        IEnumerable<DirectMidiNoteValue> source = [];
        if (_source is not null)
        {
            if (_source is IPureMidiPlaybackEndpointSource endpoints)
            {
                source = noteOn
                    ? endpoints.QueryNoteStarts(startTick, endTick)
                    : endpoints.QueryNoteEnds(startTick, endTick);
            }
            else
            {
                source = noteOn
                    ? _source.QueryNotes(startTick, endTick)
                    : _source.QueryNotes(0, endTick);
            }
        }
        IEnumerable<DirectMidiNoteValue> filteredSource = source.Where(value =>
            _sourceExclusions?.Contains(value.Id) != true
            && EndpointTick(value, noteOn) >= startTick
            && EndpointTick(value, noteOn) < endTick);
        IEnumerable<DirectMidiNoteValue> edited = _overlayIndex
            .Query(noteOn ? startTick : 0, endTick, 0, 127)
            .Where(value => EndpointTick(value, noteOn) >= startTick
                && EndpointTick(value, noteOn) < endTick)
            .OrderBy(value => EndpointTick(value, noteOn))
            .ThenBy(value => noteOn ? value.NoteOnOrder : value.NoteOffOrder)
            .ThenBy(value => value.Id);
        foreach (DirectMidiNoteValue value in MergeEndpoints(filteredSource, edited, noteOn))
            yield return value;
    }

    private static IEnumerable<DirectMidiNoteValue> MergeEndpoints(
        IEnumerable<DirectMidiNoteValue> left,
        IEnumerable<DirectMidiNoteValue> right,
        bool noteOn)
    {
        using IEnumerator<DirectMidiNoteValue> leftEnumerator = left.GetEnumerator();
        using IEnumerator<DirectMidiNoteValue> rightEnumerator = right.GetEnumerator();
        bool hasLeft = leftEnumerator.MoveNext();
        bool hasRight = rightEnumerator.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && CompareEndpoint(leftEnumerator.Current, rightEnumerator.Current, noteOn) <= 0;
            if (takeLeft)
            {
                yield return leftEnumerator.Current;
                hasLeft = leftEnumerator.MoveNext();
            }
            else
            {
                yield return rightEnumerator.Current;
                hasRight = rightEnumerator.MoveNext();
            }
        }
    }

    private static int CompareEndpoint(
        DirectMidiNoteValue left,
        DirectMidiNoteValue right,
        bool noteOn)
    {
        int result = EndpointTick(left, noteOn).CompareTo(EndpointTick(right, noteOn));
        if (result != 0) return result;
        result = (noteOn ? left.NoteOnOrder : left.NoteOffOrder)
            .CompareTo(noteOn ? right.NoteOnOrder : right.NoteOffOrder);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    private static long EndpointTick(DirectMidiNoteValue value, bool noteOn) =>
        noteOn ? value.StartTick : EndTick(value);

    private static bool IsActive(DirectMidiNoteValue value, long tick) =>
        value.StartTick < tick && EndTick(value) > tick;

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        List<DirectMidiNoteValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick
            || maximumKey < minimumKey
            || startTick >= MaximumEndTick)
        {
            return true;
        }

        List<DirectMidiNoteValue>? sourceValues = null;
        if (_source is not null && startTick < _sourceMaximumEndTick)
        {
            sourceValues = [];
            long sourceEnd = Math.Min(endTick, _sourceMaximumEndTick);
            if (_sourceExclusions is not null
                && _source is IPureMidiNoteExclusionAwareSource exclusionAware)
            {
                if (!exclusionAware.TryQueryCachedNotesExcluding(
                        startTick,
                        sourceEnd,
                        minimumKey,
                        maximumKey,
                        _sourceExclusions,
                        sourceValues))
                {
                    return false;
                }
            }
            else if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedNotes(
                        startTick,
                        sourceEnd,
                        minimumKey,
                        maximumKey,
                        sourceValues))
                {
                    return false;
                }
            }
            else
            {
                sourceValues.AddRange(_source.QueryNotes(
                    startTick,
                    sourceEnd,
                    minimumKey,
                    maximumKey));
            }
        }

        if (sourceValues is not null)
        {
            bool filteredBySource = _sourceExclusions is not null
                && _source is IPureMidiNoteExclusionAwareSource;
            foreach (DirectMidiNoteValue value in sourceValues)
            {
                if (filteredBySource || _sourceExclusions?.Contains(value.Id) != true)
                    destination.Add(value);
            }
        }

        destination.AddRange(_overlayIndex.Query(startTick, endTick, minimumKey, maximumKey));
        return true;
    }

    public void PrefetchRange(
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey,
        CancellationToken cancellationToken)
    {
        if (endTick <= startTick
            || maximumKey < minimumKey
            || startTick >= _sourceMaximumEndTick
            || _source is not IPureMidiCachedContentSource cached)
        {
            return;
        }
        long sourceEnd = Math.Min(endTick, _sourceMaximumEndTick);
        if (_sourceExclusions is not null
            && _source is IPureMidiNoteExclusionAwareSource exclusionAware)
        {
            exclusionAware.PrefetchNotesExcluding(
                startTick,
                sourceEnd,
                minimumKey,
                maximumKey,
                _sourceExclusions,
                cancellationToken);
        }
        else
        {
            cached.PrefetchNotes(
                startTick,
                sourceEnd,
                minimumKey,
                maximumKey,
                cancellationToken);
        }
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiNoteValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        if (ids.Count == 0) return true;

        List<DirectMidiNoteSourceMatch>? sourceMatches = null;
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedNotesByIds(sourceIds, sourceMatches))
                    return false;
            }
            else
            {
                sourceMatches.AddRange(_source.QueryNotesByIds(sourceIds));
            }
        }

        HashSet<MidoraId> emitted = [];
        if (sourceMatches is not null)
        {
            foreach (DirectMidiNoteSourceMatch match in sourceMatches)
            {
                if (emitted.Add(match.Value.Id)) destination.Add(match.Value);
            }
        }
        foreach (DirectMidiNoteValue value in _overlayIndex.ResolveByIds(ids))
        {
            if (emitted.Add(value.Id)) destination.Add(value);
        }
        return true;
    }

    public IEnumerable<DirectMidiNoteValue> ResolveByIds(IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;
        HashSet<MidoraId> emitted = [];
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null)
                sourceIds.RemoveWhere(_sourceExclusions.Contains);
            foreach (DirectMidiNoteSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source.QueryNotesByIds))
            {
                if (emitted.Add(match.Value.Id)) yield return match.Value;
            }
        }
        foreach (DirectMidiNoteValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) yield return value;
    }

    public void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_source is not IPureMidiCachedContentSource cached || ids.Count == 0) return;
        HashSet<MidoraId> sourceIds = [.. ids];
        if (_sourceExclusions is not null)
            sourceIds.RemoveWhere(_sourceExclusions.Contains);
        cached.PrefetchNotesByIds(sourceIds, cancellationToken);
    }

    private static long GetSourceMaximumEndTick(IPureMidiSegmentContentSource? source)
    {
        if (source is null) return 0;
        if (source is IPureMidiContentBoundsSource bounds)
            return bounds.MaximumNoteEndTick;

        TimelineValueReadScope.ThrowIfReadWouldBlock();
        long maximum = 0;
        foreach (var value in source.QueryNotes(0, long.MaxValue))
            maximum = Math.Max(maximum, EndTick(value));
        return maximum;
    }

    private static long EndTick(DirectMidiNoteValue value) =>
        value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
            ? long.MaxValue
            : value.StartTick + Math.Max(1, value.LengthTicks);

    private static DirectMidiNoteValue ToValue(DirectMidiNote value) => new(
        value.Id,
        value.StartTick,
        value.LengthTicks,
        value.Key,
        value.NoteOnVelocity,
        value.NoteOffVelocity,
        value.NoteOnOrder,
        value.NoteOffOrder);

    private static ulong Fingerprint(DirectMidiNoteValue value)
    {
        ulong fingerprint = FingerprintOffset;
        AddFingerprint(ref fingerprint, unchecked((ulong)value.Id.Value));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.StartTick));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.LengthTicks));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.Key));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.NoteOnVelocity));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.NoteOffVelocity));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.NoteOnOrder));
        AddFingerprint(ref fingerprint, unchecked((ulong)value.NoteOffOrder));
        return fingerprint;
    }

    private static ulong HashText(string value)
    {
        ulong fingerprint = FingerprintOffset;
        foreach (char character in value)
            AddFingerprint(ref fingerprint, character);
        return fingerprint;
    }

    private static void AddFingerprint(ref ulong fingerprint, ulong value)
    {
        fingerprint ^= value;
        fingerprint *= FingerprintPrime;
    }

    /// <summary>
    /// Compact range index for the mutable overlay. The query snapshot already
    /// owns a start-sorted value array for rendering; this index adds only one
    /// summary per 128 values instead of allocating a two-node object tree for
    /// every Note. Range aggregation is commutative, so inserting or removing a
    /// far-away value cannot change the fingerprint of an unchanged tile merely
    /// by shifting block boundaries.
    /// </summary>
    private sealed class FingerprintIndex
    {
        private const int BlockSize = 128;
        private readonly DirectMidiNoteValue[] _values;
        private readonly FingerprintBlock[] _blocks;
        private readonly long[] _prefixMaximumEndTicks;
        private readonly KeyBlockRange[] _keyBlockRanges = new KeyBlockRange[128];

        public FingerprintIndex(DirectMidiNoteValue[] values)
        {
            _values = values;
            List<FingerprintBlock> blocks = new((values.Length + BlockSize - 1) / BlockSize);
            List<long> prefixMaximumEndTicks = new(blocks.Capacity);
            int valueIndex = 0;
            for (int key = 0; key < _keyBlockRanges.Length; key++)
            {
                while (valueIndex < values.Length && values[valueIndex].Key < key) valueIndex++;
                int keyValueEnd = valueIndex;
                while (keyValueEnd < values.Length && values[keyValueEnd].Key == key) keyValueEnd++;
                int firstBlock = blocks.Count;
                long prefixMaximumEndTick = 0;
                while (valueIndex < keyValueEnd)
                {
                    int first = valueIndex;
                    int count = Math.Min(BlockSize, keyValueEnd - first);
                    long minimumStartTick = long.MaxValue;
                    long maximumEndTick = 0;
                    FingerprintAggregate aggregate = default;
                    for (int index = first; index < first + count; index++)
                    {
                        DirectMidiNoteValue value = values[index];
                        minimumStartTick = Math.Min(minimumStartTick, value.StartTick);
                        maximumEndTick = Math.Max(maximumEndTick, EndTick(value));
                        aggregate.Add(Fingerprint(value));
                    }
                    blocks.Add(new(
                        first,
                        count,
                        minimumStartTick,
                        maximumEndTick,
                        aggregate));
                    prefixMaximumEndTick = Math.Max(prefixMaximumEndTick, maximumEndTick);
                    prefixMaximumEndTicks.Add(prefixMaximumEndTick);
                    valueIndex += count;
                }
                _keyBlockRanges[key] = new(firstBlock, blocks.Count - firstBlock);
            }
            _blocks = blocks.ToArray();
            _prefixMaximumEndTicks = prefixMaximumEndTicks.ToArray();
        }

        public ulong GetFingerprint(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey)
        {
            FingerprintAggregate result = default;
            int firstKey = Math.Clamp(minimumKey, 0, 127);
            int lastKey = Math.Clamp(maximumKey, 0, 127);
            if (lastKey < firstKey) return result.ToFingerprint();
            for (int key = firstKey; key <= lastKey; key++)
            {
                KeyBlockRange range = _keyBlockRanges[key];
                int firstBlock = UpperBound(
                    _prefixMaximumEndTicks,
                    range.First,
                    range.Count,
                    startTick);
                int blockEnd = range.First + range.Count;
                for (int blockIndex = firstBlock; blockIndex < blockEnd; blockIndex++)
                {
                    FingerprintBlock block = _blocks[blockIndex];
                    if (block.MinimumStartTick >= endTick) break;
                    if (block.MaximumEndTick <= startTick) continue;
                    if (startTick <= block.MinimumStartTick
                        && endTick >= block.MaximumEndTick)
                    {
                        result.Combine(block.Aggregate);
                        continue;
                    }
                    int end = checked(block.First + block.Count);
                    for (int index = block.First; index < end; index++)
                    {
                        DirectMidiNoteValue value = _values[index];
                        if (value.StartTick < endTick && EndTick(value) > startTick)
                            result.Add(Fingerprint(value));
                    }
                }
            }
            return result.ToFingerprint();
        }

        private static int UpperBound(
            long[] values,
            int first,
            int count,
            long value)
        {
            int low = first;
            int high = first + count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (values[middle] <= value) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private readonly record struct FingerprintBlock(
            int First,
            int Count,
            long MinimumStartTick,
            long MaximumEndTick,
            FingerprintAggregate Aggregate);

        private readonly record struct KeyBlockRange(int First, int Count);
    }

    private struct FingerprintAggregate
    {
        private ulong _xor;
        private ulong _sum;
        private ulong _rotatedSum;
        private ulong _count;

        public void Add(ulong value)
        {
            ulong mixed = Mix(value);
            _xor ^= mixed;
            _sum = unchecked(_sum + mixed);
            _rotatedSum = unchecked(_rotatedSum + BitOperations.RotateLeft(mixed, 23));
            _count++;
        }

        public void Combine(FingerprintAggregate other)
        {
            _xor ^= other._xor;
            _sum = unchecked(_sum + other._sum);
            _rotatedSum = unchecked(_rotatedSum + other._rotatedSum);
            _count = unchecked(_count + other._count);
        }

        public ulong ToFingerprint()
        {
            ulong fingerprint = FingerprintOffset;
            AddFingerprint(ref fingerprint, _xor);
            AddFingerprint(ref fingerprint, _sum);
            AddFingerprint(ref fingerprint, _rotatedSum);
            AddFingerprint(ref fingerprint, _count);
            return fingerprint;
        }

        private static ulong Mix(ulong value)
        {
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }
}

public sealed class DirectMidiNoteCollection : IList<DirectMidiNote>, IReadOnlyList<DirectMidiNote>, IDirectMidiNoteChangeSink
{
    private readonly MidoraProject _project;
    private readonly PureMidiCowList<DirectMidiNote, DirectMidiNoteValue> _added;
    private readonly PureMidiCowIndexedLookup<DirectMidiNote> _addedById;
    private ImmutableDictionary<MidoraId, int>.Builder _addedIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private readonly PureMidiCowDictionary<DirectMidiNote, DirectMidiNoteValue> _replacements;
    private ImmutableHashSet<MidoraId>.Builder _removed = ImmutableHashSet.CreateBuilder<MidoraId>();
    private readonly HashSet<MidoraId> _materializedSourceIds = [];
    private ImmutableDictionary<MidoraId, DirectMidiNoteValue>.Builder _materializedSourceValues = ImmutableDictionary.CreateBuilder<MidoraId, DirectMidiNoteValue>();
    private readonly Dictionary<MidoraId, DirectMidiNote> _materializedSourceItems = [];
    private ImmutableDictionary<MidoraId, int>.Builder _sourceIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private DirectMidiNoteOverlayIndex _overlayIndex = DirectMidiNoteOverlayIndex.Empty;
    private readonly HashSet<MidoraId> _dirtyOverlayIds = [];
    private readonly object _snapshotPublicationSync = new();
    private DirectMidiNoteQuerySnapshot? _querySnapshot;
    private ImmutableHashSet<MidoraId> _formalRemovedSourceIds =
        ImmutableHashSet<MidoraId>.Empty;
    private ImmutableDictionary<MidoraId, DirectMidiNoteValue> _formalReplacements =
        ImmutableDictionary<MidoraId, DirectMidiNoteValue>.Empty;
    private PersistentFormalValueSequence<DirectMidiNoteValue> _formalAdded =
        PersistentFormalValueSequence<DirectMidiNoteValue>.Empty;
    private PersistentFormalValueSequence<DirectMidiNoteValue>? _pendingFormalAdded;
    private readonly HashSet<MidoraId> _formalDirtyIds = [];
    private bool _formalFullRebuild;
    private int _formalCount;
    private IPureMidiSegmentContentSource? _source;
    private PureMidiSourceIdResolutionCache<DirectMidiNoteSourceMatch> _sourceIdCache =
        CreateSourceIdCache();
    private bool _clearSource;
    private long _generation;
    private int _batchChangeDepth;
    private bool _batchChanged;
    private HashSet<MidoraId>? _batchSourceIds;
    private bool _batchIncludesAllMaterializedSource;
    private DirectMidiNoteQuerySnapshot? _compilationSnapshot;

    internal DirectMidiNoteCollection(MidoraProject project)
    {
        _project = project;
        _added = new(value =>
        {
            DirectMidiNote item = FromValue(_project, value);
            Track(item);
            return item;
        });
        _addedById = new(
            id => _addedIndices.TryGetValue(id, out int index) ? index : null,
            index => _added[index]);
        _replacements = new(MaterializeReplacement);
    }

    private DirectMidiNote MaterializeReplacement(DirectMidiNoteValue value)
    {
        DirectMidiNote item = FromValue(_project, value);
        _materializedSourceIds.Add(value.Id);
        _materializedSourceItems[value.Id] = item;
        Track(item);
        return item;
    }

    public int Count => _compilationSnapshot?.Count
        ?? checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<DirectMidiNote> EditedItems => _replacements.Values.Concat(_added);
    internal IEnumerable<DirectMidiNoteValue> EditedValues =>
        _formalReplacements.Values.Concat(_formalAdded.Enumerate());
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds =>
        _compilationSnapshot is null ? _removed : _formalRemovedSourceIds;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    public DirectMidiNoteQuerySnapshot CreateQuerySnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth == 0
                && _querySnapshot is { } cached
                && cached.Generation == _generation)
            {
                return cached;
            }
            EnsureOverlayIndex();
            IReadOnlySet<MidoraId>? exclusions = _batchChangeDepth == 0
                ? _formalRemovedSourceIds.Count == 0 && _formalReplacements.Count == 0
                    ? null
                    : new PureMidiSourceExclusionSet<DirectMidiNoteValue>(
                        _formalRemovedSourceIds,
                        _formalReplacements,
                        _sourceIndices)
                : SourceExclusions();
            var snapshot = new DirectMidiNoteQuerySnapshot(
                _source,
                _clearSource,
                exclusions,
                _materializedSourceValues,
                _overlayIndex,
                _sourceIdCache,
                _formalCount,
                _generation);
            if (_batchChangeDepth == 0) _querySnapshot = snapshot;
            return snapshot;
        }
    }

    /// <summary>
    /// Captures the committed formal sequence now and streams values without
    /// creating editable objects. Includes hidden content and preserves order.
    /// The source owner must remain alive while the sequence is consumed.
    /// </summary>
    public IEnumerable<DirectMidiNoteValue> EnumerateValues(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("Cannot capture Direct MIDI Note values during an uncommitted batch change.");
            return PureMidiReadOnlyValues.Enumerate(
                _source, _clearSource ? 0 : _source?.NoteCount ?? 0,
                static (source, index) => source.GetNote(index), static value => value.Id,
                _formalRemovedSourceIds, _formalReplacements, _formalAdded, cancellationToken);
        }
    }

    public DirectMidiNoteObjectSource CreateObjectSource()
    {
        lock (_snapshotPublicationSync)
        {
            DirectMidiNoteFormalSequenceSnapshot formal = CreateFormalSequenceSnapshot();
            return new(formal, CreateQuerySnapshot());
        }
    }

    internal DirectMidiNoteFormalSequenceSnapshot CreateFormalSequenceSnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            return new(
                _source,
                _clearSource,
                _formalRemovedSourceIds,
                _formalReplacements,
                _formalAdded,
                _formalCount,
                _generation)
            {
                AddedIndices = _addedIndices.ToImmutable()
            };
        }
    }

    public DirectMidiNote this[int index]
    {
        get
        {
            if (_compilationSnapshot is not null)
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                using IEnumerator<DirectMidiNote> values = GetEnumerator();
                for (int current = 0; current <= index; current++) values.MoveNext();
                return values.Current;
            }
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiNoteValue value = _source!.GetNote(sourceIndex);
                if (_replacements.TryGetValue(value.Id, out DirectMidiNote? replacement))
                {
                    return replacement;
                }
                return Materialize(value, sourceIndex);
            }
            int addedIndex = checked(index - LiveSourceCount);
            return _added[addedIndex];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiNoteValue original = _source!.GetNote(sourceIndex);
                EnsureIdAvailable(value.Id, original.Id);
                DirectMidiNote? previous = _replacements.GetValueOrDefault(original.Id)
                    ?? _materializedSourceItems.GetValueOrDefault(original.Id);
                if (ReferenceEquals(previous, value)) return;
                if (value.Id == original.Id)
                {
                    previous?.SetChangeSink(null);
                    Track(value);
                    _materializedSourceIds.Add(original.Id);
                    _materializedSourceValues.TryAdd(original.Id, original);
                    _materializedSourceItems[original.Id] = value;
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                    MarkOverlayDirty(original.Id);
                }
                else
                {
                    previous?.SetChangeSink(null);
                    _materializedSourceItems.Remove(original.Id);
                    _removed.Add(original.Id);
                    _replacements.Remove(original.Id);
                    MarkOverlayDirty(original.Id);
                    Track(value);
                    AddOverlay(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            DirectMidiNote old = _added[addedIndex];
            if (ReferenceEquals(old, value)) return;
            EnsureIdAvailable(value.Id, old.Id);
            old.SetChangeSink(null);
            Track(value);
            MarkOverlayDirty(old.Id);
            _addedById.Remove(old.Id);
            _addedIndices.Remove(old.Id);
            _added[addedIndex] = value;
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = addedIndex;
            MarkOverlayDirty(value.Id);
            Touch();
        }
    }

    public void Add(DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureIdAvailable(item.Id);
        Track(item);
        AddOverlay(item);
        Touch();
    }

    public void AddRange(IEnumerable<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<DirectMidiNote> pending = values as IReadOnlyList<DirectMidiNote>
            ?? [.. values];
        HashSet<MidoraId> pendingIds = [];
        foreach (DirectMidiNote value in pending)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!pendingIds.Add(value.Id))
                throw new InvalidOperationException(
                    "A Direct MIDI Note range cannot contain duplicate Stable IDs.");
            EnsureIdAvailable(value.Id);
        }
        using IDisposable batch = BeginBatchChange([]);
        foreach (DirectMidiNote value in pending) Add(value);
    }

    public void Clear()
    {
        foreach (DirectMidiNote value in _added.MaterializedItems) value.SetChangeSink(null);
        foreach (DirectMidiNote value in _materializedSourceItems.Values)
            value.SetChangeSink(null);
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _materializedSourceIds.Clear();
        _materializedSourceValues.Clear();
        _materializedSourceItems.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        _addedById.Clear();
        _addedIndices.Clear();
        _overlayIndex = DirectMidiNoteOverlayIndex.Empty;
        _dirtyOverlayIds.Clear();
        _formalFullRebuild = true;
        _pendingFormalAdded = null;
        Touch();
    }

    public bool Contains(DirectMidiNote item) => item is not null && IndexOf(item) >= 0;

    public void CopyTo(DirectMidiNote[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (DirectMidiNote value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<DirectMidiNote> GetEnumerator()
    {
        if (_compilationSnapshot is not null)
        {
            // A compilation mirror must preserve the collection's formal sequence.
            // The query snapshot is intentionally spatially indexed for viewport work,
            // so its enumeration order is not the IList/formal order.
            if (!_clearSource && _source is not null)
            {
                for (int sourceIndex = 0; sourceIndex < _source.NoteCount; sourceIndex++)
                {
                    DirectMidiNoteValue sourceValue = _source.GetNote(sourceIndex);
                    if (_formalRemovedSourceIds.Contains(sourceValue.Id)) continue;
                    yield return FromValue(
                        _project,
                        _formalReplacements.GetValueOrDefault(sourceValue.Id, sourceValue));
                }
            }
            foreach (DirectMidiNoteValue value in _formalAdded.Enumerate())
                yield return FromValue(_project, value);
            yield break;
        }
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.NoteCount; index++)
            {
                DirectMidiNoteValue value = _source.GetNote(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out DirectMidiNote? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (DirectMidiNote value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiNote item)
    {
        if (item is null) return -1;
        if (_addedIndices.TryGetValue(item.Id, out int added))
            return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id))
        {
            return VisibleIndexForSourceIndex(sourceOrdinal);
        }
        return -1;
    }

    public int FindIndex(Predicate<DirectMidiNote> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (DirectMidiNote value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out DirectMidiNote? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource
            && !_removed.Contains(id)
            && _materializedSourceItems.TryGetValue(id, out value))
        {
            return true;
        }
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindNoteIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetNote(index), index, retain: true);
                return true;
            }
        }
        return _addedById.TryGetValue(id, out value);
    }

    public IReadOnlyList<DirectMidiNoteMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        if (!_clearSource
            && _source is not null
            && _removed.Count == 0
            && requested.All(id => _sourceIndices.ContainsKey(id)
                && (_replacements.ContainsKey(id) || _materializedSourceItems.ContainsKey(id))))
        {
            List<DirectMidiNoteMatch> cached = new(requested.Count);
            HashSet<MidoraId> emittedCached = [];
            foreach (MidoraId id in ids)
            {
                if (!emittedCached.Add(id)) continue;
                DirectMidiNote value = _replacements.GetValueOrDefault(id)
                    ?? _materializedSourceItems[id];
                cached.Add(new(_sourceIndices[id], value));
            }
            return cached;
        }
        Dictionary<MidoraId, DirectMidiNoteMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            sourceIds.ExceptWith(_replacements.Keys);
            sourceIds.ExceptWith(_materializedSourceItems.Keys);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (DirectMidiNoteSourceMatch match in sourceIds.Count == 0
                ? []
                : _sourceIdCache.Resolve(sourceIds, _source.QueryNotesByIds))
            {
                DirectMidiNote value = _replacements.TryGetValue(match.Value.Id, out DirectMidiNote? replacement)
                    ? replacement
                    : Materialize(match.Value, match.Index, retain: true);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
            foreach (MidoraId id in requested)
            {
                DirectMidiNote? replacement = _replacements.GetValueOrDefault(id)
                    ?? _materializedSourceItems.GetValueOrDefault(id);
                if (replacement is null || _removed.Contains(id)) continue;
                int sourceIndex = SourceIndexOf(id);
                if (sourceIndex >= 0)
                {
                    matches[id] = new(
                        VisibleIndex(sourceIndex, removedIndices),
                        replacement);
                }
            }
        }
        int addedBase = LiveSourceCount;
        foreach (MidoraId id in requested)
        {
            if (_addedById.TryGetValue(id, out DirectMidiNote? value))
                matches[id] = new(checked(addedBase + _addedIndices[id]), value);
        }
        List<DirectMidiNoteMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id) && matches.TryGetValue(id, out DirectMidiNoteMatch match))
                result.Add(match);
        }
        return result;
    }

    public IEnumerable<DirectMidiNote> ResolveValuesByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;

        foreach (MidoraId id in ids)
        {
            if (_removed.Contains(id)) continue;
            if (_replacements.TryGetValue(id, out DirectMidiNote? replacement))
            {
                yield return replacement;
            }
            else if (_materializedSourceItems.TryGetValue(id, out DirectMidiNote? materialized))
            {
                yield return materialized;
            }
        }

        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            sourceIds.ExceptWith(_removed);
            sourceIds.ExceptWith(_replacements.Keys);
            sourceIds.ExceptWith(_materializedSourceItems.Keys);
            if (sourceIds.Count != 0)
            {
                foreach (DirectMidiNoteSourceMatch match in _sourceIdCache.Resolve(
                    sourceIds,
                    _source.QueryNotesByIds))
                    yield return Materialize(match.Value, match.Index, retain: true);
            }
        }

        foreach (MidoraId id in ids)
        {
            if (_addedById.TryGetValue(id, out DirectMidiNote? value)) yield return value;
        }
    }

    public IDisposable BeginBatchChange(IReadOnlyCollection<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Note batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchIncludesAllMaterializedSource = false;
        if (!_clearSource && _source is not null && values.Count != 0)
        {
            _batchSourceIds = values
                .Select(static value => value.Id)
                .Where(id => _materializedSourceIds.Contains(id) && !_removed.Contains(id))
                .ToHashSet();
        }
        else
        {
            _batchSourceIds = [];
        }
        return new BatchChangeScope(this);
    }

    /// <summary>
    /// Starts a batch that will enumerate and potentially mutate every live
    /// value. Unlike materializing an ID set from a multi-million-note
    /// collection, source membership remains an O(1) predicate during the
    /// batch and no second object array is retained.
    /// </summary>
    public IDisposable BeginBatchChangeAll()
    {
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Note batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = true;
        return new BatchChangeScope(this);
    }

    public void Insert(int index, DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        EnsureIdAvailable(item.Id);
        Track(item);
        int addedIndex = Math.Clamp(index - LiveSourceCount, 0, _added.Count);
        InsertOverlay(addedIndex, item);
        Touch();
    }

    public bool Remove(DirectMidiNote item)
    {
        if (item is null) return false;
        if (_addedIndices.TryGetValue(item.Id, out int addedIndex))
        {
            RemoveOverlayAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            MarkOverlayDirty(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes a batch in one stable compaction pass. Large paste/duplicate
    /// Undo must use this path; repeatedly searching and removing from the
    /// edit overlay is quadratic.
    /// </summary>
    public int RemoveRange(IReadOnlyCollection<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return 0;
        HashSet<MidoraId> ids = values.Select(static value => value.Id).ToHashSet();
        int removed = RemoveOverlayWhere(ids);
        foreach (MidoraId id in ids)
        {
            if (_clearSource || _removed.Contains(id)) continue;
            int sourceIndex = SourceIndexOf(id);
            if (sourceIndex < 0) continue;
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
            removed++;
        }
        if (removed != 0) Touch();
        return removed;
    }

    internal Action RemoveForExactCollision(DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_addedIndices.TryGetValue(item.Id, out int addedIndex))
        {
            RemoveOverlayAt(addedIndex);
            Touch();
            return () =>
            {
                Track(item);
                InsertOverlay(Math.Clamp(addedIndex, 0, _added.Count), item);
                Touch();
            };
        }

        int sourceIndex = SourceIndexOf(item.Id);
        if (_clearSource || sourceIndex < 0 || _removed.Contains(item.Id))
            throw new InvalidOperationException("The conflicting Direct MIDI Note is no longer present.");
        _replacements.TryGetValue(item.Id, out DirectMidiNote? replacement);
        _removed.Add(item.Id);
        _replacements.Remove(item.Id);
        MarkOverlayDirty(item.Id);
        Touch();
        return () =>
        {
            if (!_removed.Remove(item.Id))
                throw new InvalidOperationException("The conflicting Direct MIDI Note is already restored.");
            if (replacement is not null) _replacements[item.Id] = replacement;
            MarkOverlayDirty(item.Id);
            Touch();
        };
    }

    /// <summary>
    /// Removes exact-collision losers in one overlay pass and returns an exact
    /// restoration action. This avoids the quadratic FindIndex/RemoveAt path
    /// when a large paste contains many duplicate start-tick/key pairs.
    /// </summary>
    internal Action RemoveRangeForExactCollision(IReadOnlyCollection<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return static () => { };
        Dictionary<MidoraId, DirectMidiNote> requested = [];
        foreach (DirectMidiNote value in values)
        {
            if (!requested.TryAdd(value.Id, value))
                throw new ArgumentException("Exact-collision Notes must be distinct.", nameof(values));
        }

        List<(int Index, DirectMidiNote Value)> added = [];
        foreach (MidoraId id in requested.Keys.ToArray())
        {
            if (!_addedIndices.TryGetValue(id, out int index)) continue;
            added.Add((index, _added[index]));
            requested.Remove(id);
        }
        added.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        List<(MidoraId Id, DirectMidiNote? Replacement)> source = [];
        foreach ((MidoraId id, DirectMidiNote value) in requested)
        {
            if (_clearSource || _removed.Contains(id) || SourceIndexOf(id) < 0)
                throw new InvalidOperationException("A conflicting Direct MIDI Note is no longer present.");
            _replacements.TryGetValue(id, out DirectMidiNote? replacement);
            DirectMidiNote? live = replacement
                ?? _materializedSourceItems.GetValueOrDefault(id);
            if (live is not null && !ReferenceEquals(live, value))
                throw new InvalidOperationException("A conflicting Direct MIDI Note changed before removal.");
            source.Add((id, replacement));
        }

        if (added.Count != 0)
        {
            RemoveOverlayItems(added);
        }
        foreach ((MidoraId id, _) in source)
        {
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
        }
        Touch();

        return () =>
        {
            if (added.Any(value => _addedById.ContainsKey(value.Value.Id)))
                throw new InvalidOperationException("A conflicting Direct MIDI Note is already restored.");
            if (added.Count != 0)
                RestoreOverlayItems(added);
            foreach ((MidoraId id, DirectMidiNote? replacement) in source)
            {
                if (!_removed.Remove(id))
                    throw new InvalidOperationException("A conflicting Direct MIDI Note is already restored.");
                if (replacement is not null) _replacements.Add(id, replacement);
                MarkOverlayDirty(id);
            }
            Touch();
        };
    }

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<DirectMidiNote> values) =>
        RemoveRangeForExactCollision(values);

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<DirectMidiNote> Query(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (endTick <= startTick || maximumKey < minimumKey) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue sourceValue in _source.QueryNotes(
                startTick, endTick, minimumKey, maximumKey))
            {
                if (_removed.Contains(sourceValue.Id)) continue;
                if (_replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (DirectMidiNoteValue edited in _overlayIndex.Query(
            startTick, endTick, minimumKey, maximumKey))
        {
            if (_replacements.TryGetValue(edited.Id, out DirectMidiNote? replacement))
                yield return replacement;
            else if (_addedById.TryGetValue(edited.Id, out DirectMidiNote? added))
                yield return added;
        }
    }

    public IEnumerable<DirectMidiNoteValue> QueryValues(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (DirectMidiNoteValue value in compilationSnapshot.QueryValues(
                startTick,
                endTick,
                minimumKey,
                maximumKey))
            {
                yield return value;
            }
            yield break;
        }
        if (endTick <= startTick || maximumKey < minimumKey) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue value in _source.QueryNotes(
                startTick, endTick, minimumKey, maximumKey))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (DirectMidiNoteValue value in _overlayIndex.Query(
            startTick, endTick, minimumKey, maximumKey))
            yield return value;
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetNoteRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue value in _source.QueryNotes(0, long.MaxValue))
            {
                yield return new(value.StartTick, value.StartTick, 1);
            }
        }
        foreach (DirectMidiNote value in _replacements.Values.Concat(_added))
        {
            yield return new(value.StartTick, value.StartTick, 1);
        }
    }

    public void AccumulateOverviewColumns(long extent, Span<byte> destination)
    {
        PureMidiOverviewProjection.Validate(extent, destination);
        if (destination.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateNoteStartColumns(
                    extent,
                    destination,
                    excludedIds);
            if (!accumulated)
            {
                IEnumerable<DirectMidiNoteValue> sourceValues =
                    _source is IPureMidiPlaybackEndpointSource endpoints
                        ? endpoints.QueryNoteStarts(0, long.MaxValue)
                        : _source.QueryNotes(0, long.MaxValue);
                foreach (DirectMidiNoteValue value in sourceValues)
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
                }
            }
        }

        foreach (DirectMidiNote value in _replacements.Values.Concat(_added))
            PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
    }

    public IEnumerable<DirectMidiNoteValue> QueryStartValues(
        long startTick,
        long endTick) => _compilationSnapshot?.QueryStartValues(startTick, endTick)
            ?? QueryEndpointValues(startTick, endTick, noteOn: true);

    public IEnumerable<DirectMidiNoteValue> QueryEndValues(
        long startTick,
        long endTick) => _compilationSnapshot?.QueryEndValues(startTick, endTick)
            ?? QueryEndpointValues(startTick, endTick, noteOn: false);

    public IEnumerable<DirectMidiNoteValue> QueryActiveValues(long tick)
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (DirectMidiNoteValue value in compilationSnapshot.QueryActiveValues(tick))
                yield return value;
            yield break;
        }
        if (tick < 0) yield break;
        EnsureOverlayIndex();
        IEnumerable<DirectMidiNoteValue> source = [];
        if (!_clearSource && _source is not null)
        {
            source = _source is IPureMidiPlaybackEndpointSource endpoints
                ? endpoints.QueryActiveNotes(tick)
                : _source.QueryNotes(tick, tick == long.MaxValue ? tick : tick + 1);
        }
        foreach (DirectMidiNoteValue value in source)
        {
            if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
            if (IsActive(value, tick)) yield return value;
        }
        long endTick = tick == long.MaxValue ? tick : tick + 1;
        foreach (DirectMidiNoteValue value in _overlayIndex.Query(tick, endTick, 0, 127))
            if (IsActive(value, tick)) yield return value;
    }

    private IEnumerable<DirectMidiNoteValue> QueryEndpointValues(
        long startTick,
        long endTick,
        bool noteOn)
    {
        if (endTick <= startTick) yield break;
        IEnumerable<DirectMidiNoteValue> source = [];
        if (!_clearSource && _source is not null)
        {
            if (_source is IPureMidiPlaybackEndpointSource endpoints)
            {
                source = noteOn
                    ? endpoints.QueryNoteStarts(startTick, endTick)
                    : endpoints.QueryNoteEnds(startTick, endTick);
            }
            else
            {
                source = noteOn
                    ? _source.QueryNotes(startTick, endTick)
                    : _source.QueryNotes(0, endTick);
            }
        }

        IEnumerable<DirectMidiNoteValue> filteredSource = source.Where(value =>
            !_removed.Contains(value.Id)
            && !_replacements.ContainsKey(value.Id)
            && EndpointTick(value, noteOn) >= startTick
            && EndpointTick(value, noteOn) < endTick);
        IEnumerable<DirectMidiNoteValue> edited = _replacements.Values
            .Concat(_added)
            .Select(ToValue)
            .Where(value => EndpointTick(value, noteOn) >= startTick
                && EndpointTick(value, noteOn) < endTick)
            .OrderBy(value => EndpointTick(value, noteOn))
            .ThenBy(value => noteOn ? value.NoteOnOrder : value.NoteOffOrder)
            .ThenBy(value => value.Id);
        foreach (DirectMidiNoteValue value in MergeEndpoints(
            filteredSource,
            edited,
            noteOn))
        {
            yield return value;
        }
    }

    private static IEnumerable<DirectMidiNoteValue> MergeEndpoints(
        IEnumerable<DirectMidiNoteValue> left,
        IEnumerable<DirectMidiNoteValue> right,
        bool noteOn)
    {
        using IEnumerator<DirectMidiNoteValue> leftEnumerator = left.GetEnumerator();
        using IEnumerator<DirectMidiNoteValue> rightEnumerator = right.GetEnumerator();
        bool hasLeft = leftEnumerator.MoveNext();
        bool hasRight = rightEnumerator.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && CompareEndpoint(leftEnumerator.Current, rightEnumerator.Current, noteOn) <= 0;
            if (takeLeft)
            {
                yield return leftEnumerator.Current;
                hasLeft = leftEnumerator.MoveNext();
            }
            else
            {
                yield return rightEnumerator.Current;
                hasRight = rightEnumerator.MoveNext();
            }
        }
    }

    private static int CompareEndpoint(
        DirectMidiNoteValue left,
        DirectMidiNoteValue right,
        bool noteOn)
    {
        int result = EndpointTick(left, noteOn).CompareTo(EndpointTick(right, noteOn));
        if (result != 0) return result;
        result = (noteOn ? left.NoteOnOrder : left.NoteOffOrder)
            .CompareTo(noteOn ? right.NoteOnOrder : right.NoteOffOrder);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    private static long EndpointTick(DirectMidiNoteValue value, bool noteOn) =>
        noteOn ? value.StartTick : SaturatingAdd(value.StartTick, value.LengthTicks);

    private static bool IsActive(DirectMidiNoteValue value, long tick) =>
        value.StartTick < tick && SaturatingAdd(value.StartTick, value.LengthTicks) > tick;

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        lock (_snapshotPublicationSync)
        {
            _source = source;
            _sourceIdCache = CreateSourceIdCache();
            _formalCount = source.NoteCount;
            _querySnapshot = null;
        }
    }

    internal void CloneTo(DirectMidiNoteCollection target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("A MIDI content root cannot be captured during a batch change.");
            if (target.Count != 0 || target._source is not null)
                throw new InvalidOperationException("A MIDI clone target must be empty.");
            EnsureOverlayIndex();
            target._source = _source;
            target._clearSource = _clearSource;
            target._removed = _formalRemovedSourceIds.ToBuilder();
            target._added.Adopt(_formalAdded);
            target._replacements.Adopt(_formalReplacements);
            target._addedIndices = _addedIndices.ToImmutable().ToBuilder();
            target._sourceIndices = _sourceIndices.ToImmutable().ToBuilder();
            target._materializedSourceValues = _materializedSourceValues.ToImmutable().ToBuilder();
            target._formalRemovedSourceIds = _formalRemovedSourceIds;
            target._formalReplacements = _formalReplacements;
            target._formalAdded = _formalAdded;
            target._formalCount = _formalCount;
            target._generation = _generation;
            target._overlayIndex = _overlayIndex;
            target._querySnapshot = _querySnapshot?.Generation == _generation ? _querySnapshot : null;
        }
    }

    /// <summary>
    /// Adopts a validated immutable result source without materializing its records.
    /// The source owns its page/storage lifetime and must remain immutable while
    /// this collection, an Undo root or a reader snapshot references it.
    /// </summary>
    public void AdoptContentSource(IPureMidiSegmentContentSource source, long generation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        AttachSource(source);
        _generation = generation;
    }

    internal void RestoreFormalSequence(
        DirectMidiNoteFormalSequenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The Direct MIDI Note target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The Direct MIDI Note source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed.UnionWith(snapshot.RemovedSourceIds);
        foreach ((MidoraId id, DirectMidiNoteValue value) in snapshot.Replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = _source?.FindNoteIndex(id) ?? -1;
            if (sourceIndex >= 0)
            {
                DirectMidiNoteValue sourceValue = _source!.GetNote(sourceIndex);
                _materializedSourceIds.Add(id);
                _materializedSourceValues[id] = sourceValue;
                _sourceIndices[id] = sourceIndex;
            }
            DirectMidiNote replacement = FromValue(_project, value);
            Track(replacement);
            _materializedSourceItems[id] = replacement;
            _replacements[id] = replacement;
            _dirtyOverlayIds.Add(id);
        }
        foreach (DirectMidiNoteValue value in snapshot.Added.Enumerate(cancellationToken))
        {
            DirectMidiNote added = FromValue(_project, value);
            Track(added);
            int index = _added.Count;
            _added.Add(added);
            _addedById.Add(added.Id, added);
            _addedIndices.Add(added.Id, index);
            _dirtyOverlayIds.Add(added.Id);
        }

        lock (_snapshotPublicationSync)
        {
            _formalRemovedSourceIds = snapshot.RemovedSourceIds;
            _formalReplacements = snapshot.Replacements;
            _formalAdded = snapshot.Added;
            _formalCount = snapshot.Count;
            _generation = snapshot.Generation;
            _querySnapshot = null;
            _formalDirtyIds.Clear();
            _pendingFormalAdded = null;
            _formalFullRebuild = false;
        }
    }

    internal void RestoreFormalSequenceForCompilation(
        DirectMidiNoteFormalSequenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The Direct MIDI Note target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The Direct MIDI Note source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed = snapshot.RemovedSourceIds.ToBuilder();
        _added.Adopt(snapshot.Added);
        _replacements.Adopt(snapshot.Replacements);
        _addedIndices = snapshot.AddedIndices.ToBuilder();
        _formalRemovedSourceIds = snapshot.RemovedSourceIds;
        _formalReplacements = snapshot.Replacements;
        _formalAdded = snapshot.Added;
        _formalCount = snapshot.Count;
        _generation = snapshot.Generation;
        IReadOnlySet<MidoraId>? exclusions = snapshot.Source is not null
            && (snapshot.RemovedSourceIds.Count != 0 || snapshot.Replacements.Count != 0)
                ? new PureMidiSourceExclusionSet<DirectMidiNoteValue>(
                    snapshot.RemovedSourceIds,
                    snapshot.Replacements)
                : null;
        _overlayIndex = DirectMidiNoteOverlayIndex.Create(
            snapshot.Replacements.Values.Concat(snapshot.Added.Enumerate()));
        _compilationSnapshot = new DirectMidiNoteQuerySnapshot(
            snapshot.Source,
            snapshot.ClearsSource,
            exclusions,
            new Dictionary<MidoraId, DirectMidiNoteValue>(),
            _overlayIndex,
            CreateSourceIdCache(),
            snapshot.Count,
            snapshot.Generation);
    }

    void IDirectMidiNoteChangeSink.OnChanged(DirectMidiNote value)
    {
        bool sourceBacked = _batchChangeDepth != 0
            ? (_batchIncludesAllMaterializedSource
                ? _materializedSourceIds.Contains(value.Id)
                : _batchSourceIds?.Contains(value.Id) == true)
                && !_removed.Contains(value.Id)
            : !_clearSource
                && _materializedSourceIds.Contains(value.Id)
                && !_removed.Contains(value.Id);
        if (sourceBacked)
            _replacements[value.Id] = value;
        MarkOverlayDirty(value.Id);
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.NoteCount - _removed.Count);

    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindNoteIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int liveSourceCount = LiveSourceCount;
        if (visibleIndex >= liveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.NoteCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetNote(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged Note index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    public IEnumerable<DirectMidiNote> QueryStartKeys(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteSourceMatch match in _source.QueryNotesAtStarts(keys))
            {
                DirectMidiNoteValue sourceValue = match.Value;
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id))
                    continue;
                yield return Materialize(sourceValue, match.Index);
            }
        }
        HashSet<MidoraId> emitted = [];
        foreach (DirectMidiNoteStartKey key in keys)
        {
            foreach (DirectMidiNoteValue edited in _overlayIndex.QueryStartKey(key.Tick, key.Key))
            {
                if (!emitted.Add(edited.Id)) continue;
                if (_replacements.TryGetValue(edited.Id, out DirectMidiNote? replacement))
                    yield return replacement;
                else if (_addedById.TryGetValue(edited.Id, out DirectMidiNote? added))
                    yield return added;
            }
        }
    }

    internal IEnumerable<DirectMidiNote> QueryEditedStartKeys(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) yield break;
        EnsureOverlayIndex();
        HashSet<MidoraId> emitted = [];
        foreach (DirectMidiNoteValue edited in _overlayIndex.QueryStartKeys(keys))
        {
            if (!emitted.Add(edited.Id)) continue;
            if (_replacements.TryGetValue(edited.Id, out DirectMidiNote? replacement))
                yield return replacement;
            else if (_addedById.TryGetValue(edited.Id, out DirectMidiNote? added))
                yield return added;
        }
    }

    internal bool IsUneditedSourceNotePresent(MidoraId id) =>
        !_clearSource
        && _source is not null
        && _materializedSourceIds.Contains(id)
        && !_removed.Contains(id)
        && !_replacements.ContainsKey(id);

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private DirectMidiNote Materialize(
        DirectMidiNoteValue value,
        int sourceIndex = -1,
        bool retain = false)
    {
        if (_materializedSourceItems.TryGetValue(value.Id, out DirectMidiNote? existing))
        {
            if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
            return existing;
        }
        _materializedSourceIds.Add(value.Id);
        _materializedSourceValues.TryAdd(value.Id, value);
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        DirectMidiNote result = new(_project, value.Id)
        {
            StartTick = value.StartTick,
            LengthTicks = value.LengthTicks,
            Key = value.Key,
            NoteOnVelocity = value.NoteOnVelocity,
            NoteOffVelocity = value.NoteOffVelocity,
            NoteOnOrder = value.NoteOnOrder,
            NoteOffOrder = value.NoteOffOrder
        };
        Track(result);
        if (retain) _materializedSourceItems.Add(value.Id, result);
        return result;
    }

    private void AddOverlay(DirectMidiNote value)
    {
        int index = _added.Count;
        _added.Add(value);
        _addedById[value.Id] = value;
        _addedIndices[value.Id] = index;
        if (_pendingFormalAdded is not null)
            _pendingFormalAdded = _pendingFormalAdded.Append(ToValue(value));
        MarkOverlayDirty(value.Id);
    }

    private void InsertOverlay(int index, DirectMidiNote value)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.Insert(index, ToValue(value));
        _added.Insert(index, value);
        _addedById[value.Id] = value;
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
        MarkOverlayDirty(value.Id);
    }

    private void RemoveOverlayAt(int index)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices([index]);
        DirectMidiNote value = _added[index];
        _added.RemoveAt(index);
        _addedById.Remove(value.Id);
        _addedIndices.Remove(value.Id);
        MarkOverlayDirty(value.Id);
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
    }

    private int RemoveOverlayWhere(IReadOnlySet<MidoraId> ids)
    {
        if (ids.Count == 0 || _added.Count == 0) return 0;
        List<(int Index, DirectMidiNote Value)> removed = new(
            Math.Min(ids.Count, _added.Count));
        foreach (MidoraId id in ids)
        {
            if (_addedIndices.TryGetValue(id, out int index))
                removed.Add((index, _added[index]));
        }
        removed.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return RemoveOverlayItems(removed);
    }

    private int RemoveOverlayItems(IReadOnlyList<(int Index, DirectMidiNote Value)> removed)
    {
        if (removed.Count == 0) return 0;
        int first = removed[0].Index;
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices(
            removed.Select(static value => value.Index).ToArray());
        foreach ((_, DirectMidiNote value) in removed)
        {
            _addedById.Remove(value.Id);
            _addedIndices.Remove(value.Id);
            MarkOverlayDirty(value.Id);
        }
        int write = first;
        int removedIndex = 0;
        for (int read = first; read < _added.Count; read++)
        {
            if (removedIndex < removed.Count && removed[removedIndex].Index == read)
            {
                removedIndex++;
                continue;
            }
            DirectMidiNote value = _added[read];
            if (write != read) _added[write] = value;
            _addedIndices[value.Id] = write++;
        }
        if (removedIndex != removed.Count)
            throw new InvalidOperationException("The Direct MIDI Note overlay removal indices are invalid.");
        _added.RemoveRange(write, removed.Count);
        return removed.Count;
    }

    private void RestoreOverlayItems(IReadOnlyList<(int Index, DirectMidiNote Value)> restored)
    {
        int first = restored[0].Index;
        int finalCount = checked(_added.Count + restored.Count);
        List<DirectMidiNote> suffix = new(finalCount - first);
        int liveIndex = first;
        int restoredIndex = 0;
        for (int index = first; index < finalCount; index++)
        {
            if (restoredIndex < restored.Count && restored[restoredIndex].Index == index)
                suffix.Add(restored[restoredIndex++].Value);
            else if ((uint)liveIndex < (uint)_added.Count)
                suffix.Add(_added[liveIndex++]);
            else
                throw new InvalidOperationException("The Direct MIDI Note overlay changed before restoration.");
        }
        if (restoredIndex != restored.Count || liveIndex != _added.Count)
            throw new InvalidOperationException("The Direct MIDI Note overlay cannot be restored exactly.");
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.InsertAtFinalIndices(
            restored.Select(static value => new KeyValuePair<int, DirectMidiNoteValue>(
                value.Index,
                ToValue(value.Value))).ToArray());
        _added.RemoveRange(first, _added.Count - first);
        _added.AddRange(suffix);
        for (int index = first; index < _added.Count; index++)
        {
            DirectMidiNote value = _added[index];
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
        }
        foreach ((_, DirectMidiNote value) in restored)
        {
            Track(value);
            MarkOverlayDirty(value.Id);
        }
    }

    private void RebuildAddedIndex(bool formalStructureAlreadyUpdated = false)
    {
        if (!formalStructureAlreadyUpdated)
            throw new InvalidOperationException("The formal Direct MIDI Note order was not updated.");
        _addedById.Clear();
        _addedIndices.Clear();
        for (int index = 0; index < _added.Count; index++)
        {
            DirectMidiNote value = _added[index];
            Track(value);
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
            MarkOverlayDirty(value.Id);
        }
    }

    private void BeginFormalStructuralEdit()
    {
        if (_pendingFormalAdded is not null) return;
        PersistentFormalValueSequence<DirectMidiNoteValue> current =
            _formalAdded.AppendRange(_added, _formalAdded.Count, static value => ToValue(value));
        Dictionary<int, DirectMidiNoteValue>? updates = null;
        foreach (MidoraId id in _formalDirtyIds)
        {
            if (_addedIndices.TryGetValue(id, out int index) && index < current.Count)
                (updates ??= [])[index] = ToValue(_added[index]);
        }
        _pendingFormalAdded = updates is null ? current : current.ReplaceBatch(updates);
    }

    private void MarkOverlayDirty(MidoraId id)
    {
        _dirtyOverlayIds.Add(id);
        int capturedAddedCount = _pendingFormalAdded?.Count ?? _formalAdded.Count;
        if (!_addedIndices.TryGetValue(id, out int addedIndex)
            || addedIndex < capturedAddedCount)
        {
            _formalDirtyIds.Add(id);
        }
    }

    private void EnsureOverlayIndex()
    {
        if (_dirtyOverlayIds.Count == 0) return;
        _overlayIndex = _overlayIndex.ReplaceBatch(_dirtyOverlayIds, id =>
        {
            if (_replacements.TryGetValue(id, out DirectMidiNote? replacement))
                return ToValue(replacement);
            return _addedById.TryGetValue(id, out DirectMidiNote? added)
                ? ToValue(added)
                : null;
        });
        _dirtyOverlayIds.Clear();
    }

    private void Track(DirectMidiNote value) => value.SetChangeSink(this);

    private void EnsureIdAvailable(MidoraId id, MidoraId replacingId = default)
    {
        if (id == default) throw new ArgumentOutOfRangeException(nameof(id));
        if (id == replacingId) return;
        if (_addedById.ContainsKey(id)
            || _source is not null && _source.FindNoteIndex(id) >= 0)
        {
            throw new InvalidOperationException(
                "A Direct MIDI Note collection cannot contain duplicate Stable IDs.");
        }
    }
    private void PublishFormalRoots()
    {
        if (_formalFullRebuild)
        {
            _formalRemovedSourceIds = _removed.ToImmutableHashSet();
            _formalReplacements = _replacements.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => ToValue(pair.Value));
            _formalAdded = PersistentFormalValueSequence<DirectMidiNoteValue>.Create(
                _added,
                static value => ToValue(value));
        }
        else
        {
            if (_formalDirtyIds.Count != 0)
            {
                var removed = _formalRemovedSourceIds.ToBuilder();
                var replacements = _formalReplacements.ToBuilder();
                foreach (MidoraId id in _formalDirtyIds)
                {
                    if (_removed.Contains(id)) removed.Add(id);
                    else removed.Remove(id);
                    if (_replacements.TryGetValue(id, out DirectMidiNote? replacement))
                        replacements[id] = ToValue(replacement);
                    else
                        replacements.Remove(id);
                }
                _formalRemovedSourceIds = removed.ToImmutable();
                _formalReplacements = replacements.ToImmutable();
            }

            PersistentFormalValueSequence<DirectMidiNoteValue> next =
                _pendingFormalAdded ?? _formalAdded.AppendRange(
                    _added,
                    _formalAdded.Count,
                    static value => ToValue(value));
            Dictionary<int, DirectMidiNoteValue>? updated = null;
            foreach (MidoraId id in _formalDirtyIds)
            {
                if (_addedIndices.TryGetValue(id, out int index) && index < next.Count)
                {
                    (updated ??= [])[index] = ToValue(_added[index]);
                }
            }
            _formalAdded = updated is null ? next : next.ReplaceBatch(updated);
        }

        _formalCount = Count;
        _added.Commit(_formalAdded);
        _replacements.Commit(_formalReplacements);
        _formalDirtyIds.Clear();
        _pendingFormalAdded = null;
        _formalFullRebuild = false;
    }

    private void Touch()
    {
        if (_batchChangeDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        lock (_snapshotPublicationSync)
        {
            PublishFormalRoots();
            _generation++;
        }
    }

    private void EndBatchChange()
    {
        if (_batchChangeDepth != 1)
            throw new InvalidOperationException("No Direct MIDI Note batch change is active.");
        CollapseSourceEquivalentReplacements();
        _batchChangeDepth = 0;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = false;
        if (_batchChanged)
        {
            lock (_snapshotPublicationSync)
            {
                PublishFormalRoots();
                _generation++;
            }
        }
        _batchChanged = false;
    }

    private void CollapseSourceEquivalentReplacements()
    {
        if (_source is null || _clearSource) return;
        IEnumerable<MidoraId> candidates = _batchIncludesAllMaterializedSource
            ? _materializedSourceIds
            : _batchSourceIds ?? [];
        foreach (MidoraId id in candidates)
        {
            if (!_replacements.TryGetValue(id, out DirectMidiNote? replacement))
                continue;
            if (_materializedSourceValues.TryGetValue(id, out DirectMidiNoteValue source)
                && Matches(replacement, source))
            {
                _replacements.Remove(id);
                MarkOverlayDirty(id);
            }
        }
    }

    private static bool Matches(DirectMidiNote value, DirectMidiNoteValue source) =>
        value.Id == source.Id
        && value.StartTick == source.StartTick
        && value.LengthTicks == source.LengthTicks
        && value.Key == source.Key
        && value.NoteOnVelocity == source.NoteOnVelocity
        && value.NoteOffVelocity == source.NoteOffVelocity
        && value.NoteOnOrder == source.NoteOnOrder
        && value.NoteOffOrder == source.NoteOffOrder;

    private sealed class BatchChangeScope(DirectMidiNoteCollection owner) : IDisposable
    {
        private DirectMidiNoteCollection? _owner = owner;

        public void Dispose()
        {
            DirectMidiNoteCollection? value = Interlocked.Exchange(ref _owner, null);
            value?.EndBatchChange();
        }
    }

    private static bool Intersects(
        DirectMidiNote value,
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey) => value.StartTick < endTick
        && value.StartTick <= long.MaxValue - Math.Max(1, value.LengthTicks)
        && value.StartTick + Math.Max(1, value.LengthTicks) > startTick
        && value.Key >= minimumKey
        && value.Key <= maximumKey;

    private static DirectMidiNoteValue ToValue(DirectMidiNote value) => new(
        value.Id,
        value.StartTick,
        value.LengthTicks,
        value.Key,
        value.NoteOnVelocity,
        value.NoteOffVelocity,
        value.NoteOnOrder,
        value.NoteOffOrder);

    private static DirectMidiNote FromValue(
        MidoraProject project,
        DirectMidiNoteValue value) => new(project, value.Id)
        {
            StartTick = value.StartTick,
            LengthTicks = value.LengthTicks,
            Key = value.Key,
            NoteOnVelocity = value.NoteOnVelocity,
            NoteOffVelocity = value.NoteOffVelocity,
            NoteOnOrder = value.NoteOnOrder,
            NoteOffOrder = value.NoteOffOrder
        };

    private static DirectMidiNote Clone(
        MidoraProject project,
        DirectMidiNote source,
        IDirectMidiNoteChangeSink sink)
    {
        DirectMidiNote result = new(project, source.Id)
        {
            StartTick = source.StartTick,
            LengthTicks = source.LengthTicks,
            Key = source.Key,
            NoteOnVelocity = source.NoteOnVelocity,
            NoteOffVelocity = source.NoteOffVelocity,
            NoteOnOrder = source.NoteOnOrder,
            NoteOffOrder = source.NoteOffOrder
        };
        result.SetChangeSink(sink);
        return result;
    }

    private static PureMidiSourceIdResolutionCache<DirectMidiNoteSourceMatch> CreateSourceIdCache() =>
        new(static match => match.Value.Id, static match => match.Index);
}

public sealed class DirectMidiChannelEventCollection : IList<DirectMidiChannelEvent>, IReadOnlyList<DirectMidiChannelEvent>, IDirectMidiChannelEventChangeSink
{
    private readonly MidoraProject _project;
    private readonly PureMidiCowList<DirectMidiChannelEvent, DirectMidiChannelEventValue> _added;
    private readonly PureMidiCowIndexedLookup<DirectMidiChannelEvent> _addedById;
    private ImmutableDictionary<MidoraId, int>.Builder _addedIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private readonly PureMidiCowDictionary<DirectMidiChannelEvent, DirectMidiChannelEventValue> _replacements;
    private ImmutableHashSet<MidoraId>.Builder _removed = ImmutableHashSet.CreateBuilder<MidoraId>();
    private readonly HashSet<MidoraId> _materializedSourceIds = [];
    private ImmutableDictionary<MidoraId, DirectMidiChannelEventValue>.Builder _materializedSourceValues = ImmutableDictionary.CreateBuilder<MidoraId, DirectMidiChannelEventValue>();
    private readonly Dictionary<MidoraId, DirectMidiChannelEvent> _materializedSourceItems = [];
    private ImmutableDictionary<MidoraId, int>.Builder _sourceIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private PureMidiPointOverlayIndex<DirectMidiChannelEventValue> _overlayIndex = new(
        static value => value.Id,
        static value => value.Tick,
        static value => value.Order);
    private readonly HashSet<MidoraId> _dirtyOverlayIds = [];
    private readonly object _snapshotPublicationSync = new();
    private DirectMidiChannelEventQuerySnapshot? _querySnapshot;
    private PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> _sourceIdCache =
        CreateSourceIdCache();
    private ImmutableHashSet<MidoraId> _formalRemovedSourceIds =
        ImmutableHashSet<MidoraId>.Empty;
    private ImmutableDictionary<MidoraId, DirectMidiChannelEventValue> _formalReplacements =
        ImmutableDictionary<MidoraId, DirectMidiChannelEventValue>.Empty;
    private PersistentFormalValueSequence<DirectMidiChannelEventValue> _formalAdded =
        PersistentFormalValueSequence<DirectMidiChannelEventValue>.Empty;
    private PersistentFormalValueSequence<DirectMidiChannelEventValue>? _pendingFormalAdded;
    private readonly HashSet<MidoraId> _formalDirtyIds = [];
    private bool _formalFullRebuild;
    private int _formalCount;
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;
    private int _batchChangeDepth;
    private bool _batchChanged;
    private HashSet<MidoraId>? _batchSourceIds;
    private bool _batchIncludesAllMaterializedSource;
    private DirectMidiChannelEventQuerySnapshot? _compilationSnapshot;

    internal DirectMidiChannelEventCollection(MidoraProject project)
    {
        _project = project;
        _added = new(value =>
        {
            DirectMidiChannelEvent item = FromValue(_project, value);
            Track(item);
            return item;
        });
        _addedById = new(
            id => _addedIndices.TryGetValue(id, out int index) ? index : null,
            index => _added[index]);
        _replacements = new(MaterializeReplacement);
    }

    private DirectMidiChannelEvent MaterializeReplacement(DirectMidiChannelEventValue value)
    {
        DirectMidiChannelEvent item = FromValue(_project, value);
        _materializedSourceIds.Add(value.Id);
        _materializedSourceItems[value.Id] = item;
        Track(item);
        return item;
    }

    public int Count => _compilationSnapshot?.Count
        ?? checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<DirectMidiChannelEvent> EditedItems => _replacements.Values.Concat(_added);
    internal IEnumerable<DirectMidiChannelEventValue> EditedValues =>
        _formalReplacements.Values.Concat(_formalAdded.Enumerate());
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds =>
        _compilationSnapshot is null ? _removed : _formalRemovedSourceIds;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    internal PureMidiPointOverlayIndex<DirectMidiChannelEventValue> CaptureOverlayIndex()
    {
        lock (_snapshotPublicationSync)
        {
            EnsureOverlayIndex();
            return _overlayIndex;
        }
    }

    public DirectMidiChannelEventQuerySnapshot CreateQuerySnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth == 0
                && _querySnapshot is { } cached
                && cached.Generation == _generation)
            {
                return cached;
            }
            EnsureOverlayIndex();
            IReadOnlySet<MidoraId>? exclusions = _batchChangeDepth == 0
                ? _formalRemovedSourceIds.Count == 0 && _formalReplacements.Count == 0
                    ? null
                    : new PureMidiSourceExclusionSet<DirectMidiChannelEventValue>(
                        _formalRemovedSourceIds,
                        _formalReplacements)
                : SourceExclusions();
            var snapshot = new DirectMidiChannelEventQuerySnapshot(
                _source,
                _clearSource,
                exclusions,
                _materializedSourceValues,
                _overlayIndex,
                _sourceIdCache,
                Count,
                _generation);
            if (_batchChangeDepth == 0) _querySnapshot = snapshot;
            return snapshot;
        }
    }

    /// <summary>Captures committed values in formal order without editable facades.
    /// Consume within the source owner's lifetime; includes hidden content.</summary>
    public IEnumerable<DirectMidiChannelEventValue> EnumerateValues(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("Cannot capture Direct MIDI Event values during an uncommitted batch change.");
            return PureMidiReadOnlyValues.Enumerate(
                _source, _clearSource ? 0 : _source?.ChannelEventCount ?? 0,
                static (source, index) => source.GetChannelEvent(index), static value => value.Id,
                _formalRemovedSourceIds, _formalReplacements, _formalAdded, cancellationToken);
        }
    }

    public DirectMidiChannelEventObjectSource CreateObjectSource()
    {
        lock (_snapshotPublicationSync)
        {
            DirectMidiChannelEventFormalSequenceSnapshot formal = CreateFormalSequenceSnapshot();
            return new(formal, CreateQuerySnapshot());
        }
    }

    internal DirectMidiChannelEventFormalSequenceSnapshot CreateFormalSequenceSnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            return new(
                _source,
                _clearSource,
                _formalRemovedSourceIds,
                _formalReplacements,
                _formalAdded,
                _formalCount,
                _generation)
            {
                AddedIndices = _addedIndices.ToImmutable()
            };
        }
    }

    public DirectMidiChannelEvent this[int index]
    {
        get
        {
            if (_compilationSnapshot is not null)
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                using IEnumerator<DirectMidiChannelEvent> values = GetEnumerator();
                for (int current = 0; current <= index; current++) values.MoveNext();
                return values.Current;
            }
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiChannelEventValue value = _source!.GetChannelEvent(sourceIndex);
                return _replacements.TryGetValue(value.Id, out DirectMidiChannelEvent? replacement)
                    ? replacement
                    : Materialize(value, sourceIndex, retain: true);
            }
            return _added[checked(index - LiveSourceCount)];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiChannelEventValue original = _source!.GetChannelEvent(sourceIndex);
                EnsureIdAvailable(value.Id, original.Id);
                _materializedSourceValues.TryAdd(original.Id, original);
                DirectMidiChannelEvent? previous = _replacements.GetValueOrDefault(original.Id)
                    ?? _materializedSourceItems.GetValueOrDefault(original.Id);
                if (ReferenceEquals(previous, value)) return;
                if (value.Id == original.Id)
                {
                    previous?.SetChangeSink(null);
                    Track(value);
                    _materializedSourceIds.Add(original.Id);
                    _materializedSourceItems[original.Id] = value;
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                    MarkOverlayDirty(original.Id);
                }
                else
                {
                    previous?.SetChangeSink(null);
                    _materializedSourceItems.Remove(original.Id);
                    _removed.Add(original.Id);
                    _replacements.Remove(original.Id);
                    MarkOverlayDirty(original.Id);
                    Track(value);
                    AddOverlay(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            DirectMidiChannelEvent old = _added[addedIndex];
            if (ReferenceEquals(old, value)) return;
            EnsureIdAvailable(value.Id, old.Id);
            old.SetChangeSink(null);
            Track(value);
            MarkOverlayDirty(old.Id);
            _addedById.Remove(old.Id);
            _addedIndices.Remove(old.Id);
            _added[addedIndex] = value;
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = addedIndex;
            MarkOverlayDirty(value.Id);
            Touch();
        }
    }

    public void Add(DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureIdAvailable(item.Id);
        Track(item);
        AddOverlay(item);
        Touch();
    }

    public void AddRange(IEnumerable<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<DirectMidiChannelEvent> pending =
            values as IReadOnlyList<DirectMidiChannelEvent> ?? [.. values];
        HashSet<MidoraId> pendingIds = [];
        foreach (DirectMidiChannelEvent value in pending)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!pendingIds.Add(value.Id))
                throw new InvalidOperationException(
                    "A Direct MIDI Event range cannot contain duplicate Stable IDs.");
            EnsureIdAvailable(value.Id);
        }
        using IDisposable batch = BeginBatchChange([]);
        foreach (DirectMidiChannelEvent value in pending) Add(value);
    }

    public void Clear()
    {
        foreach (DirectMidiChannelEvent value in _added.MaterializedItems) value.SetChangeSink(null);
        foreach (DirectMidiChannelEvent value in _materializedSourceItems.Values)
            value.SetChangeSink(null);
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _materializedSourceIds.Clear();
        _materializedSourceValues.Clear();
        _materializedSourceItems.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        _addedById.Clear();
        _addedIndices.Clear();
        _overlayIndex = new(
            static value => value.Id,
            static value => value.Tick,
            static value => value.Order);
        _dirtyOverlayIds.Clear();
        _formalFullRebuild = true;
        _pendingFormalAdded = null;
        Touch();
    }

    public bool Contains(DirectMidiChannelEvent item) => item is not null && IndexOf(item) >= 0;
    public void CopyTo(DirectMidiChannelEvent[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (DirectMidiChannelEvent value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<DirectMidiChannelEvent> GetEnumerator()
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (DirectMidiChannelEventValue value in
                compilationSnapshot.QueryValues(0, long.MaxValue))
            {
                yield return FromValue(_project, value);
            }
            yield break;
        }
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.ChannelEventCount; index++)
            {
                DirectMidiChannelEventValue value = _source.GetChannelEvent(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out DirectMidiChannelEvent? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (DirectMidiChannelEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiChannelEvent item)
    {
        if (item is null) return -1;
        if (_addedIndices.TryGetValue(item.Id, out int added))
            return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        return -1;
    }

    public int FindIndex(Predicate<DirectMidiChannelEvent> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (DirectMidiChannelEvent value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out DirectMidiChannelEvent? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource
            && !_removed.Contains(id)
            && _materializedSourceItems.TryGetValue(id, out value))
        {
            return true;
        }
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindChannelEventIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetChannelEvent(index), index, retain: true);
                return true;
            }
        }
        return _addedById.TryGetValue(id, out value);
    }

    public IReadOnlyList<DirectMidiChannelEventMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<MidoraId, DirectMidiChannelEventMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (DirectMidiChannelEventSourceMatch match in
                _sourceIdCache.Resolve(sourceIds, _source.QueryChannelEventsByIds))
            {
                DirectMidiChannelEvent value = _replacements.TryGetValue(
                    match.Value.Id,
                    out DirectMidiChannelEvent? replacement)
                        ? replacement
                        : Materialize(match.Value, match.Index, retain: true);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
        }
        int addedBase = LiveSourceCount;
        foreach (MidoraId id in requested)
        {
            if (_addedById.TryGetValue(id, out DirectMidiChannelEvent? value))
                matches[id] = new(checked(addedBase + _addedIndices[id]), value);
        }
        List<DirectMidiChannelEventMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id)
                && matches.TryGetValue(id, out DirectMidiChannelEventMatch match))
            {
                result.Add(match);
            }
        }
        return result;
    }

    public IEnumerable<DirectMidiChannelEvent> ResolveValuesByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;

        HashSet<MidoraId> unresolvedSourceIds = [];
        foreach (MidoraId id in ids)
        {
            if (_removed.Contains(id)) continue;
            if (_replacements.TryGetValue(id, out DirectMidiChannelEvent? replacement))
            {
                yield return replacement;
            }
            else if (!_clearSource && _source is not null)
            {
                unresolvedSourceIds.Add(id);
            }
        }
        if (unresolvedSourceIds.Count != 0 && _source is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in
                _sourceIdCache.Resolve(
                    unresolvedSourceIds,
                    _source.QueryChannelEventsByIds))
            {
                yield return Materialize(match.Value, match.Index, retain: true);
            }
        }
        foreach (MidoraId id in ids)
        {
            if (_addedById.TryGetValue(id, out DirectMidiChannelEvent? added))
                yield return added;
        }
    }

    public IDisposable BeginBatchChange(IReadOnlyCollection<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Event batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchIncludesAllMaterializedSource = false;
        _batchSourceIds = values
            .Select(static value => value.Id)
            .Where(id => _materializedSourceIds.Contains(id) && !_removed.Contains(id))
            .ToHashSet();
        return new BatchChangeScope(this);
    }

    public IDisposable BeginBatchChangeAll()
    {
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Event batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = true;
        return new BatchChangeScope(this);
    }

    public void Insert(int index, DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        EnsureIdAvailable(item.Id);
        Track(item);
        InsertOverlay(Math.Clamp(index - LiveSourceCount, 0, _added.Count), item);
        Touch();
    }

    public bool Remove(DirectMidiChannelEvent item)
    {
        if (item is null) return false;
        if (_addedIndices.TryGetValue(item.Id, out int addedIndex))
        {
            RemoveOverlayAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            MarkOverlayDirty(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    public int RemoveRange(IReadOnlyCollection<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return 0;
        HashSet<MidoraId> ids = values.Select(static value => value.Id).ToHashSet();
        int removed = RemoveOverlayWhere(ids);
        foreach (MidoraId id in ids)
        {
            if (_clearSource || _removed.Contains(id)) continue;
            int sourceIndex = SourceIndexOf(id);
            if (sourceIndex < 0) continue;
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
            removed++;
        }
        if (removed != 0) Touch();
        return removed;
    }

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return static () => { };
        Dictionary<MidoraId, DirectMidiChannelEvent> requested = [];
        foreach (DirectMidiChannelEvent value in values)
        {
            if (!requested.TryAdd(value.Id, value))
                throw new ArgumentException("Direct MIDI Events must be distinct.", nameof(values));
        }

        List<(int Index, DirectMidiChannelEvent Value)> added = [];
        foreach (MidoraId id in requested.Keys.ToArray())
        {
            if (!_addedIndices.TryGetValue(id, out int index)) continue;
            added.Add((index, _added[index]));
            requested.Remove(id);
        }
        added.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        List<(MidoraId Id, DirectMidiChannelEvent? Replacement)> source = [];
        foreach ((MidoraId id, DirectMidiChannelEvent value) in requested)
        {
            if (_clearSource || _removed.Contains(id) || SourceIndexOf(id) < 0)
                throw new InvalidOperationException("A Direct MIDI Event is no longer present.");
            _replacements.TryGetValue(id, out DirectMidiChannelEvent? replacement);
            if (replacement is not null && !ReferenceEquals(replacement, value))
                throw new InvalidOperationException("A Direct MIDI Event changed before removal.");
            source.Add((id, replacement));
        }

        if (added.Count != 0)
        {
            RemoveOverlayItems(added);
        }
        foreach ((MidoraId id, _) in source)
        {
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
        }
        Touch();

        return () =>
        {
            if (added.Any(value => _addedById.ContainsKey(value.Value.Id)))
                throw new InvalidOperationException("A Direct MIDI Event is already restored.");
            if (added.Count != 0)
                RestoreOverlayItems(added);
            foreach ((MidoraId id, DirectMidiChannelEvent? replacement) in source)
            {
                if (!_removed.Remove(id))
                    throw new InvalidOperationException("A Direct MIDI Event is already restored.");
                if (replacement is not null) _replacements[id] = replacement;
                MarkOverlayDirty(id);
            }
            Touch();
        };
    }

    internal Action RemoveForExactCollision(DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_addedIndices.TryGetValue(item.Id, out int addedIndex))
        {
            RemoveOverlayAt(addedIndex);
            Touch();
            return () =>
            {
                Track(item);
                InsertOverlay(Math.Clamp(addedIndex, 0, _added.Count), item);
                Touch();
            };
        }

        int sourceIndex = SourceIndexOf(item.Id);
        if (_clearSource || sourceIndex < 0 || _removed.Contains(item.Id))
            throw new InvalidOperationException("The conflicting Direct MIDI Event is no longer present.");
        _replacements.TryGetValue(item.Id, out DirectMidiChannelEvent? replacement);
        _removed.Add(item.Id);
        _replacements.Remove(item.Id);
        MarkOverlayDirty(item.Id);
        Touch();
        return () =>
        {
            if (!_removed.Remove(item.Id))
                throw new InvalidOperationException("The conflicting Direct MIDI Event is already restored.");
            if (replacement is not null) _replacements[item.Id] = replacement;
            MarkOverlayDirty(item.Id);
            Touch();
        };
    }

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<DirectMidiChannelEvent> Query(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue sourceValue in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (DirectMidiChannelEventValue edited in _overlayIndex.Query(startTick, endTick))
        {
            if (_replacements.TryGetValue(edited.Id, out DirectMidiChannelEvent? replacement))
                yield return replacement;
            else if (_addedById.TryGetValue(edited.Id, out DirectMidiChannelEvent? added))
                yield return added;
        }
    }

    public IEnumerable<DirectMidiChannelEvent> QueryStartKeys(
        IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in
                _source.QueryChannelEventsAtStarts(keys))
            {
                DirectMidiChannelEventValue sourceValue = match.Value;
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id))
                    continue;
                yield return Materialize(sourceValue, match.Index);
            }
        }
        foreach (long tick in keys.Select(static key => key.Tick).Distinct())
        {
            foreach (DirectMidiChannelEventValue edited in _overlayIndex.QueryAtTick(tick))
            {
                if (!keys.Contains(StartKey(edited))) continue;
                if (_replacements.TryGetValue(edited.Id, out DirectMidiChannelEvent? replacement))
                    yield return replacement;
                else if (_addedById.TryGetValue(edited.Id, out DirectMidiChannelEvent? added))
                    yield return added;
            }
        }
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryValues(long startTick, long endTick)
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (DirectMidiChannelEventValue value in
                compilationSnapshot.QueryValues(startTick, endTick))
            {
                yield return value;
            }
            yield break;
        }
        if (endTick <= startTick) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.Query(startTick, endTick))
            yield return value;
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetChannelEventRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(0, long.MaxValue))
                yield return new(value.Tick, value.Tick, 1);
        }
        foreach (DirectMidiChannelEvent value in _replacements.Values.Concat(_added))
            yield return new(value.Tick, value.Tick, 1);
    }

    public void AccumulateOverviewColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        PureMidiOverviewProjection.Validate(extent, noteStartColumns, eventColumns);
        if (noteStartColumns.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateChannelEventColumns(
                    extent,
                    noteStartColumns,
                    eventColumns,
                    excludedIds);
            if (!accumulated)
            {
                foreach (DirectMidiChannelEventValue value in
                    _source.QueryChannelEvents(0, long.MaxValue))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        value.Kind,
                        value.Data2,
                        value.Tick,
                        extent,
                        noteStartColumns,
                        eventColumns);
                }
            }
        }

        foreach (DirectMidiChannelEvent value in _replacements.Values.Concat(_added))
        {
            PureMidiOverviewProjection.Mark(
                value.Kind,
                value.Data2,
                value.Tick,
                extent,
                noteStartColumns,
                eventColumns);
        }
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryOrderedValues(
        long startTick,
        long endTick)
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (DirectMidiChannelEventValue value in
                compilationSnapshot.QueryOrderedValues(startTick, endTick))
            {
                yield return value;
            }
            yield break;
        }
        if (endTick <= startTick) yield break;
        EnsureOverlayIndex();
        IEnumerable<DirectMidiChannelEventValue> source = [];
        if (!_clearSource && _source is not null)
        {
            source = _source is IPureMidiPlaybackEndpointSource endpoints
                ? endpoints.QueryOrderedChannelEvents(startTick, endTick)
                : _source.QueryChannelEvents(startTick, endTick)
                    .OrderBy(value => value.Tick)
                    .ThenBy(value => value.Order)
                    .ThenBy(value => value.Id);
        }
        source = source.Where(value =>
            !_removed.Contains(value.Id) && !_replacements.ContainsKey(value.Id));
        IEnumerable<DirectMidiChannelEventValue> edited =
            _overlayIndex.Query(startTick, endTick);

        using IEnumerator<DirectMidiChannelEventValue> left = source.GetEnumerator();
        using IEnumerator<DirectMidiChannelEventValue> right = edited.GetEnumerator();
        bool hasLeft = left.MoveNext();
        bool hasRight = right.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && Compare(left.Current, right.Current) <= 0;
            if (takeLeft)
            {
                yield return left.Current;
                hasLeft = left.MoveNext();
            }
            else
            {
                yield return right.Current;
                hasRight = right.MoveNext();
            }
        }

        static int Compare(
            DirectMidiChannelEventValue left,
            DirectMidiChannelEventValue right)
        {
            int result = left.Tick.CompareTo(right.Tick);
            if (result != 0) return result;
            result = left.Order.CompareTo(right.Order);
            return result != 0 ? result : left.Id.CompareTo(right.Id);
        }
    }

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        lock (_snapshotPublicationSync)
        {
            _source = source;
            _sourceIdCache = CreateSourceIdCache();
            _formalCount = source.ChannelEventCount;
            _querySnapshot = null;
        }
    }

    internal void CloneTo(DirectMidiChannelEventCollection target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("A MIDI content root cannot be captured during a batch change.");
            if (target.Count != 0 || target._source is not null)
                throw new InvalidOperationException("A MIDI clone target must be empty.");
            EnsureOverlayIndex();
            target._source = _source;
            target._clearSource = _clearSource;
            target._removed = _formalRemovedSourceIds.ToBuilder();
            target._added.Adopt(_formalAdded);
            target._replacements.Adopt(_formalReplacements);
            target._addedIndices = _addedIndices.ToImmutable().ToBuilder();
            target._sourceIndices = _sourceIndices.ToImmutable().ToBuilder();
            target._materializedSourceValues = _materializedSourceValues.ToImmutable().ToBuilder();
            target._formalRemovedSourceIds = _formalRemovedSourceIds;
            target._formalReplacements = _formalReplacements;
            target._formalAdded = _formalAdded;
            target._formalCount = _formalCount;
            target._generation = _generation;
            target._overlayIndex = _overlayIndex;
            target._querySnapshot = _querySnapshot?.Generation == _generation ? _querySnapshot : null;
        }
    }

    /// <summary>
    /// Adopts a validated immutable result source without materializing its records.
    /// The source owns its page/storage lifetime and must remain immutable while
    /// this collection, an Undo root or a reader snapshot references it.
    /// </summary>
    public void AdoptContentSource(IPureMidiSegmentContentSource source, long generation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        AttachSource(source);
        _generation = generation;
    }

    internal void RestoreFormalSequence(
        DirectMidiChannelEventFormalSequenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The Direct MIDI Event target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The Direct MIDI Event source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed.UnionWith(snapshot.RemovedSourceIds);
        foreach ((MidoraId id, DirectMidiChannelEventValue value) in snapshot.Replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = _source?.FindChannelEventIndex(id) ?? -1;
            if (sourceIndex >= 0)
            {
                DirectMidiChannelEventValue sourceValue = _source!.GetChannelEvent(sourceIndex);
                _materializedSourceIds.Add(id);
                _materializedSourceValues[id] = sourceValue;
                _sourceIndices[id] = sourceIndex;
            }
            DirectMidiChannelEvent replacement = FromValue(_project, value);
            Track(replacement);
            _materializedSourceItems[id] = replacement;
            _replacements[id] = replacement;
            _dirtyOverlayIds.Add(id);
        }
        foreach (DirectMidiChannelEventValue value in snapshot.Added.Enumerate(cancellationToken))
        {
            DirectMidiChannelEvent added = FromValue(_project, value);
            Track(added);
            int index = _added.Count;
            _added.Add(added);
            _addedById.Add(added.Id, added);
            _addedIndices.Add(added.Id, index);
            _dirtyOverlayIds.Add(added.Id);
        }

        lock (_snapshotPublicationSync)
        {
            _formalRemovedSourceIds = snapshot.RemovedSourceIds;
            _formalReplacements = snapshot.Replacements;
            _formalAdded = snapshot.Added;
            _formalCount = snapshot.Count;
            _generation = snapshot.Generation;
            _querySnapshot = null;
            _formalDirtyIds.Clear();
            _pendingFormalAdded = null;
            _formalFullRebuild = false;
        }
    }

    void IDirectMidiChannelEventChangeSink.OnChanged(DirectMidiChannelEvent value)
    {
        bool sourceBacked = _batchChangeDepth != 0
            ? (_batchIncludesAllMaterializedSource
                ? _materializedSourceIds.Contains(value.Id)
                : _batchSourceIds?.Contains(value.Id) == true)
                && !_removed.Contains(value.Id)
            : !_clearSource
                && _materializedSourceIds.Contains(value.Id)
                && !_removed.Contains(value.Id);
        if (sourceBacked)
            _replacements[value.Id] = value;
        MarkOverlayDirty(value.Id);
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.ChannelEventCount - _removed.Count);
    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindChannelEventIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    internal void RestoreFormalSequenceForCompilation(
        DirectMidiChannelEventFormalSequenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The Direct MIDI Event target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The Direct MIDI Event source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed = snapshot.RemovedSourceIds.ToBuilder();
        _added.Adopt(snapshot.Added);
        _replacements.Adopt(snapshot.Replacements);
        _addedIndices = snapshot.AddedIndices.ToBuilder();
        _formalRemovedSourceIds = snapshot.RemovedSourceIds;
        _formalReplacements = snapshot.Replacements;
        _formalAdded = snapshot.Added;
        _formalCount = snapshot.Count;
        _generation = snapshot.Generation;
        IReadOnlySet<MidoraId>? exclusions = snapshot.Source is not null
            && (snapshot.RemovedSourceIds.Count != 0 || snapshot.Replacements.Count != 0)
                ? new PureMidiSourceExclusionSet<DirectMidiChannelEventValue>(
                    snapshot.RemovedSourceIds,
                    snapshot.Replacements)
                : null;
        var emptyIndex = new PureMidiPointOverlayIndex<DirectMidiChannelEventValue>(
            static value => value.Id,
            static value => value.Tick,
            static value => value.Order);
        _overlayIndex = emptyIndex.Create(snapshot.Replacements.Values.Concat(snapshot.Added.Enumerate()));
        _compilationSnapshot = new DirectMidiChannelEventQuerySnapshot(
            snapshot.Source,
            snapshot.ClearsSource,
            exclusions,
            new Dictionary<MidoraId, DirectMidiChannelEventValue>(),
            _overlayIndex,
            CreateSourceIdCache(),
            snapshot.Count,
            snapshot.Generation);
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        if (visibleIndex >= LiveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.ChannelEventCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetChannelEvent(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged event index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private static DirectMidiEventStartKey StartKey(DirectMidiChannelEvent value) => new(
        value.Tick,
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
                ? value.Data1
                : 0);

    private static DirectMidiEventStartKey StartKey(DirectMidiChannelEventValue value) => new(
        value.Tick,
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
                ? value.Data1
                : 0);

    private DirectMidiChannelEvent Materialize(
        DirectMidiChannelEventValue value,
        int sourceIndex = -1,
        bool retain = false)
    {
        if (_materializedSourceItems.TryGetValue(value.Id, out DirectMidiChannelEvent? existing))
        {
            if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
            return existing;
        }
        _materializedSourceIds.Add(value.Id);
        _materializedSourceValues.TryAdd(value.Id, value);
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        DirectMidiChannelEvent result = new(_project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            Data1 = value.Data1,
            Data2 = value.Data2,
            Order = value.Order
        };
        Track(result);
        if (retain) _materializedSourceItems.Add(value.Id, result);
        return result;
    }

    private void AddOverlay(DirectMidiChannelEvent value)
    {
        int index = _added.Count;
        _added.Add(value);
        _addedById[value.Id] = value;
        _addedIndices[value.Id] = index;
        if (_pendingFormalAdded is not null)
            _pendingFormalAdded = _pendingFormalAdded.Append(ToValue(value));
        MarkOverlayDirty(value.Id);
    }

    private void InsertOverlay(int index, DirectMidiChannelEvent value)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.Insert(index, ToValue(value));
        _added.Insert(index, value);
        _addedById[value.Id] = value;
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
        MarkOverlayDirty(value.Id);
    }

    private DirectMidiChannelEvent RemoveOverlayAt(int index)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices([index]);
        DirectMidiChannelEvent value = _added[index];
        _added.RemoveAt(index);
        _addedById.Remove(value.Id);
        _addedIndices.Remove(value.Id);
        MarkOverlayDirty(value.Id);
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
        return value;
    }

    private int RemoveOverlayWhere(IReadOnlySet<MidoraId> ids)
    {
        if (ids.Count == 0 || _added.Count == 0) return 0;
        List<(int Index, DirectMidiChannelEvent Value)> removed = new(
            Math.Min(ids.Count, _added.Count));
        foreach (MidoraId id in ids)
        {
            if (_addedIndices.TryGetValue(id, out int index))
                removed.Add((index, _added[index]));
        }
        removed.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return RemoveOverlayItems(removed);
    }

    private int RemoveOverlayItems(
        IReadOnlyList<(int Index, DirectMidiChannelEvent Value)> removed)
    {
        if (removed.Count == 0) return 0;
        int first = removed[0].Index;
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices(
            removed.Select(static value => value.Index).ToArray());
        foreach ((_, DirectMidiChannelEvent value) in removed)
        {
            _addedById.Remove(value.Id);
            _addedIndices.Remove(value.Id);
            MarkOverlayDirty(value.Id);
        }
        int write = first;
        int removedIndex = 0;
        for (int read = first; read < _added.Count; read++)
        {
            if (removedIndex < removed.Count && removed[removedIndex].Index == read)
            {
                removedIndex++;
                continue;
            }
            DirectMidiChannelEvent value = _added[read];
            if (write != read) _added[write] = value;
            _addedIndices[value.Id] = write++;
        }
        if (removedIndex != removed.Count)
            throw new InvalidOperationException("The Direct MIDI Event overlay removal indices are invalid.");
        _added.RemoveRange(write, removed.Count);
        return removed.Count;
    }

    private void RestoreOverlayItems(
        IReadOnlyList<(int Index, DirectMidiChannelEvent Value)> restored)
    {
        int first = restored[0].Index;
        int finalCount = checked(_added.Count + restored.Count);
        List<DirectMidiChannelEvent> suffix = new(finalCount - first);
        int liveIndex = first;
        int restoredIndex = 0;
        for (int index = first; index < finalCount; index++)
        {
            if (restoredIndex < restored.Count && restored[restoredIndex].Index == index)
                suffix.Add(restored[restoredIndex++].Value);
            else if ((uint)liveIndex < (uint)_added.Count)
                suffix.Add(_added[liveIndex++]);
            else
                throw new InvalidOperationException("The Direct MIDI Event overlay changed before restoration.");
        }
        if (restoredIndex != restored.Count || liveIndex != _added.Count)
            throw new InvalidOperationException("The Direct MIDI Event overlay cannot be restored exactly.");
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.InsertAtFinalIndices(
            restored.Select(static value => new KeyValuePair<int, DirectMidiChannelEventValue>(
                value.Index,
                ToValue(value.Value))).ToArray());
        _added.RemoveRange(first, _added.Count - first);
        _added.AddRange(suffix);
        for (int index = first; index < _added.Count; index++)
        {
            DirectMidiChannelEvent value = _added[index];
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
        }
        foreach ((_, DirectMidiChannelEvent value) in restored)
        {
            Track(value);
            MarkOverlayDirty(value.Id);
        }
    }

    private void RebuildAddedIndex(bool formalStructureAlreadyUpdated = false)
    {
        if (!formalStructureAlreadyUpdated)
            throw new InvalidOperationException("The formal Direct MIDI Event order was not updated.");
        _addedById.Clear();
        _addedIndices.Clear();
        for (int index = 0; index < _added.Count; index++)
        {
            DirectMidiChannelEvent value = _added[index];
            Track(value);
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
            MarkOverlayDirty(value.Id);
        }
    }

    private void BeginFormalStructuralEdit()
    {
        if (_pendingFormalAdded is not null) return;
        PersistentFormalValueSequence<DirectMidiChannelEventValue> current =
            _formalAdded.AppendRange(_added, _formalAdded.Count, static value => ToValue(value));
        Dictionary<int, DirectMidiChannelEventValue>? updates = null;
        foreach (MidoraId id in _formalDirtyIds)
        {
            if (_addedIndices.TryGetValue(id, out int index) && index < current.Count)
                (updates ??= [])[index] = ToValue(_added[index]);
        }
        _pendingFormalAdded = updates is null ? current : current.ReplaceBatch(updates);
    }

    private void MarkOverlayDirty(MidoraId id)
    {
        _dirtyOverlayIds.Add(id);
        int capturedAddedCount = _pendingFormalAdded?.Count ?? _formalAdded.Count;
        if (!_addedIndices.TryGetValue(id, out int addedIndex)
            || addedIndex < capturedAddedCount)
        {
            _formalDirtyIds.Add(id);
        }
    }

    private void EnsureOverlayIndex()
    {
        if (_dirtyOverlayIds.Count == 0) return;
        _overlayIndex = _overlayIndex.ReplaceBatch(_dirtyOverlayIds, id =>
        {
            if (_replacements.TryGetValue(id, out DirectMidiChannelEvent? replacement))
                return ToValue(replacement);
            return _addedById.TryGetValue(id, out DirectMidiChannelEvent? added)
                ? ToValue(added)
                : null;
        });
        _dirtyOverlayIds.Clear();
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        EnsureOverlayIndex();
        List<DirectMidiChannelEventValue>? sourceValues = null;
        if (!_clearSource && _source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEvents(startTick, endTick, sourceValues))
                    return false;
            }
            else
            {
                sourceValues.AddRange(_source.QueryChannelEvents(startTick, endTick));
            }
        }
        if (sourceValues is not null)
        {
            foreach (DirectMidiChannelEventValue value in sourceValues)
            {
                if (!_removed.Contains(value.Id) && !_replacements.ContainsKey(value.Id))
                    destination.Add(value);
            }
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(
        long startTick,
        long endTick,
        CancellationToken cancellationToken)
    {
        if (!_clearSource && _source is IPureMidiCachedContentSource cached && endTick > startTick)
            cached.PrefetchChannelEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        if (ids.Count == 0) return true;
        EnsureOverlayIndex();
        List<DirectMidiChannelEventSourceMatch>? sourceMatches = null;
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            sourceIds.ExceptWith(_removed);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEventsByIds(sourceIds, sourceMatches))
                    return false;
            }
            else
            {
                sourceMatches.AddRange(_source.QueryChannelEventsByIds(sourceIds));
            }
        }

        Dictionary<MidoraId, DirectMidiChannelEventValue> values = [];
        if (sourceMatches is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in sourceMatches)
            {
                values[match.Value.Id] = _replacements.TryGetValue(
                    match.Value.Id,
                    out DirectMidiChannelEvent? replacement)
                        ? ToValue(replacement)
                        : match.Value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.ResolveByIds(ids))
            values[value.Id] = value;
        foreach (MidoraId id in ids)
            if (values.TryGetValue(id, out DirectMidiChannelEventValue value)) destination.Add(value);
        return true;
    }

    public void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_clearSource || _source is not IPureMidiCachedContentSource cached || ids.Count == 0)
            return;
        HashSet<MidoraId> sourceIds = [.. ids];
        sourceIds.ExceptWith(_removed);
        cached.PrefetchChannelEventsByIds(sourceIds, cancellationToken);
    }

    private void Track(DirectMidiChannelEvent value) => value.SetChangeSink(this);

    private void EnsureIdAvailable(MidoraId id, MidoraId replacingId = default)
    {
        if (id == default) throw new ArgumentOutOfRangeException(nameof(id));
        if (id == replacingId) return;
        if (_addedById.ContainsKey(id)
            || _source is not null && _source.FindChannelEventIndex(id) >= 0)
        {
            throw new InvalidOperationException(
                "A Direct MIDI Event collection cannot contain duplicate Stable IDs.");
        }
    }

    private void PublishFormalRoots()
    {
        if (_formalFullRebuild)
        {
            _formalRemovedSourceIds = _removed.ToImmutableHashSet();
            _formalReplacements = _replacements.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => ToValue(pair.Value));
            _formalAdded = PersistentFormalValueSequence<DirectMidiChannelEventValue>.Create(
                _added,
                static value => ToValue(value));
        }
        else
        {
            if (_formalDirtyIds.Count != 0)
            {
                var removed = _formalRemovedSourceIds.ToBuilder();
                var replacements = _formalReplacements.ToBuilder();
                foreach (MidoraId id in _formalDirtyIds)
                {
                    if (_removed.Contains(id)) removed.Add(id);
                    else removed.Remove(id);
                    if (_replacements.TryGetValue(id, out DirectMidiChannelEvent? replacement))
                        replacements[id] = ToValue(replacement);
                    else
                        replacements.Remove(id);
                }
                _formalRemovedSourceIds = removed.ToImmutable();
                _formalReplacements = replacements.ToImmutable();
            }

            PersistentFormalValueSequence<DirectMidiChannelEventValue> next =
                _pendingFormalAdded ?? _formalAdded.AppendRange(
                    _added,
                    _formalAdded.Count,
                    static value => ToValue(value));
            Dictionary<int, DirectMidiChannelEventValue>? updated = null;
            foreach (MidoraId id in _formalDirtyIds)
            {
                if (_addedIndices.TryGetValue(id, out int index) && index < next.Count)
                {
                    (updated ??= [])[index] = ToValue(_added[index]);
                }
            }
            _formalAdded = updated is null ? next : next.ReplaceBatch(updated);
        }

        _formalCount = Count;
        _added.Commit(_formalAdded);
        _replacements.Commit(_formalReplacements);
        _formalDirtyIds.Clear();
        _pendingFormalAdded = null;
        _formalFullRebuild = false;
    }

    private void Touch()
    {
        if (_batchChangeDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        lock (_snapshotPublicationSync)
        {
            PublishFormalRoots();
            _generation++;
        }
    }

    private void EndBatchChange()
    {
        if (_batchChangeDepth != 1)
            throw new InvalidOperationException("No Direct MIDI Event batch change is active.");
        CollapseSourceEquivalentReplacements();
        _batchChangeDepth = 0;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = false;
        if (_batchChanged)
        {
            lock (_snapshotPublicationSync)
            {
                PublishFormalRoots();
                _generation++;
            }
        }
        _batchChanged = false;
    }

    private void CollapseSourceEquivalentReplacements()
    {
        if (_source is null || _clearSource) return;
        IEnumerable<MidoraId> candidates = _batchIncludesAllMaterializedSource
            ? _materializedSourceIds
            : _batchSourceIds ?? [];
        foreach (MidoraId id in candidates)
        {
            if (!_replacements.TryGetValue(id, out DirectMidiChannelEvent? replacement))
                continue;
            if (_materializedSourceValues.TryGetValue(id, out DirectMidiChannelEventValue source)
                && Matches(replacement, source))
            {
                _replacements.Remove(id);
                MarkOverlayDirty(id);
            }
        }
    }

    private static bool Matches(
        DirectMidiChannelEvent value,
        DirectMidiChannelEventValue source) =>
        value.Id == source.Id
        && value.Tick == source.Tick
        && value.Kind == source.Kind
        && value.Data1 == source.Data1
        && value.Data2 == source.Data2
        && value.Order == source.Order;

    private sealed class BatchChangeScope(DirectMidiChannelEventCollection owner) : IDisposable
    {
        private DirectMidiChannelEventCollection? _owner = owner;

        public void Dispose()
        {
            DirectMidiChannelEventCollection? value = Interlocked.Exchange(ref _owner, null);
            value?.EndBatchChange();
        }
    }

    private static DirectMidiChannelEventValue ToValue(DirectMidiChannelEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.Data1,
        value.Data2,
        value.Order);

    private static DirectMidiChannelEvent FromValue(
        MidoraProject project,
        DirectMidiChannelEventValue value) => new(project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            Data1 = value.Data1,
            Data2 = value.Data2,
            Order = value.Order
        };

    private static DirectMidiChannelEvent Clone(
        MidoraProject project,
        DirectMidiChannelEvent source,
        IDirectMidiChannelEventChangeSink sink)
    {
        DirectMidiChannelEvent result = new(project, source.Id)
        {
            Tick = source.Tick,
            Kind = source.Kind,
            Data1 = source.Data1,
            Data2 = source.Data2,
            Order = source.Order
        };
        result.SetChangeSink(sink);
        return result;
    }
    private static PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> CreateSourceIdCache() =>
        new(static match => match.Value.Id, static match => match.Index);
}

public sealed class OpaqueMidiEventCollection : IList<OpaqueMidiEvent>, IReadOnlyList<OpaqueMidiEvent>, IOpaqueMidiEventChangeSink
{
    private readonly MidoraProject _project;
    private readonly PureMidiCowList<OpaqueMidiEvent, OpaqueMidiEventValue> _added;
    private readonly PureMidiCowIndexedLookup<OpaqueMidiEvent> _addedById;
    private ImmutableDictionary<MidoraId, int>.Builder _addedIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private readonly PureMidiCowDictionary<OpaqueMidiEvent, OpaqueMidiEventValue> _replacements;
    private ImmutableHashSet<MidoraId>.Builder _removed = ImmutableHashSet.CreateBuilder<MidoraId>();
    private ImmutableDictionary<MidoraId, OpaqueMidiEventValue>.Builder _materializedSourceValues = ImmutableDictionary.CreateBuilder<MidoraId, OpaqueMidiEventValue>();
    private readonly Dictionary<MidoraId, OpaqueMidiEvent> _materializedSourceItems = [];
    private ImmutableDictionary<MidoraId, int>.Builder _sourceIndices = ImmutableDictionary.CreateBuilder<MidoraId, int>();
    private PureMidiPointOverlayIndex<OpaqueMidiEventValue> _overlayIndex = new(
        static value => value.Id,
        static value => value.Tick,
        static value => value.Order);
    private readonly HashSet<MidoraId> _dirtyOverlayIds = [];
    private readonly object _snapshotPublicationSync = new();
    private OpaqueMidiEventQuerySnapshot? _querySnapshot;
    private PureMidiOpaqueIdResolutionCache _sourceIdCache =
        CreateSourceIdCache();
    private ImmutableHashSet<MidoraId> _formalRemovedSourceIds =
        ImmutableHashSet<MidoraId>.Empty;
    private ImmutableDictionary<MidoraId, OpaqueMidiEventValue> _formalReplacements =
        ImmutableDictionary<MidoraId, OpaqueMidiEventValue>.Empty;
    private PersistentFormalValueSequence<OpaqueMidiEventValue> _formalAdded =
        PersistentFormalValueSequence<OpaqueMidiEventValue>.Empty;
    private PersistentFormalValueSequence<OpaqueMidiEventValue>? _pendingFormalAdded;
    private readonly HashSet<MidoraId> _formalDirtyIds = [];
    private bool _formalFullRebuild;
    private int _formalCount;
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;
    private int _batchChangeDepth;
    private bool _batchChanged;
    private HashSet<MidoraId>? _batchSourceIds;
    private bool _batchIncludesAllMaterializedSource;
    private OpaqueMidiEventQuerySnapshot? _compilationSnapshot;

    internal OpaqueMidiEventCollection(MidoraProject project)
    {
        _project = project;
        _added = new(value =>
        {
            OpaqueMidiEvent item = FromValue(_project, value);
            Track(item);
            return item;
        });
        _addedById = new(
            id => _addedIndices.TryGetValue(id, out int index) ? index : null,
            index => _added[index]);
        _replacements = new(MaterializeReplacement);
    }

    private OpaqueMidiEvent MaterializeReplacement(OpaqueMidiEventValue value)
    {
        OpaqueMidiEvent item = FromValue(_project, value);
        _materializedSourceItems[value.Id] = item;
        Track(item);
        return item;
    }

    public int Count => _compilationSnapshot?.Count
        ?? checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<OpaqueMidiEvent> EditedItems => _replacements.Values.Concat(_added);
    internal IEnumerable<OpaqueMidiEventValue> EditedValues =>
        _formalReplacements.Values.Concat(_formalAdded.Enumerate());
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds =>
        _compilationSnapshot is null ? _removed : _formalRemovedSourceIds;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    internal PureMidiPointOverlayIndex<OpaqueMidiEventValue> CaptureOverlayIndex()
    {
        lock (_snapshotPublicationSync)
        {
            EnsureOverlayIndex();
            return _overlayIndex;
        }
    }

    public OpaqueMidiEventQuerySnapshot CreateQuerySnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth == 0
                && _querySnapshot is { } cached
                && cached.Generation == _generation)
            {
                return cached;
            }
            EnsureOverlayIndex();
            IReadOnlySet<MidoraId>? exclusions = _batchChangeDepth == 0
                ? _formalRemovedSourceIds.Count == 0 && _formalReplacements.Count == 0
                    ? null
                    : new PureMidiSourceExclusionSet<OpaqueMidiEventValue>(
                        _formalRemovedSourceIds,
                        _formalReplacements)
                : SourceExclusions();
            var snapshot = new OpaqueMidiEventQuerySnapshot(
                _source,
                _clearSource,
                exclusions,
                _materializedSourceValues,
                _overlayIndex,
                _sourceIdCache,
                Count,
                _generation);
            if (_batchChangeDepth == 0) _querySnapshot = snapshot;
            return snapshot;
        }
    }

    /// <summary>Captures committed values in formal order without editable facades
    /// or payload copies. Consume within the source owner's lifetime.</summary>
    public IEnumerable<OpaqueMidiEventValue> EnumerateValues(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("Cannot capture opaque MIDI Event values during an uncommitted batch change.");
            return PureMidiReadOnlyValues.Enumerate(
                _source, _clearSource ? 0 : _source?.OpaqueEventCount ?? 0,
                static (source, index) => source.GetOpaqueEvent(index), static value => value.Id,
                _formalRemovedSourceIds, _formalReplacements, _formalAdded, cancellationToken);
        }
    }

    public OpaqueMidiEventObjectSource CreateObjectSource()
    {
        lock (_snapshotPublicationSync)
        {
            OpaqueMidiEventFormalSequenceSnapshot formal = CreateFormalSequenceSnapshot();
            return new(formal, CreateQuerySnapshot());
        }
    }

    internal OpaqueMidiEventFormalSequenceSnapshot CreateFormalSequenceSnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            return new(
                _source,
                _clearSource,
                _formalRemovedSourceIds,
                _formalReplacements,
                _formalAdded,
                _formalCount,
                _generation)
            {
                AddedIndices = _addedIndices.ToImmutable()
            };
        }
    }

    public OpaqueMidiEvent this[int index]
    {
        get
        {
            if (_compilationSnapshot is not null)
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                using IEnumerator<OpaqueMidiEvent> values = GetEnumerator();
                for (int current = 0; current <= index; current++) values.MoveNext();
                return values.Current;
            }
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                OpaqueMidiEventValue value = _source!.GetOpaqueEvent(sourceIndex);
                return _replacements.TryGetValue(value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(value, sourceIndex, retain: true);
            }
            return _added[checked(index - LiveSourceCount)];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                OpaqueMidiEventValue original = _source!.GetOpaqueEvent(sourceIndex);
                EnsureIdAvailable(value.Id, original.Id);
                _materializedSourceValues.TryAdd(original.Id, original);
                OpaqueMidiEvent? previous = _replacements.GetValueOrDefault(original.Id)
                    ?? _materializedSourceItems.GetValueOrDefault(original.Id);
                if (ReferenceEquals(previous, value)) return;
                if (value.Id == original.Id)
                {
                    previous?.SetChangeSink(null);
                    Track(value);
                    _materializedSourceItems[original.Id] = value;
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                    MarkOverlayDirty(original.Id);
                }
                else
                {
                    previous?.SetChangeSink(null);
                    _materializedSourceItems.Remove(original.Id);
                    _removed.Add(original.Id);
                    _replacements.Remove(original.Id);
                    MarkOverlayDirty(original.Id);
                    Track(value);
                    AddOverlay(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            OpaqueMidiEvent old = _added[addedIndex];
            if (ReferenceEquals(old, value)) return;
            EnsureIdAvailable(value.Id, old.Id);
            old.SetChangeSink(null);
            Track(value);
            MarkOverlayDirty(old.Id);
            _addedById.Remove(old.Id);
            _addedIndices.Remove(old.Id);
            _added[addedIndex] = value;
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = addedIndex;
            MarkOverlayDirty(value.Id);
            Touch();
        }
    }

    public void Add(OpaqueMidiEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureIdAvailable(item.Id);
        Track(item);
        AddOverlay(item);
        Touch();
    }

    public void AddRange(IEnumerable<OpaqueMidiEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<OpaqueMidiEvent> pending = values as IReadOnlyList<OpaqueMidiEvent>
            ?? [.. values];
        HashSet<MidoraId> pendingIds = [];
        foreach (OpaqueMidiEvent value in pending)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!pendingIds.Add(value.Id))
                throw new InvalidOperationException(
                    "An imported MIDI Event range cannot contain duplicate Stable IDs.");
            EnsureIdAvailable(value.Id);
        }
        using IDisposable batch = BeginBatchChange([]);
        foreach (OpaqueMidiEvent value in pending) Add(value);
    }

    public IDisposable BeginBatchChange(IReadOnlyCollection<OpaqueMidiEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Nested imported MIDI Event batches are not supported.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchIncludesAllMaterializedSource = false;
        _batchSourceIds = values
            .Select(static value => value.Id)
            .Where(id => _materializedSourceValues.ContainsKey(id) && !_removed.Contains(id))
            .ToHashSet();
        return new OpaqueBatchChangeScope(this);
    }

    public IDisposable BeginBatchChangeAll()
    {
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Nested imported MIDI Event batches are not supported.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = true;
        return new OpaqueBatchChangeScope(this);
    }

    public void Clear()
    {
        foreach (OpaqueMidiEvent value in _added.MaterializedItems) value.SetChangeSink(null);
        foreach (OpaqueMidiEvent value in _materializedSourceItems.Values)
            value.SetChangeSink(null);
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _materializedSourceValues.Clear();
        _materializedSourceItems.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        _addedById.Clear();
        _addedIndices.Clear();
        _overlayIndex = new(
            static value => value.Id,
            static value => value.Tick,
            static value => value.Order);
        _dirtyOverlayIds.Clear();
        _formalFullRebuild = true;
        _pendingFormalAdded = null;
        Touch();
    }

    public bool Contains(OpaqueMidiEvent item) => item is not null && IndexOf(item) >= 0;
    public void CopyTo(OpaqueMidiEvent[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (OpaqueMidiEvent value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<OpaqueMidiEvent> GetEnumerator()
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (OpaqueMidiEventValue value in
                compilationSnapshot.QueryValues(0, long.MaxValue))
            {
                yield return FromValue(_project, value);
            }
            yield break;
        }
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.OpaqueEventCount; index++)
            {
                OpaqueMidiEventValue value = _source.GetOpaqueEvent(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (OpaqueMidiEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(OpaqueMidiEvent item)
    {
        if (item is null) return -1;
        if (_addedIndices.TryGetValue(item.Id, out int added))
            return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        return -1;
    }

    public int FindIndex(Predicate<OpaqueMidiEvent> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (OpaqueMidiEvent value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out OpaqueMidiEvent? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource
            && !_removed.Contains(id)
            && _materializedSourceItems.TryGetValue(id, out value))
        {
            return true;
        }
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindOpaqueEventIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetOpaqueEvent(index), index, retain: true);
                return true;
            }
        }
        return _addedById.TryGetValue(id, out value);
    }

    public IReadOnlyList<OpaqueMidiEventMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<MidoraId, OpaqueMidiEventMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (OpaqueMidiEventSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source))
            {
                OpaqueMidiEvent value = _replacements.TryGetValue(match.Value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(match.Value, match.Index, retain: true);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
        }
        int addedBase = LiveSourceCount;
        foreach (MidoraId id in requested)
        {
            if (_addedById.TryGetValue(id, out OpaqueMidiEvent? value))
                matches[id] = new(checked(addedBase + _addedIndices[id]), value);
        }
        List<OpaqueMidiEventMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id) && matches.TryGetValue(id, out OpaqueMidiEventMatch match))
                result.Add(match);
        }
        return result;
    }

    public IEnumerable<OpaqueMidiEvent> ResolveValuesByIds(IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;

        HashSet<MidoraId> unresolvedSourceIds = [];
        foreach (MidoraId id in ids)
        {
            if (_removed.Contains(id)) continue;
            if (_replacements.TryGetValue(id, out OpaqueMidiEvent? replacement))
            {
                yield return replacement;
            }
            else if (!_clearSource && _source is not null)
            {
                unresolvedSourceIds.Add(id);
            }
        }
        if (unresolvedSourceIds.Count != 0 && _source is not null)
        {
            foreach (OpaqueMidiEventSourceMatch match in _sourceIdCache.Resolve(
                unresolvedSourceIds,
                _source))
                yield return Materialize(match.Value, match.Index, retain: true);
        }
        foreach (MidoraId id in ids)
        {
            if (_addedById.TryGetValue(id, out OpaqueMidiEvent? added)) yield return added;
        }
    }

    public void Insert(int index, OpaqueMidiEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        EnsureIdAvailable(item.Id);
        Track(item);
        InsertOverlay(Math.Clamp(index - LiveSourceCount, 0, _added.Count), item);
        Touch();
    }

    public bool Remove(OpaqueMidiEvent item)
    {
        if (item is null) return false;
        if (_addedIndices.TryGetValue(item.Id, out int addedIndex))
        {
            RemoveOverlayAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            MarkOverlayDirty(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    public int RemoveRange(IReadOnlyCollection<OpaqueMidiEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return 0;
        HashSet<MidoraId> ids = values.Select(static value => value.Id).ToHashSet();
        int removed = RemoveOverlayWhere(ids);
        foreach (MidoraId id in ids)
        {
            if (_clearSource || _removed.Contains(id)) continue;
            int sourceIndex = SourceIndexOf(id);
            if (sourceIndex < 0) continue;
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
            removed++;
        }
        if (removed != 0) Touch();
        return removed;
    }

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<OpaqueMidiEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return static () => { };
        Dictionary<MidoraId, OpaqueMidiEvent> requested = [];
        foreach (OpaqueMidiEvent value in values)
        {
            if (!requested.TryAdd(value.Id, value))
                throw new ArgumentException("Imported MIDI Events must be distinct.", nameof(values));
        }

        List<(int Index, OpaqueMidiEvent Value)> added = [];
        foreach (MidoraId id in requested.Keys.ToArray())
        {
            if (!_addedIndices.TryGetValue(id, out int index)) continue;
            added.Add((index, _added[index]));
            requested.Remove(id);
        }
        added.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        List<(MidoraId Id, OpaqueMidiEvent? Replacement)> source = [];
        foreach ((MidoraId id, OpaqueMidiEvent value) in requested)
        {
            if (_clearSource || _removed.Contains(id) || SourceIndexOf(id) < 0)
                throw new InvalidOperationException("An imported MIDI Event is no longer present.");
            _replacements.TryGetValue(id, out OpaqueMidiEvent? replacement);
            if (replacement is not null && !ReferenceEquals(replacement, value))
                throw new InvalidOperationException("An imported MIDI Event changed before removal.");
            source.Add((id, replacement));
        }

        if (added.Count != 0)
        {
            RemoveOverlayItems(added);
        }
        foreach ((MidoraId id, _) in source)
        {
            _removed.Add(id);
            _replacements.Remove(id);
            MarkOverlayDirty(id);
        }
        Touch();

        return () =>
        {
            if (added.Any(value => _addedById.ContainsKey(value.Value.Id)))
                throw new InvalidOperationException("An imported MIDI Event is already restored.");
            if (added.Count != 0)
                RestoreOverlayItems(added);
            foreach ((MidoraId id, OpaqueMidiEvent? replacement) in source)
            {
                if (!_removed.Remove(id))
                    throw new InvalidOperationException("An imported MIDI Event is already restored.");
                if (replacement is not null) _replacements[id] = replacement;
                MarkOverlayDirty(id);
            }
            Touch();
        };
    }

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<OpaqueMidiEvent> Query(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue sourceValue in _source.QueryOpaqueEvents(startTick, endTick))
            {
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (OpaqueMidiEventValue edited in _overlayIndex.Query(startTick, endTick))
        {
            if (_replacements.TryGetValue(edited.Id, out OpaqueMidiEvent? replacement))
                yield return replacement;
            else if (_addedById.TryGetValue(edited.Id, out OpaqueMidiEvent? added))
                yield return added;
        }
    }

    public IEnumerable<OpaqueMidiEventValue> QueryValues(long startTick, long endTick)
    {
        if (_compilationSnapshot is { } compilationSnapshot)
        {
            foreach (OpaqueMidiEventValue value in
                compilationSnapshot.QueryValues(startTick, endTick))
            {
                yield return value;
            }
            yield break;
        }
        if (endTick <= startTick) yield break;
        EnsureOverlayIndex();
        if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(startTick, endTick))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.Query(startTick, endTick))
            yield return value;
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        EnsureOverlayIndex();
        List<OpaqueMidiEventValue>? sourceValues = null;
        if (!_clearSource && _source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEvents(startTick, endTick, sourceValues))
                    return false;
            }
            else
            {
                sourceValues.AddRange(_source.QueryOpaqueEvents(startTick, endTick));
            }
        }
        if (sourceValues is not null)
        {
            foreach (OpaqueMidiEventValue value in sourceValues)
            {
                if (!_removed.Contains(value.Id) && !_replacements.ContainsKey(value.Id))
                    destination.Add(value);
            }
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(
        long startTick,
        long endTick,
        CancellationToken cancellationToken)
    {
        if (!_clearSource && _source is IPureMidiCachedContentSource cached && endTick > startTick)
            cached.PrefetchOpaqueEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        if (ids.Count == 0) return true;
        EnsureOverlayIndex();
        List<OpaqueMidiEventSourceMatch>? sourceMatches = null;
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            sourceIds.ExceptWith(_removed);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEventsByIds(sourceIds, sourceMatches))
                    return false;
            }
            else
            {
                sourceMatches.AddRange(_source.QueryOpaqueEventsByIds(sourceIds));
            }
        }

        Dictionary<MidoraId, OpaqueMidiEventValue> values = [];
        if (sourceMatches is not null)
        {
            foreach (OpaqueMidiEventSourceMatch match in sourceMatches)
            {
                values[match.Value.Id] = _replacements.TryGetValue(
                    match.Value.Id,
                    out OpaqueMidiEvent? replacement)
                        ? ToValue(replacement)
                        : match.Value;
            }
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.ResolveByIds(ids))
            values[value.Id] = value;
        foreach (MidoraId id in ids)
            if (values.TryGetValue(id, out OpaqueMidiEventValue value)) destination.Add(value);
        return true;
    }

    public void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_clearSource || _source is not IPureMidiCachedContentSource cached || ids.Count == 0)
            return;
        HashSet<MidoraId> sourceIds = [.. ids];
        sourceIds.ExceptWith(_removed);
        cached.PrefetchOpaqueEventsByIds(sourceIds, cancellationToken);
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetOpaqueEventRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(0, long.MaxValue))
                yield return new(value.Tick, value.Tick, 1);
        }
        foreach (OpaqueMidiEvent value in _replacements.Values.Concat(_added))
            yield return new(value.Tick, value.Tick, 1);
    }

    public void AccumulateOverviewColumns(long extent, Span<byte> destination)
    {
        PureMidiOverviewProjection.Validate(extent, destination);
        if (destination.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateOpaqueEventColumns(
                    extent,
                    destination,
                    excludedIds);
            if (!accumulated)
            {
                foreach (OpaqueMidiEventValue value in
                    _source.QueryOpaqueEvents(0, long.MaxValue))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(destination, value.Tick, extent);
                }
            }
        }

        foreach (OpaqueMidiEvent value in _replacements.Values.Concat(_added))
            PureMidiOverviewProjection.Mark(destination, value.Tick, extent);
    }

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        lock (_snapshotPublicationSync)
        {
            _source = source;
            _sourceIdCache = CreateSourceIdCache();
            _formalCount = source.OpaqueEventCount;
            _querySnapshot = null;
        }
    }

    internal void CloneTo(OpaqueMidiEventCollection target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_snapshotPublicationSync)
        {
            if (_batchChangeDepth != 0)
                throw new InvalidOperationException("A MIDI content root cannot be captured during a batch change.");
            if (target.Count != 0 || target._source is not null)
                throw new InvalidOperationException("A MIDI clone target must be empty.");
            EnsureOverlayIndex();
            target._source = _source;
            target._clearSource = _clearSource;
            target._removed = _formalRemovedSourceIds.ToBuilder();
            target._added.Adopt(_formalAdded);
            target._replacements.Adopt(_formalReplacements);
            target._addedIndices = _addedIndices.ToImmutable().ToBuilder();
            target._sourceIndices = _sourceIndices.ToImmutable().ToBuilder();
            target._materializedSourceValues = _materializedSourceValues.ToImmutable().ToBuilder();
            target._formalRemovedSourceIds = _formalRemovedSourceIds;
            target._formalReplacements = _formalReplacements;
            target._formalAdded = _formalAdded;
            target._formalCount = _formalCount;
            target._generation = _generation;
            target._overlayIndex = _overlayIndex;
            target._querySnapshot = _querySnapshot?.Generation == _generation ? _querySnapshot : null;
        }
    }

    /// <summary>
    /// Adopts a validated immutable result source without materializing its records.
    /// The source owns its page/storage lifetime and must remain immutable while
    /// this collection, an Undo root or a reader snapshot references it.
    /// </summary>
    public void AdoptContentSource(IPureMidiSegmentContentSource source, long generation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        AttachSource(source);
        _generation = generation;
    }

    internal void RestoreFormalSequence(
        OpaqueMidiEventFormalSequenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The opaque MIDI Event target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The opaque MIDI Event source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed.UnionWith(snapshot.RemovedSourceIds);
        foreach ((MidoraId id, OpaqueMidiEventValue value) in snapshot.Replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = _source?.FindOpaqueEventIndex(id) ?? -1;
            if (sourceIndex >= 0)
            {
                OpaqueMidiEventValue sourceValue = _source!.GetOpaqueEvent(sourceIndex);
                _materializedSourceValues[id] = sourceValue;
                _sourceIndices[id] = sourceIndex;
            }
            OpaqueMidiEvent replacement = FromValue(_project, value);
            Track(replacement);
            _materializedSourceItems[id] = replacement;
            _replacements[id] = replacement;
            _dirtyOverlayIds.Add(id);
        }
        foreach (OpaqueMidiEventValue value in snapshot.Added.Enumerate(cancellationToken))
        {
            OpaqueMidiEvent added = FromValue(_project, value);
            Track(added);
            int index = _added.Count;
            _added.Add(added);
            _addedById.Add(added.Id, added);
            _addedIndices.Add(added.Id, index);
            _dirtyOverlayIds.Add(added.Id);
        }

        lock (_snapshotPublicationSync)
        {
            _formalRemovedSourceIds = snapshot.RemovedSourceIds;
            _formalReplacements = snapshot.Replacements;
            _formalAdded = snapshot.Added;
            _formalCount = snapshot.Count;
            _generation = snapshot.Generation;
            _querySnapshot = null;
            _formalDirtyIds.Clear();
            _pendingFormalAdded = null;
            _formalFullRebuild = false;
        }
    }

    void IOpaqueMidiEventChangeSink.OnChanged(OpaqueMidiEvent value)
    {
        bool sourceBacked = _batchChangeDepth != 0
            ? (_batchIncludesAllMaterializedSource
                ? _materializedSourceValues.ContainsKey(value.Id)
                : _batchSourceIds?.Contains(value.Id) == true)
                && !_removed.Contains(value.Id)
            : !_clearSource
                && _materializedSourceValues.ContainsKey(value.Id)
                && !_removed.Contains(value.Id);
        if (sourceBacked)
            _replacements[value.Id] = value;
        MarkOverlayDirty(value.Id);
        Touch();
    }

    internal void RestoreFormalSequenceForCompilation(
        OpaqueMidiEventFormalSequenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_added.Count != 0 || _replacements.Count != 0 || _removed.Count != 0)
            throw new InvalidOperationException("The opaque MIDI Event target is not empty.");
        if (_source is not null && !ReferenceEquals(_source, snapshot.Source))
            throw new InvalidOperationException("The opaque MIDI Event source does not match the captured revision.");

        _source = snapshot.Source;
        _sourceIdCache = CreateSourceIdCache();
        _clearSource = snapshot.ClearsSource;
        _removed = snapshot.RemovedSourceIds.ToBuilder();
        _added.Adopt(snapshot.Added);
        _replacements.Adopt(snapshot.Replacements);
        _addedIndices = snapshot.AddedIndices.ToBuilder();
        _formalRemovedSourceIds = snapshot.RemovedSourceIds;
        _formalReplacements = snapshot.Replacements;
        _formalAdded = snapshot.Added;
        _formalCount = snapshot.Count;
        _generation = snapshot.Generation;
        IReadOnlySet<MidoraId>? exclusions = snapshot.Source is not null
            && (snapshot.RemovedSourceIds.Count != 0 || snapshot.Replacements.Count != 0)
                ? new PureMidiSourceExclusionSet<OpaqueMidiEventValue>(
                    snapshot.RemovedSourceIds,
                    snapshot.Replacements)
                : null;
        var emptyIndex = new PureMidiPointOverlayIndex<OpaqueMidiEventValue>(
            static value => value.Id,
            static value => value.Tick,
            static value => value.Order);
        _overlayIndex = emptyIndex.Create(snapshot.Replacements.Values.Concat(snapshot.Added.Enumerate()));
        _compilationSnapshot = new OpaqueMidiEventQuerySnapshot(
            snapshot.Source,
            snapshot.ClearsSource,
            exclusions,
            new Dictionary<MidoraId, OpaqueMidiEventValue>(),
            _overlayIndex,
            CreateSourceIdCache(),
            snapshot.Count,
            snapshot.Generation);
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.OpaqueEventCount - _removed.Count);
    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindOpaqueEventIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        if (visibleIndex >= LiveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.OpaqueEventCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetOpaqueEvent(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged opaque-event index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private OpaqueMidiEvent Materialize(
        OpaqueMidiEventValue value,
        int sourceIndex = -1,
        bool retain = false)
    {
        if (_materializedSourceItems.TryGetValue(value.Id, out OpaqueMidiEvent? existing))
        {
            if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
            return existing;
        }
        _materializedSourceValues.TryAdd(value.Id, value);
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        OpaqueMidiEvent result = new(_project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            MetaType = value.MetaType,
            Payload = value.Payload.ToArray(),
            Order = value.Order
        };
        Track(result);
        if (retain) _materializedSourceItems.Add(value.Id, result);
        return result;
    }

    private void AddOverlay(OpaqueMidiEvent value)
    {
        int index = _added.Count;
        _added.Add(value);
        _addedById[value.Id] = value;
        _addedIndices[value.Id] = index;
        if (_pendingFormalAdded is not null)
            _pendingFormalAdded = _pendingFormalAdded.Append(ToValue(value));
        MarkOverlayDirty(value.Id);
    }

    private void InsertOverlay(int index, OpaqueMidiEvent value)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.Insert(index, ToValue(value));
        _added.Insert(index, value);
        _addedById[value.Id] = value;
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
        MarkOverlayDirty(value.Id);
    }

    private OpaqueMidiEvent RemoveOverlayAt(int index)
    {
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices([index]);
        OpaqueMidiEvent value = _added[index];
        _added.RemoveAt(index);
        _addedById.Remove(value.Id);
        _addedIndices.Remove(value.Id);
        MarkOverlayDirty(value.Id);
        for (int current = index; current < _added.Count; current++)
            _addedIndices[_added[current].Id] = current;
        return value;
    }

    private int RemoveOverlayWhere(IReadOnlySet<MidoraId> ids)
    {
        if (ids.Count == 0 || _added.Count == 0) return 0;
        List<(int Index, OpaqueMidiEvent Value)> removed = new(
            Math.Min(ids.Count, _added.Count));
        foreach (MidoraId id in ids)
        {
            if (_addedIndices.TryGetValue(id, out int index))
                removed.Add((index, _added[index]));
        }
        removed.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return RemoveOverlayItems(removed);
    }

    private int RemoveOverlayItems(IReadOnlyList<(int Index, OpaqueMidiEvent Value)> removed)
    {
        if (removed.Count == 0) return 0;
        int first = removed[0].Index;
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.RemoveIndices(
            removed.Select(static value => value.Index).ToArray());
        foreach ((_, OpaqueMidiEvent value) in removed)
        {
            _addedById.Remove(value.Id);
            _addedIndices.Remove(value.Id);
            MarkOverlayDirty(value.Id);
        }
        int write = first;
        int removedIndex = 0;
        for (int read = first; read < _added.Count; read++)
        {
            if (removedIndex < removed.Count && removed[removedIndex].Index == read)
            {
                removedIndex++;
                continue;
            }
            OpaqueMidiEvent value = _added[read];
            if (write != read) _added[write] = value;
            _addedIndices[value.Id] = write++;
        }
        if (removedIndex != removed.Count)
            throw new InvalidOperationException("The imported MIDI Event overlay removal indices are invalid.");
        _added.RemoveRange(write, removed.Count);
        return removed.Count;
    }

    private void RestoreOverlayItems(IReadOnlyList<(int Index, OpaqueMidiEvent Value)> restored)
    {
        int first = restored[0].Index;
        int finalCount = checked(_added.Count + restored.Count);
        List<OpaqueMidiEvent> suffix = new(finalCount - first);
        int liveIndex = first;
        int restoredIndex = 0;
        for (int index = first; index < finalCount; index++)
        {
            if (restoredIndex < restored.Count && restored[restoredIndex].Index == index)
                suffix.Add(restored[restoredIndex++].Value);
            else if ((uint)liveIndex < (uint)_added.Count)
                suffix.Add(_added[liveIndex++]);
            else
                throw new InvalidOperationException("The imported MIDI Event overlay changed before restoration.");
        }
        if (restoredIndex != restored.Count || liveIndex != _added.Count)
            throw new InvalidOperationException("The imported MIDI Event overlay cannot be restored exactly.");
        BeginFormalStructuralEdit();
        _pendingFormalAdded = _pendingFormalAdded!.InsertAtFinalIndices(
            restored.Select(static value => new KeyValuePair<int, OpaqueMidiEventValue>(
                value.Index,
                ToValue(value.Value))).ToArray());
        _added.RemoveRange(first, _added.Count - first);
        _added.AddRange(suffix);
        for (int index = first; index < _added.Count; index++)
        {
            OpaqueMidiEvent value = _added[index];
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
        }
        foreach ((_, OpaqueMidiEvent value) in restored)
        {
            Track(value);
            MarkOverlayDirty(value.Id);
        }
    }

    private void RebuildAddedIndex(bool formalStructureAlreadyUpdated = false)
    {
        if (!formalStructureAlreadyUpdated)
            throw new InvalidOperationException("The formal imported MIDI Event order was not updated.");
        _addedById.Clear();
        _addedIndices.Clear();
        for (int index = 0; index < _added.Count; index++)
        {
            OpaqueMidiEvent value = _added[index];
            Track(value);
            _addedById[value.Id] = value;
            _addedIndices[value.Id] = index;
            MarkOverlayDirty(value.Id);
        }
    }

    private void BeginFormalStructuralEdit()
    {
        if (_pendingFormalAdded is not null) return;
        PersistentFormalValueSequence<OpaqueMidiEventValue> current =
            _formalAdded.AppendRange(_added, _formalAdded.Count, static value => ToValue(value));
        Dictionary<int, OpaqueMidiEventValue>? updates = null;
        foreach (MidoraId id in _formalDirtyIds)
        {
            if (_addedIndices.TryGetValue(id, out int index) && index < current.Count)
                (updates ??= [])[index] = ToValue(_added[index]);
        }
        _pendingFormalAdded = updates is null ? current : current.ReplaceBatch(updates);
    }

    private void MarkOverlayDirty(MidoraId id)
    {
        _dirtyOverlayIds.Add(id);
        int capturedAddedCount = _pendingFormalAdded?.Count ?? _formalAdded.Count;
        if (!_addedIndices.TryGetValue(id, out int addedIndex)
            || addedIndex < capturedAddedCount)
        {
            _formalDirtyIds.Add(id);
        }
    }

    private void EnsureOverlayIndex()
    {
        if (_dirtyOverlayIds.Count == 0) return;
        _overlayIndex = _overlayIndex.ReplaceBatch(_dirtyOverlayIds, id =>
        {
            if (_replacements.TryGetValue(id, out OpaqueMidiEvent? replacement))
                return ToValue(replacement);
            return _addedById.TryGetValue(id, out OpaqueMidiEvent? added)
                ? ToValue(added)
                : null;
        });
        _dirtyOverlayIds.Clear();
    }

    private void Track(OpaqueMidiEvent value) => value.SetChangeSink(this);

    private void EnsureIdAvailable(MidoraId id, MidoraId replacingId = default)
    {
        if (id == default) throw new ArgumentOutOfRangeException(nameof(id));
        if (id == replacingId) return;
        if (_addedById.ContainsKey(id)
            || _source is not null && _source.FindOpaqueEventIndex(id) >= 0)
        {
            throw new InvalidOperationException(
                "An imported MIDI Event collection cannot contain duplicate Stable IDs.");
        }
    }

    private void PublishFormalRoots()
    {
        if (_formalFullRebuild)
        {
            _formalRemovedSourceIds = _removed.ToImmutableHashSet();
            _formalReplacements = _replacements.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => ToValue(pair.Value));
            _formalAdded = PersistentFormalValueSequence<OpaqueMidiEventValue>.Create(
                _added,
                static value => ToValue(value));
        }
        else
        {
            if (_formalDirtyIds.Count != 0)
            {
                var removed = _formalRemovedSourceIds.ToBuilder();
                var replacements = _formalReplacements.ToBuilder();
                foreach (MidoraId id in _formalDirtyIds)
                {
                    if (_removed.Contains(id)) removed.Add(id);
                    else removed.Remove(id);
                    if (_replacements.TryGetValue(id, out OpaqueMidiEvent? replacement))
                        replacements[id] = ToValue(replacement);
                    else
                        replacements.Remove(id);
                }
                _formalRemovedSourceIds = removed.ToImmutable();
                _formalReplacements = replacements.ToImmutable();
            }

            PersistentFormalValueSequence<OpaqueMidiEventValue> next =
                _pendingFormalAdded ?? _formalAdded.AppendRange(
                    _added,
                    _formalAdded.Count,
                    static value => ToValue(value));
            Dictionary<int, OpaqueMidiEventValue>? updated = null;
            foreach (MidoraId id in _formalDirtyIds)
            {
                if (_addedIndices.TryGetValue(id, out int index) && index < next.Count)
                {
                    (updated ??= [])[index] = ToValue(_added[index]);
                }
            }
            _formalAdded = updated is null ? next : next.ReplaceBatch(updated);
        }

        _formalCount = Count;
        _added.Commit(_formalAdded);
        _replacements.Commit(_formalReplacements);
        _formalDirtyIds.Clear();
        _pendingFormalAdded = null;
        _formalFullRebuild = false;
    }

    private void Touch()
    {
        if (_batchChangeDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        lock (_snapshotPublicationSync)
        {
            PublishFormalRoots();
            _generation++;
        }
    }

    private void EndBatchChange()
    {
        if (_batchChangeDepth != 1)
            throw new InvalidOperationException("No imported MIDI Event batch change is active.");
        CollapseSourceEquivalentReplacements();
        _batchChangeDepth = 0;
        _batchSourceIds = null;
        _batchIncludesAllMaterializedSource = false;
        if (_batchChanged)
        {
            lock (_snapshotPublicationSync)
            {
                PublishFormalRoots();
                _generation++;
            }
        }
        _batchChanged = false;
    }

    private void CollapseSourceEquivalentReplacements()
    {
        if (_source is null || _clearSource) return;
        IEnumerable<MidoraId> candidates = _batchIncludesAllMaterializedSource
            ? _materializedSourceValues.Keys
            : _batchSourceIds ?? [];
        foreach (MidoraId id in candidates)
        {
            if (!_replacements.TryGetValue(id, out OpaqueMidiEvent? replacement))
                continue;
            if (_materializedSourceValues.TryGetValue(id, out OpaqueMidiEventValue source)
                && Matches(replacement, source))
            {
                _replacements.Remove(id);
                MarkOverlayDirty(id);
            }
        }
    }

    private static bool Matches(OpaqueMidiEvent value, OpaqueMidiEventValue source) =>
        value.Id == source.Id
        && value.Tick == source.Tick
        && value.Kind == source.Kind
        && value.MetaType == source.MetaType
        && value.Payload.AsSpan().SequenceEqual(source.Payload.Span)
        && value.Order == source.Order;

    private sealed class OpaqueBatchChangeScope(OpaqueMidiEventCollection owner) : IDisposable
    {
        private OpaqueMidiEventCollection? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatchChange();
    }

    private static OpaqueMidiEventValue ToValue(OpaqueMidiEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload.ToArray(),
        value.Order);

    private static OpaqueMidiEvent FromValue(
        MidoraProject project,
        OpaqueMidiEventValue value) => new(project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            MetaType = value.MetaType,
            Payload = value.Payload.ToArray(),
            Order = value.Order
        };

    private static OpaqueMidiEvent Clone(
        MidoraProject project,
        OpaqueMidiEvent source,
        IOpaqueMidiEventChangeSink sink)
    {
        OpaqueMidiEvent result = new(project, source.Id)
        {
            Tick = source.Tick,
            Kind = source.Kind,
            MetaType = source.MetaType,
            Payload = [.. source.Payload],
            Order = source.Order
        };
        result.SetChangeSink(sink);
        return result;
    }

    private static PureMidiOpaqueIdResolutionCache CreateSourceIdCache() => new();
}
