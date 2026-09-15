using System.Collections;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Midora.Common;

namespace Midora.Compiler;

/// <summary>Shared by all intermediate/value stores of one compiler preparation.</summary>
internal sealed class CompilerStorageBudget : IRetainedStorageSource
{
    private readonly object _sync = new();
    private MidoraOwnedTemporaryDirectoryLease? _directory;
    private long _resident, _working, _spill, _metadata, _fileNumber;
    private readonly Dictionary<long, WeakReference<IDisposable>> _stores = [];
    private long _storeNumber;
    public CompilerStorageBudget(long maximumResidentBytes = 128L << 20,
        long maximumSpillBytes = 16L << 30, long maximumWorkingBytes = 128L << 20,
        long maximumMetadataBytes = 64L << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResidentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSpillBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWorkingBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMetadataBytes);
        MaximumResidentBytes = maximumResidentBytes;
        MaximumSpillBytes = maximumSpillBytes;
        MaximumWorkingBytes = maximumWorkingBytes;
        MaximumMetadataBytes = maximumMetadataBytes;
    }
    public long MaximumResidentBytes { get; }
    public long MaximumSpillBytes { get; }
    public long MaximumWorkingBytes { get; }
    public long MaximumMetadataBytes { get; }
    public long ResidentBytes { get { lock (_sync) return _resident; } }
    public long SpillBytes { get { lock (_sync) return _spill; } }
    public long MetadataBytes { get { lock (_sync) return _metadata; } }
    public long PeakResidentBytes { get; private set; }
    public long PeakWorkingBytes { get; private set; }
    public long PeakSpillBytes { get; private set; }
    public long PeakMetadataBytes { get; private set; }
    public void ReserveMetadata(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > MaximumMetadataBytes - _metadata)
                throw new InvalidOperationException("Logical compilation exceeds its metadata-memory budget.");
            _metadata += bytes;
            PeakMetadataBytes = Math.Max(PeakMetadataBytes, _metadata);
        }
    }
    public void ReleaseMetadata(long bytes) { lock (_sync) _metadata -= bytes; }
    internal static long MetadataArrayBytes<T>(int capacity) =>
        capacity == 0 ? 0 : checked(24L + (long)capacity * Unsafe.SizeOf<T>());
    public bool TryReserveResident(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > MaximumResidentBytes - _resident) return false;
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
            if (bytes < 0 || bytes > MaximumSpillBytes - _spill)
                throw new InvalidOperationException("Logical compilation exceeds its temporary-storage budget.");
            _spill += bytes;
            PeakSpillBytes = Math.Max(PeakSpillBytes, _spill);
        }
    }
    public void ReleaseSpill(long bytes) { lock (_sync) _spill -= bytes; }
    public IDisposable ReserveWorking(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > MaximumWorkingBytes - _working)
                throw new InvalidOperationException("Logical compilation exceeds its working-memory budget.");
            _working += bytes;
            PeakWorkingBytes = Math.Max(PeakWorkingBytes, _working);
        }
        return new WorkingLease(this, bytes);
    }
    public string NewFilePath()
    {
        lock (_sync)
        {
            _directory ??= MidoraOwnedTemporaryDirectoryLease.Create(
                MidoraProgramData.Current.CompilerRunsDirectory, "logical-compile");
            return Path.Combine(_directory.DirectoryPath, $"values-{++_fileNumber}.pages");
        }
    }
    public long Register(IDisposable store)
    {
        lock (_sync)
        {
            long id = checked(++_storeNumber);
            _stores.Add(id, new(store));
            return id;
        }
    }
    public void Unregister(long id) { lock (_sync) _stores.Remove(id); }
    public void Abort()
    {
        // Only a failed, unpublished compiler transaction calls this, after restoring its old cache.
        WeakReference<IDisposable>[] stores;
        lock (_sync) { stores = _stores.Values.ToArray(); _stores.Clear(); }
        List<Exception>? failures = null;
        foreach (WeakReference<IDisposable> reference in stores)
        {
            if (!reference.TryGetTarget(out IDisposable? store)) continue;
            try { store.Dispose(); } catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        try { _directory?.Dispose(); _directory = null; }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures is not null) throw new AggregateException("Logical compiler temporary storage cleanup failed.", failures);
    }
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 176)) return;
        lock (_sync)
        {
            collector.Dictionary(_stores);
            foreach (WeakReference<IDisposable> reference in _stores.Values) collector.Add(reference, 32);
            // Weak registry entries do not own their targets.
            if (_directory is not null)
            {
                collector.Add(_directory, 64);
                collector.Text(_directory.DirectoryPath);
            }
        }
    }
    ~CompilerStorageBudget()
    {
        // The lease cannot be released while any store/reader still holds this budget.
        try { _directory?.Dispose(); } catch { /* Never throw from finalizer cleanup. */ }
    }
    private sealed class WorkingLease(CompilerStorageBudget owner, long bytes) : IDisposable
    {
        private CompilerStorageBudget? _owner = owner;
        public void Dispose()
        {
            CompilerStorageBudget? value = Interlocked.Exchange(ref _owner, null);
            if (value is not null) lock (value._sync) value._working -= bytes;
        }
    }
}

