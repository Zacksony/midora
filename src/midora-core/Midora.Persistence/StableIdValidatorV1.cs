using System.Numerics;
using Midora.Common;
using Midora.Domain;

namespace Midora.Persistence;

/// <summary>
/// Exact, package-wide ID validation. Sparse 16-bit blocks promote to bitmaps;
/// if the resident budget is exhausted, validation finishes through bounded
/// sorted runs. It never renumbers IDs or assumes that allocator gaps are small.
/// The caller must Complete before publishing and Dispose on every exit path.
/// </summary>
internal sealed class StableIdValidatorV1 : IDisposable
{
    internal const long DefaultMemoryBudgetBytes = 8L * 1024 * 1024;
    private const int RecordBytes = 24;
    private static readonly string[] Sources =
    [
        "Conductor event", "Event Instrument object", "Event Instrument Usage",
        "Logical Track object", "MIDI Channel Root", "Pure MIDI Track object",
        "Event Instrument", "Event Instrument nested object", "Arrangement Track",
        "Logical Track nested object", "Pure MIDI Track nested object"
    ];
    private readonly CancellationToken _cancellationToken;
    private readonly string? _temporaryRoot;
    private readonly int _chunkCapacity;
    private readonly int _fileBufferBytes;
    private readonly long _spillReserve;
    private readonly long _controlAllowance;
    private long[] _keys = [];
    private Block?[] _blocks = [];
    private int _blockCount;
    private long _blockBytes;
    private long _lastKey;
    private Block? _lastBlock;
    private IdRecord[]? _chunk;
    private int _chunkCount;
    private readonly Run?[] _runs = new Run?[64];
    private MidoraOwnedTemporaryDirectoryLease? _directory;
    private long _ordinal;
    private long _nextRun;
    private long _duplicateOrdinal = long.MaxValue;
    private int _duplicateSource;
    private bool _completed;
    private bool _disposed;

