using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Midora.Domain;
using Midora.Midi;
using Midora.Common;

namespace Midora.Compiler;

internal sealed class BoundedCanonicalMidiRenderEventSorter : IDisposable
{
    private const int SortRunRecordCount = 131_072;
    private const int MaximumMergeFanIn = 64;
    private const int ReadBatchRecordCount = 256;
    private const int RecordByteCount = 112;
    private readonly int _sortRunRecordCount;
    private readonly int _maximumMergeFanIn;
    private readonly List<SortRecord> _buffer;
    private readonly List<RunDescriptor> _runs = [];
    private MidoraOwnedTemporaryDirectoryLease? _runDirectoryLease;
    private FileStream? _runFile;
    private string? _runPath;
    private bool _reading;
    private bool _disposed;
    private readonly IComparer<SortRecord> _comparer;

    public BoundedCanonicalMidiRenderEventSorter()
        : this(SortRunRecordCount, MaximumMergeFanIn)
    {
    }

    internal BoundedCanonicalMidiRenderEventSorter(
        int sortRunRecordCount,
        int maximumMergeFanIn,
        bool descending = false)
    {
        if (sortRunRecordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sortRunRecordCount));
        if (maximumMergeFanIn < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumMergeFanIn));
        _sortRunRecordCount = sortRunRecordCount;
        _maximumMergeFanIn = maximumMergeFanIn;
        _comparer = descending
            ? System.Collections.Generic.Comparer<SortRecord>.Create((x, y) => Comparer.Instance.Compare(y, x))
            : Comparer.Instance;
        // A full run is bounded at 131,072 records (112 wire bytes each).
        // Avoid preallocating it on the LOH: most rolling windows are far smaller. Grow on
        // actual demand and spill at the same fixed run boundary.
        _buffer = new(Math.Min(sortRunRecordCount, 4_096));
    }

    public void Add(CanonicalMidiEvent value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded MIDI render-event sorter is already sealed.");
        MidoraId monitoringSourceId = value.Source.Origin == SourceOrigin.MidiChannelRootLifecycle
                && value.Source.MidiChannelRootId != default
            ? value.Source.MidiChannelRootId
            : value.Source.TrackId;
        if (monitoringSourceId == default)
            throw new InvalidDataException("A paged MIDI render event has no monitoring source.");
        _buffer.Add(new(
            value.Tick,
            value.ZeroBasedPort,
            value.ZeroBasedChannel,
            value.Message,
            value.Role,
            value.StableOrder,
            value.SemanticTargetKey,
            value.SemanticGroup,
            value.Source.TrackId,
            value.Source.SegmentId,
            value.Source.DirectMidiObjectId,
            value.ExportTrackId,
            value.SmfTrackOrder,
            value.SmfEventOrder,
            monitoringSourceId,
            value.Source.MidiChannelRootId,
            value.Source.Tick,
            value.Source.Origin));
        if (_buffer.Count == _sortRunRecordCount) FlushRun();
    }

    public IEnumerable<CanonicalMidiRenderEventPage> ReadPages(CancellationToken cancellationToken)
    {
        List<CanonicalMidiRenderEvent> page = new(CanonicalMidiRenderEventPage.MaximumRecordCount);
        foreach (SortRecord value in ReadSortedRecords(cancellationToken))
        {
            page.Add(value.ToRenderEvent());
            if (page.Count != CanonicalMidiRenderEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray());
            page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    public IEnumerable<CanonicalMidiEventPage> ReadCanonicalPages(CancellationToken cancellationToken)
    {
        List<CanonicalMidiEvent> page = new(CanonicalMidiEventPage.MaximumRecordCount);
        foreach (SortRecord value in ReadSortedRecords(cancellationToken))
        {
            page.Add(value.ToCanonicalEvent());
            if (page.Count != CanonicalMidiEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray());
            page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private IEnumerable<SortRecord> ReadSortedRecords(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded MIDI sorter can only be read once.");
        _reading = true;
        if (_runFile is null)
        {
            _buffer.Sort(_comparer);
            foreach (SortRecord value in _buffer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
            yield break;
        }
        if (_buffer.Count != 0) FlushRun();
        _runFile.Flush(flushToDisk: false);
        CollapseRuns(cancellationToken);
        foreach (SortRecord value in Merge(_runFile.SafeFileHandle, _runs, cancellationToken))
            yield return value;
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
        _buffer.Sort(_comparer);
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
                $"midi-render-pass-{++pass}.runs");
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
                        foreach (SortRecord value in Merge(
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
            "midi-render-sort");
        _runPath = Path.Combine(_runDirectoryLease.DirectoryPath, "midi-render.runs");
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

    private IEnumerable<SortRecord> Merge(
        SafeFileHandle handle,
        IReadOnlyList<RunDescriptor> runs,
        CancellationToken cancellationToken)
    {
        PriorityQueue<RunReader, SortRecord> queue = new(_comparer);
        foreach (RunDescriptor descriptor in runs)
        {
            RunReader reader = new(handle, descriptor);
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
        while (queue.TryDequeue(out RunReader? reader, out SortRecord value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
    }

    private static void WriteRecord(Span<byte> destination, SortRecord value)
    {
        destination.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Tick);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], value.StableOrder);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], value.SemanticTargetKey);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], value.SemanticGroup);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], value.SmfEventOrder);
        WriteId(destination[40..], value.TrackId);
        WriteId(destination[48..], value.SegmentId);
        WriteId(destination[56..], value.DirectMidiObjectId);
        WriteId(destination[64..], value.ExportTrackId);
        WriteId(destination[72..], value.MonitoringSourceId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[80..], value.Message.PackedValue);
        BinaryPrimitives.WriteInt32LittleEndian(destination[84..], value.SmfTrackOrder);
        destination[88] = value.ZeroBasedPort;
        destination[89] = value.ZeroBasedChannel;
        destination[90] = (byte)value.Role;
        destination[91] = (byte)value.Origin;
        WriteId(destination[96..], value.RootId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[104..], value.SourceTick);
    }

    private static SortRecord ReadRecord(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadInt64LittleEndian(source),
        source[88],
        source[89],
        MidiMessage.FromPackedValue(BinaryPrimitives.ReadUInt32LittleEndian(source[80..])),
        (CanonicalEventRole)source[90],
        BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
        ReadId(source[40..]),
        ReadId(source[48..]),
        ReadId(source[56..]),
        ReadId(source[64..]),
        BinaryPrimitives.ReadInt32LittleEndian(source[84..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
        ReadId(source[72..]),
        ReadId(source[96..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[104..]),
        (SourceOrigin)source[91]);

    private static void WriteId(Span<byte> destination, MidoraId value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Value);

    private static MidoraId ReadId(ReadOnlySpan<byte> source)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(source);
        return value == 0 ? default : MidoraId.FromSequence(value);
    }

    private readonly record struct SortRecord(
        long Tick,
        byte ZeroBasedPort,
        byte ZeroBasedChannel,
        MidiMessage Message,
        CanonicalEventRole Role,
        long StableOrder,
        long SemanticTargetKey,
        long SemanticGroup,
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId DirectMidiObjectId,
        MidoraId ExportTrackId,
        int SmfTrackOrder,
        long SmfEventOrder,
        MidoraId MonitoringSourceId,
        MidoraId RootId,
        long SourceTick,
        SourceOrigin Origin)
    {
        public CanonicalMidiEvent ToCanonicalEvent() => new(
            Tick, ZeroBasedPort, ZeroBasedChannel, Message, Role, StableOrder,
            SemanticTargetKey, SemanticGroup,
            new(TrackId: TrackId, SegmentId: SegmentId, SourceEventId: DirectMidiObjectId,
                Tick: SourceTick, Origin: Origin, MidiChannelRootId: RootId,
                PureMidiTrackId: TrackId, MidiSegmentId: SegmentId,
                DirectMidiObjectId: DirectMidiObjectId, ExportTrackId: ExportTrackId),
            ExportTrackId, SmfTrackOrder, SmfEventOrder);

        public CanonicalMidiRenderEvent ToRenderEvent() => new(
            Tick,
            ZeroBasedPort,
            Message,
            TrackId,
            MonitoringSourceId,
            Role,
            StableOrder,
            SmfTrackOrder,
            SmfEventOrder,
            DirectMidiObjectId);
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

        public SortRecord Current { get; private set; }

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
                if (read == 0) throw new EndOfStreamException("A bounded MIDI render-event sort run is truncated.");
                position += read;
            }
            _offset += byteCount;
            _bufferIndex = 0;
        }
    }

    private sealed class Comparer : IComparer<SortRecord>
    {
        public static Comparer Instance { get; } = new();

        public int Compare(SortRecord x, SortRecord y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.Role.CompareTo(y.Role);
            if (value != 0) return value;
            value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
            if (value != 0) return value;
            value = x.ZeroBasedChannel.CompareTo(y.ZeroBasedChannel);
            if (value != 0) return value;
            value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
            if (value != 0) return value;
            if (x.Role == CanonicalEventRole.DirectMidi
                && y.Role == CanonicalEventRole.DirectMidi)
            {
                value = x.SmfEventOrder.CompareTo(y.SmfEventOrder);
                if (value != 0) return value;
            }
            value = x.StableOrder.CompareTo(y.StableOrder);
            if (value != 0) return value;
            value = x.TrackId.CompareTo(y.TrackId);
            if (value != 0) return value;
            value = x.SegmentId.CompareTo(y.SegmentId);
            if (value != 0) return value;
            value = x.DirectMidiObjectId.CompareTo(y.DirectMidiObjectId);
            if (value != 0) return value;
            value = x.ExportTrackId.CompareTo(y.ExportTrackId);
            if (value != 0) return value;
            value = x.SemanticTargetKey.CompareTo(y.SemanticTargetKey);
            if (value != 0) return value;
            value = x.SemanticGroup.CompareTo(y.SemanticGroup);
            if (value != 0) return value;
            return x.Message.PackedValue.CompareTo(y.Message.PackedValue);
        }
    }
}
