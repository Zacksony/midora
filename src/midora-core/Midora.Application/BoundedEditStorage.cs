using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Midora.Common;

namespace Midora.Application;

/// <summary>One budget shared by every intermediate store in a single edit.</summary>
internal sealed class BoundedEditResources
{
    private readonly object _sync = new();
    private long _resident;
    private long _working;
    private long _spill;
    private long _borrowedPayload;
    private readonly Dictionary<byte[], int> _borrowedPayloadArrays = new(ReferenceEqualityComparer.Instance);

    public BoundedEditResources(PagedEditResourceBudget budget = default, string? temporaryRoot = null)
    {
        Budget = budget == default ? new PagedEditResourceBudget() : budget;
        TemporaryRoot = temporaryRoot ?? MidoraProgramData.Current.CompilerRunsDirectory;
        if (!Path.IsPathFullyQualified(TemporaryRoot))
            throw new ArgumentException("The edit storage root must be absolute.", nameof(temporaryRoot));
    }

    public PagedEditResourceBudget Budget { get; }
    public string TemporaryRoot { get; }
    public long PeakResidentBytes { get; private set; }
    public long PeakWorkingBytes { get; private set; }
    public long PeakSpillBytes { get; private set; }
    public long PeakBorrowedPayloadBytes { get; private set; }
    // Active immutable payloads generally borrow source/page-cache storage;
    // a clipboard/PayloadBank read can instead own one decoded return value.
    // Count actual backing capacity separately from fixed-width working pages,
    // across readers of this operation; the same array counts only once.
    public long BorrowedPayloadBytes { get { lock (_sync) return _borrowedPayload; } }
    public long ResidentBytes { get { lock (_sync) return _resident; } }
    public long WorkingBytes { get { lock (_sync) return _working; } }
    public long SpillBytes { get { lock (_sync) return _spill; } }
    public BoundedEditResourceLease BeginResourceLease() => new(this);
    internal void TrackProvider(IDisposable value) => BoundedEditResourceLease.Track(this, value);

    public bool TryReserveResident(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > Budget.MaximumResidentBytes - _resident) return false;
            _resident += bytes;
            PeakResidentBytes = Math.Max(PeakResidentBytes, _resident);
            return true;
        }
    }

    public void ReleaseResident(long bytes) { lock (_sync) _resident -= bytes; }
    public void ReserveSpill(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > Budget.MaximumSpillBytes - _spill)
                throw new InvalidOperationException("The edit exceeds its temporary-storage budget.");
            _spill += bytes;
            PeakSpillBytes = Math.Max(PeakSpillBytes, _spill);
        }
    }
    public void ReleaseSpill(long bytes) { lock (_sync) _spill -= bytes; }
    public IDisposable ReserveWorking(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > Budget.MaximumWorkingBytes - _working)
                throw new InvalidOperationException("The edit exceeds its working-memory budget.");
            _working += bytes;
            PeakWorkingBytes = Math.Max(PeakWorkingBytes, _working);
            return new WorkingLease(this, bytes);
        }
    }

    public IDisposable BorrowPayload(ReadOnlyMemory<byte> payload)
    {
        byte[]? array = MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) ? segment.Array : null;
        long bytes = array?.LongLength ?? payload.Length;
        lock (_sync)
        {
            bool first = array is null || !_borrowedPayloadArrays.ContainsKey(array);
            if (array is not null)
            {
                _borrowedPayloadArrays.TryGetValue(array, out int existing);
                _borrowedPayloadArrays[array] = checked(existing + 1);
            }
            if (first) _borrowedPayload = checked(_borrowedPayload + bytes);
            PeakBorrowedPayloadBytes = Math.Max(PeakBorrowedPayloadBytes, _borrowedPayload);
            return new BorrowedPayloadLease(this, array, bytes);
        }
    }

    private sealed class BorrowedPayloadLease(BoundedEditResources owner, byte[]? array, long bytes) : IDisposable
    {
        private BoundedEditResources? _owner = owner;
        private byte[]? _array = array;
        public void Dispose()
        {
            BoundedEditResources? value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            lock (value._sync)
            {
                byte[]? retained = Interlocked.Exchange(ref _array, null);
                if (retained is not null)
                {
                    int remaining = value._borrowedPayloadArrays[retained] - 1;
                    if (remaining != 0) { value._borrowedPayloadArrays[retained] = remaining; return; }
                    value._borrowedPayloadArrays.Remove(retained);
                }
                value._borrowedPayload -= bytes;
            }
        }
    }

    private sealed class WorkingLease(BoundedEditResources owner, long bytes) : IDisposable
    {
        private BoundedEditResources? _owner = owner;
        public void Dispose()
        {
            BoundedEditResources? value = Interlocked.Exchange(ref _owner, null);
            if (value is not null) lock (value._sync) value._working -= bytes;
        }
    }
}