/// <summary>Bounded metadata, including simultaneous old/new arrays during capacity growth.</summary>
internal sealed class CompilerMetadataList<T>(CompilerStorageBudget budget) : IReadOnlyList<T>, IDisposable
    where T : struct
{
    private T[] _items = [];
    private long _reserved;
    private bool _disposed;
    public int Count { get; private set; }
    public T this[int index]
    {
        get { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); return _items[index]; }
        set { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); _items[index] = value; }
    }
    public void Add(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Count == _items.Length)
        {
            if (Count == Array.MaxLength) throw new InvalidOperationException("Logical compilation exceeds its metadata-index capacity.");
            int capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, (long)_items.Length * 2));
            long bytes = CompilerStorageBudget.MetadataArrayBytes<T>(capacity);
            budget.ReserveMetadata(bytes);
            try
            {
                T[] next = new T[capacity];
                _items.AsSpan(0, Count).CopyTo(next);
                _items = next;
            }
            catch { budget.ReleaseMetadata(bytes); throw; }
            budget.ReleaseMetadata(_reserved);
            _reserved = bytes;
        }
        _items[Count++] = value;
    }
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 56)) return;
        collector.Array(_items);
    }
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return _items[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _items = []; Count = 0;
        budget.ReleaseMetadata(_reserved); _reserved = 0;
        GC.SuppressFinalize(this);
    }
    ~CompilerMetadataList() { try { Dispose(); } catch { /* Never throw from finalizer cleanup. */ } }
}

/// <summary>
/// Replayable process-local value pages. No Project wire format is involved.
/// After Seal, independent cursors share immutable pages/one read-only file handle.
/// </summary>
internal sealed class CompilerValueStore<T> : IEnumerable<T>, IDisposable, IRetainedStorageSource where T : unmanaged
{
    public const int PageCapacity = 4096;
    // Process-local format only. Bind the record type as well as its size: unrelated
    // structs with equal widths must not authenticate as interchangeable records.
    private static readonly byte[] RecordIdentity = SHA256.HashData(
        Encoding.UTF8.GetBytes(typeof(T).AssemblyQualifiedName!));
    private readonly CompilerStorageBudget _budget;
    private readonly long _registrationId;
    private readonly CancellationToken _token;
    private readonly CompilerMetadataList<Page> _pages;
    private readonly int _recordBytes = Unsafe.SizeOf<T>();
    private T[] _pending = [];
    private IDisposable? _pendingLease;
    private byte[] _compressionBuffer = [];
    private IDisposable? _compressionLease;
    private int _pendingCount;
    private long _resident, _spill, _ownedMetadata;
    private FileStream? _file;
    private string? _path;
    private bool _sealed, _disposed;

    public CompilerValueStore(CompilerStorageBudget budget, CancellationToken cancellationToken = default)
    {
        _budget = budget; _token = cancellationToken;
        _pages = new(budget); _registrationId = budget.Register(this);
    }
    public long Count { get; private set; }
    public bool IsSealed => _sealed;
    public long ResidentBytes => _resident;
    public long SpillBytes => _spill;
    public int PageCount => _pages.Count + (_pendingCount == 0 ? 0 : 1);
    // Published page indexes remain accounted for as long as this value source or any reader lives.
    public void ReserveOwnedMetadata(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.ReserveMetadata(bytes);
        _ownedMetadata += bytes;
    }
    public void ReleaseOwnedMetadata(long bytes)
    {
        if (bytes < 0 || bytes > _ownedMetadata) throw new ArgumentOutOfRangeException(nameof(bytes));
        _ownedMetadata -= bytes; _budget.ReleaseMetadata(bytes);
    }

