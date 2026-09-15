using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Midora.Common;
using Midora.Domain;

namespace Midora.Application;

public readonly record struct PagedEditResourceBudget
{
    public const int DefaultPageRecordCount = 4096;
    public const int DefaultCancellationCheckInterval = 256;
    public const long DefaultMaximumWorkingBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumResidentBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumSpillBytes = 16L * 1024 * 1024 * 1024;
    public const long DefaultMaximumRecordCount = 100_000_000;

    public PagedEditResourceBudget() : this(DefaultMaximumRecordCount,
        DefaultMaximumWorkingBytes, DefaultMaximumResidentBytes,
        DefaultMaximumSpillBytes, DefaultPageRecordCount,
        DefaultCancellationCheckInterval) { }

    public PagedEditResourceBudget(
        long maximumRecordCount = DefaultMaximumRecordCount,
        long maximumWorkingBytes = DefaultMaximumWorkingBytes,
        long maximumResidentBytes = DefaultMaximumResidentBytes,
        long maximumSpillBytes = DefaultMaximumSpillBytes,
        int pageRecordCount = DefaultPageRecordCount,
        int cancellationCheckInterval = DefaultCancellationCheckInterval)
    {
        if (maximumRecordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordCount));
        if (maximumWorkingBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWorkingBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResidentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSpillBytes);
        if (pageRecordCount is < 64 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(pageRecordCount));
        if (cancellationCheckInterval is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(cancellationCheckInterval));
        MaximumRecordCount = maximumRecordCount;
        MaximumWorkingBytes = maximumWorkingBytes;
        MaximumResidentBytes = maximumResidentBytes;
        MaximumSpillBytes = maximumSpillBytes;
        PageRecordCount = pageRecordCount;
        CancellationCheckInterval = cancellationCheckInterval;
    }

    public long MaximumRecordCount { get; }
    public long MaximumWorkingBytes { get; }
    public long MaximumResidentBytes { get; }
    public long MaximumSpillBytes { get; }
    public int PageRecordCount { get; }
    public int CancellationCheckInterval { get; }
}

public interface IFixedSizePagedEditCodec<TValue>
    where TValue : struct
{
    int RecordByteCount { get; }
    void Write(TValue value, Span<byte> destination);
    TValue Read(ReadOnlySpan<byte> source);
}

public readonly record struct DetachedPagedEditPage<TValue>(
    long FirstOrdinal,
    ReadOnlyMemory<TValue> Values)
    where TValue : struct
{
    public int Count => Values.Length;
}

public readonly record struct DetachedPagedEditResourceUsage(
    long RecordCount,
    long ResidentBytes,
    long SpillBytes,
    int ResidentPageCount,
    int SpillPageCount);