/// <summary>
/// Fixed-width, replayable records. Intermediate stores share a command budget;
/// full pages spill instead of growing the resident object graph. The format is
/// process-local scratch data, never a persisted Project contract.
/// </summary>
internal sealed class BoundedEditRecordStore<T> : IReadOnlyList<T>, IDisposable where T : unmanaged
{
    private readonly BoundedEditResources _resources;
    private readonly List<Page> _pages = [];
    private readonly int _pageCapacity;
    private readonly int _recordBytes = Unsafe.SizeOf<T>();
    private T[] _pending;
    private IDisposable? _pendingLease;
    private int _pendingCount;
    private int _count;
    private long _residentBytes;
    private long _spillBytes;
    private MidoraOwnedTemporaryDirectoryLease? _directory;
    private FileStream? _file;
    private bool _sealed;
    private bool _disposed;

    public BoundedEditRecordStore(BoundedEditResources resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        // Tiny, known-size edits may select smaller pages without creating a
        // separate budget. Normal/large commands keep the standard page size.
        var context = BulkEditPreparationContext.Current;
        _pageCapacity = context is not null && ReferenceEquals(context.Resources, resources)
            ? context.PreferredPageRecordCount : resources.Budget.PageRecordCount;
        _pendingLease = resources.ReserveWorking(checked((long)_pageCapacity * _recordBytes));
        try { _pending = new T[_pageCapacity]; }
        catch { _pendingLease.Dispose(); throw; }
    }

    public int Count => _count;
    internal bool IsDisposed => _disposed;
    public int PageCapacity => _pageCapacity;
    public bool IsSealed => _sealed;
    public int PageCount => _pages.Count;
    public long ResidentBytes => _residentBytes;
    public long SpillBytes => _spillBytes;