    public void Add(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) throw new InvalidOperationException("The compiler value store is sealed.");
        if ((Count & 255) == 0) _token.ThrowIfCancellationRequested();
        if (_pendingCount == _pending.Length) GrowPending();
        _pending[_pendingCount++] = value;
        Count = checked(Count + 1);
        if (_pendingCount == PageCapacity) FlushPage();
    }
    private void GrowPending()
    {
        int capacity = Math.Min(PageCapacity, Math.Max(16, _pending.Length * 2));
        IDisposable lease = _budget.ReserveWorking((long)capacity * _recordBytes);
        try
        {
            T[] next = new T[capacity];
            _pending.AsSpan(0, _pendingCount).CopyTo(next);
            _pending = next;
        }
        catch { lease.Dispose(); throw; }
        _pendingLease?.Dispose();
        _pendingLease = lease;
    }
    public void Seal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) return;
        FlushPage();
        _file?.Flush();
        _pending = [];
        _pendingLease?.Dispose(); _pendingLease = null;
        ReleaseCompressionBuffer();
        _sealed = true;
    }
    private void FlushPage()
    {
        if (_pendingCount == 0) return;
        long bytes = (long)_pendingCount * _recordBytes;
        if (_budget.TryReserveResident(bytes))
        {
            try
            {
                T[] values = _pending.AsSpan(0, _pendingCount).ToArray();
                _pages.Add(new(values, 0, values.Length, 0, false, default));
                _resident += bytes;
            }
            catch { _budget.ReleaseResident(bytes); throw; }
        }
        else
        {
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(_pending.AsSpan(0, _pendingCount));
            EnsureCompressionBuffer();
            // The destination never grows. Incompressible pages keep their original bytes.
            using BoundedCompressionStream destination = new(_compressionBuffer, data.Length);
            using (DeflateStream compressor = new(destination, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(data);
            bool compressed = !destination.Overflowed && destination.BytesWritten < data.Length;
            ReadOnlySpan<byte> stored = compressed ? _compressionBuffer.AsSpan(0, destination.BytesWritten) : data;
            int storedBytes = stored.Length;
            _budget.ReserveSpill(storedBytes);
            try
            {
                EnsureFile();
                long offset = _file!.Position;
                PageHash checksum = Hash(stored, offset, _pendingCount, compressed);
                _file.Write(stored);
                _pages.Add(new(null, offset, _pendingCount, storedBytes, compressed, checksum));
                _spill += storedBytes;
            }
            catch { _budget.ReleaseSpill(storedBytes); throw; }
        }
        _pendingCount = 0;
    }
    private void EnsureCompressionBuffer()
    {
        if (_compressionBuffer.Length != 0) return;
        int bytes = checked(PageCapacity * _recordBytes);
        // Conservatively reserve codec scratch as well; this is not a process/CLR heap cap.
        IDisposable lease = _budget.ReserveWorking((long)bytes + (512 << 10));
        try { _compressionBuffer = new byte[bytes]; _compressionLease = lease; }
        catch { lease.Dispose(); throw; }
    }
    private void ReleaseCompressionBuffer()
    {
        _compressionBuffer = [];
        _compressionLease?.Dispose(); _compressionLease = null;
    }
    private void EnsureFile()
    {
        if (_file is not null) return;
        _path = _budget.NewFilePath();
        _file = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
    }

    public T this[long index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((ulong)index >= (ulong)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int pageIndex = checked((int)(index / PageCapacity));
            int local = (int)(index % PageCapacity);
            if (pageIndex == _pages.Count) return _pending[local];
            Page page = _pages[pageIndex];
            if (page.Values is not null) return page.Values[local];
            ValidatePage(page);
            // Random probes are exceptional; sequential consumers use one decoded page per cursor.
            using IDisposable lease = _budget.ReserveWorking((long)page.Count * _recordBytes);
            T[] buffer = new T[page.Count];
            byte[]? compressed = null;
            IDisposable? compressedLease = null;
            try { ReadPage(page, buffer, ref compressed, ref compressedLease); }
            finally { compressedLease?.Dispose(); }
            return buffer[local];
        }
    }

    public IEnumerable<T> Enumerate(bool reverse = false, CancellationToken cancellationToken = default) =>
        EnumerateRange(0, Count, reverse, cancellationToken);

    internal RandomReader OpenRandomReader(CancellationToken token = default) => new(this, token);

    // A cursor-local four-page cache for cold source sidecars. No shared mutable
    // cache or per-event buffer allocation; every actual spill read is verified.
    internal sealed class RandomReader : IDisposable
    {
        private const int CachedPageCount = 4;
        // Four directory arrays (including headers), reader/lease overhead and
        // the fixed decoded-buffer headers; record payloads are reserved below.
        internal const long DirectoryWorkingBytes = 1024;
        private readonly CompilerValueStore<T> _owner;
        private readonly CancellationToken _token;
        private readonly T[]?[] _buffers;
        private readonly long[] _indices;
        private readonly long[] _ages;
        private readonly IDisposable?[] _leases;
        private readonly IDisposable _directoryLease;
        private byte[]? _compressed;
        private IDisposable? _compressedLease;
        private long _clock;
        private bool _disposed;
        public RandomReader(CompilerValueStore<T> owner, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            _owner = owner;
            _token = token;
            _directoryLease = owner._budget.ReserveWorking(DirectoryWorkingBytes);
            try
            {
                _buffers = new T[CachedPageCount][];
                _indices = [-1, -1, -1, -1];
                _ages = new long[CachedPageCount];
                _leases = new IDisposable[CachedPageCount];
            }
            catch { _directoryLease.Dispose(); throw; }
        }
        public T Read(long index)
        {
            ObjectDisposedException.ThrowIf(_disposed || _owner._disposed, this);
            _token.ThrowIfCancellationRequested();
            if ((ulong)index >= (ulong)_owner.Count) throw new ArgumentOutOfRangeException(nameof(index));
            int pageIndex = checked((int)(index / PageCapacity)), local = (int)(index % PageCapacity);
            if (pageIndex == _owner._pages.Count) return _owner._pending[local];
            Page page = _owner._pages[pageIndex];
            if (page.Values is not null) return page.Values[local];
            for (int i = 0; i < CachedPageCount; i++)
                if (_indices[i] == pageIndex) { _ages[i] = ++_clock; return _buffers[i]![local]; }
            int slot = 0;
            for (int i = 1; i < CachedPageCount; i++) if (_ages[i] < _ages[slot]) slot = i;
            if (_buffers[slot] is null)
            {
                IDisposable lease = _owner._budget.ReserveWorking((long)PageCapacity * _owner._recordBytes);
                try { _buffers[slot] = new T[PageCapacity]; _leases[slot] = lease; }
                catch { lease.Dispose(); throw; }
            }
            _indices[slot] = -1;
            _owner.ReadPage(page, _buffers[slot]!, ref _compressed, ref _compressedLease);
            _indices[slot] = pageIndex; _ages[slot] = ++_clock;
            return _buffers[slot]![local];
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < CachedPageCount; i++) { _buffers[i] = null; _leases[i]?.Dispose(); }
            _compressed = null; _compressedLease?.Dispose(); _directoryLease.Dispose();
        }
    }

    // Only final publication calls this, after temporary sort/range buffers have been disposed.
    // Use the now-free shared resident budget for final immutable pages, rather than leaving
    // the budget empty while subsequent consumers repeatedly read those same pages from disk.
    public IEnumerable<T> EnumerateForPublication(CancellationToken cancellationToken = default)
    {
        if (!_sealed) throw new InvalidOperationException("Only sealed compiler pages can be published.");
        return EnumerateRange(0, Count, false, cancellationToken, retainReadPages: true);
    }

    public void ReleaseFullyResidentBacking()
    {
        if (!_sealed) throw new InvalidOperationException("Only sealed compiler pages can be published.");
        if (_file is null || _pages.Any(page => page.Values is null)) return;
        _file.Dispose(); _file = null;
        try
        {
            File.Delete(_path!); _path = null;
            _budget.ReleaseSpill(_spill); _spill = 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { /* All pages remain readable; the owned store still accounts for and cleans the file. */ }
    }

    public IEnumerable<T> EnumerateRange(long start, long count, bool reverse = false,
        CancellationToken cancellationToken = default, bool retainReadPages = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (start < 0 || count < 0 || start > Count - count) throw new ArgumentOutOfRangeException(nameof(start));
        if (count == 0) yield break;
        long first = start / PageCapacity, last = (start + count - 1) / PageCapacity;
        T[]? decoded = null;
        IDisposable? lease = null;
        byte[]? compressed = null;
        IDisposable? compressedLease = null;
        try
        {
            for (long pageIndex = reverse ? last : first;
                pageIndex >= first && pageIndex <= last; pageIndex += reverse ? -1 : 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                T[] values;
                int length;
                if (pageIndex == _pages.Count) { values = _pending; length = _pendingCount; }
                else
                {
                    Page page = _pages[checked((int)pageIndex)];
                    if (page.Values is null) ValidatePage(page);
                    length = page.Count;
                    if (page.Values is not null) values = page.Values;
                    else if (retainReadPages && _budget.TryReserveResident((long)page.Count * _recordBytes))
                    {
                        long bytes = (long)page.Count * _recordBytes;
                        try
                        {
                            values = new T[page.Count];
                            ReadPage(page, values, ref compressed, ref compressedLease);
                            _pages[checked((int)pageIndex)] = page with { Values = values };
                            _resident += bytes;
                        }
                        catch { _budget.ReleaseResident(bytes); throw; }
                    }
                    else
                    {
                        if (decoded is null)
                        {
                            lease = _budget.ReserveWorking((long)PageCapacity * _recordBytes);
                            decoded = new T[PageCapacity];
                        }
                        ReadPage(page, decoded, ref compressed, ref compressedLease);
                        values = decoded;
                    }
                }
                int lo = (int)Math.Max(0, start - pageIndex * PageCapacity);
                int hi = (int)Math.Min(length, start + count - pageIndex * PageCapacity);
                for (int i = reverse ? hi - 1 : lo; i >= lo && i < hi; i += reverse ? -1 : 1)
                {
                    if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    yield return values[i];
                }
            }
        }
        finally { lease?.Dispose(); compressedLease?.Dispose(); }
    }
    private void ReadPage(Page page, T[] buffer, ref byte[]? compressed, ref IDisposable? compressedLease)
    {
        ValidatePage(page);
        Span<byte> all = MemoryMarshal.AsBytes(buffer.AsSpan(0, page.Count));
        if (page.Compressed && compressed is null)
        {
            int bytes = checked(PageCapacity * _recordBytes);
            IDisposable lease = _budget.ReserveWorking((long)bytes + 65536);
            try { compressed = new byte[bytes]; compressedLease = lease; }
            catch { lease.Dispose(); throw; }
        }
        Span<byte> stored = page.Compressed ? compressed.AsSpan(0, page.StoredBytes) : all;
        Span<byte> remaining = stored;
        long offset = page.Offset;
        while (!remaining.IsEmpty)
        {
            int count = RandomAccess.Read(_file!.SafeFileHandle, remaining, offset);
            if (count == 0) throw new EndOfStreamException("Logical compilation page storage is truncated.");
            remaining = remaining[count..]; offset += count;
        }
        if (Hash(stored, page.Offset, page.Count, page.Compressed) != page.Hash)
            throw new InvalidDataException("Logical compilation page checksum mismatch.");
        if (page.Compressed)
        {
            using MemoryStream memory = new(compressed!, 0, page.StoredBytes, writable: false);
            using DeflateStream decoder = new(memory, CompressionMode.Decompress);
            decoder.ReadExactly(all);
            if (decoder.ReadByte() != -1) throw new InvalidDataException("Logical compilation page has an invalid decoded length.");
        }
    }
    private void ValidatePage(Page page)
    {
        if (page.Count <= 0 || page.Count > PageCapacity || page.Offset < 0
            || page.Offset > long.MaxValue - page.StoredBytes
            || page.StoredBytes <= 0 || page.StoredBytes > (long)page.Count * _recordBytes
            || page.Compressed == (page.StoredBytes == (long)page.Count * _recordBytes))
            throw new InvalidDataException("Logical compilation page descriptor is invalid.");
    }
    private static PageHash Hash(ReadOnlySpan<byte> bytes, long offset, int count, bool compressed)
    {
        Span<byte> descriptor = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, 2);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[4..], Unsafe.SizeOf<T>());
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], count);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[12..], checked(count * Unsafe.SizeOf<T>()));
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[16..], bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[20..], compressed ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(descriptor[24..], offset);
        using IncrementalHash checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        checksum.AppendData(RecordIdentity);
        checksum.AppendData(descriptor);
        checksum.AppendData(bytes);
        Span<byte> hash = stackalloc byte[32];
        checksum.GetHashAndReset(hash);
        return MemoryMarshal.Read<PageHash>(hash);
    }
    public IEnumerator<T> GetEnumerator() => Enumerate().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 160)) return;
        _budget.CollectRetainedStorage(collector);
        _pages.CollectRetainedStorage(collector);
        collector.Array(_pending);
        collector.Array(_compressionBuffer);
        foreach (Page page in _pages) if (page.Values is not null) collector.Array(page.Values);
    }
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }
    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        _pending = [];
        _pendingLease?.Dispose(); _pendingLease = null;
        ReleaseCompressionBuffer();
        try
        {
            _file?.Dispose(); _file = null;
            if (_path is not null)
            {
                try { File.Delete(_path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _pages.Dispose();
            _budget.ReleaseResident(_resident); _budget.ReleaseSpill(_spill);
            _budget.ReleaseMetadata(_ownedMetadata);
            _budget.Unregister(_registrationId);
            _resident = _spill = _ownedMetadata = 0;
        }
    }
    ~CompilerValueStore() { try { DisposeCore(); } catch { /* Owned startup cleanup remains available. */ } }
    private readonly record struct Page(T[]? Values, long Offset, int Count, int StoredBytes, bool Compressed, PageHash Hash);
    private readonly record struct PageHash(ulong A, ulong B, ulong C, ulong D);

    private sealed class BoundedCompressionStream(byte[] buffer, int capacity) : Stream
    {
        public int BytesWritten { get; private set; }
        public bool Overflowed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (Overflowed) return;
            if (bytes.Length > capacity - BytesWritten) { Overflowed = true; return; }
            bytes.CopyTo(buffer.AsSpan(BytesWritten));
            BytesWritten += bytes.Length;
        }
    }
}