/// <summary>
/// Bounded, repeatably readable staging for a detached edit. Full pages remain
/// resident only while the explicit RAM budget permits; later pages spill to
/// one Midora-owned EditRuns lease under ProgramRoot/.tmp. Disposing the stage
/// releases both readers and the owned directory.
/// </summary>
public sealed class DetachedPagedEditStaging<TValue> : IDisposable
    where TValue : struct
{
    private readonly IFixedSizePagedEditCodec<TValue> _codec;
    private readonly PagedEditResourceBudget _budget;
    private readonly string _temporaryRoot;
    private readonly int _residentRecordByteCount;
    private readonly List<TValue[]> _residentPages = [];
    private readonly List<int> _spillPageCounts = [];
    private TValue[] _pending;
    private int _pendingCount;
    private long _recordCount;
    private long _residentBytes;
    private long _spillBytes;
    private MidoraOwnedTemporaryDirectoryLease? _lease;
    private FileStream? _spillWriter;
    private string? _spillPath;
    private bool _sealed;
    private bool _disposed;

    public DetachedPagedEditStaging(
        IFixedSizePagedEditCodec<TValue> codec,
        PagedEditResourceBudget budget = default,
        string? temporaryRoot = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (_codec.RecordByteCount <= 0 || _codec.RecordByteCount > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(codec));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            throw new ArgumentException(
                "Detached edit records must not contain managed references.",
                nameof(codec));
        }
        _residentRecordByteCount = Unsafe.SizeOf<TValue>();
        _budget = budget == default ? new PagedEditResourceBudget() : budget;
        long encodedPageBytes = checked(
            (long)_budget.PageRecordCount * _codec.RecordByteCount);
        long decodedPageBytes = checked(
            (long)_budget.PageRecordCount * _residentRecordByteCount);
        long maximumPageWorkingBytes = Math.Max(
            checked(decodedPageBytes * 2),
            checked(decodedPageBytes + encodedPageBytes));
        if (maximumPageWorkingBytes > _budget.MaximumWorkingBytes
            || encodedPageBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget),
                "One detached edit page exceeds the bounded working-buffer limit.");
        }
        _temporaryRoot = temporaryRoot ?? MidoraProgramData.Current.CompilerRunsDirectory;
        if (!Path.IsPathFullyQualified(_temporaryRoot))
            throw new ArgumentException("The detached edit temporary root must be absolute.", nameof(temporaryRoot));
        _pending = new TValue[_budget.PageRecordCount];
    }

    public DetachedPagedEditResourceUsage Usage => new(
        _recordCount,
        _residentBytes,
        _spillBytes,
        _residentPages.Count,
        _spillPageCounts.Count);

    public void Add(TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) throw new InvalidOperationException("The detached edit staging is already sealed.");
        if (_recordCount >= _budget.MaximumRecordCount)
            throw new InvalidOperationException("The detached edit exceeds its bounded record limit.");
        if ((_recordCount % _budget.CancellationCheckInterval) == 0)
            cancellationToken.ThrowIfCancellationRequested();
        _pending[_pendingCount++] = value;
        _recordCount++;
        if (_pendingCount == _pending.Length) FlushPending();
    }

    public void AddRange(
        IEnumerable<TValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (TValue value in values) Add(value, cancellationToken);
    }

    public void Seal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) return;
        FlushPending();
        _spillWriter?.Flush(flushToDisk: false);
        _spillWriter?.Dispose();
        _spillWriter = null;
        _pending = [];
        _sealed = true;
    }

    public IEnumerable<DetachedPagedEditPage<TValue>> ReadPages(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sealed) throw new InvalidOperationException("Seal detached edit staging before reading it.");
        long firstOrdinal = 0;
        foreach (TValue[] page in _residentPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(firstOrdinal, page);
            firstOrdinal = checked(firstOrdinal + page.Length);
        }
        if (_spillPageCounts.Count == 0) yield break;

        using FileStream reader = new(
            _spillPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            256 * 1024,
            FileOptions.SequentialScan);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(
            checked(_budget.PageRecordCount * _codec.RecordByteCount));
        try
        {
            foreach (int count in _spillPageCounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int byteCount = checked(count * _codec.RecordByteCount);
                reader.ReadExactly(bytes.AsSpan(0, byteCount));
                TValue[] values = new TValue[count];
                for (int index = 0; index < count; index++)
                {
                    if ((index % _budget.CancellationCheckInterval) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    values[index] = _codec.Read(bytes.AsSpan(
                        index * _codec.RecordByteCount,
                        _codec.RecordByteCount));
                }
                yield return new(firstOrdinal, values);
                firstOrdinal = checked(firstOrdinal + count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        if (firstOrdinal != _recordCount)
            throw new InvalidDataException("The detached edit spill length does not match its manifest in memory.");
    }

    public IEnumerable<TValue> ReadValues(CancellationToken cancellationToken = default)
    {
        foreach (DetachedPagedEditPage<TValue> page in ReadPages(cancellationToken))
        {
            if (!MemoryMarshal.TryGetArray(page.Values, out ArraySegment<TValue> values)
                || values.Array is null)
            {
                throw new InvalidDataException("A detached edit page is not array-backed.");
            }
            int end = checked(values.Offset + values.Count);
            for (int index = values.Offset; index < end; index++)
                yield return values.Array[index];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spillWriter?.Dispose();
        _spillWriter = null;
        _lease?.Dispose();
        _lease = null;
        _pending = [];
        _residentPages.Clear();
        _spillPageCounts.Clear();
    }

    private void FlushPending()
    {
        if (_pendingCount == 0) return;
        long residentPageBytes = checked(
            (long)_pendingCount * _residentRecordByteCount);
        long encodedPageBytes = checked(
            (long)_pendingCount * _codec.RecordByteCount);
        if (_residentBytes <= _budget.MaximumResidentBytes - residentPageBytes)
        {
            TValue[] page = new TValue[_pendingCount];
            _pending.AsSpan(0, _pendingCount).CopyTo(page);
            _residentPages.Add(page);
            _residentBytes = checked(_residentBytes + residentPageBytes);
        }
        else
        {
            if (_spillBytes > _budget.MaximumSpillBytes - encodedPageBytes)
                throw new InvalidOperationException("The detached edit exceeds its bounded spill-file limit.");
            EnsureSpillWriter();
            byte[] bytes = ArrayPool<byte>.Shared.Rent(checked(_pendingCount * _codec.RecordByteCount));
            try
            {
                Span<byte> destination = bytes.AsSpan(0, checked(_pendingCount * _codec.RecordByteCount));
                for (int index = 0; index < _pendingCount; index++)
                {
                    _codec.Write(
                        _pending[index],
                        destination.Slice(index * _codec.RecordByteCount, _codec.RecordByteCount));
                }
                _spillWriter!.Write(destination);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
            _spillPageCounts.Add(_pendingCount);
            _spillBytes = checked(_spillBytes + encodedPageBytes);
        }
        _pendingCount = 0;
    }

    private void EnsureSpillWriter()
    {
        if (_spillWriter is not null) return;
        Directory.CreateDirectory(_temporaryRoot);
        _lease = MidoraOwnedTemporaryDirectoryLease.Create(_temporaryRoot, "edit-run");
        _spillPath = Path.Combine(_lease.DirectoryPath, "detached-edit.pages");
        _spillWriter = new(
            _spillPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            256 * 1024,
            FileOptions.SequentialScan);
    }
}

public readonly record struct TimelineOwnerChangeRange
{
    public TimelineOwnerChangeRange(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
        if (maximumLane < minimumLane) throw new ArgumentOutOfRangeException(nameof(maximumLane));
        StartTick = startTick;
        EndTick = endTick;
        MinimumLane = minimumLane;
        MaximumLane = maximumLane;
    }

    public long StartTick { get; }
    public long EndTick { get; }
    public int MinimumLane { get; }
    public int MaximumLane { get; }
}

public sealed record TimelineOwnerChangeSet(
    MidoraId OwnerId,
    long PreviousRevision,
    long CurrentRevision,
    ImmutableArray<TimelineOrdinalRange> OrdinalRanges,
    ImmutableArray<TimelineOwnerChangeRange> ContentRanges);

/// <summary>
/// Owner adapter whose root publication is atomic. BuildRoot must be detached;
/// TrySwapRoot is the only operation allowed to mutate the formal owner.
/// </summary>
public interface IPagedEditRootOwner<TValue, TRoot>
    where TValue : struct
    where TRoot : class
{
    MidoraId OwnerId { get; }
    long Revision { get; }
    TRoot CaptureRoot();
    TRoot BuildRoot(
        IEnumerable<DetachedPagedEditPage<TValue>> pages,
        CancellationToken cancellationToken);
    bool TrySwapRoot(
        TRoot expectedRoot,
        TRoot replacementRoot,
        out TimelineOwnerChangeSet changeSet);
}

public sealed class CompactPagedEditUndo<TValue, TRoot>
    where TValue : struct
    where TRoot : class
{
    private readonly IPagedEditRootOwner<TValue, TRoot> _owner;
    private readonly TRoot _oldRoot;
    private readonly TRoot _newRoot;
    private bool _isApplied = true;

    internal CompactPagedEditUndo(
        IPagedEditRootOwner<TValue, TRoot> owner,
        TRoot oldRoot,
        TRoot newRoot)
    {
        _owner = owner;
        _oldRoot = oldRoot;
        _newRoot = newRoot;
    }

    public TimelineOwnerChangeSet Undo()
    {
        if (!_isApplied) throw new InvalidOperationException("The detached edit is already undone.");
        if (!_owner.TrySwapRoot(_newRoot, _oldRoot, out TimelineOwnerChangeSet changeSet))
            throw new InvalidOperationException("The detached edit owner changed before Undo.");
        _isApplied = false;
        return changeSet;
    }

    public TimelineOwnerChangeSet Redo()
    {
        if (_isApplied) throw new InvalidOperationException("The detached edit is already applied.");
        if (!_owner.TrySwapRoot(_oldRoot, _newRoot, out TimelineOwnerChangeSet changeSet))
            throw new InvalidOperationException("The detached edit owner changed before Redo.");
        _isApplied = true;
        return changeSet;
    }
}

public static class DetachedPagedEditTransaction
{
    public static (CompactPagedEditUndo<TValue, TRoot> Undo, TimelineOwnerChangeSet ChangeSet)
        Commit<TValue, TRoot>(
            IPagedEditRootOwner<TValue, TRoot> owner,
            long expectedRevision,
            DetachedPagedEditStaging<TValue> staging,
            CancellationToken cancellationToken = default)
        where TValue : struct
        where TRoot : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(staging);
        if (owner.Revision != expectedRevision)
            throw new InvalidOperationException("The detached edit result belongs to a stale owner revision.");
        staging.Seal();
        TRoot oldRoot = owner.CaptureRoot();
        TRoot newRoot = owner.BuildRoot(staging.ReadPages(cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.Revision != expectedRevision
            || !owner.TrySwapRoot(oldRoot, newRoot, out TimelineOwnerChangeSet changeSet))
        {
            throw new InvalidOperationException("The detached edit owner changed before atomic publication.");
        }
        return (new(owner, oldRoot, newRoot), changeSet);
    }
}