    public void Add(T value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) throw new InvalidOperationException("The edit record store is sealed.");
        if (_count >= _resources.Budget.MaximumRecordCount)
            throw new InvalidOperationException("The edit exceeds its result-record limit.");
        if (_count % _resources.Budget.CancellationCheckInterval == 0)
            cancellationToken.ThrowIfCancellationRequested();
        _pending[_pendingCount++] = value;
        _count++;
        if (_pendingCount == _pending.Length) FlushPage();
    }

    public void AddRange(IEnumerable<T> values, CancellationToken cancellationToken = default)
    {
        foreach (T value in values) Add(value, cancellationToken);
    }

    public void Seal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) return;
        FlushPage();
        _file?.Flush();
        _pending = [];
        _pendingLease?.Dispose();
        _pendingLease = null;
        _sealed = true;
    }

    private void FlushPage()
    {
        if (_pendingCount == 0) return;
        long bytes = checked((long)_pendingCount * _recordBytes);
        if (_resources.TryReserveResident(bytes))
        {
            try
            {
                T[] values = new T[_pendingCount];
                _pending.AsSpan(0, _pendingCount).CopyTo(values);
                _pages.Add(new(values, 0, values.Length));
                _residentBytes += bytes;
            }
            catch { _resources.ReleaseResident(bytes); throw; }
        }
        else
        {
            _resources.ReserveSpill(bytes);
            try
            {
                EnsureFile();
                long offset = _file!.Position;
                _file.Write(MemoryMarshal.AsBytes(_pending.AsSpan(0, _pendingCount)));
                _pages.Add(new(null, offset, _pendingCount));
                _spillBytes += bytes;
            }
            catch { _resources.ReleaseSpill(bytes); throw; }
        }
        _pendingCount = 0;
    }

    private void EnsureFile()
    {
        if (_file is not null) return;
        _directory = MidoraOwnedTemporaryDirectoryLease.Create(_resources.TemporaryRoot, "bulk-edit");
        _file = new FileStream(Path.Combine(_directory.DirectoryPath, "records.pages"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            bufferSize: 1, FileOptions.RandomAccess);
    }

    public T this[int index]
    {
        get
        {
            EnsureReadable();
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            Page page = _pages[index / _pageCapacity];
            int offset = index % _pageCapacity;
            if (page.Values is not null) return page.Values[offset];
            Span<T> value = stackalloc T[1];
            ReadExactly(page.FileOffset + ((long)offset * _recordBytes), MemoryMarshal.AsBytes(value));
            return value[0];
        }
    }

    public IEnumerable<T> ReadValues(CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        using IDisposable working = _resources.ReserveWorking(checked((long)_pageCapacity * _recordBytes));
        T[] buffer = new T[_pageCapacity];
        foreach (Page page in _pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T[] values = page.Values ?? buffer;
            if (page.Values is null)
                ReadExactly(page.FileOffset, MemoryMarshal.AsBytes(buffer.AsSpan(0, page.Count)));
            for (int index = 0; index < page.Count; index++)
            {
                if (index % _resources.Budget.CancellationCheckInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                yield return values[index];
            }
        }
    }

    public IEnumerator<T> GetEnumerator() => ReadValues(BulkEditPreparationContext.Current?.Token ?? default).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyPage(int pageIndex, Span<T> destination)
    {
        EnsureReadable();
        if ((uint)pageIndex >= (uint)_pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        Page page = _pages[pageIndex];
        if (destination.Length < page.Count) throw new ArgumentException("The page buffer is too small.");
        if (page.Values is not null) page.Values.AsSpan().CopyTo(destination);
        else ReadExactly(page.FileOffset, MemoryMarshal.AsBytes(destination[..page.Count]));
    }

    public int GetPageRecordCount(int pageIndex)
    {
        EnsureReadable();
        return _pages[pageIndex].Count;
    }

    /// <summary>Move retained result data out of the per-operation resident tier.</summary>
    public void SpillResidentPages(CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
    {
        EnsureReadable();
        cancellationToken.ThrowIfCancellationRequested();
        long totalBytes = _residentBytes;
        long completedBytes = 0;
        if (totalBytes == 0)
        {
            progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, 1, 1));
            return;
        }
        progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, 0, totalBytes));
        for (int index = 0; index < _pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page page = _pages[index];
            if (page.Values is null) continue;
            long bytes = checked((long)page.Count * _recordBytes);
            _resources.ReserveSpill(bytes);
            try
            {
                EnsureFile();
                long offset = _file!.Position;
                _file.Write(MemoryMarshal.AsBytes(page.Values.AsSpan()));
                _pages[index] = new(null, offset, page.Count);
                _spillBytes += bytes;
                _residentBytes -= bytes;
                _resources.ReleaseResident(bytes);
            }
            catch { _resources.ReleaseSpill(bytes); throw; }
            completedBytes += bytes;
            if (completedBytes < totalBytes)
                progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, completedBytes, totalBytes));
        }
        _file?.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(TimelineEditPreparationPhase.WritingStorage, totalBytes, totalBytes));
    }

    private void ReadExactly(long offset, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            int read = RandomAccess.Read(_file!.SafeFileHandle, destination, offset);
            if (read == 0) throw new EndOfStreamException("The owned edit record file is truncated.");
            destination = destination[read..];
            offset += read;
        }
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sealed) throw new InvalidOperationException("Seal the edit record store before reading.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending = [];
        _pendingLease?.Dispose();
        _pendingLease = null;
        _file?.Dispose();
        _file = null;
        _directory?.Dispose();
        _directory = null;
        _pages.Clear();
        _resources.ReleaseResident(_residentBytes);
        _resources.ReleaseSpill(_spillBytes);
        _residentBytes = _spillBytes = 0;
    }

    private readonly record struct Page(T[]? Values, long FileOffset, int Count);
}

internal static class BoundedEditSort
{
    private const int MaximumMergeInputs = 16;
    private const int InitialBufferCapacity = 64;