internal sealed class CompilerExternalSorter<T> : IDisposable where T : unmanaged
{
    private readonly CompilerStorageBudget _budget;
    private readonly IComparer<T> _comparer;
    private readonly CancellationToken _token;
    private readonly int _runSize, _fanIn;
    private readonly List<T> _buffer = new(16);
    private CompilerMetadataList<Run> _runs;
    private CompilerValueStore<T>? _store;
    private IDisposable? _bufferLease;
    private bool _reading;
    public CompilerExternalSorter(CompilerStorageBudget budget, IComparer<T> comparer,
        CancellationToken cancellationToken = default, int runSize = 32768, int fanIn = 16)
    {
        if (runSize < 1 || fanIn < 2 || fanIn > 64) throw new ArgumentOutOfRangeException(nameof(runSize));
        _budget = budget; _comparer = comparer; _token = cancellationToken; _runSize = runSize; _fanIn = fanIn;
        _runs = new(budget);
        // List growth temporarily owns both old and new capacity. Account for
        // the rounded capacity, including small/non-power-of-two custom runs.
        long capacity = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, runSize));
        _bufferLease = budget.ReserveWorking(checked(2 * capacity * Unsafe.SizeOf<T>()));
    }
    public void Add(T value)
    {
        if (_reading) throw new InvalidOperationException("The compiler sorter is sealed.");
        if ((_buffer.Count & 255) == 0) _token.ThrowIfCancellationRequested();
        _buffer.Add(value);
        if (_buffer.Count == _runSize) Flush();
    }
    private void Flush()
    {
        if (_buffer.Count == 0) return;
        _buffer.Sort(_comparer);
        _token.ThrowIfCancellationRequested();
        _store ??= new(_budget, _token);
        long start = _store.Count;
        foreach (T value in _buffer) _store.Add(value);
        _runs.Add(new(start, _buffer.Count)); _buffer.Clear();
    }
    public IEnumerable<T> ReadSorted(CancellationToken cancellationToken = default)
    {
        if (_reading) throw new InvalidOperationException("The compiler sorter can only be read once.");
        _reading = true;
        if (_store is null)
        {
            _buffer.Sort(_comparer);
            for (int i = 0; i < _buffer.Count; i++)
            {
                if ((i & 255) == 0) { _token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested(); }
                yield return _buffer[i];
            }
            yield break;
        }
        Flush(); _store.Seal();
        _buffer.Clear(); _buffer.TrimExcess(); _bufferLease?.Dispose(); _bufferLease = null;
        while (_runs.Count > _fanIn)
        {
            var next = new CompilerValueStore<T>(_budget, _token);
            CompilerMetadataList<Run>? runs = new(_budget);
            try
            {
                for (int i = 0; i < _runs.Count; i += _fanIn)
                {
                    long start = next.Count;
                    foreach (T value in Merge(_store, _runs, i, Math.Min(_fanIn, _runs.Count - i), cancellationToken)) next.Add(value);
                    runs.Add(new(start, next.Count - start));
                }
                next.Seal();
                _store.Dispose(); _store = next; next = null!;
                _runs.Dispose(); _runs = runs; runs = null;
            }
            finally { try { next?.Dispose(); } finally { runs?.Dispose(); } }
        }
        foreach (T value in Merge(_store, _runs, 0, _runs.Count, cancellationToken)) yield return value;
    }
    private IEnumerable<T> Merge(CompilerValueStore<T> store, IReadOnlyList<Run> runs,
        int startRun, int runCount, CancellationToken token)
    {
        long metadataBytes = checked(CompilerStorageBudget.MetadataArrayBytes<IEnumerator<T>>(runCount)
            + CompilerStorageBudget.MetadataArrayBytes<T>(runCount)
            + CompilerStorageBudget.MetadataArrayBytes<(int, int)>(runCount));
        _budget.ReserveMetadata(metadataBytes);
        IEnumerator<T>?[]? readers = null;
        try
        {
            T[] heads = new T[runCount];
            var queue = new PriorityQueue<int, int>(runCount, new HeadComparer(heads, _comparer));
            readers = new IEnumerator<T>?[runCount];
            for (int i = 0; i < runCount; i++)
            {
                Run run = runs[startRun + i];
                var reader = store.EnumerateRange(run.Start, run.Count, false, token).GetEnumerator();
                readers[i] = reader;
                if (reader.MoveNext()) { heads[i] = reader.Current; queue.Enqueue(i, i); }
            }
            int index = 0;
            while (queue.TryDequeue(out int readerIndex, out _))
            {
                if ((index++ & 255) == 0) { token.ThrowIfCancellationRequested(); _token.ThrowIfCancellationRequested(); }
                yield return heads[readerIndex];
                IEnumerator<T> reader = readers[readerIndex]!;
                // Only the removed cursor's head can change. Heads of queued cursors stay immutable,
                // so the heap compares exactly the same values without repeatedly moving large T's.
                if (reader.MoveNext())
                {
                    heads[readerIndex] = reader.Current;
                    queue.Enqueue(readerIndex, readerIndex);
                }
            }
        }
        finally
        {
            try { if (readers is not null) foreach (var reader in readers) reader?.Dispose(); }
            finally { _budget.ReleaseMetadata(metadataBytes); }
        }
    }
    private sealed class HeadComparer(T[] heads, IComparer<T> comparer) : IComparer<int>
    {
        public int Compare(int left, int right) => comparer.Compare(heads[left], heads[right]);
    }
    public void Dispose()
    {
        try { _store?.Dispose(); _store = null; }
        finally
        {
            _buffer.Clear(); _buffer.TrimExcess(); _bufferLease?.Dispose(); _bufferLease = null;
            _runs.Dispose();
        }
    }
    private readonly record struct Run(long Start, long Count);
}
