using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Midora.Domain;
using Midora.Midi;
using Midora.Common;

namespace Midora.Compiler;

internal sealed class BoundedCanonicalSmfEventSorter : IDisposable
{
    private const int SortRunRecordCount = 131_072;
    private const int MaximumMergeFanIn = 64;
    private const int ReadBatchRecordCount = 256;
    private const int RecordByteCount = 40;
    private readonly int _sortRunRecordCount;
    private readonly int _maximumMergeFanIn;
    private readonly List<CanonicalSmfTrackChannelEvent> _buffer;
    private readonly List<RunDescriptor> _runs = [];
    private MidoraOwnedTemporaryDirectoryLease? _runDirectoryLease;
    private FileStream? _runFile;
    private string? _runPath;
    private bool _reading;
    private bool _disposed;

    public BoundedCanonicalSmfEventSorter()
        : this(SortRunRecordCount, MaximumMergeFanIn)
    {
    }

    internal BoundedCanonicalSmfEventSorter(
        int sortRunRecordCount,
        int maximumMergeFanIn)
    {
        if (sortRunRecordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sortRunRecordCount));
        if (maximumMergeFanIn < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumMergeFanIn));
        _sortRunRecordCount = sortRunRecordCount;
        _maximumMergeFanIn = maximumMergeFanIn;
        _buffer = new(sortRunRecordCount);
    }

    public void Add(CanonicalSmfTrackChannelEvent value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded SMF sorter is already sealed.");
        _buffer.Add(value);
        if (_buffer.Count == _sortRunRecordCount) FlushRun();
    }

    public IEnumerable<CanonicalSmfTrackChannelEventPage> ReadPages(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded SMF sorter can only be read once.");
        _reading = true;
        if (_runFile is null)
        {
            _buffer.Sort(Comparer.Instance);
            foreach (CanonicalSmfTrackChannelEventPage page in Page(_buffer, cancellationToken))
                yield return page;
            yield break;
        }

        if (_buffer.Count != 0) FlushRun();
        _runFile.Flush(flushToDisk: false);
        CollapseRuns(cancellationToken);

        List<CanonicalSmfTrackChannelEvent> output = new(
            CanonicalSmfTrackChannelEventPage.MaximumRecordCount);
        foreach (CanonicalSmfTrackChannelEvent value in Merge(
            _runFile.SafeFileHandle,
            _runs,
            cancellationToken))
        {
            output.Add(value);
            if (output.Count != CanonicalSmfTrackChannelEventPage.MaximumRecordCount) continue;
            yield return new(output.ToArray());
            output.Clear();
        }
        if (output.Count != 0) yield return new(output.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runFile?.Dispose();
        _runFile = null;
        _runDirectoryLease?.Dispose();
        _runDirectoryLease = null;
    }

    private void FlushRun()
    {
        if (_buffer.Count == 0) return;
        _buffer.Sort(Comparer.Instance);
        EnsureRunFile();
        long offset = _runFile!.Position;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(ReadBatchRecordCount * RecordByteCount);
        try
        {
            int index = 0;
            while (index < _buffer.Count)
            {
                int count = Math.Min(ReadBatchRecordCount, _buffer.Count - index);
                Span<byte> destination = bytes.AsSpan(0, count * RecordByteCount);
                for (int local = 0; local < count; local++)
                    WriteRecord(destination.Slice(local * RecordByteCount, RecordByteCount), _buffer[index + local]);
                _runFile.Write(destination);
                index += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        _runs.Add(new(offset, _buffer.Count));
        _buffer.Clear();
    }

    private void CollapseRuns(CancellationToken cancellationToken)
    {
        int pass = 0;
        while (_runs.Count > _maximumMergeFanIn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string nextPath = Path.Combine(
                Path.GetDirectoryName(_runPath!)!,
                $"smf-channel-pass-{++pass}.runs");
            FileStream next = OpenRunFile(nextPath);
            List<RunDescriptor> nextRuns = [];
            try
            {
                for (int offset = 0; offset < _runs.Count; offset += _maximumMergeFanIn)
                {
                    RunDescriptor[] group = _runs
                        .Skip(offset)
                        .Take(_maximumMergeFanIn)
                        .ToArray();
                    long runOffset = next.Position;
                    int count = 0;
                    byte[] bytes = ArrayPool<byte>.Shared.Rent(ReadBatchRecordCount * RecordByteCount);
                    try
                    {
                        int buffered = 0;
                        foreach (CanonicalSmfTrackChannelEvent value in Merge(
                            _runFile!.SafeFileHandle,
                            group,
                            cancellationToken))
                        {
                            WriteRecord(
                                bytes.AsSpan(buffered * RecordByteCount, RecordByteCount),
                                value);
                            buffered++;
                            count++;
                            if (buffered != ReadBatchRecordCount) continue;
                            next.Write(bytes, 0, buffered * RecordByteCount);
                            buffered = 0;
                        }
                        if (buffered != 0) next.Write(bytes, 0, buffered * RecordByteCount);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                    nextRuns.Add(new(runOffset, count));
                }
                next.Flush(flushToDisk: false);
            }
            finally
            {
                next.Dispose();
            }
            _runFile!.Dispose();
            File.Delete(_runPath!);
            _runPath = nextPath;
            _runFile = OpenRunFile(nextPath, FileMode.Open);
            _runFile.Position = _runFile.Length;
            _runs.Clear();
            _runs.AddRange(nextRuns);
        }
    }

    private void EnsureRunFile()
    {
        if (_runFile is not null) return;
        _runDirectoryLease = MidoraOwnedTemporaryDirectoryLease.Create(
            MidoraProgramData.Current.CompilerRunsDirectory,
            "smf-sort");
        _runPath = Path.Combine(_runDirectoryLease.DirectoryPath, "smf-channel.runs");
        _runFile = OpenRunFile(_runPath);
    }

    private static FileStream OpenRunFile(
        string path,
        FileMode mode = FileMode.CreateNew) => new(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete,
            256 * 1024,
            FileOptions.RandomAccess);

    private static IEnumerable<CanonicalSmfTrackChannelEvent> Merge(
        SafeFileHandle handle,
        IReadOnlyList<RunDescriptor> runs,
        CancellationToken cancellationToken)
    {
        PriorityQueue<RunReader, CanonicalSmfTrackChannelEvent> queue = new(Comparer.Instance);
        foreach (RunDescriptor descriptor in runs)
        {
            RunReader reader = new(handle, descriptor);
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
        while (queue.TryDequeue(out RunReader? reader, out CanonicalSmfTrackChannelEvent value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
    }

    private static IEnumerable<CanonicalSmfTrackChannelEventPage> Page(
        IReadOnlyList<CanonicalSmfTrackChannelEvent> source,
        CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < source.Count;
            offset += CanonicalSmfTrackChannelEventPage.MaximumRecordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(
                CanonicalSmfTrackChannelEventPage.MaximumRecordCount,
                source.Count - offset);
            CanonicalSmfTrackChannelEvent[] page = new CanonicalSmfTrackChannelEvent[count];
            for (int index = 0; index < count; index++) page[index] = source[offset + index];
            yield return new(page);
        }
    }

    private static void WriteRecord(Span<byte> destination, CanonicalSmfTrackChannelEvent value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Tick);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], value.ExportTrackId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], value.EventOrder);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], value.SourceObjectId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], value.Message.PackedValue);
        destination[36] = value.ZeroBasedPort;
        destination[37] = value.ZeroBasedChannel;
        destination[38] = (byte)value.Role;
        destination[39] = 0;
    }

    private static CanonicalSmfTrackChannelEvent ReadRecord(ReadOnlySpan<byte> source) => new(
        MidoraId.FromSequence(BinaryPrimitives.ReadInt64LittleEndian(source[8..])),
        BinaryPrimitives.ReadInt64LittleEndian(source),
        source[36],
        source[37],
        MidiMessage.FromPackedValue(BinaryPrimitives.ReadUInt32LittleEndian(source[32..])),
        (CanonicalEventRole)source[38],
        BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
        ReadOptionalSourceId(source[24..]));

    private static MidoraId ReadOptionalSourceId(ReadOnlySpan<byte> source)
    {
        // Root lifecycle/default events have a required ExportTrackId, but no
        // direct source object. Preserve that absence across spill/merge just
        // as the resident path does; negative/corrupt IDs must still fail.
        long value = BinaryPrimitives.ReadInt64LittleEndian(source);
        return value == 0 ? default : MidoraId.FromSequence(value);
    }

    private readonly record struct RunDescriptor(long Offset, int RecordCount);

    private sealed class RunReader
    {
        private readonly SafeFileHandle _handle;
        private readonly byte[] _buffer = new byte[ReadBatchRecordCount * RecordByteCount];
        private long _offset;
        private int _remaining;
        private int _bufferIndex;
        private int _bufferCount;

        public RunReader(SafeFileHandle handle, RunDescriptor descriptor)
        {
            _handle = handle;
            _offset = descriptor.Offset;
            _remaining = descriptor.RecordCount;
        }

        public CanonicalSmfTrackChannelEvent Current { get; private set; }

        public bool MoveNext()
        {
            if (_remaining == 0) return false;
            if (_bufferIndex == _bufferCount) Fill();
            Current = ReadRecord(_buffer.AsSpan(_bufferIndex * RecordByteCount, RecordByteCount));
            _bufferIndex++;
            _remaining--;
            return true;
        }

        private void Fill()
        {
            _bufferCount = Math.Min(ReadBatchRecordCount, _remaining);
            int byteCount = _bufferCount * RecordByteCount;
            int position = 0;
            while (position < byteCount)
            {
                int read = RandomAccess.Read(
                    _handle,
                    _buffer.AsSpan(position, byteCount - position),
                    _offset + position);
                if (read == 0) throw new EndOfStreamException("A bounded SMF sort run is truncated.");
                position += read;
            }
            _offset += byteCount;
            _bufferIndex = 0;
        }
    }

    private sealed class Comparer : IComparer<CanonicalSmfTrackChannelEvent>
    {
        public static Comparer Instance { get; } = new();

        public int Compare(CanonicalSmfTrackChannelEvent x, CanonicalSmfTrackChannelEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.Role.CompareTo(y.Role);
            if (value != 0) return value;
            value = x.EventOrder.CompareTo(y.EventOrder);
            if (value != 0) return value;
            if (x.Role == CanonicalEventRole.DirectMidi
                && y.Role == CanonicalEventRole.DirectMidi)
            {
                value = CanonicalMidiOrdering.DirectEndpointOrder(x.Message).CompareTo(
                    CanonicalMidiOrdering.DirectEndpointOrder(y.Message));
                if (value != 0) return value;
            }
            value = KindOrder(x.Role).CompareTo(KindOrder(y.Role));
            if (value != 0) return value;
            return x.SourceObjectId.CompareTo(y.SourceObjectId);
        }

        private static int KindOrder(CanonicalEventRole role) => role switch
        {
            CanonicalEventRole.Reset => 0,
            CanonicalEventRole.RootBoundaryCleanup => 2,
            _ => 1
        };
    }
}