    public static BoundedEditRecordStore<T> Sort<T>(
        IEnumerable<T> source, IComparer<T> comparer, BoundedEditResources resources,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(comparer);
        // Leave room for output and every merge reader in the shared working budget.
        int capacity = checked((int)Math.Min(65_536,
            resources.Budget.MaximumWorkingBytes / (4L * Unsafe.SizeOf<T>())));
        if (capacity < resources.Budget.PageRecordCount)
            throw new InvalidOperationException("The edit sort working-memory budget is too small.");
        List<BoundedEditRecordStore<T>> runs = [];
        long visited = 0;
        try
        {
            // Tiny owners are common (e.g. many short Segments). Reserving and
            // allocating a full 65K run for each of their intermediate sorts
            // creates gigabytes of garbage even when each input has one value.
            // Growth changes allocation only: run boundaries and sorting/merge
            // order remain identical to the fixed-buffer implementation.
            int initialCapacity = source.TryGetNonEnumeratedCount(out int knownCount)
                ? Math.Clamp(knownCount, 1, capacity)
                : Math.Min(InitialBufferCapacity, capacity);
            IDisposable? bufferLease = null;
            try
            {
                bufferLease = resources.ReserveWorking(checked((long)initialCapacity * Unsafe.SizeOf<T>()));
                T[] buffer = new T[initialCapacity];
                int count = 0;
                foreach (T value in source)
                {
                    if (visited++ % resources.Budget.CancellationCheckInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (visited > resources.Budget.MaximumRecordCount)
                        throw new InvalidOperationException("The edit sort exceeds its candidate-record limit.");
                    if (count == buffer.Length)
                    {
                        int nextCapacity = Math.Min(capacity, checked(buffer.Length * 2));
                        // Both arrays are live during the copy. Reserve the
                        // complete new buffer before allocation, not just its
                        // size delta, and retain the old lease until copied.
                        IDisposable nextLease = resources.ReserveWorking(checked((long)nextCapacity * Unsafe.SizeOf<T>()));
                        T[] next;
                        try
                        {
                            next = new T[nextCapacity];
                            buffer.AsSpan(0, count).CopyTo(next);
                        }
                        catch { nextLease.Dispose(); throw; }
                        buffer = next;
                        bufferLease.Dispose();
                        bufferLease = nextLease;
                    }
                    buffer[count++] = value;
                    if (count != capacity) continue;
                    AddRun(buffer, count);
                    count = 0;
                }
                if (count != 0 || runs.Count == 0) AddRun(buffer, count);
            }
            finally { bufferLease?.Dispose(); }

            int passes = 0;
            for (int remaining = runs.Count; remaining > 1;
                remaining = (remaining + MaximumMergeInputs - 1) / MaximumMergeInputs) passes++;
            long mergeTotal = checked(visited * passes);
            long mergedRecords = 0;
            // Input enumeration may itself report an earlier selection-read
            // phase. Do not advance to Sorting until that enumeration finishes.
            progress?.Report(new TimelineEditPreparationProgress(TimelineEditPreparationPhase.Sorting, 0, mergeTotal)
                .InWorkRange(0, 1));
            while (runs.Count > 1)
            {
                List<BoundedEditRecordStore<T>> next = [];
                try
                {
                    for (int first = 0; first < runs.Count; first += MaximumMergeInputs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = Math.Min(MaximumMergeInputs, runs.Count - first);
                        var merged = new BoundedEditRecordStore<T>(resources);
                        next.Add(merged);
                        Merge(runs, first, count, merged, comparer, cancellationToken,
                            progress is null ? null : ReportMerged);
                        merged.Seal();
                        for (int index = first; index < first + count; index++) runs[index].Dispose();
                    }
                }
                catch { foreach (var value in next) value.Dispose(); throw; }
                runs = next;
            }
            BoundedEditRecordStore<T> result = runs[0];
            progress?.Report(new TimelineEditPreparationProgress(TimelineEditPreparationPhase.Sorting, 1, 1)
                .InWorkRange(0, 1));
            cancellationToken.ThrowIfCancellationRequested();
            runs.Clear();
            return result;

            void ReportMerged(long count)
            {
                mergedRecords += count;
                progress!.Report(new TimelineEditPreparationProgress(
                    TimelineEditPreparationPhase.Sorting, mergedRecords, mergeTotal).InWorkRange(0, 1));
            }
        }
        catch { foreach (var value in runs) value.Dispose(); throw; }

        void AddRun(T[] buffer, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Sort(buffer, 0, count, comparer);
            cancellationToken.ThrowIfCancellationRequested();
            var run = new BoundedEditRecordStore<T>(resources);
            runs.Add(run);
            for (int index = 0; index < count; index++) run.Add(buffer[index], cancellationToken);
            run.Seal();
        }
    }

    private static void Merge<T>(IReadOnlyList<BoundedEditRecordStore<T>> runs,
        int first, int count, BoundedEditRecordStore<T> result, IComparer<T> comparer,
        CancellationToken cancellationToken, Action<long>? progress = null) where T : unmanaged
    {
        var readers = new IEnumerator<T>?[count];
        var queue = new PriorityQueue<int, (T Value, int Run)>(Comparer<(T Value, int Run)>.Create((x, y) =>
        {
            int order = comparer.Compare(x.Value, y.Value);
            return order != 0 ? order : x.Run.CompareTo(y.Run);
        }));
        try
        {
            for (int index = 0; index < count; index++)
            {
                readers[index] = runs[first + index].ReadValues(cancellationToken).GetEnumerator();
                if (readers[index]!.MoveNext()) queue.Enqueue(index, (readers[index]!.Current, index));
            }
            int pending = 0;
            while (queue.TryDequeue(out int index, out var priority))
            {
                result.Add(priority.Value, cancellationToken);
                if (readers[index]!.MoveNext()) queue.Enqueue(index, (readers[index]!.Current, index));
                if (++pending == 4096) { progress?.Invoke(pending); pending = 0; }
            }
            if (pending != 0) progress?.Invoke(pending);
        }
        finally { foreach (var reader in readers) reader?.Dispose(); }
    }
}
