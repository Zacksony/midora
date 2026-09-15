using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Midora.Domain;
using Midora.Common;

namespace Midora.Compiler;

internal sealed class BoundedCanonicalOpaqueEventSorter : IDisposable
{
    private const int MaximumRunRecordCount = 16_384;
    private const int MaximumRunPayloadByteCount = 4 * 1024 * 1024;
    private const int MaximumMergeFanIn = 64;
    private const int HeaderByteCount = 192;
    private readonly int _maximumRunRecordCount;
    private readonly int _maximumRunPayloadByteCount;
    private readonly int _maximumMergeFanIn;
    private readonly List<CanonicalOpaqueMidiEvent> _buffer;
    private readonly List<RunDescriptor> _runs = [];
    private MidoraOwnedTemporaryDirectoryLease? _runDirectoryLease;
    private FileStream? _runFile;
    private string? _runPath;
    private int _bufferPayloadByteCount;
    private bool _reading;
    private bool _disposed;

    public BoundedCanonicalOpaqueEventSorter()
        : this(MaximumRunRecordCount, MaximumRunPayloadByteCount, MaximumMergeFanIn)
    {
    }

    internal BoundedCanonicalOpaqueEventSorter(
        int maximumRunRecordCount,
        int maximumRunPayloadByteCount,
        int maximumMergeFanIn)
    {
        if (maximumRunRecordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRunRecordCount));
        if (maximumRunPayloadByteCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRunPayloadByteCount));
        if (maximumMergeFanIn < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumMergeFanIn));
        _maximumRunRecordCount = maximumRunRecordCount;
        _maximumRunPayloadByteCount = maximumRunPayloadByteCount;
        _maximumMergeFanIn = maximumMergeFanIn;
        _buffer = new(maximumRunRecordCount);
    }

    public void Add(CanonicalOpaqueMidiEvent value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded opaque-event sorter is already sealed.");
        int payloadByteCount = value.Payload.Length;
        if (payloadByteCount > PureMidiContentPackWriter.MaximumDecodedPageByteCount - 33)
        {
            throw new InvalidDataException(
                "An Opaque MIDI event exceeds the bounded page payload limit.");
        }
        if (_buffer.Count != 0
            && (_buffer.Count == _maximumRunRecordCount
                || _bufferPayloadByteCount > _maximumRunPayloadByteCount - payloadByteCount))
        {
            FlushRun();
        }
        _buffer.Add(value);
        _bufferPayloadByteCount = checked(_bufferPayloadByteCount + payloadByteCount);
    }

    public IEnumerable<CanonicalOpaqueMidiEventPage> ReadPages(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reading) throw new InvalidOperationException("The bounded opaque-event sorter can only be read once.");
        _reading = true;
        if (_runFile is null)
        {
            _buffer.Sort(Comparer.Instance);
            foreach (CanonicalOpaqueMidiEventPage page in Page(_buffer, cancellationToken))
                yield return page;
            yield break;
        }

        if (_buffer.Count != 0) FlushRun();
        _runFile.Flush(flushToDisk: false);
        CollapseRuns(cancellationToken);
        foreach (CanonicalOpaqueMidiEventPage page in MergeToPages(
            _runFile.SafeFileHandle,
            _runs,
            cancellationToken))
        {
            yield return page;
        }
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
        foreach (CanonicalOpaqueMidiEvent value in _buffer) WriteRecord(_runFile, value);
        _runs.Add(new(offset, checked(_runFile.Position - offset), _buffer.Count));
        _buffer.Clear();
        _bufferPayloadByteCount = 0;
    }

    private void CollapseRuns(CancellationToken cancellationToken)
    {
        int pass = 0;
        while (_runs.Count > _maximumMergeFanIn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string nextPath = Path.Combine(
                _runDirectoryLease!.DirectoryPath,
                $"opaque-pass-{++pass}.runs");
            FileStream next = OpenRunFile(nextPath);
            List<RunDescriptor> nextRuns = [];
            try
            {
                for (int offset = 0; offset < _runs.Count; offset += _maximumMergeFanIn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RunDescriptor[] group = _runs
                        .Skip(offset)
                        .Take(_maximumMergeFanIn)
                        .ToArray();
                    long runOffset = next.Position;
                    int count = 0;
                    foreach (CanonicalOpaqueMidiEvent value in Merge(
                        _runFile!.SafeFileHandle,
                        group,
                        cancellationToken))
                    {
                        WriteRecord(next, value);
                        count++;
                    }
                    nextRuns.Add(new(runOffset, checked(next.Position - runOffset), count));
                }
                next.Flush(flushToDisk: false);
            }
            finally
            {
                next.Dispose();
            }
            _runFile!.Dispose();
            if (_runPath is not null) File.Delete(_runPath);
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
            "opaque-sort");
        _runPath = Path.Combine(_runDirectoryLease.DirectoryPath, "opaque-pass-0.runs");
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

    private static IEnumerable<CanonicalOpaqueMidiEventPage> MergeToPages(
        SafeFileHandle handle,
        IReadOnlyList<RunDescriptor> runs,
        CancellationToken cancellationToken)
    {
        List<CanonicalOpaqueMidiEvent> page = new(CanonicalOpaqueMidiEventPage.MaximumRecordCount);
        int payloadByteCount = 0;
        foreach (CanonicalOpaqueMidiEvent value in Merge(handle, runs, cancellationToken))
        {
            if (page.Count != 0
                && (page.Count == CanonicalOpaqueMidiEventPage.MaximumRecordCount
                    || payloadByteCount > MaximumRunPayloadByteCount - value.Payload.Length))
            {
                yield return new(page.ToArray());
                page.Clear();
                payloadByteCount = 0;
            }
            page.Add(value);
            payloadByteCount = checked(payloadByteCount + value.Payload.Length);
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private static IEnumerable<CanonicalOpaqueMidiEvent> Merge(
        SafeFileHandle handle,
        IReadOnlyList<RunDescriptor> runs,
        CancellationToken cancellationToken)
    {
        PriorityQueue<RunReader, CanonicalOpaqueMidiEvent> queue = new(Comparer.Instance);
        foreach (RunDescriptor descriptor in runs)
        {
            RunReader reader = new(handle, descriptor);
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
        while (queue.TryDequeue(out RunReader? reader, out CanonicalOpaqueMidiEvent value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            if (reader.MoveNext()) queue.Enqueue(reader, reader.Current);
        }
    }

    private static IEnumerable<CanonicalOpaqueMidiEventPage> Page(
        IReadOnlyList<CanonicalOpaqueMidiEvent> source,
        CancellationToken cancellationToken)
    {
        List<CanonicalOpaqueMidiEvent> page = new(CanonicalOpaqueMidiEventPage.MaximumRecordCount);
        int payloadByteCount = 0;
        foreach (CanonicalOpaqueMidiEvent value in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Count != 0
                && (page.Count == CanonicalOpaqueMidiEventPage.MaximumRecordCount
                    || payloadByteCount > MaximumRunPayloadByteCount - value.Payload.Length))
            {
                yield return new(page.ToArray());
                page.Clear();
                payloadByteCount = 0;
            }
            page.Add(value);
            payloadByteCount = checked(payloadByteCount + value.Payload.Length);
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private static void WriteRecord(Stream stream, CanonicalOpaqueMidiEvent value)
    {
        Span<byte> header = stackalloc byte[HeaderByteCount];
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(header, value.Payload.Length);
        WriteId(header[8..], value.ExportTrackId);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], value.Tick);
        header[24] = (byte)value.Kind;
        header[25] = value.MetaType;
        BinaryPrimitives.WriteInt64LittleEndian(header[28..], value.StableOrder);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], value.SmfTrackOrder);
        SourceReference source = value.Source;
        WriteId(header[40..], source.TrackId);
        WriteId(header[48..], source.SegmentId);
        WriteId(header[56..], source.LogicalNoteId);
        WriteId(header[64..], source.EventInstrumentId);
        WriteId(header[72..], source.SubVoiceId);
        WriteId(header[80..], source.SourceEventId);
        BinaryPrimitives.WriteInt64LittleEndian(header[88..], source.Tick);
        WriteId(header[96..], source.LogicalParameterId);
        WriteId(header[104..], source.LogicalParameterMappingId);
        WriteId(header[112..], source.MappingStepId);
        WriteId(header[120..], source.MappingFunctionId);
        WriteId(header[128..], source.ValueCurveId);
        WriteId(header[136..], source.EnvelopeId);
        BinaryPrimitives.WriteInt32LittleEndian(header[144..], (int)source.Origin);
        WriteId(header[152..], source.MidiChannelRootId);
        WriteId(header[160..], source.PureMidiTrackId);
        WriteId(header[168..], source.MidiSegmentId);
        WriteId(header[176..], source.DirectMidiObjectId);
        WriteId(header[184..], source.ExportTrackId);
        stream.Write(header);
        stream.Write(value.Payload.Span);
    }

    private static CanonicalOpaqueMidiEvent ReadRecord(
        ReadOnlySpan<byte> header,
        ReadOnlyMemory<byte> payload) => new(
        ReadId(header[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(header[16..]),
        (OpaqueMidiEventKind)header[24],
        header[25],
        payload,
        BinaryPrimitives.ReadInt64LittleEndian(header[28..]),
        new SourceReference(
            TrackId: ReadId(header[40..]),
            SegmentId: ReadId(header[48..]),
            LogicalNoteId: ReadId(header[56..]),
            EventInstrumentId: ReadId(header[64..]),
            SubVoiceId: ReadId(header[72..]),
            SourceEventId: ReadId(header[80..]),
            Tick: BinaryPrimitives.ReadInt64LittleEndian(header[88..]),
            LogicalParameterId: ReadId(header[96..]),
            LogicalParameterMappingId: ReadId(header[104..]),
            MappingStepId: ReadId(header[112..]),
            MappingFunctionId: ReadId(header[120..]),
            ValueCurveId: ReadId(header[128..]),
            EnvelopeId: ReadId(header[136..]),
            Origin: (SourceOrigin)BinaryPrimitives.ReadInt32LittleEndian(header[144..]),
            MidiChannelRootId: ReadId(header[152..]),
            PureMidiTrackId: ReadId(header[160..]),
            MidiSegmentId: ReadId(header[168..]),
            DirectMidiObjectId: ReadId(header[176..]),
            ExportTrackId: ReadId(header[184..])),
        BinaryPrimitives.ReadInt32LittleEndian(header[36..]));

    private static void WriteId(Span<byte> destination, MidoraId value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Value);

    private static MidoraId ReadId(ReadOnlySpan<byte> source)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(source);
        return value == 0 ? default : MidoraId.FromSequence(value);
    }

    private readonly record struct RunDescriptor(long Offset, long ByteCount, int RecordCount);

    private sealed class RunReader
    {
        private readonly SafeFileHandle _handle;
        private readonly long _endOffset;
        private long _offset;
        private int _remaining;

        public RunReader(SafeFileHandle handle, RunDescriptor descriptor)
        {
            _handle = handle;
            _offset = descriptor.Offset;
            _endOffset = checked(descriptor.Offset + descriptor.ByteCount);
            _remaining = descriptor.RecordCount;
        }

        public CanonicalOpaqueMidiEvent Current { get; private set; }

        public bool MoveNext()
        {
            if (_remaining == 0)
            {
                if (_offset != _endOffset)
                    throw new InvalidDataException("A bounded opaque-event sort run has trailing bytes.");
                return false;
            }
            Span<byte> header = stackalloc byte[HeaderByteCount];
            ReadExactly(_handle, _offset, header);
            int payloadByteCount = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (payloadByteCount < 0
                || payloadByteCount > PureMidiContentPackWriter.MaximumDecodedPageByteCount - 33
                || _offset > _endOffset - HeaderByteCount - payloadByteCount)
            {
                throw new InvalidDataException("A bounded opaque-event sort run contains an invalid record extent.");
            }
            byte[] payload = GC.AllocateUninitializedArray<byte>(payloadByteCount);
            ReadExactly(_handle, checked(_offset + HeaderByteCount), payload);
            Current = ReadRecord(header, payload);
            _offset = checked(_offset + HeaderByteCount + payloadByteCount);
            _remaining--;
            return true;
        }

        private static void ReadExactly(SafeFileHandle handle, long offset, Span<byte> destination)
        {
            int position = 0;
            while (position < destination.Length)
            {
                int read = RandomAccess.Read(handle, destination[position..], offset + position);
                if (read == 0) throw new EndOfStreamException("A bounded opaque-event sort run is truncated.");
                position += read;
            }
        }
    }

    private sealed class Comparer : IComparer<CanonicalOpaqueMidiEvent>
    {
        public static Comparer Instance { get; } = new();

        public int Compare(CanonicalOpaqueMidiEvent x, CanonicalOpaqueMidiEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.StableOrder.CompareTo(y.StableOrder);
            if (value != 0) return value;
            value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
            if (value != 0) return value;
            return x.Source.DirectMidiObjectId.CompareTo(y.Source.DirectMidiObjectId);
        }
    }
}