    public StableIdValidatorV1(
        CancellationToken cancellationToken = default,
        long memoryBudgetBytes = DefaultMemoryBudgetBytes,
        string? temporaryRoot = null,
        int chunkCapacity = 32_768,
        int fileBufferBytes = 65_536)
    {
        if (chunkCapacity <= 0 || fileBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        // Reserve the bounded 64-run directory and transient path strings too;
        // no per-ID dictionary or per-run unbounded list is hidden in the budget.
        int maximumRunPathChars = checked((temporaryRoot ?? AppContext.BaseDirectory).Length + 128);
        _controlAllowance = checked(16_384L + 70 * (64 + ArrayBytes(maximumRunPathChars, sizeof(char))));
        _spillReserve = checked(ArrayBytes(chunkCapacity, RecordBytes) + 3L * (fileBufferBytes + 256));
        if (memoryBudgetBytes < _spillReserve + _controlAllowance + 32_768)
            throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        MemoryBudgetBytes = memoryBudgetBytes;
        _cancellationToken = cancellationToken;
        _temporaryRoot = temporaryRoot;
        _chunkCapacity = chunkCapacity;
        _fileBufferBytes = fileBufferBytes;
        ObserveResident();
    }

    public long MemoryBudgetBytes { get; }
    // Array capacities are exact; fixed-size control objects and up to three
    // FileStream buffers are conservatively reserved, including during merges.
    public long AccountedResidentBytes => _disposed ? 0 : checked(_controlAllowance
        + ArrayBytes(_keys.Length, sizeof(long)) + ArrayBytes(_blocks.Length, IntPtr.Size)
        + _blockBytes + (_chunk is null ? 0 : _spillReserve));
    public long PeakResidentBytes { get; private set; }
    public long LiveSpillBytes { get; private set; }
    public long PeakSpillBytes { get; private set; }
    public long TotalSpillBytesWritten { get; private set; }
    public long InputCount => _ordinal;
    public bool HasSpilled => _directory is not null;
    internal string? SpillDirectory => _directory?.DirectoryPath;

    public void Add(MidoraId id, long nextStableId, string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Stable ID validation is already complete.");
        if ((_ordinal & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
        if (id.Value <= 0 || id.Value >= nextStableId)
        {
            // An earlier duplicate in a spilled run still wins over this later
            // range error, exactly as it did in the original online validator.
            Complete();
            throw Failure(source);
        }
        if (_directory is null && TryAddResident(id.Value, out bool added))
        {
            if (!added) throw Failure(source);
            _ordinal = checked(_ordinal + 1);
            return;
        }
        if (_directory is null) BeginSpill();
        int sourceIndex = Array.IndexOf(Sources, source);
        if (sourceIndex < 0) throw new ArgumentException("Unknown stable ID validation source.", nameof(source));
        Append(new(id.Value, _ordinal, sourceIndex));
        _ordinal = checked(_ordinal + 1);
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return;
        _cancellationToken.ThrowIfCancellationRequested();
        if (_directory is not null)
        {
            FlushChunk();
            Run? accumulated = null;
            for (int i = 0; i < _runs.Length; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_runs[i] is not Run run) continue;
                _runs[i] = null;
                accumulated = accumulated is null ? run : Merge(accumulated, run);
            }
            // Keep the final run owned until Dispose. No data is published by
            // this validator; successful completion is the sole commit gate.
            _runs[0] = accumulated;
            if (accumulated is not null)
            {
                using var reader = new RunReader(accumulated, _fileBufferBytes, _ordinal);
                long count = 0;
                while (reader.Read())
                    if ((count++ & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
            }
        }
        if (_duplicateOrdinal != long.MaxValue) throw Failure(Sources[_duplicateSource]);
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keys = [];
        _blocks = [];
        _lastBlock = null;
        _chunk = null;
        Array.Clear(_runs);
        _blockBytes = 0;
        _directory?.Dispose();
        LiveSpillBytes = 0;
    }

    private static InvalidDataException Failure(string source) =>
        new($"{source} stable ID is zero, duplicated, or not below nextStableId.");

    private bool TryAddResident(long value, out bool added)
    {
        long key = (value >> 16) + 1; // zero is the empty hash slot, not ID zero.
        Block? block = _lastKey == key ? _lastBlock : FindBlock(key);
        if (block is null)
        {
            if (_keys.Length == 0 || (_blockCount + 1L) * 10 >= _keys.Length * 7L)
            {
                int capacity = _keys.Length == 0 ? 16 : checked(_keys.Length * 2);
                long extra = ArrayBytes(capacity, sizeof(long)) + ArrayBytes(capacity, IntPtr.Size);
                if (!CanAllocate(extra + Block.InitialBytes)) { added = false; return false; }
                var keys = new long[capacity];
                var blocks = new Block?[capacity];
                ObserveResident(extra);
                for (int i = 0; i < _keys.Length; i++)
                {
                    if (_keys[i] == 0) continue;
                    int slot = FindSlot(keys, _keys[i]);
                    keys[slot] = _keys[i];
                    blocks[slot] = _blocks[i];
                }
                _keys = keys;
                _blocks = blocks;
            }
            if (!CanAllocate(Block.InitialBytes)) { added = false; return false; }
            block = new();
            int index = FindSlot(_keys, key);
            _keys[index] = key;
            _blocks[index] = block;
            _blockCount++;
            _blockBytes += block.AccountedBytes;
            ObserveResident();
        }
        _lastKey = key;
        _lastBlock = block;
        ushort offset = (ushort)value;
        if (block.Contains(offset)) { added = false; return true; }
        long newAllocation = block.NextAllocationBytes;
        if (newAllocation == 0)
        {
            block.AddKnownAbsent(offset);
            added = true;
            return true;
        }
        if (!CanAllocate(newAllocation)) { added = false; return false; }
        ObserveResident(newAllocation);
        long before = block.AccountedBytes;
        block.AddKnownAbsent(offset);
        _blockBytes += block.AccountedBytes - before;
        added = true;
        return true;
    }

    private Block? FindBlock(long key) => _keys.Length == 0 ? null : _blocks[FindSlot(_keys, key)];

    private static int FindSlot(long[] keys, long key)
    {
        ulong hash = unchecked((ulong)key * 11400714819323198485UL);
        int slot = (int)((hash ^ (hash >> 32)) & (uint)(keys.Length - 1));
        while (keys[slot] != 0 && keys[slot] != key) slot = (slot + 1) & (keys.Length - 1);
        return slot;
    }

    private bool CanAllocate(long extra) =>
        checked(AccountedResidentBytes + extra + (_chunk is null ? _spillReserve : 0)) <= MemoryBudgetBytes;

    private void ObserveResident(long temporaryAllocation = 0) =>
        PeakResidentBytes = Math.Max(PeakResidentBytes, checked(AccountedResidentBytes + temporaryAllocation));

    private static long ArrayBytes(int count, int elementBytes) => count == 0 ? 0 :
        checked(24L + ((count * (long)elementBytes + 7) & ~7L));

    private void BeginSpill()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _directory = MidoraOwnedTemporaryDirectoryLease.Create(
            _temporaryRoot ?? MidoraProgramData.Current.CompilerRunsDirectory, "stable-ids");
        _chunk = new IdRecord[_chunkCapacity];
        ObserveResident();
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] == 0) continue;
            long prefix = (_keys[i] - 1) << 16;
            foreach (ushort offset in _blocks[i]!.Enumerate())
            {
                // The prefix was already checked online. Only the later
                // occurrence's ordinal/source is needed for a duplicate.
                Append(new(prefix | offset, -1, 0));
            }
        }
        _keys = [];
        _blocks = [];
        _lastBlock = null;
        _blockBytes = 0;
    }

    private void Append(IdRecord value)
    {
        if ((_chunkCount & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
        _chunk![_chunkCount++] = value;
        if (_chunkCount == _chunk.Length) FlushChunk();
    }

    private void FlushChunk()
    {
        if (_chunkCount == 0) return;
        _cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(_chunk!, 0, _chunkCount, IdRecordComparer.Instance);
        _cancellationToken.ThrowIfCancellationRequested();
        Run run;
        using (RunWriter writer = CreateWriter())
        {
            IdRecord? previous = null;
            for (int i = 0; i < _chunkCount; i++)
            {
                if ((i & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
                IdRecord value = _chunk![i];
                if (previous is IdRecord prior && prior.Value == value.Value) RecordDuplicate(value);
                else { writer.Write(value); previous = value; }
            }
            run = writer.Complete();
        }
        _chunkCount = 0;
        for (int level = 0; ; level++)
        {
            if (_runs[level] is not Run prior) { _runs[level] = run; break; }
            _runs[level] = null;
            run = Merge(prior, run);
        }
    }

    private Run Merge(Run leftRun, Run rightRun)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        Run result;
        using (var left = new RunReader(leftRun, _fileBufferBytes, _ordinal))
        using (var right = new RunReader(rightRun, _fileBufferBytes, _ordinal))
        using (RunWriter writer = CreateWriter())
        {
            bool hasLeft = left.Read(), hasRight = right.Read();
            long count = 0;
            while (hasLeft || hasRight)
            {
                if ((count++ & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
                if (hasLeft && hasRight && left.Current.Value == right.Current.Value)
                {
                    IdRecord first = left.Current.Ordinal <= right.Current.Ordinal ? left.Current : right.Current;
                    IdRecord second = left.Current.Ordinal <= right.Current.Ordinal ? right.Current : left.Current;
                    RecordDuplicate(second);
                    writer.Write(first);
                    hasLeft = left.Read(); hasRight = right.Read();
                }
                else if (hasLeft && (!hasRight || left.Current.Value < right.Current.Value))
                { writer.Write(left.Current); hasLeft = left.Read(); }
                else { writer.Write(right.Current); hasRight = right.Read(); }
            }
            result = writer.Complete();
        }
        DeleteRun(leftRun);
        DeleteRun(rightRun);
        return result;
    }

    private void RecordDuplicate(IdRecord value)
    {
        if (value.Ordinal < 0) throw new InvalidDataException("The stable ID validation prefix contains a duplicate.");
        if (value.Ordinal < _duplicateOrdinal)
        { _duplicateOrdinal = value.Ordinal; _duplicateSource = checked((int)value.Source); }
    }

    private RunWriter CreateWriter() => new(this,
        Path.Combine(_directory!.DirectoryPath, $"run-{_nextRun++}.ids"), _fileBufferBytes);

    private void DeleteRun(Run run)
    {
        File.Delete(run.Path);
        LiveSpillBytes -= checked(run.Count * RecordBytes);
    }

    private sealed class Block
    {
        public const long InitialBytes = 48 + 24 + 8;
        private ushort[]? _sparse = new ushort[4];
        private ulong[]? _dense;
        private int _count;
        public long AccountedBytes => 48 + (_sparse is not null
            ? ArrayBytes(_sparse.Length, sizeof(ushort)) : ArrayBytes(_dense!.Length, sizeof(ulong)));
        public long NextAllocationBytes => _sparse is null || _count < _sparse.Length ? 0 :
            _sparse.Length == 4096 ? ArrayBytes(1024, sizeof(ulong)) : ArrayBytes(_sparse.Length * 2, sizeof(ushort));
        public bool Contains(ushort value) => _dense is not null
            ? (_dense[value >> 6] & (1UL << (value & 63))) != 0
            : _count != 0 && value <= _sparse![_count - 1]
                && Array.BinarySearch(_sparse, 0, _count, value) >= 0;
        public void AddKnownAbsent(ushort value)
        {
            if (_sparse is not null && _count == _sparse.Length)
            {
                if (_sparse.Length == 4096)
                {
                    _dense = new ulong[1024];
                    foreach (ushort item in _sparse) _dense[item >> 6] |= 1UL << (item & 63);
                    _sparse = null;
                }
                else Array.Resize(ref _sparse, _sparse.Length * 2);
            }
            if (_dense is not null) _dense[value >> 6] |= 1UL << (value & 63);
            else
            {
                int index = _count == 0 || value > _sparse![_count - 1]
                    ? _count : ~Array.BinarySearch(_sparse, 0, _count, value);
                Array.Copy(_sparse!, index, _sparse!, index + 1, _count - index);
                _sparse![index] = value;
            }
            _count++;
        }
        public IEnumerable<ushort> Enumerate()
        {
            if (_sparse is not null)
            { for (int i = 0; i < _count; i++) yield return _sparse[i]; }
            else for (int i = 0; i < _dense!.Length; i++)
            {
                ulong word = _dense[i];
                while (word != 0)
                { int bit = BitOperations.TrailingZeroCount(word); yield return (ushort)(i * 64 + bit); word &= word - 1; }
            }
        }
    }

    private readonly record struct IdRecord(long Value, long Ordinal, long Source);
    private sealed record Run(string Path, long Count);
    private sealed class IdRecordComparer : IComparer<IdRecord>
    {
        public static readonly IdRecordComparer Instance = new();
        public int Compare(IdRecord x, IdRecord y)
        { int value = x.Value.CompareTo(y.Value); return value != 0 ? value : x.Ordinal.CompareTo(y.Ordinal); }
    }
    private sealed class RunReader : IDisposable
    {
        private readonly BinaryReader _reader;
        private long _remaining;
        private long _previous;
        private readonly long _maximumOrdinal;
        public RunReader(Run run, int bufferBytes, long maximumOrdinal)
        {
            _maximumOrdinal = maximumOrdinal;
            var stream = new FileStream(run.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferBytes, FileOptions.SequentialScan);
            try
            {
                if (stream.Length != checked(run.Count * RecordBytes))
                    throw new InvalidDataException("A stable ID validation run has an invalid extent.");
                _reader = new(stream);
                _remaining = run.Count;
            }
            catch { stream.Dispose(); throw; }
        }
        public IdRecord Current { get; private set; }
        public bool Read()
        {
            if (_remaining == 0) return false;
            IdRecord next = new(_reader.ReadInt64(), _reader.ReadInt64(), _reader.ReadInt64());
            if (next.Value <= _previous || next.Ordinal < -1 || next.Ordinal > _maximumOrdinal
                || (ulong)next.Source >= (ulong)Sources.Length)
                throw new InvalidDataException("A stable ID validation run contains an invalid record.");
            Current = next; _previous = next.Value; _remaining--;
            return true;
        }
        public void Dispose() => _reader.Dispose();
    }
    private sealed class RunWriter : IDisposable
    {
        private readonly StableIdValidatorV1 _owner;
        private readonly BinaryWriter _writer;
        private readonly string _path;
        private long _count;
        public RunWriter(StableIdValidatorV1 owner, string path, int bufferBytes)
        {
            _owner = owner; _path = path;
            _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferBytes, FileOptions.SequentialScan));
        }
        public void Write(IdRecord value)
        {
            _writer.Write(value.Value); _writer.Write(value.Ordinal); _writer.Write(value.Source);
            _count++;
            _owner.LiveSpillBytes = checked(_owner.LiveSpillBytes + RecordBytes);
            _owner.TotalSpillBytesWritten = checked(_owner.TotalSpillBytesWritten + RecordBytes);
            _owner.PeakSpillBytes = Math.Max(_owner.PeakSpillBytes, _owner.LiveSpillBytes);
        }
        public Run Complete() { _writer.Flush(); return new(_path, _count); }
        public void Dispose() => _writer.Dispose();
    }
}
